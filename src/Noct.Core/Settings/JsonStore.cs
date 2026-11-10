using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noct.Core.Settings;

/// <summary>
/// Versioned JSON persistence with atomic replacement.
/// </summary>
/// <remarks>
/// Writes go to a temporary sibling file and are then moved over the target, so a crash or a pulled
/// power cable leaves either the previous file or the new one, never a half-written document. A
/// document whose schema version is newer than this build understands is left untouched and the
/// caller gets defaults, so a downgrade cannot silently destroy settings.
/// </remarks>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path, int supportedVersion, Func<T> createDefault)
        where T : IVersionedDocument
    {
        try
        {
            if (!File.Exists(path))
            {
                return createDefault();
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return createDefault();
            }

            var document = JsonSerializer.Deserialize<T>(json, Options);
            if (document is null)
            {
                return createDefault();
            }

            if (document.SchemaVersion > supportedVersion)
            {
                // A future schema is preserved on disk rather than overwritten with defaults.
                var fallback = createDefault();
                fallback.LoadIssue = $"{Path.GetFileName(path)} uses schema version {document.SchemaVersion}, newer than this build supports ({supportedVersion}). It was left unchanged and defaults are in use.";
                return fallback;
            }

            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            var fallback = createDefault();
            fallback.LoadIssue = $"Could not read {Path.GetFileName(path)}: {ex.Message}. Defaults are in use.";
            return fallback;
        }
    }

    public static void Save<T>(string path, T document)
        where T : IVersionedDocument
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        var json = JsonSerializer.Serialize(document, Options);
        File.WriteAllText(temporary, json);

        // File.Move with overwrite is atomic enough for this purpose on NTFS: the destination is
        // either the old file or the complete new one.
        File.Move(temporary, path, overwrite: true);
    }
}

public interface IVersionedDocument
{
    int SchemaVersion { get; }

    /// <summary>Set when loading fell back to defaults, so the UI can say so instead of staying silent.</summary>
    [JsonIgnore]
    string? LoadIssue { get; set; }
}
