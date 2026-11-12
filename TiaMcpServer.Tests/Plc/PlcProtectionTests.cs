using System.Text.Json;
using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.Contracts.Worker;
using TiaMcpServer.Plc;
using TiaMcpServer.Tests.Network;
using TiaMcpServer.Tests.TestSupport;
using Xunit;
using static TiaMcpServer.Tests.Plc.PlcGuardedWriteFixture;

namespace TiaMcpServer.Tests.Plc;

/// <summary>The safety net of the Produktiv profile against the stateful plc-write FakeWorker scenarios.</summary>
[Collection(RealWorkerProcessCollection.Name)]
public sealed class PlcProtectionTests : IDisposable
{
    private const string Roundtrip = "plc-write-roundtrip";
    private const string Compile = "plc-write-compile";
    private const string Main = "PLC_2/Main";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "herzog-tia-test-" + Guid.NewGuid().ToString("N"));

    private PlcProtection Protection() => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static PlcOperationRequest Edit(string id, string block, string oldText, string newText, bool? allowErrors = null)
        => new()
        {
            OperationId = id,
            Operation = "update_block_logic",
            BlockPath = block,
            Format = "xml",
            AllowCompileErrors = allowErrors,
            Edits = new[] { new PlcContentEdit { OldText = oldText, NewText = newText } },
        };

    private static async Task<string> ContentAsync(PlcGuardedWriteFixture fixture, string block)
        => (await fixture.Client.GetBlockContentAsync(block, format: "xml")).Payload;

    private static async Task<PlcUndoResponse> UndoAsync(PlcGuardedWriteFixture fixture, int steps = 1, bool force = false)
        => UndoDocument(await PlcUndoTools.PlcUndo(fixture.Client, fixture.Runner, steps, force));

    private static PlcUndoResponse UndoDocument(ModelContextProtocol.Protocol.CallToolResult result)
        => TiaMcpServer.Json.CanonicalJson.Deserialize<PlcUndoResponse>(
            ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text);

    private static async Task SeedAsync(PlcGuardedWriteFixture fixture, string block, string content)
    {
        var hash = await fixture.ReadHashAsync(block, "xml");
        var seeded = await fixture.Client.UpdateBlockLogicAsync(block, content, format: "xml", expectedContentHash: hash);
        Assert.True(seeded.Success, seeded.Error);
    }

    [Fact]
    public async Task Edits_ApplyToFreshExport_WithoutHash_AndKeepSnapshot()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var before = await ContentAsync(fixture, Main);

        var response = await fixture.RunAsync(false, Edit("edit", Main, "of=", "von="));

        Assert.True(response.Success, response.Error?.Message);
        Assert.Equal(before.Replace("of=", "von=", StringComparison.Ordinal), await ContentAsync(fixture, Main));
        var item = Assert.Single(response.Protection!.Items);
        Assert.Equal(before, File.ReadAllText(item.SnapshotFile!));
        Assert.False(item.RollbackApplied);
        Assert.True(response.Protection.Undoable);
        Assert.Null(item.ImportDifference);
    }

    [Theory]
    [InlineData("gibt-es-nicht", "was not found")]
    [InlineData("<", "occurs")]
    public async Task Edits_ThatDoNotMatchExactlyOnce_ChangeNothing(string oldText, string message)
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        await SeedAsync(fixture, Main, "<A/><B/>");

        var response = await fixture.RunAsync(false, Edit("edit", Main, oldText, "x"));

        Assert.Equal("error", response.Phase);
        Assert.Equal(WorkerFailureCategories.ValidationError, response.Error!.Category);
        Assert.Contains(message, response.Error.Message, StringComparison.Ordinal);
        Assert.Equal("<A/><B/>", await ContentAsync(fixture, Main));
    }

    [Fact]
    public async Task Edits_WithStaleHash_AreRefused()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var stale = await fixture.ReadHashAsync(Main, "xml");
        await SeedAsync(fixture, Main, "<Neu/>");
        var edit = Edit("edit", Main, "Neu", "Neuer");
        edit.ExpectedContentHash = stale;

        var response = await fixture.RunAsync(false, edit);

        Assert.Equal(WorkerFailureCategories.StateChanged, response.Error!.Category);
        Assert.Equal("<Neu/>", await ContentAsync(fixture, Main));
    }

    [Fact]
    public async Task CompileErrors_TakeTheChangeBack_AndReportTheMessages()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Compile, McpAccessMode.Produktiv, Protection());
        var before = await ContentAsync(fixture, Main);

        var response = await fixture.RunAsync(false, Edit("edit", Main, "of=", "COMPILE_ERROR of="));

        Assert.False(response.Success);
        var failure = Assert.Single(response.Batch!.Operations).Failure!;
        Assert.Equal(WorkerFailureCategories.RolledBack, failure.Category);
        Assert.Equal(1, failure.BlockImportOutcome!.CompileReport!.TotalErrorCount);
        Assert.Equal(before, await ContentAsync(fixture, Main));
        Assert.True(Assert.Single(response.Protection!.Items).RollbackApplied);
    }

    [Fact]
    public async Task CompileErrorsThatExistedBefore_KeepTheChange()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, "plc-write-compile-preexisting", McpAccessMode.Produktiv, Protection());

        var response = await fixture.RunAsync(false, Edit("edit", Main, "of=", "von="));

        Assert.Equal("succeeded", Assert.Single(response.Batch!.Operations).Status);
        Assert.Contains("von=", await ContentAsync(fixture, Main), StringComparison.Ordinal);
        var item = Assert.Single(response.Protection!.Items);
        Assert.False(item.RollbackApplied);
        Assert.Contains("existed before", item.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileErrorsOfTheBlockAlone_ThatThePlcCompileClears_KeepTheChange_WithoutTakingItBack()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, "plc-write-compile-block-only", McpAccessMode.Produktiv, Protection());

        var response = await fixture.RunAsync(false, Edit("edit", Main, "of=", "von="));

        Assert.True(response.Success, response.Error?.Message);
        var operation = Assert.Single(response.Batch!.Operations);
        Assert.Equal("succeeded", operation.Status);
        Assert.Contains("von=", await ContentAsync(fixture, Main), StringComparison.Ordinal);
        var item = Assert.Single(response.Protection!.Items);
        Assert.False(item.RollbackApplied);
        Assert.Contains("Compiling its PLC", item.Note!, StringComparison.Ordinal);
        Assert.True(response.Protection.Undoable);

        var outcome = operation.Result!.Value.GetProperty("importOutcome");
        Assert.Equal("succeeded", outcome.GetProperty("compileStage").GetString());
        var report = outcome.GetProperty("compileReport");
        Assert.Equal(0, report.GetProperty("totalErrorCount").GetInt32());
        var message = Assert.Single(report.GetProperty("plcs")[0].GetProperty("messages").EnumerateArray());
        Assert.Equal("Warning", message.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task AllowCompileErrors_KeepsTheChange()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Compile, McpAccessMode.Produktiv, Protection());

        var response = await fixture.RunAsync(false, Edit("edit", Main, "of=", "COMPILE_ERROR of=", allowErrors: true));

        Assert.Equal("succeeded", Assert.Single(response.Batch!.Operations).Status);
        Assert.Contains("COMPILE_ERROR", await ContentAsync(fixture, Main), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBackThatDiffersFromTheSubmittedText_IsReported()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Compile, McpAccessMode.Produktiv, Protection());

        var response = await fixture.RunAsync(false, Edit("edit", Main, "of=", "LReal of="));

        Assert.True(response.Success, response.Error?.Message);
        Assert.NotNull(Assert.Single(response.Protection!.Items).ImportDifference);
    }

    [Fact]
    public async Task Undo_RestoresContent_AcrossAServerRestart()
    {
        using var audit = new TempAuditDirectory();
        string before;
        using (var first = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection()))
        {
            before = await ContentAsync(first, Main);
            Assert.True((await first.RunAsync(false, Edit("edit", Main, "of=", "von="))).Success);
            var undone = await UndoAsync(first);
            Assert.True(undone.Success, undone.Error?.Message);
            Assert.Equal("plc_undo", undone.Tool);
            Assert.Equal(before, await ContentAsync(first, Main));
            var nothing = await UndoAsync(first);
            Assert.Equal("error", nothing.Phase);
        }

        Assert.Empty(new PlcProtection(_root).ReadOpenCalls(PlcProtection.ProjectKey(Roundtrip)));
    }

    [Fact]
    public async Task Undo_List_ShowsOpenCalls_AndChangesNothing()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        Assert.True((await fixture.RunAsync(false, Edit("edit", Main, "of=", "von="))).Success);
        var changed = await ContentAsync(fixture, Main);

        var listed = UndoDocument(await PlcUndoTools.PlcUndo(fixture.Client, fixture.Runner, list: true));

        Assert.True(listed.Success, listed.Error?.Message);
        var call = Assert.Single(listed.OpenCalls!);
        Assert.False(call.EarlierSession);
        Assert.Contains(call.Operations, operation => operation.Contains("update_block_logic", StringComparison.Ordinal));
        Assert.Equal(changed, await ContentAsync(fixture, Main));
    }

    [Fact]
    public async Task Undo_NamesTheCallsItTookBack()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var written = await fixture.RunAsync(false, Edit("edit", Main, "of=", "von="));

        var undone = UndoDocument(await PlcUndoTools.PlcUndo(fixture.Client, fixture.Runner));

        Assert.True(undone.Success, undone.Error?.Message);
        Assert.Equal(written.Protection!.CallId, Assert.Single(undone.TakenBack!).CallId);
    }

    [Fact]
    public async Task Undo_StopsBeforeACallFromAnEarlierSession_UnlessAskedFor()
    {
        using var audit = new TempAuditDirectory();
        var protection = Protection();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, protection);
        var key = PlcProtection.ProjectKey(fixture.Client.BindingSnapshot.ProjectPath);
        protection.Append(key, new PlcJournalEntry("alt", protection.SessionStart.AddHours(-1), "edit", "create_tag_table", "Alt",
            Array.Empty<PlcInverseOperation>(), null));
        var own = await fixture.RunAsync(false, Edit("edit", Main, "of=", "von="));
        Assert.True(own.Success, own.Error?.Message);

        var listed = UndoDocument(await PlcUndoTools.PlcUndo(fixture.Client, fixture.Runner, list: true));
        Assert.Equal(new[] { false, true }, listed.OpenCalls!.Select(call => call.EarlierSession));

        var refused = UndoDocument(await PlcUndoTools.PlcUndo(fixture.Client, fixture.Runner, steps: 2));
        Assert.Equal("error", refused.Phase);
        Assert.Contains("earlier server session", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(2, protection.ReadOpenCalls(key).Count);

        var mine = UndoDocument(await PlcUndoTools.PlcUndo(fixture.Client, fixture.Runner));
        Assert.True(mine.Success, mine.Error?.Message);
        Assert.Equal(own.Protection!.CallId, Assert.Single(mine.TakenBack!).CallId);

        var stopped = UndoDocument(await PlcUndoTools.PlcUndo(fixture.Client, fixture.Runner));
        Assert.Contains("earlier server session", stopped.Error!.Message, StringComparison.Ordinal);

        await PlcUndoTools.PlcUndo(fixture.Client, fixture.Runner, includeEarlierSessions: true);
        Assert.Empty(protection.ReadOpenCalls(key));
    }

    [Fact]
    public void CompileRemovals_NamesWhatTiaPortalDeleted()
    {
        var report = TiaMcpServer.Json.CanonicalJson.Deserialize<TiaMcpServer.Contracts.Block.CompileCheckReport>(
            """{"scope":"plc","overallState":"Success","plcs":[{"plcName":"PLC_1","state":"Success","diagnosticNotes":[],"messages":[{"severity":"Information","path":"","description":"Organization block MC_Servo was deleted because there is no technology objects under the controller."},{"severity":"Information","path":"","description":"Block was successfully compiled."}]}]}""");

        var warning = Assert.Single(TiaMcpServer.Tools.CompileRemovals.Warnings(report));

        Assert.Contains("MC_Servo", warning, StringComparison.Ordinal);
        Assert.Contains("plc_undo", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureHint_ExplainsTheUnansweredAccessDialog_AndLeavesOthersAlone()
    {
        var hinted = TiaMcpServer.Worker.OpennessWorkerClient.WithFailureHint("TIA Portal operation failed: Security error.\r\n\r\nThe operation has timed out.");
        Assert.Contains("access dialog", hinted, StringComparison.Ordinal);

        Assert.Equal("TIA Portal operation failed: read failed", TiaMcpServer.Worker.OpennessWorkerClient.WithFailureHint("TIA Portal operation failed: read failed"));
    }

    [Fact]
    public async Task Undo_TakesBackNewestCallFirst_AndTwoSteps()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var before = await ContentAsync(fixture, Main);
        Assert.True((await fixture.RunAsync(false, Edit("a", Main, "of=", "von="))).Success);
        var middle = await ContentAsync(fixture, Main);
        Assert.True((await fixture.RunAsync(false, Edit("b", Main, "von=", "aus="))).Success);

        Assert.True((await UndoAsync(fixture)).Success);
        Assert.Equal(middle, await ContentAsync(fixture, Main));

        Assert.True((await fixture.RunAsync(false, Edit("c", Main, "von=", "aus="))).Success);
        Assert.True((await UndoAsync(fixture, steps: 2)).Success);
        Assert.Equal(before, await ContentAsync(fixture, Main));
    }

    [Fact]
    public async Task Undo_RefusesAnObjectChangedSince_UnlessForced()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var before = await ContentAsync(fixture, Main);
        Assert.True((await fixture.RunAsync(false, Edit("edit", Main, "of=", "von="))).Success);
        await SeedAsync(fixture, Main, "<VonHandGeaendert/>");

        var refused = await UndoAsync(fixture);
        Assert.False(refused.Success);
        Assert.Equal("<VonHandGeaendert/>", await ContentAsync(fixture, Main));

        var forced = await UndoAsync(fixture, force: true);
        Assert.True(forced.Success, forced.Error?.Message);
        Assert.Equal(before, await ContentAsync(fixture, Main));
    }

    [Fact]
    public async Task Undo_TakesBackTagsAndTables()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var before = await fixture.InventoryAsync();

        var created = await fixture.RunAsync(false, CreateTable("table", "Rueckbau"), CreateTag("tag", "Fresh", "Rueckbau"));
        Assert.True(created.Success, created.Error?.Message);
        Assert.Contains("Rueckbau", await fixture.InventoryAsync(), StringComparison.Ordinal);

        var undone = await UndoAsync(fixture);

        Assert.True(undone.Success, undone.Error?.Message);
        Assert.Equal(before, await fixture.InventoryAsync());
    }

    [Fact]
    public async Task Undo_RestoresADeletedBlockWithItsContent()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        const string block = "Station_1/Blocks/Motors/FB_Motor";
        var content = await ContentAsync(fixture, block);

        var deleted = await fixture.RunAsync(false, new PlcOperationRequest { OperationId = "gone", Operation = "delete_block", BlockPath = block });
        Assert.True(deleted.Success, deleted.Error?.Message);
        Assert.False((await fixture.Client.GetBlockContentAsync(block, format: "xml")).Success);

        var undone = await UndoAsync(fixture);

        Assert.True(undone.Success, undone.Error?.Message);
        Assert.Equal(content, await ContentAsync(fixture, block));
    }

    [Fact]
    public async Task SafetyBlocks_AreRefused_ByExportAndBySubmittedContent()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var hash = await fixture.ReadHashAsync(Main, "xml");

        var submitted = await fixture.RunAsync(false, new PlcOperationRequest
        {
            OperationId = "f", Operation = "update_block_logic", BlockPath = Main, Format = "xml",
            ExpectedContentHash = hash, Content = "<ProgrammingLanguage>F_LAD</ProgrammingLanguage>",
        });
        Assert.Equal("blocked", submitted.Phase);
        Assert.Contains(submitted.Guards, g => g.Id == PlcGuardDefinitions.SafetyBlock);

        await SeedAsync(fixture, Main, "<ProgrammingLanguage>F_FBD</ProgrammingLanguage>");
        var existing = await fixture.RunAsync(false, Edit("edit", Main, "F_FBD", "FBD"));
        Assert.Equal("blocked", existing.Phase);
        Assert.Contains("F_FBD", await ContentAsync(fixture, Main), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SafetyBlocks_AreRefused_WhenTheExportIsOnlyTheS7dclDocument()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        const string s7dcl = "--- FILE: Main_Safety.s7dcl ---\n{\n    S7_Safety := \"TRUE\";\n}\nFUNCTION_BLOCK \"Main_Safety\"\n";
        await SeedAsync(fixture, Main, s7dcl);

        // The text does not occur: the guard still has to be what stops the call.
        var response = await fixture.RunAsync(false, Edit("edit", Main, "GIBT_ES_NICHT", "x"));

        Assert.Equal("blocked", response.Phase);
        Assert.Contains(response.Guards, g => g.Id == PlcGuardDefinitions.SafetyBlock);
        Assert.Equal(s7dcl, await ContentAsync(fixture, Main));
    }

    [Fact]
    public async Task DeletingANonEmptyGroup_IsRefused()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());

        var response = await fixture.RunAsync(false, new PlcOperationRequest
        {
            OperationId = "gone", Operation = "delete_block_group", BlockPath = "Station_1/Blocks/Motors",
        });

        Assert.Equal("blocked", response.Phase);
        Assert.Contains(response.Guards, g => g.Id == PlcGuardDefinitions.DeleteNotEmpty);
    }

    [Fact]
    public async Task WithoutProtection_NothingChanges()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip);
        var hash = await fixture.ReadHashAsync(Main, "xml");

        var response = await fixture.RunAsync(false, UpdateBlock("update", Main, hash));

        Assert.True(response.Success);
        Assert.Null(response.Protection);
        Assert.Equal("error", (await UndoAsync(fixture)).Phase);
    }

    [Theory]
    [InlineData("save_project", true)]
    [InlineData("update_block_logic", true)]
    [InlineData("delete_block", true)]
    [InlineData("compile_check", true)]
    [InlineData("get_block_content", true)]
    [InlineData("hmi_create_tag", true)]
    [InlineData("open_project", true)]
    [InlineData("create_project", true)]
    [InlineData("close_project", true)]
    [InlineData("archive_project", true)]
    [InlineData("save_project_as", true)]
    [InlineData("add_network_device", false)]
    [InlineData("configure_network_device", false)]
    [InlineData("create_subnet", false)]
    [InlineData("delete_subnet", false)]
    [InlineData("gibt_es_nicht", false)]
    public void ProduktivPolicy_RefusesAtDispatch(string operation, bool allowed)
        => Assert.Equal(allowed, OperationPolicyCatalog.IsAllowed(McpAccessMode.Produktiv, operation));

    [Fact]
    public void ProduktivPolicy_RefusesEveryVciWrite()
    {
        foreach (var method in TiaMcpServer.Contracts.Vci.VciMethods.Write)
        {
            Assert.False(OperationPolicyCatalog.IsAllowed(McpAccessMode.Produktiv, method));
        }
    }

    [Fact]
    public void Patcher_MatchesLf_InCrLfDocument_AndKeepsCrLf()
    {
        var result = PlcContentPatcher.Apply("a\r\nb\r\nc\r\n", new[] { new PlcContentEdit { OldText = "a\nb", NewText = "x\ny" } });

        Assert.Null(result.Error);
        Assert.Equal("x\r\ny\r\nc\r\n", result.Content);
    }

    [Fact]
    public void Patcher_ReplaceAll_AndSequenceOfEdits()
    {
        var result = PlcContentPatcher.Apply("a a b", new[]
        {
            new PlcContentEdit { OldText = "a", NewText = "c", ReplaceAll = true },
            new PlcContentEdit { OldText = "b", NewText = "d" },
        });

        Assert.Equal("c c d", result.Content);
    }

    [Fact]
    public void Patcher_RefusesAnEditThatChangesNothing()
        => Assert.NotNull(PlcContentPatcher.Apply("a", new[] { new PlcContentEdit { OldText = "a", NewText = "a" } }).Error);

    [Fact]
    public void Bundle_PrimaryIsTheXmlDocument()
    {
        const string bundle = "--- FILE: FB.xml ---\r\n<Xml/>\r\n--- FILE: FB.s7dcl ---\r\nRung\r\n";

        Assert.Equal("<Xml/>\r\n", PlcContentBundle.Primary(bundle));
        Assert.Equal("<Xml/>", PlcContentBundle.Primary("<Xml/>"));
    }

    [Fact]
    public void Catalog_AcceptsEditsInsteadOfContent_AndRefusesBoth()
    {
        var edits = Edit("edit", Main, "a", "b");
        Assert.True(PlcOperationCatalog.ValidateWrite(new[] { edits }).IsValid);

        edits.Content = "<Block/>";
        var both = PlcOperationCatalog.ValidateWrite(new[] { edits });
        Assert.False(both.IsValid);

        var none = PlcOperationCatalog.ValidateWrite(new[]
        {
            new PlcOperationRequest { OperationId = "x", Operation = "update_block_logic", BlockPath = Main },
        });
        Assert.False(none.IsValid);
    }

    [Fact]
    public async Task ToFile_WritesTheDocument_AndReturnsItsPath()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var read = new PlcOperationRequest { OperationId = "read", Operation = "get_block_content", BlockPath = Main, Format = "xml", ToFile = true };

        var item = PlcPayloadContract.Project(read, await PlcWorkerInvoker.InvokeReadAsync(fixture.Client, read));

        var result = item.Result!.Value;
        Assert.Equal(string.Empty, result.GetProperty("content").GetString());
        var file = result.GetProperty("contentFile").GetString()!;
        try
        {
            Assert.Equal(await ContentAsync(fixture, Main), File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void PersonalDataFilter_NullsNames_AndLeavesTheShape()
    {
        const string json = """{"value":{"author":"Jemand","name":"Projekt","lastModifiedBy":"Jemand","metadata":{"historyEntries":[{"text":"x"}]},"details":[{"name":"HeaderAuthor","value":"Jemand"},{"name":"Number","value":"1"}]}}""";

        var scrubbed = TiaMcpServer.Tools.PersonalDataFilter.Scrub(json);

        Assert.DoesNotContain("Jemand", scrubbed, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(scrubbed);
        var value = document.RootElement.GetProperty("value");
        Assert.Equal(JsonValueKind.Null, value.GetProperty("author").ValueKind);
        Assert.Equal("Projekt", value.GetProperty("name").GetString());
        Assert.Equal("1", value.GetProperty("details")[1].GetProperty("value").GetString());
    }

    [Fact]
    public void PersonalDataFilter_RemovesTheAuthorFromAStringMap_SoTheSchemaHolds()
    {
        // browse_project_tree: details is a map of strings. A system block carries a vendor as author.
        const string json = """{"nodes":[{"name":"FB","details":{"HeaderAuthor":"Siemens","Number":"1"}}]}""";

        var scrubbed = TiaMcpServer.Tools.PersonalDataFilter.Scrub(json);

        using var document = JsonDocument.Parse(scrubbed);
        var details = document.RootElement.GetProperty("nodes")[0].GetProperty("details");
        Assert.False(details.TryGetProperty("HeaderAuthor", out _));
        Assert.Equal("1", details.GetProperty("Number").GetString());
    }

    [Fact]
    public void PersonalDataFilter_LeavesBlockTextAlone()
    {
        const string json = """{"content":"<HeaderAuthor>Jemand</HeaderAuthor>","author":null}""";

        Assert.Equal(json, TiaMcpServer.Tools.PersonalDataFilter.Scrub(json));
    }

    [Fact]
    public async Task AuditOfTheProduktivProfile_HoldsNoBlockText()
    {
        using var audit = new TempAuditDirectory();
        using var fixture = await PlcGuardedWriteFixture.CreateAsync(audit, Roundtrip, McpAccessMode.Produktiv, Protection());
        var hash = await fixture.ReadHashAsync(Main, "xml");
        var write = UpdateBlock("update", Main, hash);
        write.Content = "<Block geheim=\"text\"/>";

        Assert.True((await fixture.RunAsync(false, write)).Success);

        var lines = Directory.GetFiles(audit.Path, "writes-*.jsonl").SelectMany(File.ReadAllLines).ToArray();
        var record = JsonDocument.Parse(lines[^1]).RootElement;
        var requested = record.GetProperty("requestedOperations")[0];
        Assert.Equal(JsonValueKind.Null, requested.GetProperty("content").ValueKind);
        Assert.True(requested.GetProperty("contentCharacters").GetInt32() > 0);
    }

    [Fact]
    public void Protection_KeepsOpenCalls_AndSkipsADamagedLine()
    {
        var protection = Protection();
        var key = PlcProtection.ProjectKey("Projekt");
        var entry = new PlcJournalEntry("c1", protection.Now, "a", "create_tag", "T", Array.Empty<PlcInverseOperation>(), null);
        protection.Append(key, entry);
        File.AppendAllText(Path.Combine(_root, "journal", key + ".jsonl"), "kein json\r\n");
        protection.Append(key, entry with { CallId = "c2" });
        protection.MarkUndone(key, "c1");

        var open = protection.ReadOpenCalls(key);

        Assert.Equal("c2", Assert.Single(open)[0].CallId);
        Assert.NotEqual(PlcProtection.ProjectKey("Projekt"), PlcProtection.ProjectKey("Anderes"));
    }
}
