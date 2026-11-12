using System.Text.Json.Serialization;
using TiaMcpServer.Contracts.Block;

namespace TiaMcpServer.Plc;

/// <summary>
/// Result of a get_block_content / get_type_content read. ContentHash is null for withDependencies
/// reads. With <see cref="ContentFile"/> the document is in that file and Content is empty.
/// </summary>
public sealed record PlcContentResult(
    string Format,
    string Content,
    string? ContentHash,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Set when the document was written to a text file instead of the response.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentFile { get; init; }

    /// <summary>Length of the document in <see cref="ContentFile"/>; absent for inline content.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CharacterCount { get; init; }

    /// <summary>Lines of the document in <see cref="ContentFile"/>; absent for inline content.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LineCount { get; init; }
}

/// <summary>Result of a completed update_block_logic: the normalized write format and the worker's typed import evidence.</summary>
public sealed record PlcBlockImportResult(string Format, BlockImportOutcomeInfo ImportOutcome);
