using TiaMcpServer.Contracts.Worker;
using TiaMcpServer.OpennessWorker.Openness.Block;
using TiaMcpServer.OpennessWorker.Worker;
using Xunit;

namespace TiaMcpServer.Tests.Block;

public class BlockImportDiagnosticSanitizerTests
{
    public static IEnumerable<object[]> FailureContexts()
    {
        foreach (var context in Enum.GetValues<BlockImportDiagnosticContext>())
            yield return new object[] { (int)context };
    }

    [Theory]
    [MemberData(nameof(FailureContexts))]
    public void FailureSummaryNeverIncludesExceptionDetails(int contextValue)
    {
        var context = (BlockImportDiagnosticContext)contextValue;
        var failure = new InvalidOperationException(
            "C:\\Users\\operator\\secret.ap21 \\\\server\\share\\source.db "
            + "tia-mcp-import-temp InternalNode SECRET_CONTENT");

        var summary = BlockImportDiagnosticSanitizer.Failure(context, failure);

        Assert.InRange(summary.Length, 1, 256);
        Assert.DoesNotContain("InvalidOperationException", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\server", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("tia-mcp", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InternalNode", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_CONTENT", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeWarningsKeepsOnlyClosedKnownFactsAndBoundsThem()
    {
        var warnings = BlockImportDiagnosticSanitizer.SanitizeWarnings(new[]
        {
            "C:\\private\\Fixture.ap21 InternalNode SECRET_CONTENT",
            BlockImportDiagnosticSanitizer.GeneratedCountWarning(2),
            BlockImportDiagnosticSanitizer.SourceNodeCleanupWarning(),
            BlockImportDiagnosticSanitizer.StagingCleanupWarning()
        });

        Assert.Contains("TIA Portal reported 2 generated block objects.", warnings);
        Assert.Contains(BlockImportDiagnosticSanitizer.SourceNodeCleanupWarning(), warnings);
        Assert.Contains(BlockImportDiagnosticSanitizer.StagingCleanupWarning(), warnings);
        Assert.DoesNotContain(warnings, warning => warning.Contains("Fixture", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, warning => warning.Contains("InternalNode", StringComparison.Ordinal));
        Assert.All(warnings, warning => Assert.InRange(warning.Length, 1, 256));
    }

    /// <summary>The rows of a real compile report: owners without description, the warning, a note, the summary.</summary>
    private static TiaMcpServer.Contracts.Block.CompileCheckReport WarningReport() => new()
    {
        Scope = "plc",
        OverallState = "Warning",
        TotalWarningCount = 1,
        Plcs =
        {
            new TiaMcpServer.Contracts.Plc.PlcCompileInfo
            {
                PlcName = "PLC_1",
                State = "Warning",
                WarningCount = 1,
                Messages =
                {
                    new() { Description = "", Path = "PLC_1", Severity = "Warning" },
                    new() { Description = "", Path = "Program blocks", Severity = "Warning" },
                    new() { Description = "", Path = "Motor (FB13)", Severity = "Warning" },
                    new() { Description = "The parameter '#Lauf' might not be initialized.", Path = "1", Severity = "Warning" },
                    new() { Description = "Block was successfully compiled.", Path = "", Severity = "Information" },
                    new() { Description = "Compiling finished (errors: 0; warnings: 1)", Path = "", Severity = "Warning" },
                }
            }
        }
    };

    private static BlockImportResult ImportWith(TiaMcpServer.Contracts.Block.CompileCheckReport report, bool includeCompileWarnings)
        => BlockImportCoordinator.Execute(
            "Main.xml",
            "<Main />",
            (_, _, boundary) =>
            {
                boundary.BeforeSiemensCall();
                boundary.AfterSiemensCallReturned();
                boundary.RecordReturnedResult(BlockImportReturnedState.Success);
            },
            _ => BlockPostconditionEvidence.Import(BlockCompileObservation.FromReport(report), "succeeded", true),
            includeCompileWarnings: includeCompileWarnings);

    [Fact]
    public void CompileWarningsReachTheWarningsOnlyWhenAsked()
    {
        var hidden = ImportWith(WarningReport(), includeCompileWarnings: false);
        Assert.DoesNotContain(hidden.Warnings, warning => warning.Contains("Lauf", StringComparison.Ordinal));

        var shown = ImportWith(WarningReport(), includeCompileWarnings: true);
        var text = Assert.Single(shown.Warnings);
        Assert.Equal("Compiler warning in Motor (FB13) at 1: The parameter '#Lauf' might not be initialized.", text);

        // The outcome keeps its fixed texts: the report stays free of compiler wording either way.
        Assert.All(shown.Outcome.CompileReport!.Plcs.SelectMany(plc => plc.Messages),
            message => Assert.Equal("Compiler diagnostic omitted.", message.Description));
    }

    [Fact]
    public void CompileWarningTextsAreBoundedAndSayWhatIsMissing()
    {
        var report = WarningReport();
        report.TotalWarningCount = 30;
        var plc = report.Plcs[0];
        plc.Messages.Clear();
        for (var index = 0; index < 25; index++)
        {
            plc.Messages.Add(new() { Description = "Line\nbreak " + new string('x', 900), Path = index.ToString(), Severity = "Warning" });
        }

        var texts = BlockImportDiagnosticSanitizer.CompileWarningTexts(report);

        Assert.Equal(21, texts.Count);
        Assert.All(texts, text => Assert.InRange(text.Length, 1, 512));
        Assert.All(texts, text => Assert.DoesNotContain('\n', text));
        Assert.StartsWith("10 more compiler warning(s)", texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void CompileWarningTextsAreEmptyWithoutWarnings()
    {
        Assert.Empty(BlockImportDiagnosticSanitizer.CompileWarningTexts(null));
        var report = WarningReport();
        report.TotalWarningCount = 0;
        Assert.Empty(BlockImportDiagnosticSanitizer.CompileWarningTexts(report));
    }

    [Fact]
    public void OnlyTheProduktivWorkerAsksForCompileWarnings()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TiaMcpServer.slnx")))
            root = root.Parent;
        var program = File.ReadAllText(Path.Combine(root!.FullName, "TiaMcpServer.OpennessWorker", "Program.cs"));

        Assert.Contains("includeCompileWarnings: _accessMode == McpAccessMode.Produktiv", program, StringComparison.Ordinal);
    }

    [Fact]
    public void CoordinatorImportAndStagingCleanupFailuresAreSanitized()
    {
        var exception = Assert.Throws<WorkerOperationException>(() => BlockImportCoordinator.Execute(
            "Main.xml",
            "<Main />",
            (_, _, boundary) =>
            {
                boundary.BeforeSiemensCall();
                throw new IOException("C:\\secret\\Fixture.ap21 SECRET_CONTENT");
            },
            _ => BlockPostconditionEvidence.Import(
                BlockCompileObservation.Unavailable(null),
                "unavailable",
                null),
            cleanupDirectory: _ => throw new IOException("\\\\server\\secret\\temp")));

        Assert.Equal(WorkerFailureCategories.WorkerOperationFailed, exception.FailureCategory);
        Assert.Equal("Block import did not complete.", exception.Message);
        Assert.Contains(BlockImportDiagnosticSanitizer.StagingCleanupWarning(), exception.Warnings);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(exception.Warnings, warning =>
            warning.Contains("server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerationThrowRetainsSourceCleanupStateWithoutFabricatingGeneratedCount()
    {
        var exception = Assert.Throws<WorkerOperationException>(() => BlockImportCoordinator.ExecuteSource(
            (boundary, source) =>
            {
                source.BeforeCreateFromFile();
                source.AfterCreateFromFileReturned();
                boundary.BeforeSiemensCall();
                source.AfterDeleteAttempt(removed: true);
                throw new InvalidOperationException("C:\\private\\generated.db");
            },
            _ => BlockPostconditionEvidence.Import(
                BlockCompileObservation.Unavailable(null),
                "succeeded",
                true)));

        Assert.Equal("unknown", exception.BlockImportOutcome!.ImportStage);
        Assert.Equal("removed", exception.BlockImportOutcome.TemporarySourceState);
        Assert.DoesNotContain(exception.Warnings, warning =>
            warning.Contains("0 generated", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Block source generation did not complete.", exception.Message);
    }

    [Fact]
    public void TheDataBlockKindRefusalReachesTheCallerThroughPreflightAndDecorator()
    {
        var refusal = BlockImportDiagnosticSanitizer.Failure(BlockImportDiagnosticContext.DataBlockKindChange);

        var exception = Assert.Throws<WorkerOperationException>(() =>
            BlockImportCoordinator.ExecuteWithPreTargetOutcome<BlockImportResult>(
                () => throw new WorkerOperationException(WorkerFailureCategories.ValidationError, refusal),
                sourceApplicable: true));
        var response = BlockUpdateOutcomeDecorator.Decorate(exception, "source", importerEntered: false);

        Assert.Equal(WorkerFailureCategories.ValidationError, exception.FailureCategory);
        Assert.Equal(refusal, exception.Message);
        Assert.Equal(refusal, response.Error);
        Assert.Equal(WorkerFailureCategories.ValidationError, response.FailureCategory);
        Assert.Equal("not_started", response.BlockImportOutcome!.ImportStage);
        Assert.False(response.BlockImportOutcome.TargetMutationCommitted);
        Assert.Contains("plc_struktur", refusal, StringComparison.Ordinal);
        Assert.Contains("instanz_db_anlegen", refusal, StringComparison.Ordinal);
    }
}
