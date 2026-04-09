using System.Text;

namespace FredDotNet;

/// <summary>
/// Generates unified diff output comparing two strings, similar to diff -u.
/// </summary>
public static class UnifiedDiff
{
    /// <summary>
    /// Generate a unified diff between original and modified content.
    /// Returns empty string if contents are identical.
    /// </summary>
    public static string Generate(string original, string modified, string originalLabel, string modifiedLabel)
    {
        if (original == modified)
            return string.Empty;

        string[] origLines = SplitLines(original);
        string[] modLines = SplitLines(modified);

        var sb = new StringBuilder();
        sb.Append("--- ");
        sb.AppendLine(originalLabel);
        sb.Append("+++ ");
        sb.AppendLine(modifiedLabel);

        // Simple diff: find contiguous changed regions (hunks)
        var hunks = ComputeHunks(origLines, modLines);
        for (int h = 0; h < hunks.Count; h++)
        {
            var hunk = hunks[h];
            WriteHunk(sb, origLines, modLines, hunk);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Count the number of lines that differ between original and modified.
    /// </summary>
    public static int CountChangedLines(string original, string modified)
    {
        if (original == modified)
            return 0;

        string[] origLines = SplitLines(original);
        string[] modLines = SplitLines(modified);

        int changes = 0;
        int maxLen = origLines.Length > modLines.Length ? origLines.Length : modLines.Length;
        int minLen = origLines.Length < modLines.Length ? origLines.Length : modLines.Length;

        for (int i = 0; i < minLen; i++)
        {
            if (origLines[i] != modLines[i])
                changes++;
        }

        changes += maxLen - minLen;
        return changes;
    }

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

    private struct Hunk
    {
        public int OrigStart;
        public int OrigCount;
        public int ModStart;
        public int ModCount;
    }

    private static List<Hunk> ComputeHunks(string[] origLines, string[] modLines)
    {
        // Compute LCS-based edit script using simple O(mn) algorithm
        // For typical file editing (few changes), this is fine
        var hunks = new List<Hunk>();

        // Build a list of matching/non-matching regions using LCS
        int oi = 0, mi = 0;
        int origLen = origLines.Length;
        int modLen = modLines.Length;

        while (oi < origLen || mi < modLen)
        {
            // Skip matching lines
            if (oi < origLen && mi < modLen && origLines[oi] == modLines[mi])
            {
                oi++;
                mi++;
                continue;
            }

            // Found a difference - scan forward to find where they re-sync
            int origDiffStart = oi;
            int modDiffStart = mi;

            // Try to find next matching point
            bool found = false;
            int bestOi = origLen;
            int bestMi = modLen;
            int bestCost = origLen - oi + modLen - mi;

            // Look for the closest re-sync point
            int maxScan = 500; // limit scan range for large files
            int scanOrig = origLen - oi < maxScan ? origLen - oi : maxScan;
            int scanMod = modLen - mi < maxScan ? modLen - mi : maxScan;

            for (int dOi = 0; dOi <= scanOrig && !found; dOi++)
            {
                for (int dMi = 0; dMi <= scanMod; dMi++)
                {
                    if (dOi == 0 && dMi == 0)
                        continue;

                    int testOi = oi + dOi;
                    int testMi = mi + dMi;
                    int cost = dOi + dMi;

                    if (cost >= bestCost)
                        break;

                    if (testOi < origLen && testMi < modLen && origLines[testOi] == modLines[testMi])
                    {
                        // Verify at least 2 consecutive matching lines (or end of file)
                        bool goodSync = true;
                        if (testOi + 1 < origLen && testMi + 1 < modLen)
                        {
                            goodSync = origLines[testOi + 1] == modLines[testMi + 1];
                        }

                        if (goodSync && cost < bestCost)
                        {
                            bestOi = testOi;
                            bestMi = testMi;
                            bestCost = cost;
                            found = true;
                            break;
                        }
                    }
                }
            }

            // Create hunk with context
            int contextBefore = 3;
            int contextAfter = 3;

            int hunkOrigStart = origDiffStart - contextBefore;
            if (hunkOrigStart < 0) hunkOrigStart = 0;
            int hunkModStart = modDiffStart - contextBefore;
            if (hunkModStart < 0) hunkModStart = 0;

            int hunkOrigEnd = bestOi + contextAfter;
            if (hunkOrigEnd > origLen) hunkOrigEnd = origLen;
            int hunkModEnd = bestMi + contextAfter;
            if (hunkModEnd > modLen) hunkModEnd = modLen;

            hunks.Add(new Hunk
            {
                OrigStart = hunkOrigStart,
                OrigCount = hunkOrigEnd - hunkOrigStart,
                ModStart = hunkModStart,
                ModCount = hunkModEnd - hunkModStart,
            });

            oi = bestOi;
            mi = bestMi;
        }

        // Merge overlapping hunks
        if (hunks.Count <= 1)
            return hunks;

        var merged = new List<Hunk>();
        merged.Add(hunks[0]);
        for (int i = 1; i < hunks.Count; i++)
        {
            var prev = merged[merged.Count - 1];
            var curr = hunks[i];

            if (curr.OrigStart <= prev.OrigStart + prev.OrigCount)
            {
                // Overlapping - merge
                int origEnd = curr.OrigStart + curr.OrigCount;
                int modEnd = curr.ModStart + curr.ModCount;
                int prevOrigEnd = prev.OrigStart + prev.OrigCount;
                int prevModEnd = prev.ModStart + prev.ModCount;

                prev.OrigCount = (origEnd > prevOrigEnd ? origEnd : prevOrigEnd) - prev.OrigStart;
                prev.ModCount = (modEnd > prevModEnd ? modEnd : prevModEnd) - prev.ModStart;
                merged[merged.Count - 1] = prev;
            }
            else
            {
                merged.Add(curr);
            }
        }

        return merged;
    }

    private static void WriteHunk(StringBuilder sb, string[] origLines, string[] modLines, Hunk hunk)
    {
        // Write hunk header: @@ -origStart,origCount +modStart,modCount @@
        sb.Append("@@ -");
        sb.Append(hunk.OrigStart + 1);
        sb.Append(',');
        sb.Append(hunk.OrigCount);
        sb.Append(" +");
        sb.Append(hunk.ModStart + 1);
        sb.Append(',');
        sb.Append(hunk.ModCount);
        sb.AppendLine(" @@");

        // Write the lines using simple alignment
        int oi = hunk.OrigStart;
        int mi = hunk.ModStart;
        int origEnd = hunk.OrigStart + hunk.OrigCount;
        int modEnd = hunk.ModStart + hunk.ModCount;

        while (oi < origEnd || mi < modEnd)
        {
            if (oi < origEnd && mi < modEnd && oi < origLines.Length && mi < modLines.Length
                && origLines[oi] == modLines[mi])
            {
                sb.Append(' ');
                sb.AppendLine(origLines[oi]);
                oi++;
                mi++;
            }
            else
            {
                // Write removed lines first, then added
                int removeStart = oi;
                while (oi < origEnd && oi < origLines.Length
                    && (mi >= modEnd || mi >= modLines.Length || origLines[oi] != modLines[mi]))
                {
                    // Check if this orig line matches any upcoming mod line
                    bool matchesLater = false;
                    for (int look = mi; look < modEnd && look < modLines.Length; look++)
                    {
                        if (origLines[oi] == modLines[look])
                        {
                            matchesLater = true;
                            break;
                        }
                    }
                    if (matchesLater)
                        break;

                    sb.Append('-');
                    sb.AppendLine(origLines[oi]);
                    oi++;
                }

                while (mi < modEnd && mi < modLines.Length
                    && (oi >= origEnd || oi >= origLines.Length || modLines[mi] != origLines[oi]))
                {
                    // Check if this mod line matches any upcoming orig line
                    bool matchesLater = false;
                    for (int look = oi; look < origEnd && look < origLines.Length; look++)
                    {
                        if (modLines[mi] == origLines[look])
                        {
                            matchesLater = true;
                            break;
                        }
                    }
                    if (matchesLater)
                        break;

                    sb.Append('+');
                    sb.AppendLine(modLines[mi]);
                    mi++;
                }

                // Safety: if neither pointer advanced, force advance to avoid infinite loop
                if (oi == removeStart && oi < origEnd && mi < modEnd)
                {
                    if (oi < origLines.Length)
                    {
                        sb.Append('-');
                        sb.AppendLine(origLines[oi]);
                        oi++;
                    }
                    if (mi < modLines.Length)
                    {
                        sb.Append('+');
                        sb.AppendLine(modLines[mi]);
                        mi++;
                    }
                }
            }
        }
    }
}
