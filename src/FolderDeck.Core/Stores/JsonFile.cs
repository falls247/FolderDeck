using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Core.Stores;

public enum LoadStatus
{
    /// <summary>Read successfully.</summary>
    Loaded,

    /// <summary>No file yet; started empty.</summary>
    Missing,

    /// <summary>The file was broken and was moved aside; started empty.</summary>
    Recovered,

    /// <summary>The file was broken (or unreadable) and could not be moved aside; the feature is disabled.</summary>
    Failed,
}

public sealed record LoadResult<T>(T Data, LoadStatus Status, string? BackupPath, string? Detail);

/// <summary>JSON persistence shared by every store (§13.2, §13.3).</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>UTC, ISO 8601 to the second: <c>2026-11-21T01:23:45+00:00</c>.</summary>
    public static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var value))
        {
            return value.ToUniversalTime();
        }

        return null;
    }

    /// <summary>Truncates to whole seconds (what is stored on disk).</summary>
    public static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    public static LoadResult<T> Load<T>(string path, Func<T> empty, Func<T, bool>? validate, IClock clock)
        where T : class
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return new LoadResult<T>(empty(), LoadStatus.Missing, null, null);
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Could not read {path}", ex);
            return new LoadResult<T>(empty(), LoadStatus.Failed, null, ex.Message);
        }

        T? data = null;
        string? detail = null;
        try
        {
            ReadOnlySpan<byte> span = bytes;
            if (span.StartsWith(Utf8Bom))
            {
                span = span[Utf8Bom.Length..];
            }

            data = JsonSerializer.Deserialize<T>(span, Options);
            if (data is null)
            {
                detail = "empty document";
            }
            else if (validate is not null && !validate(data))
            {
                detail = "unexpected format";
                data = null;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            detail = ex.Message;
            data = null;
        }

        if (data is not null)
        {
            return new LoadResult<T>(data, LoadStatus.Loaded, null, null);
        }

        // Broken: move aside, continue empty. Never overwrite the broken file.
        string backup = BackupPathFor(path, clock.UtcNow);
        try
        {
            File.Move(path, backup);
            AppLog.Warn($"Broken data file moved aside: {path} -> {backup} ({detail})");
            return new LoadResult<T>(empty(), LoadStatus.Recovered, backup, detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Broken data file could not be moved aside: {path}", ex);
            return new LoadResult<T>(empty(), LoadStatus.Failed, null, ex.Message);
        }
    }

    /// <summary><c>&lt;name&gt;.broken-&lt;unix seconds&gt;.json</c> next to the original (unique).</summary>
    public static string BackupPathFor(string path, DateTimeOffset now)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileNameWithoutExtension(path);
        long unix = now.ToUnixTimeSeconds();
        string candidate = Path.Combine(dir, $"{name}.broken-{unix}.json");
        int n = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{name}.broken-{unix}-{n++}.json");
        }

        return candidate;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>
    /// Writes to <c>&lt;name&gt;.&lt;random&gt;.tmp</c> in the same folder, then replaces the target.
    /// The temporary file never survives a failure.
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $"{Path.GetFileName(path)}.{RandomNumberGenerator.GetHexString(8, lowercase: true)}.tmp");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // best effort
            }

            throw;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new LenientTimeConverter());
        return options;
    }
}

/// <summary>
/// Reads ISO 8601 strings leniently (anything unparsable becomes null instead of failing the whole
/// file) and writes <c>yyyy-MM-ddTHH:mm:ss+00:00</c>.
/// </summary>
public sealed class LenientTimeConverter : JsonConverter<DateTimeOffset?>
{
    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return JsonFile.ParseTime(reader.GetString());
            case JsonTokenType.StartArray:
            case JsonTokenType.StartObject:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(JsonFile.FormatTime(value.Value));
        }
    }
}
