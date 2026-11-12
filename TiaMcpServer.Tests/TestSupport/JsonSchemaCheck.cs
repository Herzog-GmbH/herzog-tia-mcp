using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Tests.TestSupport;

/// <summary>
/// Checks a tool's structuredContent against its advertised outputSchema, the way an MCP client
/// does. Claude Code rejects a response that does not match and shows the model only the error, even
/// though the tool ran. The C# SDK client used by these tests does not check, so the tests must.
///
/// <para>
/// A keyword this class does not know is itself a violation: a schema feature nobody checks must not
/// pass silently.
/// </para>
/// </summary>
internal static class JsonSchemaCheck
{
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$comment", "title", "description", "default", "examples", "format",
        "deprecated", "readOnly", "writeOnly", "$defs", "definitions",
    };

    public static IReadOnlyList<string> Violations(JsonElement schema, JsonElement document)
    {
        var found = new List<string>();
        Check(schema, schema, document, "$", found);
        return found;
    }

    private static void Check(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> found)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            found.Add($"{path}: the schema allows no value here");
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            switch (keyword.Name)
            {
                case "$ref":
                    Check(root, Resolve(root, keyword.Value.GetString()!), value, path, found);
                    break;
                case "type":
                    if (!MatchesType(keyword.Value, value))
                    {
                        found.Add($"{path}: must be {keyword.Value.GetRawText()}, is {value.ValueKind}");
                    }

                    break;
                case "enum":
                    if (!keyword.Value.EnumerateArray().Any(allowed => JsonElement.DeepEquals(allowed, value)))
                    {
                        found.Add($"{path}: {value.GetRawText()} is not one of {keyword.Value.GetRawText()}");
                    }

                    break;
                case "const":
                    if (!JsonElement.DeepEquals(keyword.Value, value))
                    {
                        found.Add($"{path}: must equal {keyword.Value.GetRawText()}, is {value.GetRawText()}");
                    }

                    break;
                case "required":
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var name in keyword.Value.EnumerateArray().Select(entry => entry.GetString()!))
                        {
                            if (!value.TryGetProperty(name, out _))
                            {
                                found.Add($"{path}: must have required property '{name}'");
                            }
                        }
                    }

                    break;
                case "properties":
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in value.EnumerateObject())
                        {
                            if (keyword.Value.TryGetProperty(property.Name, out var member))
                            {
                                Check(root, member, property.Value, $"{path}.{property.Name}", found);
                            }
                        }
                    }

                    break;
                case "additionalProperties":
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        var declared = schema.TryGetProperty("properties", out var names) ? names : default;
                        foreach (var property in value.EnumerateObject())
                        {
                            if (declared.ValueKind != JsonValueKind.Object || !declared.TryGetProperty(property.Name, out _))
                            {
                                Check(root, keyword.Value, property.Value, $"{path}.{property.Name}", found);
                            }
                        }
                    }

                    break;
                case "items":
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        var index = 0;
                        foreach (var item in value.EnumerateArray())
                        {
                            Check(root, keyword.Value, item, $"{path}[{index++}]", found);
                        }
                    }

                    break;
                case "anyOf":
                    if (CountMatching(root, keyword.Value, value, path) == 0)
                    {
                        found.Add($"{path}: matches no entry of anyOf");
                    }

                    break;
                case "oneOf":
                    var matching = CountMatching(root, keyword.Value, value, path);
                    if (matching != 1)
                    {
                        found.Add($"{path}: matches {matching} entries of oneOf, must match exactly 1");
                    }

                    break;
                case "allOf":
                    foreach (var entry in keyword.Value.EnumerateArray())
                    {
                        Check(root, entry, value, path, found);
                    }

                    break;
                case "minimum":
                    if (value.ValueKind == JsonValueKind.Number && value.GetDouble() < keyword.Value.GetDouble())
                    {
                        found.Add($"{path}: must be at least {keyword.Value.GetRawText()}");
                    }

                    break;
                case "maximum":
                    if (value.ValueKind == JsonValueKind.Number && value.GetDouble() > keyword.Value.GetDouble())
                    {
                        found.Add($"{path}: must be at most {keyword.Value.GetRawText()}");
                    }

                    break;
                case "minLength":
                    if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length < keyword.Value.GetInt32())
                    {
                        found.Add($"{path}: must have at least {keyword.Value.GetRawText()} characters");
                    }

                    break;
                case "maxLength":
                    if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > keyword.Value.GetInt32())
                    {
                        found.Add($"{path}: must have at most {keyword.Value.GetRawText()} characters");
                    }

                    break;
                case "minItems":
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() < keyword.Value.GetInt32())
                    {
                        found.Add($"{path}: must have at least {keyword.Value.GetRawText()} entries");
                    }

                    break;
                case "maxItems":
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > keyword.Value.GetInt32())
                    {
                        found.Add($"{path}: must have at most {keyword.Value.GetRawText()} entries");
                    }

                    break;
                case "pattern":
                    if (value.ValueKind == JsonValueKind.String && !Regex.IsMatch(value.GetString()!, keyword.Value.GetString()!))
                    {
                        found.Add($"{path}: does not match the pattern {keyword.Value.GetString()}");
                    }

                    break;
                default:
                    if (!Annotations.Contains(keyword.Name))
                    {
                        found.Add($"{path}: the schema keyword '{keyword.Name}' is not checked by this class");
                    }

                    break;
            }
        }
    }

    private static int CountMatching(JsonElement root, JsonElement entries, JsonElement value, string path)
        => entries.EnumerateArray().Count(entry =>
        {
            var inner = new List<string>();
            Check(root, entry, value, path, inner);
            return inner.Count == 0;
        });

    private static bool MatchesType(JsonElement type, JsonElement value)
        => type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(entry => MatchesType(entry.GetString()!, value))
            : MatchesType(type.GetString()!, value);

    private static bool MatchesType(string type, JsonElement value)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number
                && Math.Floor(value.GetDouble()) == value.GetDouble(),
            _ => throw new InvalidOperationException($"Unknown schema type '{type}'."),
        };

    private static JsonElement Resolve(JsonElement root, string reference)
    {
        if (reference != "#" && !reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Only local references are checked, not '{reference}'.");
        }

        var current = root;
        foreach (var raw in reference.Split('/').Skip(1))
        {
            var part = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current.ValueKind == JsonValueKind.Array
                ? current[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]
                : current.GetProperty(part);
        }

        return current;
    }
}
