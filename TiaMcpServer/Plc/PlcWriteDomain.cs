using TiaMcpServer.Json;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.Safety.Pipeline;
using TiaMcpServer.Worker;
using TiaMcpServer.Contracts.Block;
using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.Contracts.Worker;

namespace TiaMcpServer.Plc;

/// <summary>How a non-content item is taken back: the operations to run, or why it cannot be.</summary>
public sealed record PlcUndoPlan(IReadOnlyList<PlcOperationRequest> Operations, string? NotUndoableReason);

/// <summary>
/// The plc_write domain for the guarded pipeline; one instance per call. The planner's guards are
/// cached by operationId so <see cref="EvaluateGuards"/> stays pure, and a late re-plan refreshes
/// its item's entry. Only block and info guards exist, so nothing ever asks the user.
/// With a <see cref="PlcProtection"/> on the client (Produktiv profile) every applied item gets a
/// snapshot and a journal entry, a change that causes new compile errors is taken back, and the
/// read-back is compared with what was sent.
/// </summary>
public sealed class PlcWriteDomain
    : IWriteDomain<PlcOperationRequest, PlcWriteEffect, PlcWriteVerification, PlcGuardedWriteResponse>
{
    private readonly OpennessWorkerClient client;
    private readonly PlcWritePlanner _planner;
    private readonly PlcProtection? _protection;
    private readonly bool _isUndo;
    private readonly string _toolName;
    private readonly string? _projectKey;
    private readonly string? _callId;
    private readonly Dictionary<string, IReadOnlyList<FiredGuard>> _guards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlcWriteEffect> _effects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _snapshotFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _readBacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rolledBack = new(StringComparer.Ordinal);
    private readonly List<PlcItemProtection> _protectionItems = new();
    private IReadOnlyList<PlcOperationRequest> _items = Array.Empty<PlcOperationRequest>();
    private bool _journaled;

    public PlcWriteDomain(OpennessWorkerClient client) : this(client, isUndo: false)
    {
    }

    internal PlcWriteDomain(OpennessWorkerClient client, bool isUndo)
    {
        this.client = client;
        _protection = client.Protection;
        _isUndo = isUndo;
        _toolName = isUndo ? "plc_undo" : "plc_write";
        _planner = new PlcWritePlanner(client, strict: _protection is not null);
        if (_protection is not null)
        {
            _projectKey = PlcProtection.ProjectKey(client.BindingSnapshot.ProjectPath);
            _callId = PlcProtection.NewCallId(_protection.Now);
        }
    }

    public string ToolName => _toolName;

    public string ContractVersion => PlcWriteContractVersion.Current;

    public bool ConfirmsEveryCall => false;

    public WriteValidation Validate(IReadOnlyList<PlcOperationRequest> items, McpAccessMode accessMode)
        => WriteValidation.Compose(
            PlcOperationCatalog.ValidateWrite(items),
            () => PlcWritePayloadBudget.MeasureProtectedCore(items),
            () => PlcOperationCatalog.ValidateAccessMode(items, accessMode),
            ToolName);

    public async Task<WritePlan<PlcWriteEffect>> PlanAsync(IReadOnlyList<PlcOperationRequest> items)
    {
        var result = await _planner.PlanAsync(items).ConfigureAwait(false);
        if (!result.Plan.Success) return result.Plan;
        _items = items.ToArray();
        for (var i = 0; i < items.Count; i++)
        {
            _guards[items[i].OperationId] = result.Guards.GetValueOrDefault(items[i].OperationId) ?? Array.Empty<FiredGuard>();
            _effects[items[i].OperationId] = result.Plan.Items[i].Effect!;
        }

        return result.Plan;
    }

    public IReadOnlyList<FiredGuard> EvaluateGuards(IReadOnlyList<PlcOperationRequest> items, IReadOnlyList<ItemPlan<PlcWriteEffect>> plans)
        => items.SelectMany(item => _guards.GetValueOrDefault(item.OperationId) ?? Array.Empty<FiredGuard>()).ToArray();

    public async Task<ItemReplan<PlcWriteEffect>> ReplanAsync(PlcOperationRequest item)
    {
        var result = await _planner.ReplanAsync(item).ConfigureAwait(false);
        if (!result.Replan.Success) return result.Replan;
        _guards[item.OperationId] = result.Guards;
        // The re-plan reads real state, so it knows no dependency; the presented effect keeps the planned one.
        var plan = result.Replan.Plan!;
        var effect = plan.Effect! with { DependsOn = _effects.GetValueOrDefault(item.OperationId)?.DependsOn };
        _effects[item.OperationId] = effect;
        return ItemReplan<PlcWriteEffect>.Ok(plan with { Effect = effect });
    }

    public async Task<WorkerCallResult> MutateAsync(PlcOperationRequest item)
    {
        _attempted.Add(item.OperationId);
        var plan = _planner.Contents.GetValueOrDefault(item.OperationId);
        var effective = plan?.Effective is { } text && item.Edits is not null
            ? item.WithContent(text, plan.BeforeHash)
            : item;
        if (_protection is null || plan is null)
        {
            return await PlcWorkerInvoker.InvokeWriteAsync(client, effective).ConfigureAwait(false);
        }

        _snapshotFiles[item.OperationId] = _protection.SaveSnapshot(_projectKey!, _callId!, item.OperationId,
            item.BlockPath ?? item.TypePath ?? item.OperationId, plan.Format, Restorable(plan));
        var result = await PlcWorkerInvoker.InvokeWriteAsync(client, effective).ConfigureAwait(false);
        if (item.Operation is not ("update_block_logic" or "update_type_content"))
        {
            return result;
        }

        // TIA Portal reports an import that compiles with errors as a failed call with a committed import.
        if (CompileErrors(result) > 0)
        {
            if (_isUndo || item.AllowCompileErrors == true)
            {
                _notes[item.OperationId] = "The change has compile errors and was kept as requested. Run compile_check when the last step is done.";
                return Kept(result);
            }

            return await TakeBackCompileErrorsAsync(item, plan, effective, result).ConfigureAwait(false);
        }

        return result.Success ? result : await RestoreAfterFailureAsync(item, plan, result).ConfigureAwait(false);
    }

    /// <summary>A change that stays in the project is a completed item, whatever the compile said about it.</summary>
    private static WorkerCallResult Kept(WorkerCallResult result)
        => result.Success ? result : WorkerCallResult.Ok("Import succeeded.") with { BlockImportOutcome = result.BlockImportOutcome };

    private static bool Committed(WorkerCallResult result)
        => result.Success || result.BlockImportOutcome?.TargetMutationCommitted == true;

    /// <summary>The import applies only the Simatic ML document, so that is what a restore sends.</summary>
    private static string Restorable(PlcWritePlanner.ContentPlan plan)
        => plan.Format == "xml" ? PlcContentBundle.Primary(plan.Before) : plan.Before;

    private static int CompileErrors(WorkerCallResult result)
        => result.BlockImportOutcome?.CompileReport?.TotalErrorCount ?? 0;

    /// <summary>A failed import can still have changed the object; only then the previous content comes back.</summary>
    private async Task<WorkerCallResult> RestoreAfterFailureAsync(PlcOperationRequest item, PlcWritePlanner.ContentPlan plan, WorkerCallResult failed)
    {
        var current = await ExportAsync(item, plan.Format).ConfigureAwait(false);
        if (current is null)
        {
            _notes[item.OperationId] = "The write failed and the current content could not be read. Check the object with plc_read; plc_undo can restore the snapshot.";
            return failed;
        }

        if (string.Equals(ContentHashes.Compute(plan.Format, current), plan.BeforeHash, StringComparison.Ordinal))
        {
            return failed;
        }

        var restored = await RestoreAsync(item, plan, current).ConfigureAwait(false);
        if (Committed(restored))
        {
            _rolledBack.Add(item.OperationId);
            _notes[item.OperationId] = "The write failed after changing the object. The previous content was restored.";
        }
        else
        {
            _notes[item.OperationId] = "The write failed after changing the object, and the automatic restore failed too. The previous content is in the snapshot file.";
        }

        return failed;
    }

    /// <summary>
    /// First compiles the PLC: errors that compile clears were never the change's. Otherwise restores the
    /// previous content and compares: errors that remain were there before this change, so the change is
    /// applied again. Otherwise the change caused them and stays out.
    /// </summary>
    private async Task<WorkerCallResult> TakeBackCompileErrorsAsync(PlcOperationRequest item, PlcWritePlanner.ContentPlan plan,
        PlcOperationRequest effective, WorkerCallResult applied)
    {
        // An xml import compiles the block alone. After an interface change its calls and instance data are out
        // of date until the PLC is compiled, and the block compile reports errors that the PLC compile clears.
        // One PLC compile settles that; the takeback below costs a restore, a second import and their compiles.
        var plcReport = await CompilePlcAsync(item).ConfigureAwait(false);
        if (plcReport is { TotalErrorCount: 0 } && applied.BlockImportOutcome is { } importOutcome)
        {
            _notes[item.OperationId] = "TIA Portal reported compile errors for the block alone. Compiling its PLC updated calls and instance data and found none, so the change was kept.";
            return Kept(applied with
            {
                BlockImportOutcome = importOutcome with
                {
                    CompileStage = "succeeded",
                    CompileReport = OnlySeverity(plcReport, "Warning"),
                    CompileDetailsOmitted = false,
                },
            });
        }

        // The import report leaves out the compiler's wording, which is what a fix needs. Read it while the errors exist.
        var outcome = await WithCompilerMessagesAsync(item, plan, applied, plcReport).ConfigureAwait(false);
        var current = await ExportAsync(item, plan.Format).ConfigureAwait(false);
        if (current is null)
        {
            _notes[item.OperationId] = "The change caused compile errors and the current content could not be read, so nothing was taken back.";
            return applied with { BlockImportOutcome = outcome };
        }

        var restored = await RestoreAsync(item, plan, current).ConfigureAwait(false);
        if (!Committed(restored))
        {
            _notes[item.OperationId] = "The change caused compile errors and the automatic restore failed. The previous content is in the snapshot file.";
            return applied with { BlockImportOutcome = outcome };
        }

        if (CompileErrors(restored) >= CompileErrors(applied))
        {
            var before = await ExportAsync(item, plan.Format).ConfigureAwait(false);
            var again = await PlcWorkerInvoker.InvokeWriteAsync(client,
                effective.WithContent(effective.Content!, before is null ? null : ContentHashes.Compute(plan.Format, before))).ConfigureAwait(false);
            _notes[item.OperationId] = "These compile errors existed before this change, so the change was kept.";
            return Committed(again) ? Kept(again) : again;
        }

        _rolledBack.Add(item.OperationId);
        _notes[item.OperationId] = "The change caused new compile errors and was taken back.";
        return WorkerCallResult.Fail(WorkerFailureCategories.RolledBack,
            $"The change caused {CompileErrors(applied)} compile error(s) and was taken back; the block has its previous content again. Fix the change and send it again, or set allowCompileErrors if it needs several steps. The messages are in blockImportOutcome.compileReport.")
            with { BlockImportOutcome = outcome, DispatchState = WorkerDispatchState.Unknown };
    }

    private const int MaxCompilerMessages = 20;

    /// <param name="plcReport">The PLC compile that was just run. A source import is judged by it; an xml import
    /// compiles the block for the messages, so errors elsewhere in the PLC cannot push the block's own out of the list.</param>
    private async Task<BlockImportOutcomeInfo?> WithCompilerMessagesAsync(PlcOperationRequest item, PlcWritePlanner.ContentPlan plan,
        WorkerCallResult applied, CompileCheckReport? plcReport)
    {
        var outcome = applied.BlockImportOutcome;
        if (outcome is null)
        {
            return null;
        }

        try
        {
            var report = plan.Format == "xml" ? null : plcReport;
            if (report is null)
            {
                var check = plan.Format == "xml"
                    ? await client.CompileCheckAsync(item.BlockPath, null).ConfigureAwait(false)
                    : await client.CompileCheckAsync(null, PlcNameOf(item)).ConfigureAwait(false);
                if (string.IsNullOrEmpty(check.Payload))
                {
                    return outcome;
                }

                report = CanonicalJson.Deserialize<CompileCheckReport>(check.Payload);
            }

            report = OnlySeverity(report, "Error");
            return report.TotalErrorCount > 0 ? outcome with { CompileReport = report, CompileDetailsOmitted = false } : outcome;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return outcome;
        }
    }

    /// <summary>Compiles the PLC the item belongs to; null when no report came back.</summary>
    private async Task<CompileCheckReport?> CompilePlcAsync(PlcOperationRequest item)
    {
        try
        {
            var check = await client.CompileCheckAsync(null, PlcNameOf(item)).ConfigureAwait(false);
            return check.Success && !string.IsNullOrEmpty(check.Payload)
                ? CanonicalJson.Deserialize<CompileCheckReport>(check.Payload)
                : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? PlcNameOf(PlcOperationRequest item) => _effects.GetValueOrDefault(item.OperationId)?.Target.PlcName;

    /// <summary>Keeps the messages of one severity, at most <see cref="MaxCompilerMessages"/> per PLC; the totals stay.</summary>
    private static CompileCheckReport OnlySeverity(CompileCheckReport report, string severity)
    {
        foreach (var compiled in report.Plcs)
        {
            compiled.Messages = compiled.Messages
                .Where(m => string.Equals(m.Severity, severity, StringComparison.OrdinalIgnoreCase))
                .Take(MaxCompilerMessages).ToList();
        }

        return report;
    }

    private Task<WorkerCallResult> RestoreAsync(PlcOperationRequest item, PlcWritePlanner.ContentPlan plan, string current)
        => PlcWorkerInvoker.InvokeWriteAsync(client, new PlcOperationRequest
        {
            OperationId = item.OperationId,
            Operation = item.Operation,
            BlockPath = item.BlockPath,
            TypePath = item.TypePath,
            Format = plan.Format,
            Content = Restorable(plan),
            ExpectedContentHash = ContentHashes.Compute(plan.Format, current),
        });

    private async Task<string?> ExportAsync(PlcOperationRequest item, string format)
    {
        var export = item.Operation == "update_block_logic"
            ? await client.GetBlockContentAsync(item.BlockPath!, format: format).ConfigureAwait(false)
            : await client.GetTypeContentAsync(item.TypePath!, format: format).ConfigureAwait(false);
        return export.Success && !string.IsNullOrEmpty(export.Payload) ? export.Payload : null;
    }

    public StructuredOperationItem Project(PlcOperationRequest item, WorkerCallResult result)
        => PlcPayloadContract.ProjectWrite(item, result);

    public async Task<PlcWriteVerification?> VerifyAsync(StructuredOperationBatch batch)
    {
        var verification = await PlcVerificationPolicy.VerifyAsync(client, _items, _effects,
            StructuredOperationBatch.FromItems(batch.Operations.Where(o => _attempted.Contains(o.OperationId)).ToArray()),
            _protection is null ? null : (operationId, payload) => _readBacks[operationId] = payload).ConfigureAwait(false);
        if (_protection is not null)
        {
            RecordProtection(batch, verification);
        }

        return verification;
    }

    /// <summary>Compares each read-back with what was sent and journals every item that took effect.</summary>
    private void RecordProtection(StructuredOperationBatch batch, PlcWriteVerification verification)
    {
        foreach (var item in _items)
        {
            if (!_attempted.Contains(item.OperationId))
            {
                continue;
            }

            var succeeded = batch.Operations.Any(o => o.OperationId == item.OperationId
                && string.Equals(o.Status, OperationBatchStatus.Succeeded, StringComparison.Ordinal));
            var plan = _planner.Contents.GetValueOrDefault(item.OperationId);
            var hash = verification.Operations.FirstOrDefault(o => o.OperationId == item.OperationId)?.ContentHash;
            var inverse = BuildInverse(item, plan, hash, out var reason);
            if (succeeded && !_isUndo)
            {
                _journaled = true;
                _protection!.Append(_projectKey!, new PlcJournalEntry(_callId!, _protection.Now, item.OperationId, item.Operation,
                    item.BlockPath ?? item.TypePath ?? item.TableName, inverse, reason));
            }

            _protectionItems.Add(new PlcItemProtection(item.OperationId, _snapshotFiles.GetValueOrDefault(item.OperationId),
                _rolledBack.Contains(item.OperationId), _notes.GetValueOrDefault(item.OperationId),
                succeeded ? ImportDifference(item, plan) : null, succeeded ? reason : null));
        }
    }

    private List<PlcInverseOperation> BuildInverse(PlcOperationRequest item, PlcWritePlanner.ContentPlan? plan, string? afterHash, out string? reason)
    {
        reason = null;
        var snapshot = _snapshotFiles.GetValueOrDefault(item.OperationId);
        if (item.Operation is "update_block_logic" or "update_type_content")
        {
            if (snapshot is null)
            {
                reason = "no snapshot of the previous content exists";
                return new();
            }

            return new()
            {
                new(new PlcOperationRequest
                {
                    OperationId = $"undo_{item.OperationId}",
                    Operation = item.Operation,
                    BlockPath = item.BlockPath,
                    TypePath = item.TypePath,
                    Format = plan!.Format,
                    AllowCompileErrors = item.Operation == "update_block_logic" ? true : null,
                }, snapshot, afterHash, false),
            };
        }

        var undoPlan = _planner.UndoPlans.GetValueOrDefault(item.OperationId);
        if (undoPlan is null || undoPlan.NotUndoableReason is not null)
        {
            reason = undoPlan?.NotUndoableReason ?? "this operation has no way back";
            return new();
        }

        var operations = undoPlan.Operations.Select(op => new PlcInverseOperation(op, null, null, false)).ToList();
        if (item.Operation == "delete_block")
        {
            if (snapshot is null)
            {
                reason = "the block could not be exported before it was deleted";
                return new();
            }

            operations.Add(new(new PlcOperationRequest
            {
                OperationId = $"undo_{item.OperationId}_content",
                Operation = "update_block_logic",
                BlockPath = item.BlockPath,
                Format = "xml",
                AllowCompileErrors = true,
            }, snapshot, null, true));
        }

        return operations;
    }

    /// <summary>What TIA Portal changed beyond the submitted text; null when the read-back equals it.</summary>
    private PlcContentDiffEvidence? ImportDifference(PlcOperationRequest item, PlcWritePlanner.ContentPlan? plan)
    {
        if (plan?.Effective is null || !_readBacks.TryGetValue(item.OperationId, out var readBack))
        {
            return null;
        }

        var sent = plan.Format == "xml" ? PlcContentBundle.Primary(plan.Effective) : plan.Effective;
        var read = plan.Format == "xml" ? PlcContentBundle.Primary(readBack) : readBack;
        return string.Equals(Normalize(sent), Normalize(read), StringComparison.Ordinal) ? null : PlcContentDiff.Build(sent, read);
    }

    /// <summary>TIA Portal reformats text when it imports source; only a difference in content is worth reporting.</summary>
    private static string Normalize(string text)
        => string.Join('\n', text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    public bool VerificationSucceeded(PlcWriteVerification? verification) => verification?.Success == true;

    public PlcGuardedWriteResponse Compose(WriteReport<PlcWriteEffect, PlcWriteVerification> report)
        => PlcWritePayloadBudget.Apply(new PlcGuardedWriteResponse(ToolName, ContractVersion, report.Phase, report.Success,
            report.Error, report.Warnings, report.Guards,
            report.Effects.Select(e => new PlcWriteEffectPresentation(e.OperationId, e.Effect, null)).ToArray(),
            report.Batch, report.Verification, null)
        {
            Protection = _protection is null || _callId is null || _protectionItems.Count == 0
                ? null
                : new PlcWriteProtectionReport(_callId, _journaled && _protectionItems.All(i => i.NotUndoableReason is null), _protectionItems.ToArray()),
        });
}
