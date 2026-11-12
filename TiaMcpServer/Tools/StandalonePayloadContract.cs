using System.Text.Json;
using TiaMcpServer.Json;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.Worker;
using TiaMcpServer.Contracts.Block;
using TiaMcpServer.Contracts.Project;
using TiaMcpServer.Contracts.Worker;

namespace TiaMcpServer.Tools;

/// <summary>Strict, operation-specific worker decoding before any public budget projection.</summary>
internal static class StandalonePayloadContract
{
    internal const string ProtocolFailureMessage = "The worker payload did not match the declared standalone result contract.";

    internal static StructuredOperationFailure Failure(WorkerCallResult result)
        => new(result.FailureCategory ?? WorkerFailureCategories.ProtocolError,
            result.Error ?? "The worker operation failed.");

    internal static StructuredOperationFailure? Rejection(WorkerCallResult result)
        => !result.Success && !result.IsPostOperationFailure && (result.DispatchState == WorkerDispatchState.NotSent
            || result.FailureCategory is WorkerFailureCategories.AccessDenied
                or WorkerFailureCategories.BindingConflict or WorkerFailureCategories.ValidationError)
            ? Failure(result) : null;

    internal static StandaloneToolOutcome<ProjectStatusInfo> DecodeStatus(WorkerCallResult result)
    {
        if (!result.Success)
            return new(OperationBatchStatus.Failed, null, Failure(result), null);

        try
        {
            var payload = CanonicalJson.DeserializeWorkerPayload<ProjectStatusResultInfo>(result.Payload);
            var status = payload.Project;
            if (!payload.Success || payload.Operation != "get_project_status" || status is null
                || !string.Equals(ProjectPathNormalization.Canonicalize(payload.ProjectPath),
                    ProjectPathNormalization.Canonicalize(status.Path), StringComparison.OrdinalIgnoreCase)
                || (status.IsOpen && string.IsNullOrWhiteSpace(status.Path))
                || (!status.IsOpen && status.Path is not null)
                || status.Size < 0)
                throw new JsonException();
            ValidateMetadata(status.Metadata);
            if (status.IsOpen) ProjectContextPayloadContract.Validate(status.Context, status.Path!);
            else if (status.Context is not null) throw new JsonException();
            return new(OperationBatchStatus.Succeeded, status, null, null);
        }
        catch (JsonException)
        {
            return ProtocolFailure<ProjectStatusInfo>();
        }
    }

    internal static StandaloneToolOutcome<T> ProtocolFailure<T>() where T : class
        => new(OperationBatchStatus.Failed, null,
            new(WorkerFailureCategories.ProtocolError, ProtocolFailureMessage), null);

    internal static StandaloneToolOutcome<CompileCheckReport> DecodeCompile(WorkerCallResult result)
    {
        if (!result.Success)
            return new(OperationBatchStatus.Failed, null, Failure(result), null);
        try
        {
            var report = CanonicalJson.DeserializeWorkerPayload<CompileCheckReport>(result.Payload);
            ValidateCompile(report);
            return new(StandaloneCompileOutcome.IsPassed(report) ? OperationBatchStatus.Succeeded : OperationBatchStatus.Failed,
                report, null, null);
        }
        catch (JsonException)
        {
            return ProtocolFailure<CompileCheckReport>();
        }
    }

    private static void ValidateCompile(CompileCheckReport report)
    {
        if (report.Scope is not ("plc" or "block") || report.Plcs is null || report.Plcs.Count == 0
            || string.IsNullOrWhiteSpace(report.OverallState)
            || report.TotalErrorCount < 0 || report.TotalWarningCount < 0
            || (report.Scope == "plc" && report.BlockPath is not null)
            || (report.Scope == "block" && (string.IsNullOrWhiteSpace(report.BlockPath) || report.Plcs.Count != 1)))
            throw new JsonException();

        long errors = 0, warnings = 0;
        foreach (var plc in report.Plcs)
        {
            if (plc is null || string.IsNullOrWhiteSpace(plc.PlcName) || string.IsNullOrWhiteSpace(plc.State)
                || plc.Messages is null || plc.DiagnosticNotes is null || plc.ErrorCount < 0 || plc.WarningCount < 0
                || (plc.State is "Success" or "Warning" && plc.ErrorCount > 0))
                throw new JsonException();
            RejectNullElements(plc.Messages);
            RejectNullElements(plc.DiagnosticNotes);
            foreach (var message in plc.Messages)
                if (message.Description is null || message.Path is null || message.Severity is not ("Error" or "Warning" or "Information"))
                    throw new JsonException();
            errors += plc.ErrorCount;
            warnings += plc.WarningCount;
        }
        if (errors != report.TotalErrorCount || warnings != report.TotalWarningCount)
            throw new JsonException();

        // Mirror only the producer's aggregation, not a guessed enum whitelist. Unknown states
        // remain evidence and are classified as unsuccessful by IsPassed even if aggregation says Success.
        var expectedState = report.Scope == "block" ? report.Plcs[0].State
            : report.Plcs.Any(plc => plc.State == "Error") ? "Error"
            : report.Plcs.Any(plc => plc.State == "Warning") ? "Warning" : "Success";
        if (report.OverallState != expectedState) throw new JsonException();
    }

    private static void ValidateMetadata(ProjectMetadataInfo? metadata)
    {
        if (metadata is null) return;
        RejectNullElements(metadata.Comment?.Translations);
        RejectNullElements(metadata.LanguageSettings?.Languages);
        RejectNullElements(metadata.LanguageSettings?.ActiveLanguages);
        RejectNullElements(metadata.HistoryEntries);
        RejectNullElements(metadata.UsedProducts);
    }

    internal static void RejectNullElements<T>(IEnumerable<T>? values)
    {
        if (values?.Any(value => value is null) == true) throw new JsonException();
    }
}

/// <summary>
/// TIA Portal can delete generated objects while it compiles and reports that as plain information,
/// so a successful compile hides it. The worker reports these messages in the UI language or in English.
/// </summary>
internal static class CompileRemovals
{
    private static readonly System.Text.RegularExpressions.Regex Removal = new(
        @"\b(was|were) deleted\b|\b(wurde|wurden) gelöscht\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> Warnings(CompileCheckReport report)
        => report.Plcs
            .SelectMany(plc => plc.Messages
                .Where(message => message.Description is not null && Removal.IsMatch(message.Description))
                .Select(message => $"{plc.PlcName}: TIA Portal removed an object while compiling: {message.Description} "
                    + "plc_undo does not cover this. Closing the project without saving brings it back."))
            .ToArray();
}

internal static class StandaloneCompileOutcome
{
    internal static bool IsPassed(CompileCheckReport report)
        => report.TotalErrorCount == 0 && report.Plcs.Count > 0
            && report.OverallState is "Success" or "Warning"
            && report.Plcs.All(plc => plc.ErrorCount == 0 && plc.State is "Success" or "Warning");
}
