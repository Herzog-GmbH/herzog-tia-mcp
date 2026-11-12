using TiaMcpServer.OpennessWorker.Openness.Block;
using Xunit;

namespace TiaMcpServer.Tests.Block;

public class SourceDeclarationScannerTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Single_object_scl_export_yields_one_function_block()
    {
        var declarations = SourceDeclarationScanner.Scan(Fixture("DamperDigital.scl"));

        var declaration = Assert.Single(declarations);
        Assert.Equal(SourceObjectKind.FunctionBlock, declaration.Kind);
        Assert.Equal("DamperDigital", declaration.Name);
        Assert.Equal(1, declaration.LineNumber);
    }

    [Fact]
    public void Two_object_scl_export_yields_the_type_then_the_function_block()
    {
        var declarations = SourceDeclarationScanner.Scan(Fixture("AnalogInput.scl"));

        Assert.Equal(2, declarations.Count);
        Assert.Equal(SourceObjectKind.Type, declarations[0].Kind);
        Assert.Equal("AnalogInputSettings", declarations[0].Name);
        Assert.Equal(SourceObjectKind.FunctionBlock, declarations[1].Kind);
        Assert.Equal("AnalogInput", declarations[1].Name);
    }

    [Fact]
    public void Four_object_scl_export_yields_every_object_in_file_order()
    {
        var declarations = SourceDeclarationScanner.Scan(Fixture("DamperAnalog.scl"));

        Assert.Equal(4, declarations.Count);
        Assert.Equal(
            new[] { "AnalogInputSettings", "UDT_Settings", "HMI_Settings_DB", "DamperAnalog" },
            declarations.Select(d => d.Name).ToArray());
        Assert.Equal(
            new[]
            {
                SourceObjectKind.Type,
                SourceObjectKind.Type,
                SourceObjectKind.DataBlock,
                SourceObjectKind.FunctionBlock,
            },
            declarations.Select(d => d.Kind).ToArray());
    }

    [Fact]
    public void A_nested_anonymous_struct_is_not_mistaken_for_a_declaration()
    {
        var declarations = SourceDeclarationScanner.Scan(Fixture("nStageHeater.scl"));

        var declaration = Assert.Single(declarations);
        Assert.Equal("nStageHeater", declaration.Name);
    }

    [Fact]
    public void A_member_named_Type_is_not_a_declaration()
    {
        // DamperDigital's first VAR_INPUT member is literally named "Type".
        var declarations = SourceDeclarationScanner.Scan(Fixture("DamperDigital.scl"));

        Assert.DoesNotContain(declarations, d => d.Kind == SourceObjectKind.Type);
    }

    [Fact]
    public void Real_V21_udt_export_yields_one_type()
    {
        var declarations = SourceDeclarationScanner.Scan(Fixture("AnalogInputSettings.udt"));

        var declaration = Assert.Single(declarations);
        Assert.Equal(SourceObjectKind.Type, declaration.Kind);
        Assert.Equal("AnalogInputSettings", declaration.Name);
    }

    [Fact]
    public void Real_V21_db_export_yields_one_data_block()
    {
        var declarations = SourceDeclarationScanner.Scan(Fixture("Simulation_DB.db"));

        var declaration = Assert.Single(declarations);
        Assert.Equal(SourceObjectKind.DataBlock, declaration.Kind);
        Assert.Equal("Simulation_DB", declaration.Name);
    }

    [Fact]
    public void A_declaration_inside_a_line_comment_is_ignored()
    {
        var declarations = SourceDeclarationScanner.Scan(
            "// TYPE \"Ghost\"\r\nTYPE \"Real\"\r\nEND_TYPE\r\n");

        var declaration = Assert.Single(declarations);
        Assert.Equal("Real", declaration.Name);
        Assert.Equal(2, declaration.LineNumber);
    }

    [Fact]
    public void A_declaration_inside_a_block_comment_is_ignored()
    {
        var declarations = SourceDeclarationScanner.Scan(
            "(*\r\nTYPE \"Ghost\"\r\n*)\r\nTYPE \"Real\"\r\nEND_TYPE\r\n");

        var declaration = Assert.Single(declarations);
        Assert.Equal("Real", declaration.Name);
        Assert.Equal(4, declaration.LineNumber);
    }

    [Fact]
    public void A_declaration_inside_a_string_literal_is_ignored()
    {
        var declarations = SourceDeclarationScanner.Scan(
            "FUNCTION_BLOCK \"Real\"\r\nBEGIN\r\n#msg := '\r\nTYPE \"Ghost\"\r\n';\r\nEND_FUNCTION_BLOCK\r\n");

        var declaration = Assert.Single(declarations);
        Assert.Equal("Real", declaration.Name);
    }

    [Fact]
    public void An_END_keyword_is_not_a_declaration()
    {
        var declarations = SourceDeclarationScanner.Scan(
            "FUNCTION_BLOCK \"Real\"\r\nEND_FUNCTION_BLOCK\r\n");

        Assert.Single(declarations);
    }

    [Fact]
    public void An_unquoted_name_is_accepted()
    {
        var declarations = SourceDeclarationScanner.Scan("TYPE Foo\r\nEND_TYPE\r\n");

        Assert.Equal("Foo", Assert.Single(declarations).Name);
    }

    [Fact]
    public void A_leading_byte_order_mark_does_not_hide_the_first_declaration()
    {
        var declarations = SourceDeclarationScanner.Scan("\uFEFFTYPE \"Foo\"\r\nEND_TYPE\r\n");

        Assert.Equal("Foo", Assert.Single(declarations).Name);
    }

    [Fact]
    public void Functions_and_organization_blocks_are_recognized()
    {
        var declarations = SourceDeclarationScanner.Scan(
            "FUNCTION \"Calc\" : Real\r\nEND_FUNCTION\r\n"
            + "ORGANIZATION_BLOCK \"Main\"\r\nEND_ORGANIZATION_BLOCK\r\n");

        Assert.Equal(SourceObjectKind.Function, declarations[0].Kind);
        Assert.Equal("Calc", declarations[0].Name);
        Assert.Equal(SourceObjectKind.OrganizationBlock, declarations[1].Kind);
        Assert.Equal("Main", declarations[1].Name);
    }

    [Fact]
    public void Empty_content_yields_no_declarations()
    {
        Assert.Empty(SourceDeclarationScanner.Scan(string.Empty));
    }

    [Fact]
    public void Describe_lists_every_declaration_with_keyword_name_and_line()
    {
        var text = SourceDeclarationScanner.Describe(
            SourceDeclarationScanner.Scan(Fixture("AnalogInput.scl")));

        Assert.Contains("TYPE 'AnalogInputSettings' (line 1)", text);
        Assert.Contains("FUNCTION_BLOCK 'AnalogInput' (line 21)", text);
    }

    [Fact]
    public void A_global_db_with_its_own_var_section_has_no_derived_type()
    {
        Assert.Null(SourceDeclarationScanner.ReadDataBlockType(Fixture("Simulation_DB.db")));
    }

    [Fact]
    public void A_db_derived_from_a_plc_data_type_names_that_type()
    {
        // The real V21 export: attribute block over three lines, VERSION, then the quoted type name.
        Assert.Equal("UDT_Settings", SourceDeclarationScanner.ReadDataBlockType(Fixture("DamperAnalog.scl")));
    }

    [Fact]
    public void An_instance_db_source_names_its_fb()
    {
        const string source = "DATA_BLOCK \"Motor_DB\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nNON_RETAIN\r\n"
            + "\"Motor\"\r\nBEGIN\r\nEND_DATA_BLOCK\r\n";

        Assert.Equal("Motor", SourceDeclarationScanner.ReadDataBlockType(source));
    }

    [Fact]
    public void Header_lines_comments_and_flags_are_skipped_and_a_bare_type_name_is_read()
    {
        const string source = "DATA_BLOCK Timer_DB\nTITLE = 'VAR in a title'\n// \"Ghost\" in a comment\n"
            + "(* VAR *)\nAUTHOR : Someone\nFAMILY : Tools\nNAME : TimerDb\nVERSION : 1.0\nKNOW_HOW_PROTECT\nRETAIN\nTON_TIME\nBEGIN\nEND_DATA_BLOCK\n";

        Assert.Equal("TON_TIME", SourceDeclarationScanner.ReadDataBlockType(source));
    }

    [Fact]
    public void An_array_db_is_read_as_ARRAY()
    {
        const string source = "DATA_BLOCK \"Recipes\"\r\nVERSION : 0.1\r\nArray[0..9] of \"Recipe\"\r\nBEGIN\r\nEND_DATA_BLOCK\r\n";

        Assert.Equal("ARRAY", SourceDeclarationScanner.ReadDataBlockType(source));
    }

    [Theory]
    [InlineData("DATA_BLOCK \"D\"\nVERSION : 0.1\nSTRUCT\n a : Int;\nEND_STRUCT;\nBEGIN\nEND_DATA_BLOCK\n")]
    [InlineData("DATA_BLOCK \"D\"\nVERSION : 0.1\nNON_RETAIN\n   VAR RETAIN\n a : Int;\n   END_VAR\nBEGIN\nEND_DATA_BLOCK\n")]
    [InlineData("DATA_BLOCK \"D\"\nBEGIN\nEND_DATA_BLOCK\n")]
    [InlineData("FUNCTION_BLOCK \"F\"\nVERSION : 0.1\n\"Ghost\"\nBEGIN\nEND_FUNCTION_BLOCK\n")]
    [InlineData("")]
    public void Sources_without_a_derived_data_block_type_yield_null(string source)
    {
        Assert.Null(SourceDeclarationScanner.ReadDataBlockType(source));
    }
}