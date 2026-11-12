using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Contracts.Worker;
using TiaMcpServer.Json;
using TiaMcpServer.Safety.Pipeline;
using TiaMcpServer.Tools;
using TiaMcpServer.Worker;

namespace TiaMcpServer.Plc;

/// <summary>Takes back earlier plc_write calls from the journal of the Produktiv profile.</summary>
[McpServerToolType]
public class PlcUndoTools
{
    [McpServerTool(Name = "plc_undo", ReadOnly = false, Destructive = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PlcUndoResponse))]
    [Description("Takes back the most recent plc_write calls of this project, newest first: block and type content from its snapshot, tags, tag tables, user constants, blocks and groups through the opposite operation. The answer names the calls in takenBack. list: true only shows the open calls. A call from before this server started is not taken back unless includeEarlierSessions is true, so one step too many cannot undo older work; the journal survives a restart and snapshots are kept for 30 days. An object changed since the write fails with state_changed unless force is true. Never saves the project. HMI changes are not covered.")]
    public static async Task<CallToolResult> PlcUndo(
        OpennessWorkerClient workerClient,
        WriteExecution execution,
        [Description("How many plc_write calls to take back, newest first. Default 1.")] int steps = 1,
        [Description("True restores even if the object was changed after the write; that later change is lost.")] bool force = false,
        [Description("True changes nothing and lists the open calls in openCalls, newest first.")] bool list = false,
        [Description("True also takes back calls made before this server started. Check them with list first.")] bool includeEarlierSessions = false,
        CancellationToken cancellationToken = default)
    {
        var protection = workerClient.Protection;
        if (protection is null)
        {
            return Error("plc_undo is only available in the produktiv access mode.");
        }

        if (steps < 1)
        {
            return Error("steps must be at least 1.");
        }

        // Unbound, the journal key is empty and an empty list would read as "nothing is open".
        var binding = await workerClient.RequireVerifiedBindingAsync().ConfigureAwait(false);
        if (!binding.Success)
        {
            return Error(binding.FailureCategory ?? WorkerFailureCategories.BindingConflict,
                binding.Error ?? "No project is bound. Use bind_project first.");
        }

        var projectKey = PlcProtection.ProjectKey(workerClient.BindingSnapshot.ProjectPath);
        var calls = protection.ReadOpenCalls(projectKey);
        if (list)
        {
            return StructuredToolResult.CreateCanonical(CanonicalJson.Serialize(new PlcUndoResponse(
                "plc_undo", PlcWriteContractVersion.Current, WritePhases.Preview, true, null,
                Array.Empty<string>(), Array.Empty<WriteGuardReport>(), Array.Empty<PlcWriteEffectPresentation>(), null, null, null)
            {
                OpenCalls = calls.Reverse().Select(call => Describe(call, protection)).ToArray(),
            }), isError: false);
        }

        if (calls.Count == 0)
        {
            return Error("There is nothing to take back for this project.");
        }

        var requested = calls.Reverse().Take(steps).ToArray();
        var earlier = requested.FirstOrDefault(call => call[0].At < protection.SessionStart);
        if (earlier is not null && !includeEarlierSessions)
        {
            var own = requested.TakeWhile(call => call[0].At >= protection.SessionStart).Count();
            var info = Describe(earlier, protection);
            var advice = own > 0
                ? $"This session has {own} open call(s) in the requested range: use steps {own}, or includeEarlierSessions if the older call is meant."
                : "This session has no open calls. Use includeEarlierSessions if the older call is meant.";
            return Error($"Nothing was taken back. Call {info.CallId} ({string.Join(", ", info.Operations)}) comes from an earlier server session. {advice} list shows all open calls.");
        }

        CallToolResult? last = null;
        var takenBack = new List<PlcUndoCallInfo>();
        foreach (var call in requested)
        {
            var refused = call.FirstOrDefault(entry => entry.NotUndoableReason is not null);
            if (refused is not null)
            {
                return Error($"Call {refused.CallId} cannot be taken back: operation '{refused.OperationId}': {refused.NotUndoableReason}.");
            }

            // Newest item first; an operation that needs a fresh read of an object created just before it runs alone.
            var batches = new List<List<PlcInverseOperation>> { new() };
            foreach (var inverse in call.AsEnumerable().Reverse().SelectMany(entry => entry.Inverse))
            {
                if (inverse.RunSeparately && batches[^1].Count > 0)
                {
                    batches.Add(new());
                }

                batches[^1].Add(inverse);
            }

            foreach (var batch in batches.Where(b => b.Count > 0))
            {
                var items = new List<PlcOperationRequest>();
                foreach (var inverse in batch)
                {
                    var prepared = await PrepareAsync(workerClient, inverse, force).ConfigureAwait(false);
                    if (prepared.Error is not null)
                    {
                        return Error($"Call {call[0].CallId}: {prepared.Error}");
                    }

                    items.Add(prepared.Request!);
                }

                last = await execution.RunAsync(new PlcWriteDomain(workerClient, isUndo: true),
                    new WriteCall<PlcOperationRequest>(items, false), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (last.IsError == true || !Succeeded(last))
                {
                    return last;
                }
            }

            protection.MarkUndone(projectKey, call[0].CallId);
            takenBack.Add(Describe(call, protection));
        }

        return last is null ? Error("There is nothing to take back for this project.") : WithTakenBack(last, takenBack);
    }

    private static PlcUndoCallInfo Describe(IReadOnlyList<PlcJournalEntry> call, PlcProtection protection)
        => new(call[0].CallId, call[0].At, call[0].At < protection.SessionStart,
            call.Select(entry => string.IsNullOrEmpty(entry.Target) ? entry.Operation : $"{entry.Operation} {entry.Target}").ToArray(),
            call.FirstOrDefault(entry => entry.NotUndoableReason is not null)?.NotUndoableReason);

    /// <summary>The answer of the last write, plus every call this request took back.</summary>
    private static CallToolResult WithTakenBack(CallToolResult result, IReadOnlyList<PlcUndoCallInfo> takenBack)
    {
        if (result.StructuredContent is not System.Text.Json.JsonElement element)
        {
            return result;
        }

        var document = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())!.AsObject();
        document["takenBack"] = System.Text.Json.Nodes.JsonNode.Parse(CanonicalJson.Serialize(takenBack));
        return StructuredToolResult.CreateCanonical(
            document.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            result.IsError == true);
    }

    private static async Task<(PlcOperationRequest? Request, string? Error)> PrepareAsync(
        OpennessWorkerClient client, PlcInverseOperation inverse, bool force)
    {
        var request = inverse.Request;
        if (inverse.ContentFile is null)
        {
            return (request, null);
        }

        if (!File.Exists(inverse.ContentFile))
        {
            return (null, "the snapshot file no longer exists.");
        }

        var content = await File.ReadAllTextAsync(inverse.ContentFile).ConfigureAwait(false);
        var hash = inverse.ExpectedContentHash;
        if (hash is null || force)
        {
            var format = PlcFormatNames.Normalize(request.Operation, request.Format);
            var export = request.Operation == "update_block_logic"
                ? await client.GetBlockContentAsync(request.BlockPath!, format: format).ConfigureAwait(false)
                : await client.GetTypeContentAsync(request.TypePath!, format: format).ConfigureAwait(false);
            if (!export.Success || string.IsNullOrEmpty(export.Payload))
            {
                return (null, "the current content could not be read.");
            }

            hash = ContentHashes.Compute(format, export.Payload);
        }

        return (request.WithContent(content, hash), null);
    }

    private static bool Succeeded(CallToolResult result)
    {
        if (result.StructuredContent is not System.Text.Json.JsonElement element
            || !element.TryGetProperty("success", out var success))
        {
            return false;
        }

        return success.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static CallToolResult Error(string message) => Error(WorkerFailureCategories.ValidationError, message);

    private static CallToolResult Error(string category, string message)
        => StructuredToolResult.CreateCanonical(CanonicalJson.Serialize(new PlcGuardedWriteResponse(
            "plc_undo", PlcWriteContractVersion.Current, WritePhases.Error, false,
            new WriteToolError(category, message),
            Array.Empty<string>(), Array.Empty<WriteGuardReport>(), Array.Empty<PlcWriteEffectPresentation>(),
            null, null, null)), isError: true);
}
