using System.Text.Json;
using System.Text.Json.Serialization;

namespace FredDotNet;

/// <summary>
/// JSON-serializable result from a fred pipeline operation.
/// </summary>
public sealed class FredResult
{
    [JsonPropertyName("filesSearched")]
    public int FilesSearched { get; set; }

    [JsonPropertyName("filesMatched")]
    public int FilesMatched { get; set; }

    [JsonPropertyName("filesModified")]
    public int FilesModified { get; set; }

    [JsonPropertyName("matches")]
    public List<FredFileMatch> Matches { get; set; } = new();

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, FredJsonContext.Default.FredResult);
    }
}

/// <summary>
/// A single file's match results.
/// </summary>
public sealed class FredFileMatch
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("lines")]
    public List<FredLineMatch> Lines { get; set; } = new();
}

/// <summary>
/// A single line match within a file.
/// </summary>
public sealed class FredLineMatch
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("replacement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Replacement { get; set; }
}

/// <summary>
/// Source-generated JSON serialization context for FredResult types.
/// Avoids reflection-based serialization overhead.
/// </summary>
[JsonSerializable(typeof(FredResult))]
[JsonSerializable(typeof(FredFileMatch))]
[JsonSerializable(typeof(FredLineMatch))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class FredJsonContext : JsonSerializerContext
{
}
