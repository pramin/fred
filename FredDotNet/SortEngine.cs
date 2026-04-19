using System.Globalization;
using System.Text;

namespace FredDotNet;

/// <summary>
/// Options controlling line sorting behavior.
/// </summary>
public sealed class SortOptions
{
    /// <summary>Reverse sort order.</summary>
    public bool Reverse { get; set; }

    /// <summary>Numeric sort (compare as numbers, not strings).</summary>
    public bool Numeric { get; set; }

    /// <summary>Case-insensitive sort.</summary>
    public bool IgnoreCase { get; set; }

    /// <summary>Remove duplicate lines (like sort -u).</summary>
    public bool Unique { get; set; }

    /// <summary>Sort by field number (1-based, like sort -k).</summary>
    public int? KeyField { get; set; }

    /// <summary>Field separator for -k sorting.</summary>
    public string? FieldSeparator { get; set; }

    /// <summary>Stable sort (preserve order of equal elements).</summary>
    public bool Stable { get; set; }
}

/// <summary>
/// Sorts and optionally deduplicates lines of text.
/// Supports numeric, case-insensitive, key-field, reverse, and stable sort modes.
/// </summary>
public static class SortEngine
{
    private static readonly SortOptions s_defaultOptions = new();

    /// <summary>Sort lines of text according to the specified options.</summary>
    /// <param name="input">The multi-line text to sort.</param>
    /// <param name="options">Sort options, or <c>null</c> for default (ascending, string, case-sensitive).</param>
    /// <returns>The sorted text with lines joined by newline characters.</returns>
    public static string Sort(string input, SortOptions? options = null)
    {
        var opts = options ?? s_defaultOptions;

        // Split into lines, preserving trailing newline state
        bool endsWithNewline = input.Length > 0 && input[input.Length - 1] == '\n';
        string[] lines = SplitLines(input);

        // Build comparer
        var comparer = BuildComparer(opts);

        // Sort
        if (opts.Stable)
        {
            // Stable sort: use index-augmented sort to preserve insertion order for equal elements
            int[] indices = new int[lines.Length];
            for (int i = 0; i < indices.Length; i++)
                indices[i] = i;

            Array.Sort(indices, (a, b) =>
            {
                int cmp = comparer.Compare(lines[a], lines[b]);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            string[] sorted = new string[lines.Length];
            for (int i = 0; i < indices.Length; i++)
                sorted[i] = lines[indices[i]];
            lines = sorted;
        }
        else
        {
            Array.Sort(lines, comparer);
        }

        // Reverse
        if (opts.Reverse)
            Array.Reverse(lines);

        // Deduplicate
        if (opts.Unique)
            lines = Deduplicate(lines, opts.IgnoreCase);

        // Join
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Length - 1 || endsWithNewline)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string[] SplitLines(string input)
    {
        // Count lines first to avoid List overhead
        int count = 1;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\n')
                count++;
        }

        // If ends with newline, last "line" is empty — exclude it
        bool endsWithNewline = input.Length > 0 && input[input.Length - 1] == '\n';

        string[] lines = new string[endsWithNewline ? count - 1 : count];
        int lineIdx = 0;
        int start = 0;

        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\n')
            {
                if (lineIdx < lines.Length)
                    lines[lineIdx++] = input.Substring(start, i - start);
                start = i + 1;
            }
        }

        // Last segment (no trailing newline)
        if (lineIdx < lines.Length)
            lines[lineIdx] = input.Substring(start);

        return lines;
    }

    private static IComparer<string> BuildComparer(SortOptions opts)
    {
        if (opts.KeyField.HasValue)
        {
            return new KeyFieldComparer(
                opts.KeyField.Value,
                opts.FieldSeparator,
                opts.Numeric,
                opts.IgnoreCase);
        }

        if (opts.Numeric)
            return new NumericComparer(opts.IgnoreCase);

        if (opts.IgnoreCase)
            return StringComparer.OrdinalIgnoreCase;

        return StringComparer.Ordinal;
    }

    private static string[] Deduplicate(string[] sorted, bool ignoreCase)
    {
        if (sorted.Length <= 1)
            return sorted;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // Count unique first
        int uniqueCount = 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            if (!string.Equals(sorted[i], sorted[i - 1], comparison))
                uniqueCount++;
        }

        if (uniqueCount == sorted.Length)
            return sorted;

        string[] result = new string[uniqueCount];
        result[0] = sorted[0];
        int idx = 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            if (!string.Equals(sorted[i], sorted[i - 1], comparison))
                result[idx++] = sorted[i];
        }

        return result;
    }

    private sealed class NumericComparer : IComparer<string>
    {
        private readonly bool _ignoreCase;

        public NumericComparer(bool ignoreCase)
        {
            _ignoreCase = ignoreCase;
        }

        public int Compare(string? x, string? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            bool xParsed = double.TryParse(x.AsSpan().Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double xVal);
            bool yParsed = double.TryParse(y.AsSpan().Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double yVal);

            if (xParsed && yParsed)
                return xVal.CompareTo(yVal);

            // Non-numeric values sort after numeric values
            if (xParsed) return -1;
            if (yParsed) return 1;

            // Both non-numeric: fall back to string comparison
            return string.Compare(x, y, _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    private sealed class KeyFieldComparer : IComparer<string>
    {
        private readonly int _field; // 1-based
        private readonly string? _separator;
        private readonly bool _numeric;
        private readonly bool _ignoreCase;

        public KeyFieldComparer(int field, string? separator, bool numeric, bool ignoreCase)
        {
            _field = field;
            _separator = separator;
            _numeric = numeric;
            _ignoreCase = ignoreCase;
        }

        public int Compare(string? x, string? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            string xField = ExtractField(x);
            string yField = ExtractField(y);

            if (_numeric)
            {
                bool xParsed = double.TryParse(xField.AsSpan().Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double xVal);
                bool yParsed = double.TryParse(yField.AsSpan().Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double yVal);

                if (xParsed && yParsed)
                    return xVal.CompareTo(yVal);
                if (xParsed) return -1;
                if (yParsed) return 1;
            }

            return string.Compare(xField, yField, _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private string ExtractField(string line)
        {
            int fieldIndex = _field - 1; // Convert to 0-based
            int currentField = 0;
            int start = 0;

            if (_separator != null)
            {
                // Split by explicit separator
                while (currentField < fieldIndex)
                {
                    int idx = line.IndexOf(_separator, start, StringComparison.Ordinal);
                    if (idx < 0)
                        return ""; // Field doesn't exist
                    start = idx + _separator.Length;
                    currentField++;
                }
                int end = line.IndexOf(_separator, start, StringComparison.Ordinal);
                return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
            }
            else
            {
                // Split by whitespace (default)
                // Skip leading whitespace
                while (start < line.Length && char.IsWhiteSpace(line[start]))
                    start++;

                while (currentField < fieldIndex && start < line.Length)
                {
                    // Skip non-whitespace (current field)
                    while (start < line.Length && !char.IsWhiteSpace(line[start]))
                        start++;
                    // Skip whitespace (separator)
                    while (start < line.Length && char.IsWhiteSpace(line[start]))
                        start++;
                    currentField++;
                }

                if (start >= line.Length)
                    return "";

                int end = start;
                while (end < line.Length && !char.IsWhiteSpace(line[end]))
                    end++;
                return line.Substring(start, end - start);
            }
        }
    }
}
