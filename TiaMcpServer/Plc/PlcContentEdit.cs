using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Plc;

/// <summary>One targeted text replacement of a content write; the server applies it to a fresh export.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PlcContentEdit
{
    [Description("Exact text to replace, copied from the read content. Must occur exactly once unless replaceAll is true; include enough surrounding text to make it unique.")]
    public string OldText { get; set; } = string.Empty;

    [Description("Replacement text. Empty removes oldText.")]
    public string NewText { get; set; } = string.Empty;

    [Description("True replaces every occurrence instead of requiring exactly one.")]
    public bool? ReplaceAll { get; set; }
}

/// <summary>Applies <see cref="PlcContentEdit"/> items to a document. Pure; messages never echo content.</summary>
public static class PlcContentPatcher
{
    public sealed record Result(string? Content, string? Error);

    public static Result Apply(string current, IReadOnlyList<PlcContentEdit> edits)
    {
        var text = current;
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            if (edit is null || string.IsNullOrEmpty(edit.OldText))
            {
                return new(null, $"edits[{index}]: oldText is required.");
            }

            var (oldText, newText) = MatchLineEndings(text, edit.OldText, edit.NewText ?? string.Empty);
            var count = CountOccurrences(text, oldText);
            if (count == 0)
            {
                return new(null, $"edits[{index}]: oldText was not found in the current content. Read the object again; in an xml read only the .xml document can be edited.");
            }

            if (count > 1 && edit.ReplaceAll != true)
            {
                return new(null, $"edits[{index}]: oldText occurs {count} times. Add surrounding text to make it unique, or set replaceAll.");
            }

            text = text.Replace(oldText, newText, StringComparison.Ordinal);
        }

        return string.Equals(text, current, StringComparison.Ordinal)
            ? new(null, "The edits change nothing.")
            : new(text, null);
    }

    /// <summary>A client usually sends LF; exports use CRLF. Exact text wins, then CRLF is tried.</summary>
    private static (string Old, string New) MatchLineEndings(string text, string oldText, string newText)
    {
        if (!text.Contains("\r\n", StringComparison.Ordinal))
        {
            return (oldText, newText);
        }

        return text.Contains(oldText, StringComparison.Ordinal)
            ? (oldText, ToCrLf(newText))
            : (ToCrLf(oldText), ToCrLf(newText));
    }

    private static string ToCrLf(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

/// <summary>
/// The host's read-only view of the multi-document export of a block ("--- FILE: name ---" lines).
/// Only the Simatic ML document is applied by an import; companions such as .s7dcl are derived from it.
/// </summary>
public static partial class PlcContentBundle
{
    [GeneratedRegex(@"^--- FILE: (?<name>.+) ---(?:\r?\n|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Delimiter();

    /// <summary>The .xml document of a bundle; a document without delimiters is returned unchanged.</summary>
    public static string Primary(string content)
    {
        var matches = Delimiter().Matches(content);
        for (var index = 0; index < matches.Count; index++)
        {
            if (!matches[index].Groups["name"].Value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = matches[index].Index + matches[index].Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : content.Length;
            return content[start..end];
        }

        return content;
    }

    /// <summary>A fail-safe language of a block, as Simatic ML or the project tree names it (F_LAD, F_FBD, F_DB ...).</summary>
    public static bool IsSafetyLanguage(string? language)
        => language is not null && language.TrimStart().StartsWith("F_", StringComparison.OrdinalIgnoreCase);

    // Simatic ML names the language; the .s7dcl document, the only one TIA Portal exports for an F block, sets S7_Safety.
    [GeneratedRegex(@"<ProgrammingLanguage>\s*F_|\bS7_Safety\s*:=\s*""?\s*TRUE", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SafetyLanguageElement();

    /// <summary>True when an exported or submitted document marks a fail-safe block.</summary>
    public static bool DeclaresSafetyLanguage(string? content)
        => content is not null && SafetyLanguageElement().IsMatch(content);
}
