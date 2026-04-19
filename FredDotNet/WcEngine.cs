using System.Text;

namespace FredDotNet;

/// <summary>
/// Counts lines, words, characters, and bytes in text input.
/// Single-pass, zero-allocation (beyond the result struct) character scanner.
/// </summary>
public static class WcEngine
{
    /// <summary>Count lines, words, characters, and bytes in the input string.</summary>
    /// <param name="input">The text to analyze.</param>
    /// <returns>A <see cref="WcResult"/> with counts for lines, words, characters, and bytes.</returns>
    public static WcResult Count(string input)
    {
        if (input.Length == 0)
            return default;

        ReadOnlySpan<char> span = input.AsSpan();
        int lines = 0;
        int words = 0;
        int chars = span.Length;
        bool inWord = false;

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];

            if (c == '\n')
                lines++;

            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else
            {
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
        }

        long bytes = Encoding.UTF8.GetByteCount(input);

        return new WcResult
        {
            Lines = lines,
            Words = words,
            Characters = chars,
            Bytes = bytes,
        };
    }

    /// <summary>Count lines, words, characters, and bytes from a <see cref="TextReader"/>.</summary>
    /// <param name="reader">The text reader to consume.</param>
    /// <returns>A <see cref="WcResult"/> with counts for lines, words, characters, and bytes.</returns>
    public static WcResult Count(TextReader reader)
    {
        int lines = 0;
        int words = 0;
        int chars = 0;
        long bytes = 0;
        bool inWord = false;

        char[] buffer = new char[8192];
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                chars++;

                if (c == '\n')
                    lines++;

                if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                }
                else
                {
                    if (!inWord)
                    {
                        words++;
                        inWord = true;
                    }
                }
            }

            // Count UTF-8 bytes for this chunk
            bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
        }

        return new WcResult
        {
            Lines = lines,
            Words = words,
            Characters = chars,
            Bytes = bytes,
        };
    }
}

/// <summary>
/// Result of a word/line/byte count operation.
/// </summary>
public readonly record struct WcResult
{
    /// <summary>Number of newline characters in the input.</summary>
    public int Lines { get; init; }

    /// <summary>Number of whitespace-delimited words in the input.</summary>
    public int Words { get; init; }

    /// <summary>Number of characters (UTF-16 code units) in the input.</summary>
    public int Characters { get; init; }

    /// <summary>Number of bytes when encoded as UTF-8.</summary>
    public long Bytes { get; init; }
}
