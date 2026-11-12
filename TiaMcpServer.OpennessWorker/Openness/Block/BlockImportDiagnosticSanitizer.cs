using TiaMcpServer.Contracts.Block;
using TiaMcpServer.Contracts.Plc;
using TiaMcpServer.OpennessWorker.Worker;

namespace TiaMcpServer.OpennessWorker.Openness.Block;

internal enum BlockImportDiagnosticContext
{
    Import,
    Generation,
    SourceNodeCreation,
    SourceNodeDeletion,
    StagingCleanup,
    Verification,
    DataBlockKindChange
}

internal static class BlockImportDiagnosticSanitizer
{
    private const string GenericWarning = "Block import reported additional sanitized diagnostic information.";

    public static string Failure(BlockImportDiagnosticContext context, Exception? exception = null)
    {
        if (exception is WorkerOperationException workerFailure
            && IsClosedFailureSummary(workerFailure.Message))
            return workerFailure.Message;

        return context switch
        {
            BlockImportDiagnosticContext.Import => "Block import did not complete.",
            BlockImportDiagnosticContext.Generation => "Block source generation did not complete.",
            BlockImportDiagnosticContext.SourceNodeCreation => "Temporary source creation did not complete.",
            BlockImportDiagnosticContext.SourceNodeDeletion => "Temporary source cleanup could not be confirmed.",
            BlockImportDiagnosticContext.StagingCleanup => "Block import staging cleanup could not be confirmed.",
            BlockImportDiagnosticContext.DataBlockKindChange =>
                "The source would turn the global data block into an instance or array data block; update_block_logic "
                + "never changes a block's kind. Nothing was imported. Create instance data blocks with plc_struktur "
                + "(instanz_db_anlegen) in tia-hmi.",
            _ => "Block update verification did not complete."
        };
    }

    public static string GeneratedCountWarning(int generatedCount) =>
        "TIA Portal reported " + generatedCount + " generated block object"
        + (generatedCount == 1 ? "." : "s.");

    public static string SourceNodeCleanupWarning() =>
        "Temporary source cleanup could not be confirmed; a project node may remain.";

    public static string StagingCleanupWarning() =>
        "Block import staging cleanup could not be confirmed.";

    private const int MaxCompileWarningTexts = 20;
    private const int MaxCompileWarningChars = 512;

    /// <summary>
    /// The compiler's warning texts for the warnings channel of the Produktiv profile. The outcome projection keeps only
    /// counts, and a later compile_check reports the block as up to date, so this answer is the only place to read them.
    /// A flattened report lists the owners as rows without description (PLC, group, block), then the
    /// messages, then a summary row without path; the summary and the owners are not warnings of their own.
    /// </summary>
    public static IReadOnlyList<string> CompileWarningTexts(CompileCheckReport? report)
    {
        if (report is null || report.TotalWarningCount <= 0)
            return Array.Empty<string>();

        var texts = new List<string>();
        var found = 0;
        foreach (var plc in report.Plcs ?? new List<PlcCompileInfo>())
        {
            var owner = string.Empty;
            foreach (var message in plc?.Messages ?? new List<CompileMessageInfo>())
            {
                if (message is null) continue;
                if (string.IsNullOrWhiteSpace(message.Description))
                {
                    owner = message.Path ?? string.Empty;
                    continue;
                }

                if (!string.Equals(message.Severity, "Warning", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(message.Path))
                    continue;

                found++;
                if (texts.Count < MaxCompileWarningTexts)
                    texts.Add(CompileWarningText(owner, message.Path!, message.Description!));
            }
        }

        var notListed = Math.Max(report.TotalWarningCount, found) - texts.Count;
        if (notListed > 0)
            texts.Add($"{notListed} more compiler warning(s) are not listed; TIA Portal shows them in the compiler output.");
        return texts.AsReadOnly();
    }

    private static string CompileWarningText(string owner, string path, string description)
    {
        var text = "Compiler warning"
            + (string.IsNullOrWhiteSpace(owner) ? string.Empty : " in " + owner)
            + " at " + path + ": " + description;
        var chars = text.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (char.IsControl(chars[index]))
                chars[index] = ' ';
        }

        var plain = new string(chars).Trim();
        return plain.Length > MaxCompileWarningChars ? plain.Substring(0, MaxCompileWarningChars) : plain;
    }

    public static IReadOnlyList<string> SanitizeWarnings(IReadOnlyList<string>? warnings)
    {
        if (warnings is null || warnings.Count == 0)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var warning in warnings)
        {
            var sanitized = IsControlledWarning(warning) ? warning : GenericWarning;
            if (sanitized.Length > 256)
                sanitized = sanitized.Substring(0, 256);
            if (!result.Contains(sanitized))
                result.Add(sanitized);
        }
        return result.AsReadOnly();
    }

    private static bool IsControlledWarning(string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return false;
        return warning == SourceNodeCleanupWarning()
            || warning == StagingCleanupWarning()
            || warning == "Project state may have changed; inspect the project before retrying."
            || (warning!.StartsWith("TIA Portal reported ", StringComparison.Ordinal)
                && (warning.EndsWith(" generated block object.", StringComparison.Ordinal)
                    || warning.EndsWith(" generated block objects.", StringComparison.Ordinal)));
    }

    private static bool IsClosedFailureSummary(string message)
    {
        foreach (BlockImportDiagnosticContext context in Enum.GetValues(typeof(BlockImportDiagnosticContext)))
        {
            if (string.Equals(message, Failure(context), StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
