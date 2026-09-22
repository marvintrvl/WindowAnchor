using System;
using System.Collections.Generic;

namespace WindowAnchor.Services;

internal static class WindowMatchTextSimilarity
{
    internal static double Calculate(string? first, string? second)
    {
        string a = (first ?? "").ToLowerInvariant();
        string b = (second ?? "").ToLowerInvariant();
        if (a.Length < 2 || b.Length < 2) return string.Equals(a, b, StringComparison.Ordinal) ? 1 : 0;
        var grams = new Dictionary<string, int>();
        for (int i = 0; i < a.Length - 1; i++) { string gram = a.Substring(i, 2); grams[gram] = grams.TryGetValue(gram, out int count) ? count + 1 : 1; }
        int overlap = 0;
        for (int i = 0; i < b.Length - 1; i++) { string gram = b.Substring(i, 2); if (grams.TryGetValue(gram, out int count) && count > 0) { grams[gram] = count - 1; overlap++; } }
        return 2.0 * overlap / ((a.Length - 1) + (b.Length - 1));
    }
}
