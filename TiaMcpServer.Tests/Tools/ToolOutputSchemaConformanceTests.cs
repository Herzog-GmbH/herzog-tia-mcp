using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Contracts.Safety;
using TiaMcpServer.Plc;
using TiaMcpServer.Tests.TestSupport;
using Xunit;

namespace TiaMcpServer.Tests.Tools;

/// <summary>
/// A response that does not match the advertised outputSchema reaches the model as an error, even
/// though the tool ran. These tests check the responses of the Produktiv profile, whose protection
/// report only appears on a real write, and every type the schemas are generated from.
/// </summary>
[Collection("Mcp protocol serial")]
public sealed class ToolOutputSchemaConformanceTests : IDisposable
{
    private const string Roundtrip = "plc-write-roundtrip";
    private const string Compile = "plc-write-compile";
    private const string Main = "PLC_2/Main";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "herzog-tia-schema-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A positional record member is required in the schema unless it has a default value, so it
    /// must not be left off the wire when null. Covers every value, also those no fixture produces.
    /// </summary>
    [Fact]
    public void NoRequiredSchemaMemberIsOmittedOnTheWire()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var roots = typeof(PlcUndoTools).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.OutputSchemaType)
            .OfType<Type>();
        var pending = new Queue<Type>(roots);
        var seen = new HashSet<Type>();
        var offenders = new List<string>();

        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!seen.Add(type))
            {
                continue;
            }

            var info = options.GetTypeInfo(type);
            if (info.ElementType is { } element)
            {
                pending.Enqueue(element);
            }

            if (info.Kind != JsonTypeInfoKind.Object)
            {
                continue;
            }

            foreach (var property in info.Properties)
            {
                pending.Enqueue(property.PropertyType);
                var conditional = property.AttributeProvider?
                    .GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
                    .OfType<JsonIgnoreAttribute>()
                    .Any(attribute => attribute.Condition == JsonIgnoreCondition.WhenWritingNull) ?? false;
                if (conditional && property.AssociatedParameter is { HasDefaultValue: false })
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.Contains(typeof(PlcItemProtection), seen);
        Assert.True(offenders.Count == 0,
            "Required in the output schema but left off the wire when null: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task PlcWrite_Applied_MatchesTheSchema()
    {
        await using var session = await StartAsync(Roundtrip);

        var document = await session.CallAsync("plc_write", Operations(CreateTable("table")));

        Assert.True(document.GetProperty("success").GetBoolean(), document.GetRawText());
        var item = Assert.Single(document.GetProperty("protection").GetProperty("items").EnumerateArray());
        foreach (var name in new[] { "snapshotFile", "note", "importDifference", "notUndoableReason" })
        {
            Assert.True(item.TryGetProperty(name, out _), "protection.items[0] lacks " + name);
        }
    }

    [Fact]
    public async Task PlcWrite_ContentEdit_MatchesTheSchema()
    {
        await using var session = await StartAsync(Roundtrip);

        var document = await session.CallAsync("plc_write", Operations(Edit("edit", "of=", "von=")));

        Assert.True(document.GetProperty("success").GetBoolean(), document.GetRawText());
        var item = Assert.Single(document.GetProperty("protection").GetProperty("items").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("snapshotFile").GetString()));
    }

    [Fact]
    public async Task PlcWrite_RolledBack_MatchesTheSchema_AndCarriesTheCompilerMessages()
    {
        await using var session = await StartAsync(Compile);

        var document = await session.CallAsync("plc_write", Operations(Edit("edit", "of=", "COMPILE_ERROR of=")));

        Assert.False(document.GetProperty("success").GetBoolean());
        var failure = Assert.Single(document.GetProperty("batch").GetProperty("operations").EnumerateArray()).GetProperty("failure");
        Assert.Equal("rolled_back", failure.GetProperty("category").GetString());
        Assert.Equal(1, failure.GetProperty("blockImportOutcome").GetProperty("compileReport").GetProperty("totalErrorCount").GetInt32());
        var item = Assert.Single(document.GetProperty("protection").GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("rollbackApplied").GetBoolean());
    }

    [Fact]
    public async Task PlcWrite_DryRun_MatchesTheSchema()
    {
        await using var session = await StartAsync(Roundtrip);

        var document = await session.CallAsync("plc_write", Operations(CreateTable("table")), dryRun: true);

        Assert.Equal("preview", document.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task PlcUndo_AfterAWrite_MatchesTheSchema()
    {
        await using var session = await StartAsync(Roundtrip);
        await session.CallAsync("plc_write", Operations(Edit("edit", "of=", "von=")));

        var document = await session.CallAsync("plc_undo", new Dictionary<string, object?>());

        Assert.True(document.GetProperty("success").GetBoolean(), document.GetRawText());
    }

    [Fact]
    public async Task PlcUndo_WithNothingToTakeBack_MatchesTheSchema()
    {
        await using var session = await StartAsync(Roundtrip);

        var document = await session.CallAsync("plc_undo", new Dictionary<string, object?>(), expectError: true);

        Assert.Equal("error", document.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task PlcUndo_List_MatchesTheSchema()
    {
        await using var session = await StartAsync(Roundtrip);
        await session.CallAsync("plc_write", Operations(Edit("edit", "of=", "von=")));

        var document = await session.CallAsync("plc_undo", new Dictionary<string, object?> { ["list"] = true });

        Assert.Equal("preview", document.GetProperty("phase").GetString());
        Assert.Single(document.GetProperty("openCalls").EnumerateArray());
    }

    [Fact]
    public async Task PlcUndo_NamesTheCallsItTookBack_AndMatchesTheSchema()
    {
        await using var session = await StartAsync(Roundtrip);
        await session.CallAsync("plc_write", Operations(Edit("edit", "of=", "von=")));

        var document = await session.CallAsync("plc_undo", new Dictionary<string, object?>());

        Assert.Single(document.GetProperty("takenBack").EnumerateArray());
    }

    /// <summary>A system block carries a vendor as author. The filter must not leave a null in the map of strings.</summary>
    [Fact]
    public async Task ProjectTree_AfterThePersonalDataFilter_MatchesTheSchema()
    {
        await using var session = await StartAsync("project-tree-v3-small");
        var document = await session.CallAsync("browse_project_tree", new Dictionary<string, object?> { ["pageSize"] = 4 });
        Assert.Contains("Fake Vendor", document.GetRawText(), StringComparison.Ordinal);

        using var scrubbed = JsonDocument.Parse(TiaMcpServer.Tools.PersonalDataFilter.Scrub(document.GetRawText()));

        Assert.DoesNotContain("Fake Vendor", scrubbed.RootElement.GetRawText(), StringComparison.Ordinal);
        session.AssertMatches("browse_project_tree", scrubbed.RootElement);
    }

    [Fact]
    public async Task ReadTools_InProduktiv_DoNotAdvertiseWriteMembers()
    {
        await using var session = await StartAsync(Roundtrip);

        var plc = await session.ItemSchemaAsync("plc_read");
        Assert.True(plc.TryGetProperty("blockPath", out _));
        Assert.False(plc.TryGetProperty("edits", out _));
        Assert.False(plc.TryGetProperty("content", out _));
        Assert.DoesNotContain("plc_write", plc.GetProperty("operation").GetProperty("description").GetString(), StringComparison.Ordinal);

        var network = await session.ItemSchemaAsync("network_read");
        Assert.True(network.TryGetProperty("deviceName", out _));
        Assert.False(network.TryGetProperty("changes", out _));
        Assert.DoesNotContain("add_network_device", network.GetProperty("operation").GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkRead_InProduktiv_NamesNoWriteOperationAnywhereInItsSchema()
    {
        await using var session = await StartAsync(Roundtrip);

        var text = (await session.ItemSchemaAsync("network_read")).GetRawText();
        foreach (var name in new[] { "add_network_device", "configure_network_device", "create_subnet", "update_subnet", "delete_subnet", "network_write" })
        {
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        }

        Assert.Contains("rejects the whole call", await session.DescriptionAsync("network_read"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkRead_InProduktiv_RefusesAWriteOperationWithoutPointingAtAWriteTool()
    {
        await using var session = await StartAsync(Roundtrip);

        var document = await session.CallAsync("network_read", Operations(
            new { operationId = "h", operation = "read_hardware_config" },
            new { operationId = "w", operation = "add_network_device", typeIdentifier = "x", deviceName = "y" }), expectError: true);

        var message = document.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("a person does that change in TIA Portal", message, StringComparison.Ordinal);
        Assert.Contains("Nothing in this call ran", message, StringComparison.Ordinal);
        Assert.DoesNotContain("network read request", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkRead_InOtherModes_KeepsItsSchemaAndDescription()
    {
        var audit = new TempAuditDirectory();
        Directory.CreateDirectory(audit.Path);
        await using var harness = await McpProtocolTestHarness.StartProductionSurfaceAsync(McpAccessMode.ReadWrite, audit.Path);
        var listed = Assert.Single(await harness.Client.ListToolsAsync(), candidate => candidate.Name == "network_read");

        Assert.DoesNotContain("rejects the whole call", listed.ProtocolTool.Description!, StringComparison.Ordinal);
        var schema = listed.ProtocolTool.InputSchema.GetRawText();
        Assert.Contains("Writes (network_write): add_network_device", schema, StringComparison.Ordinal);
        Assert.Contains("Name for the new network device", schema, StringComparison.Ordinal);
        audit.Dispose();
    }

    [Fact]
    public async Task BrowseProjectTree_SaysWhatIsNotInTheTree()
    {
        await using var session = await StartAsync(Roundtrip);

        var description = await session.DescriptionAsync("browse_project_tree");

        foreach (var name in new[] { "plc_beobachtung", "plc_technologie", "network_read", "hmi_liste", "hmi_unified_liste" })
        {
            Assert.Contains(name, description, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlcUndo_BeforeBinding_RefusesInsteadOfAnsweringEmpty(bool list)
    {
        var audit = new TempAuditDirectory();
        Directory.CreateDirectory(audit.Path);
        await using var harness = await McpProtocolTestHarness.StartProductionSurfaceAsync(
            McpAccessMode.Produktiv, audit.Path, protection: new PlcProtection(_root));

        var result = await harness.Client.CallToolAsync("plc_undo", new Dictionary<string, object?> { ["list"] = list });

        Assert.True(result.IsError);
        var document = result.StructuredContent!.Value;
        Assert.Equal("binding_conflict", document.GetProperty("error").GetProperty("category").GetString());
        Assert.False(document.GetProperty("success").GetBoolean());
        audit.Dispose();
    }

    [Fact]
    public void TheCheck_FindsAMissingRequiredMember()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","required":["items"],"properties":{"items":{"type":"array","items":
            {"type":"object","required":["note","count"],"properties":{"note":{"type":["string","null"]},"count":{"type":"integer"}}}}}}
            """);
        using var complete = JsonDocument.Parse("""{"items":[{"note":null,"count":1}]}""");
        using var omitted = JsonDocument.Parse("""{"items":[{"count":1}]}""");
        using var wrongType = JsonDocument.Parse("""{"items":[{"note":1,"count":1.5}]}""");

        Assert.Empty(JsonSchemaCheck.Violations(schema.RootElement, complete.RootElement));
        Assert.Contains("$.items[0]: must have required property 'note'", JsonSchemaCheck.Violations(schema.RootElement, omitted.RootElement));
        Assert.Equal(2, JsonSchemaCheck.Violations(schema.RootElement, wrongType.RootElement).Count);
    }

    [Fact]
    public void TheCheck_RefusesAKeywordItDoesNotKnow()
    {
        using var schema = JsonDocument.Parse("""{"patternProperties":{"a":{"type":"string"}}}""");
        using var document = JsonDocument.Parse("""{"a":1}""");

        Assert.Single(JsonSchemaCheck.Violations(schema.RootElement, document.RootElement));
    }

    private async Task<Session> StartAsync(string scenario)
    {
        var audit = new TempAuditDirectory();
        Directory.CreateDirectory(audit.Path);
        var uiOpen = new FakeWorkerUiOpenProject(scenario);
        var harness = await McpProtocolTestHarness.StartProductionSurfaceAsync(
            McpAccessMode.Produktiv, audit.Path, scenario, protection: new PlcProtection(_root));
        var schemas = (await harness.Client.ListToolsAsync())
            .Where(tool => tool.ProtocolTool.OutputSchema is not null)
            .ToDictionary(tool => tool.Name, tool => tool.ProtocolTool.OutputSchema!.Value, StringComparer.Ordinal);
        return new Session(audit, uiOpen, harness, schemas);
    }

    private static Dictionary<string, object?> Operations(params object[] items)
        => new() { ["operations"] = items };

    private static object CreateTable(string id)
        => new { operationId = id, operation = "create_tag_table", plcName = "PLC_2", tableName = "Valves" };

    private static object Edit(string id, string oldText, string newText)
        => new
        {
            operationId = id,
            operation = "update_block_logic",
            blockPath = Main,
            format = "xml",
            edits = new[] { new { oldText, newText } },
        };

    private sealed class Session(
        TempAuditDirectory audit,
        FakeWorkerUiOpenProject uiOpen,
        McpProtocolTestHarness harness,
        IReadOnlyDictionary<string, JsonElement> schemas) : IAsyncDisposable
    {
        public async Task<JsonElement> CallAsync(
            string tool, Dictionary<string, object?> arguments, bool dryRun = false, bool expectError = false)
        {
            if (dryRun)
            {
                arguments["dryRun"] = true;
            }

            var result = await harness.Client.CallToolAsync(tool, arguments);
            Assert.True(result.StructuredContent is not null, tool + " returned no structured content.");
            var document = result.StructuredContent!.Value;
            Assert.Equal(expectError, result.IsError == true);
            AssertMatches(tool, document);
            return document;
        }

        public void AssertMatches(string tool, JsonElement document)
        {
            var violations = JsonSchemaCheck.Violations(schemas[tool], document);
            Assert.True(violations.Count == 0,
                string.Join(Environment.NewLine, violations) + Environment.NewLine + document.GetRawText());
        }

        public async Task<JsonElement> ItemSchemaAsync(string tool)
        {
            var listed = Assert.Single(await harness.Client.ListToolsAsync(), candidate => candidate.Name == tool);
            return listed.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("operations")
                .GetProperty("items").GetProperty("properties").Clone();
        }

        public async Task<string> DescriptionAsync(string tool)
            => Assert.Single(await harness.Client.ListToolsAsync(), candidate => candidate.Name == tool).ProtocolTool.Description!;

        public async ValueTask DisposeAsync()
        {
            await harness.DisposeAsync();
            uiOpen.Dispose();
            audit.Dispose();
        }
    }
}
