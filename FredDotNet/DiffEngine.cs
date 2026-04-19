using System.Text;

namespace FredDotNet;

/// <summary>
/// Exception thrown when a patch cannot be applied.
/// </summary>
public sealed class DiffException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DiffException"/> class.
    /// </summary>
    public DiffException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DiffException"/> class with an inner exception.
    /// </summary>
    public DiffException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Unified diff generation and patch application engine.
/// Wraps <see cref="UnifiedDiff"/> for diff generation and provides patch application.
/// </summary>
public static class DiffEngine
{
    /// <summary>
    /// Generate a unified diff between two strings.
    /// Returns empty string if contents are identical.
    /// </summary>
    /// <param name="original">The original text.</param>
    /// <param name="modified">The modified text.</param>
    /// <param name="label">Optional label for the file header (defaults to "a" / "b").</param>
    /// <returns>A unified diff string, or empty if no differences.</returns>
    public static string Diff(string original, string modified, string? label = null)
    {
        string origLabel = label != null ? $"a/{label}" : "a";
        string modLabel = label != null ? $"b/{label}" : "b";
        return UnifiedDiff.Generate(original, modified, origLabel, modLabel);
    }

    /// <summary>
    /// Generate a unified diff between two files.
    /// </summary>
    /// <param name="originalPath">Path to the original file.</param>
    /// <param name="modifiedPath">Path to the modified file.</param>
    /// <returns>A unified diff string, or empty if files are identical.</returns>
    /// <exception cref="FileNotFoundException">Thrown when either file does not exist.</exception>
    public static string DiffFiles(string originalPath, string modifiedPath)
    {
        if (!File.Exists(originalPath))
            throw new FileNotFoundException($"Original file not found: {originalPath}", originalPath);
        if (!File.Exists(modifiedPath))
            throw new FileNotFoundException($"Modified file not found: {modifiedPath}", modifiedPath);

        string original = File.ReadAllText(originalPath);
        string modified = File.ReadAllText(modifiedPath);
        return UnifiedDiff.Generate(original, modified, originalPath, modifiedPath);
    }

