using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcpServer.Plc;

/// <summary>One operation that takes back part of an earlier write; its content lives in a snapshot file.</summary>
public sealed record PlcInverseOperation(PlcOperationRequest Request, string? ContentFile, string? ExpectedContentHash, bool RunSeparately);

/// <summary>What one applied plc_write item needs to be taken back, or why it cannot be.</summary>
public sealed record PlcJournalEntry(
    string CallId,
    DateTimeOffset At,
    string OperationId,
    string Operation,
    string? Target,
    IReadOnlyList<PlcInverseOperation> Inverse,
    string? NotUndoableReason);

/// <summary>
/// The server-side safety net of the Produktiv profile: per project a snapshot folder and an
/// append-only journal under one root. Files older than <see cref="KeepDays"/> are removed at
/// startup. Holds block text in the clear, in the current user's profile.
/// </summary>
public sealed class PlcProtection
{
    public const int KeepDays = 30;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public PlcProtection(string rootDirectory, TimeProvider? time = null)
    {
        Root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        _time = time ?? TimeProvider.System;
        SessionStart = _time.GetUtcNow();
    }

    public string Root { get; }

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>When this server started. A journal call before it belongs to an earlier session.</summary>
    public DateTimeOffset SessionStart { get; }

    /// <summary>The default root: <c>%USERPROFILE%\herzog-tia</c>.</summary>
    public static string DefaultRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "herzog-tia");

    /// <summary>A stable folder and file name for a project path; a path is never used as a name.</summary>
    public static string ProjectKey(string? projectPath)
    {
        var normalized = (projectPath ?? "ohne-projekt").Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }

    public static string NewCallId(DateTimeOffset at)
        => at.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    /// <summary>Writes the content an operation is about to replace and returns the file path.</summary>
    public string SaveSnapshot(string projectKey, string callId, string operationId, string target, string format, string content)
    {
        var directory = Path.Combine(Root, "sicherung", projectKey, callId);
        Directory.CreateDirectory(directory);
        var extension = string.Equals(format, "xml", StringComparison.OrdinalIgnoreCase) ? "xml" : "txt";
        var path = Path.Combine(directory, $"{SafeName(operationId)}_{SafeName(target)}.{extension}");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public void Append(string projectKey, PlcJournalEntry entry)
    {
        var directory = Path.Combine(Root, "journal");
        Directory.CreateDirectory(directory);
        lock (_gate)
        {
            File.AppendAllText(JournalPath(projectKey), JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
        }
    }

    public void MarkUndone(string projectKey, string callId)
    {
        var directory = Path.Combine(Root, "journal");
        Directory.CreateDirectory(directory);
        lock (_gate)
        {
            File.AppendAllText(JournalPath(projectKey),
                JsonSerializer.Serialize(new UndoneMarker(callId), Json) + Environment.NewLine);
        }
    }

    /// <summary>Applied calls not yet taken back, newest last; a damaged line is skipped, never guessed.</summary>
    public IReadOnlyList<IReadOnlyList<PlcJournalEntry>> ReadOpenCalls(string projectKey)
    {
        var path = JournalPath(projectKey);
        if (!File.Exists(path))
        {
            return Array.Empty<IReadOnlyList<PlcJournalEntry>>();
        }

        string[] lines;
        lock (_gate)
        {
            lines = File.ReadAllLines(path);
        }

        var calls = new List<List<PlcJournalEntry>>();
        var undone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("undoneCallId", out var marker))
                {
                    undone.Add(marker.GetString() ?? string.Empty);
                    continue;
                }

                var entry = JsonSerializer.Deserialize<PlcJournalEntry>(line, Json);
                if (entry is null)
                {
                    continue;
                }

                if (calls.Count > 0 && calls[^1][0].CallId == entry.CallId)
                {
                    calls[^1].Add(entry);
                }
                else
                {
                    calls.Add(new List<PlcJournalEntry> { entry });
                }
            }
            catch (JsonException)
            {
            }
        }

        return calls.Where(call => !undone.Contains(call[0].CallId)).ToArray();
    }

    /// <summary>Removes snapshots and exports past their keep time. A failure here never stops the server.</summary>
    public void Cleanup()
    {
        foreach (var (folder, days) in new[] { ("sicherung", KeepDays), ("export", 7) })
        {
            var directory = new DirectoryInfo(Path.Combine(Root, folder));
            if (!directory.Exists)
            {
                continue;
            }

            var limit = Now.AddDays(-days).UtcDateTime;
            foreach (var old in directory.EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.LastWriteTimeUtc < limit).ToArray())
            {
                try
                {
                    old.Delete();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private string JournalPath(string projectKey) => Path.Combine(Root, "journal", projectKey + ".jsonl");

    private static string SafeName(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 60));
        foreach (var c in value.Take(60))
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return builder.Length == 0 ? "objekt" : builder.ToString();
    }

    private sealed record UndoneMarker(string UndoneCallId);
}

/// <summary>Writes read content to a text file, so a large block never has to pass through the response.</summary>
public static class PlcContentFiles
{
    private static string? _directory;
    private static int? _autoThresholdChars;

    /// <summary>Set once at startup. Null keeps content in the response unless a read asks for a file.</summary>
    public static void Configure(string? directory, int? autoThresholdChars)
    {
        _directory = directory;
        _autoThresholdChars = autoThresholdChars;
    }

    public static bool ShouldWrite(bool? toFile, int characters)
        => toFile == true || (toFile is null && _autoThresholdChars is { } threshold && characters > threshold);

    public static string Write(string objectPath, string format, string content)
    {
        var directory = _directory ?? Path.Combine(Path.GetTempPath(), "herzog-tia", "export");
        Directory.CreateDirectory(directory);
        var name = new string(objectPath.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        if (name.Length > 120)
        {
            name = name[^120..];
        }

        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}.{(format == "xml" ? "xml" : "txt")}");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
