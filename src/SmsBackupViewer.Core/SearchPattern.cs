using System.Text;
using System.Text.RegularExpressions;

namespace SmsBackupViewer.Core;

/// <summary>
/// A search term typed into one of the search boxes, with optional wildcards.
///
/// <c>*</c> stands for any run of characters (including none) and <c>?</c> for exactly one
/// character. A backslash makes either literal, so <c>\?</c> finds a real question mark.
///
/// Like a plain search, a pattern only has to match somewhere inside the text, not the whole of
/// it: "pay*ment" finds "Did you make the payment?" and "pay the first installment". Matching
/// ignores case. A term without wildcards is a plain case-insensitive substring search, exactly
/// as before wildcards were supported.
/// </summary>
public sealed class SearchPattern
{
    private readonly string _literal;
    private readonly Regex? _regex;

    private SearchPattern(string text, string literal, Regex? regex)
    {
        Text = text;
        _literal = literal;
        _regex = regex;
    }

    /// <summary>The term as typed, trimmed.</summary>
    public string Text { get; }

    /// <summary>True when the term contains an unescaped <c>*</c> or <c>?</c>.</summary>
    public bool HasWildcards => _regex is not null;

    /// <summary>Parses a term, or returns null when it is blank.</summary>
    public static SearchPattern? Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return null;

        var literal = new StringBuilder(trimmed.Length);
        var regex = new StringBuilder(trimmed.Length + 8);
        var wildcards = false;
        var afterStar = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            var star = false;
            if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] is '*' or '?')
            {
                c = trimmed[++i];
                literal.Append(c);
                regex.Append(Regex.Escape(c.ToString()));
            }
            else if (c == '*')
            {
                wildcards = true;
                star = true;
                // Collapse runs of stars; "a**b" means the same as "a*b".
                if (!afterStar) regex.Append(".*");
            }
            else if (c == '?')
            {
                wildcards = true;
                regex.Append('.');
            }
            else
            {
                literal.Append(c);
                regex.Append(Regex.Escape(c.ToString()));
            }

            afterStar = star;
        }

        // NonBacktracking keeps matching linear in the text length however many stars are typed.
        var compiled = wildcards
            ? new Regex(regex.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline |
                RegexOptions.NonBacktracking)
            : null;

        return new SearchPattern(trimmed, literal.ToString(), compiled);
    }

    /// <summary>
    /// Parses a term for comparison against phone numbers: spaces, dashes and brackets are
    /// dropped from the term, and must likewise be dropped from the number with
    /// <see cref="AddressKey.StripFormatting"/>, so "555-*-5309" finds "+1 (555) 867-5309".
    /// </summary>
    public static SearchPattern? ParseNumber(string? text) => Parse(AddressKey.StripFormatting(text));

    public bool IsMatch(string? input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        return _regex?.IsMatch(input) ?? input.Contains(_literal, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Text;
}
