using System;
using System.Text.RegularExpressions;

namespace Babel.Player.Services;

/// <summary>
/// Pure C# implementation of Word Error Rate (WER) and Character Error Rate (CER)
/// using Levenshtein edit distance.
///
/// No external Python packages required — this intentionally avoids a jiwer dependency
/// so that WER computation works in CI and on machines without the Python inference venv.
///
/// Reference formula:
///   WER = (S + D + I) / N   where N = number of words in the reference
///   CER = edit_distance(ref_chars, hyp_chars) / len(ref_chars)
/// </summary>
public static class WerComputer
{
    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the Word Error Rate between <paramref name="reference"/> and
    /// <paramref name="hypothesis"/>.
    /// Returns a value in [0, ∞) — values above 1.0 are possible when the hypothesis
    /// contains many insertions. Returns 0 when the reference is empty.
    /// </summary>
    public static double ComputeWer(string reference, string hypothesis)
    {
        var refWords = Tokenize(reference);
        var hypWords = Tokenize(hypothesis);

        if (refWords.Length == 0) return 0.0;

        var edits = EditDistance(refWords, hypWords);
        return (double)edits / refWords.Length;
    }

    /// <summary>
    /// Computes the Character Error Rate between <paramref name="reference"/> and
    /// <paramref name="hypothesis"/>.
    /// Returns 0 when the reference string is empty.
    /// </summary>
    public static double ComputeCer(string reference, string hypothesis)
    {
        var refNorm = Normalize(reference);
        var hypNorm = Normalize(hypothesis);

        if (refNorm.Length == 0) return 0.0;

        var edits = EditDistanceChars(refNorm, hypNorm);
        return (double)edits / refNorm.Length;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static string[] Tokenize(string text)
        => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Normalises text for comparison:
    ///   1. Lowercase
    ///   2. Strip punctuation (including ¿ ¡ , . ! ? etc.) so Spanish clips don't
    ///      accumulate false edit-distance hits against punctuation-free hypotheses
    ///   3. Collapse whitespace
    /// </summary>
    private static string Normalize(string text)
        => Regex.Replace(
            Regex.Replace(text.ToLowerInvariant().Trim(), @"[^\w\s]", " "),
            @"\s+", " ").Trim();

    /// <summary>Standard Levenshtein distance over string arrays (word tokens).</summary>
    private static int EditDistance(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;

        // Use two rows to keep O(n) space.
        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int j = 0; j <= n; j++) prev[j] = j;

        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= n; j++)
            {
                if (string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal))
                    curr[j] = prev[j - 1];
                else
                    curr[j] = 1 + Math.Min(prev[j - 1], Math.Min(prev[j], curr[j - 1]));
            }
            (prev, curr) = (curr, prev);
        }

        return prev[n];
    }

    /// <summary>Levenshtein distance over character sequences (for CER).</summary>
    private static int EditDistanceChars(string a, string b)
    {
        var m = a.Length;
        var n = b.Length;

        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int j = 0; j <= n; j++) prev[j] = j;

        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= n; j++)
            {
                curr[j] = a[i - 1] == b[j - 1]
                    ? prev[j - 1]
                    : 1 + Math.Min(prev[j - 1], Math.Min(prev[j], curr[j - 1]));
            }
            (prev, curr) = (curr, prev);
        }

        return prev[n];
    }
}