    /// <summary>
    /// Apply a unified diff patch to the original text.
    /// </summary>
    /// <param name="original">The original text to patch.</param>
    /// <param name="unifiedDiff">The unified diff to apply.</param>
    /// <returns>The patched text.</returns>
    /// <exception cref="DiffException">Thrown when the patch cannot be applied cleanly.</exception>
    public static string Patch(string original, string unifiedDiff)
    {
        if (string.IsNullOrEmpty(unifiedDiff))
            return original;

        var hunks = ParseHunks(unifiedDiff);
        if (hunks.Count == 0)
            return original;

        // SplitLines matches UnifiedDiff.Generate's internal splitting:
        // "hello\n" -> ["hello", ""]  (trailing empty element = file ends with newline)
        // "hello"   -> ["hello"]      (no trailing element = no trailing newline)
        string[] lines = SplitLines(original);

        // Collect output lines, then join with \n at the end.
        // The output list mirrors the same convention: trailing empty = trailing newline.
        var outputLines = new List<string>();
        int currentLine = 0; // 0-based index into lines[]

        for (int h = 0; h < hunks.Count; h++)
        {
            var hunk = hunks[h];
            int hunkStart = hunk.OrigStart - 1; // convert 1-based to 0-based

            if (hunkStart < currentLine)
                throw new DiffException($"Hunk {h + 1}: overlapping or out-of-order hunk at line {hunk.OrigStart}");

            // Copy lines before this hunk
            for (int i = currentLine; i < hunkStart && i < lines.Length; i++)
            {
                outputLines.Add(lines[i]);
            }

            // Apply hunk
            int origIdx = hunkStart;
            for (int i = 0; i < hunk.Lines.Count; i++)
            {
                var (type, content) = hunk.Lines[i];
                switch (type)
                {
                    case HunkLineType.Context:
                        if (origIdx >= lines.Length)
                            throw new DiffException($"Hunk {h + 1}: context line beyond end of file at line {origIdx + 1}: '{content}'");
                        if (lines[origIdx] != content)
                            throw new DiffException($"Hunk {h + 1}: context mismatch at line {origIdx + 1}: expected '{content}', got '{lines[origIdx]}'");
                        outputLines.Add(content);
                        origIdx++;
                        break;

                    case HunkLineType.Remove:
                        if (origIdx >= lines.Length)
                            throw new DiffException($"Hunk {h + 1}: remove line beyond end of file at line {origIdx + 1}: '{content}'");
                        if (lines[origIdx] != content)
                            throw new DiffException($"Hunk {h + 1}: remove mismatch at line {origIdx + 1}: expected '{content}', got '{lines[origIdx]}'");
                        origIdx++;
                        break;

                    case HunkLineType.Add:
                        outputLines.Add(content);
                        break;
                }
            }

            currentLine = origIdx;
        }

        // Copy remaining lines after last hunk
        for (int i = currentLine; i < lines.Length; i++)
        {
            outputLines.Add(lines[i]);
        }

        // Join output lines with \n. Each line boundary gets a \n separator.
        // If output ends with an empty string element (trailing newline convention),
        // this naturally produces a trailing \n.
        var sb = new StringBuilder();
        for (int i = 0; i < outputLines.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(outputLines[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Apply a unified diff patch to a file, writing the result.
    /// </summary>
    /// <param name="filePath">Path to the file to patch.</param>
    /// <param name="unifiedDiff">The unified diff to apply.</param>
    /// <param name="backupSuffix">If non-null, create a backup with this suffix before patching.</param>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="DiffException">Thrown when the patch cannot be applied cleanly.</exception>
    public static void PatchFile(string filePath, string unifiedDiff, string? backupSuffix = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        string original = File.ReadAllText(filePath);
        string patched = Patch(original, unifiedDiff);

        if (backupSuffix != null)
        {
            File.Copy(filePath, filePath + backupSuffix, overwrite: true);
        }

        File.WriteAllText(filePath, patched);
    }

    /// <summary>
    /// Check if a patch can be applied cleanly without modifying anything.
    /// </summary>
    /// <param name="original">The original text.</param>
    /// <param name="unifiedDiff">The unified diff to check.</param>
    /// <returns>True if the patch can be applied cleanly, false otherwise.</returns>
    public static bool CanPatch(string original, string unifiedDiff)
    {
        try
        {
            Patch(original, unifiedDiff);
            return true;
        }
        catch (DiffException)
        {
            return false;
        }
    }

    private enum HunkLineType : byte
    {
        Context,
        Remove,
        Add,
    }

    private readonly struct ParsedHunk
    {
        public readonly int OrigStart;
        public readonly int OrigCount;
        public readonly int ModStart;
        public readonly int ModCount;
        public readonly List<(HunkLineType Type, string Content)> Lines;

        public ParsedHunk(int origStart, int origCount, int modStart, int modCount,
            List<(HunkLineType, string)> lines)
        {
            OrigStart = origStart;
            OrigCount = origCount;
            ModStart = modStart;
            ModCount = modCount;
            Lines = lines;
        }
    }

    /// <summary>
    /// Parse hunk headers and lines from a unified diff string.
    /// </summary>
    private static List<ParsedHunk> ParseHunks(string diff)
    {
        var hunks = new List<ParsedHunk>();
        string[] diffLines = SplitLines(diff);

        int i = 0;

        // Skip file headers (--- and +++ lines)
        while (i < diffLines.Length)
        {
            ReadOnlySpan<char> line = diffLines[i].AsSpan();
            if (line.StartsWith("@@".AsSpan()))
                break;
            i++;
        }

        while (i < diffLines.Length)
        {
            ReadOnlySpan<char> line = diffLines[i].AsSpan();
            if (!line.StartsWith("@@".AsSpan()))
            {
                i++;
                continue;
            }

            // Parse @@ -origStart,origCount +modStart,modCount @@
            if (!TryParseHunkHeader(diffLines[i], out int origStart, out int origCount,
                    out int modStart, out int modCount))
            {
                throw new DiffException($"Invalid hunk header: {diffLines[i]}");
            }

            i++;
            var hunkLines = new List<(HunkLineType, string)>();

            while (i < diffLines.Length)
            {
                ReadOnlySpan<char> hLine = diffLines[i].AsSpan();
                if (hLine.StartsWith("@@".AsSpan()))
                    break;
                if (hLine.StartsWith("---".AsSpan()) || hLine.StartsWith("+++".AsSpan()))
                    break;

                if (hLine.Length == 0)
                {
                    // Empty line in diff — could be trailing empty line from SplitLines or
                    // a genuine empty context line. Check if all remaining lines are also empty;
                    // if so, this is just the trailing artifact from SplitLines and we skip it.
                    bool isTrailing = true;
                    for (int j = i; j < diffLines.Length; j++)
                    {
                        if (diffLines[j].Length > 0)
                        {
                            isTrailing = false;
                            break;
                        }
                    }
                    if (isTrailing)
                    {
                        i = diffLines.Length;
                        break;
                    }
                    // Genuine empty context line (e.g., blank line in the file)
                    hunkLines.Add((HunkLineType.Context, string.Empty));
                }
                else
                {
                    char prefix = hLine[0];
                    string content = diffLines[i].Substring(1);
                    switch (prefix)
                    {
                        case ' ':
                            hunkLines.Add((HunkLineType.Context, content));
                            break;
                        case '-':
                            hunkLines.Add((HunkLineType.Remove, content));
                            break;
                        case '+':
                            hunkLines.Add((HunkLineType.Add, content));
                            break;
                        case '\\':
                            // "\ No newline at end of file" -- skip
                            break;
                        default:
                            // Treat as context (some diffs have unmarked context lines)
                            hunkLines.Add((HunkLineType.Context, diffLines[i]));
                            break;
                    }
                }

                i++;
            }

            hunks.Add(new ParsedHunk(origStart, origCount, modStart, modCount, hunkLines));
        }

        return hunks;
    }

    /// <summary>
    /// Parse a hunk header line like "@@ -1,5 +1,6 @@" or "@@ -1 +1,2 @@".
    /// </summary>
    private static bool TryParseHunkHeader(string header, out int origStart, out int origCount,
        out int modStart, out int modCount)
    {
        origStart = origCount = modStart = modCount = 0;

        ReadOnlySpan<char> span = header.AsSpan();

        // Find the first @@ and second @@
        int firstAt = span.IndexOf("@@".AsSpan());
        if (firstAt < 0) return false;

        int afterFirst = firstAt + 2;
        int secondAt = span.Slice(afterFirst).IndexOf("@@".AsSpan());
        if (secondAt < 0) return false;
        secondAt += afterFirst;

        // Extract the range between @@ markers: " -1,5 +1,6 "
        ReadOnlySpan<char> range = span.Slice(afterFirst, secondAt - afterFirst).Trim();

        // Find the minus range: -origStart,origCount
        int minusIdx = range.IndexOf('-');
        if (minusIdx < 0) return false;

        int plusIdx = range.Slice(minusIdx + 1).IndexOf('+');
        if (plusIdx < 0) return false;
        plusIdx += minusIdx + 1;

        ReadOnlySpan<char> origRange = range.Slice(minusIdx + 1, plusIdx - minusIdx - 1).Trim();
        ReadOnlySpan<char> modRange = range.Slice(plusIdx + 1).Trim();

        // Parse orig range
        int origComma = origRange.IndexOf(',');
        if (origComma >= 0)
        {
            if (!int.TryParse(origRange.Slice(0, origComma), out origStart)) return false;
            if (!int.TryParse(origRange.Slice(origComma + 1), out origCount)) return false;
        }
        else
        {
            if (!int.TryParse(origRange, out origStart)) return false;
            origCount = 1;
        }

        // Parse mod range
        int modComma = modRange.IndexOf(',');
        if (modComma >= 0)
        {
            if (!int.TryParse(modRange.Slice(0, modComma), out modStart)) return false;
            if (!int.TryParse(modRange.Slice(modComma + 1), out modCount)) return false;
        }
        else
        {
            if (!int.TryParse(modRange, out modStart)) return false;
            modCount = 1;
        }

        return true;
    }

    /// <summary>
    /// Split text into lines, handling \n and \r\n.
    /// Matches the splitting used by <see cref="UnifiedDiff"/>.
    /// A trailing newline produces a trailing empty element.
    /// </summary>
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return Array.Empty<string>();

        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = (i > 0 && text[i - 1] == '\r') ? i - 1 : i;
                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }
        }
        if (start <= text.Length)
        {
            lines.Add(text.Substring(start));
        }
        return lines.ToArray();
    }
}
