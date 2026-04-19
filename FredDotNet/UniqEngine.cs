namespace FredDotNet;

/// <summary>
/// Options controlling uniq behaviour.
/// </summary>
public sealed class UniqOptions
{
    /// <summary>Prefix each line with the number of occurrences (like uniq -c).</summary>
    public bool Count { get; set; }
    /// <summary>Only output duplicate lines (like uniq -d).</summary>
    public bool OnlyDuplicates { get; set; }
    /// <summary>Only output unique lines — lines that are NOT repeated (like uniq -u).</summary>
    public bool OnlyUnique { get; set; }
    /// <summary>Case-insensitive comparison (like uniq -i).</summary>
    public bool IgnoreCase { get; set; }
}

/// <summary>
/// Filters adjacent duplicate lines from text input.
/// Unlike sort -u which removes all duplicates globally, uniq only collapses
/// consecutive identical lines — matching Unix uniq behaviour.
/// </summary>
public static class UniqEngine
{
    /// <summary>Filter adjacent duplicate lines from the input text.</summary>
    public static string Execute(string input, UniqOptions? options = null)
    {
        var opts = options ?? new UniqOptions();
        var comparison = opts.IgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Split into lines preserving trailing newline behaviour
        var lines = input.Split('\n');
        bool trailingNewline = lines.Length > 0 && lines[^1].Length == 0;
        int lineCount = trailingNewline ? lines.Length - 1 : lines.Length;

        if (lineCount == 0)
            return input;

        var result = new System.Text.StringBuilder();
        string currentLine = lines[0];
        int count = 1;

        for (int i = 1; i < lineCount; i++)
        {
            if (string.Equals(lines[i], currentLine, comparison))
            {
                count++;
            }
            else
            {
                AppendLine(result, currentLine, count, opts);
                currentLine = lines[i];
                count = 1;
            }
        }

        // Flush last group
        AppendLine(result, currentLine, count, opts);

        return result.ToString();
    }

    private static void AppendLine(System.Text.StringBuilder sb, string line, int count, UniqOptions opts)
    {
        // -d: only duplicates (count > 1)
        if (opts.OnlyDuplicates && count <= 1) return;
        // -u: only unique (count == 1)
        if (opts.OnlyUnique && count > 1) return;

        if (opts.Count)
        {
            sb.Append(count.ToString().PadLeft(7));
            sb.Append(' ');
        }
        sb.Append(line);
        sb.Append('\n');
    }
}
