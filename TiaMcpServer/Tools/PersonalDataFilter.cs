using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Tools;

/// <summary>
/// Keeps names of people out of what the server returns in the Produktiv profile: project author,
/// last editor, block authors and the project history. The values become null, so the shape of a
/// response stays the same. The exception is a <c>details</c> object: it maps names to strings and
/// a null there breaks the output schema, so the entry is removed, as for a block without an author.
/// Block text that is read on purpose is left alone: it has to stay byte-identical for its content hash.
/// </summary>
public static class PersonalDataFilter
{
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "author", "lastModifiedBy", "headerAuthor", "historyEntries", "copyright",
    };

    private static volatile bool _enabled;

    /// <summary>Set once at startup; off in every other access mode.</summary>
    public static void Enable() => _enabled = true;

    internal static string Apply(string canonicalText) => _enabled ? Scrub(canonicalText) : canonicalText;

    /// <summary>The document without personal values; a document with none comes back unchanged.</summary>
    public static string Scrub(string json)
    {
        var root = JsonNode.Parse(json);
        return root is not null && ScrubNode(root) ? root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) : json;
    }

    private static bool ScrubNode(JsonNode node, bool isStringMap = false)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            // Openness attributes also come as { "name": "HeaderAuthor", "value": "..." }.
            if (obj["name"] is JsonValue attribute && attribute.TryGetValue<string>(out var name)
                && Keys.Contains(name) && obj.ContainsKey("value") && obj["value"] is not null)
            {
                obj["value"] = null;
                changed = true;
            }

            foreach (var key in obj.Select(entry => entry.Key).ToArray())
            {
                if (Keys.Contains(key))
                {
                    if (isStringMap)
                    {
                        obj.Remove(key);
                        changed = true;
                    }
                    else if (obj[key] is not null)
                    {
                        obj[key] = null;
                        changed = true;
                    }
                }
                else if (obj[key] is { } child)
                {
                    changed |= ScrubNode(child, child is JsonObject && key.Equals("details", StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    changed |= ScrubNode(child);
                }
            }
        }

        return changed;
    }
}
