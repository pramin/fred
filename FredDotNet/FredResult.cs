using System.Text.Json;
using System.Text.Json.Serialization;

namespace FredDotNet;

/// <summary>
/// JSON-serializable result from a fred pipeline operation.
/// </summary>
public sealed class FredResult
{
    /// <summary>Total number of files examined during the operation.</summary>
    [JsonPropertyName("filesSearched")]
    public int FilesSearched { get; set; }

    /// <summary>Number of files that contained at least one match.</summary>
    [JsonPropertyName("filesMatched")]
    public int FilesMatched { get; set; }

    /// <summary>Number of files modified by a sed or AWK transformation.</summary>
    [JsonPropertyName("filesModified")]
    public int FilesModified { get; set; }

    /// <summary>Per-file match details for every matched file.</summary>
    [JsonPropertyName("matches")]
    public List<FredFileMatch> Matches { get; set; } = new();

    /// <summary>Serializes this result to indented JSON using source-generated context.</summary>
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
    /// <summary>Absolute or relative path of the matched file.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    /// <summary>Individual line matches found within this file.</summary>
    [JsonPropertyName("lines")]
    public List<FredLineMatch> Lines { get; set; } = new();
}

/// <summary>
/// A single line match within a file.
/// </summary>
public sealed class FredLineMatch
{
    /// <summary>One-based line number of the match.</summary>
    [JsonPropertyName("number")]
    public int Number { get; set; }

    /// <summary>Original content of the matched line.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>Replacement text after sed/AWK transformation, or null if unmodified.</summary>
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
