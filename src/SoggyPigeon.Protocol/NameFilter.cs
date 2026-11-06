using System.Text.RegularExpressions;

namespace SoggyPigeon.Protocol;

/// <summary>Screen-name content filter (owner request 2026-11-06). Checked in
/// BOTH places: the app rejects the name at edit time with a clear message,
/// and the server rejects it at Hello (a modified client could bypass the
/// app — the server check is the one that actually matters).
/// A word list is a filter, not a guarantee: leetspeak beyond the simple
/// fold below, other languages, and rare real names that contain a banned
/// substring (Dickens…) are blind spots. Starter list: hard profanity and
/// the most serious slurs.</summary>
public static class NameFilter
{
    /// <summary>Matched against the normalized name. Each word becomes a
    /// stutter-tolerant regex ("fuck" → f+u+c+k) so "fuuuck" is caught while
    /// "jason" is NOT caught by "ass" — a single s fails the second s+ run.</summary>
    private static readonly string[] Banned =
    {
        "fuck", "shit", "bitch", "asshole", "cunt", "dick", "cock", "pussy",
        "whore", "slut", "wank", "twat", "rape", "rapist",
        "nigger", "nigga", "faggot", "kike", "spic", "chink",
        "tranny", "retard", "wetback",
        "nazi", "hitler",
    };

    /// <summary>Common leetspeak: digits and symbol stand-ins fold to the
    /// letter they visually replace before matching.</summary>
    private static readonly Dictionary<char, char> Leet = new()
    {
        ['0'] = 'o', ['1'] = 'i', ['3'] = 'e', ['4'] = 'a',
        ['5'] = 's', ['7'] = 't', ['8'] = 'b', ['@'] = 'a',
        ['$'] = 's', ['!'] = 'i', ['+'] = 't',
    };

    private static readonly Lazy<Regex[]> Patterns = new(() =>
        Banned.Select(w => new Regex(
            string.Concat(w.Select(c => Regex.Escape(c.ToString()) + "+")),
            RegexOptions.Compiled)).ToArray());

    /// <summary>Lower-case, fold leet stand-ins, drop everything that is not
    /// a letter — so "a$$" becomes "ass", "sh1t" becomes "shit", and "f.uck"
    /// becomes "fuck" before the patterns run.</summary>
    public static string Normalize(string name)
    {
        var chars = name.ToLowerInvariant().Select(c =>
            Leet.TryGetValue(c, out var folded) ? folded : c)
            .Where(char.IsLetter);
        return new string(chars.ToArray());
    }

    public static bool IsOffensive(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = Normalize(name);
        return Patterns.Value.Any(p => p.IsMatch(normalized));
    }
}
