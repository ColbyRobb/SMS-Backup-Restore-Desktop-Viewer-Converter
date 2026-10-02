namespace SmsBackupViewer.Core;

/// <summary>
/// Decides which conversations a set of search terms refers to.
///
/// A term can be a contact name, a phone number in any formatting, or a fragment of either.
/// Numbers match on the same last-eight-digits rule used for conversation grouping, so
/// "5558675309" finds a thread stored as "+1 555-867-5309".
///
/// A term may also use the wildcards described on <see cref="SearchPattern"/>: "Rob*n" finds
/// Robin and Robinson, and "555-???-5309" finds any 555 number ending 5309. A wildcard number
/// is compared against the full number with its formatting removed, rather than the last eight
/// digits, so the term can pin down the area code.
///
/// Matching is done per conversation rather than per record, because a contact name is only
/// present on some records — the same thread can have named and unnamed messages depending on
/// whether the contact existed in the phonebook when each backup was taken.
/// </summary>
public sealed class ContactMatcher(IEnumerable<string> terms, IEnumerable<string>? keys = null)
{
    private readonly string[] _terms = terms
        .Select(t => t.Trim())
        .Where(t => t.Length > 0)
        .ToArray();

    /// <summary>
    /// Exact conversation keys, used when the caller has already picked conversations from a
    /// scan. Unlike a search term this is unambiguous, which matters for group threads whose
    /// key is a tilde-joined list rather than a single number.
    /// </summary>
    private readonly HashSet<string> _keys = keys is null
        ? new HashSet<string>(StringComparer.Ordinal)
        : new HashSet<string>(keys, StringComparer.Ordinal);

    /// <summary>True when nothing was specified, meaning every conversation is wanted.</summary>
    public bool MatchesEverything => _terms.Length == 0 && _keys.Count == 0;

    /// <summary>
    /// Terms containing wildcards, each parsed twice: once as typed for names, and once with
    /// number formatting removed for comparison against phone numbers.
    /// </summary>
    private readonly (SearchPattern Text, SearchPattern Number)[] _wildcards = terms
        .Select(SearchPattern.Parse)
        .OfType<SearchPattern>()
        .Where(p => p.HasWildcards)
        .Select(p => (p, SearchPattern.ParseNumber(p.Text)!))
        .ToArray();

    /// <summary>Terms without wildcards, matched by the original substring and number rules.</summary>
    private readonly string[] _plainTerms = terms
        .Select(t => t.Trim())
        .Where(t => SearchPattern.Parse(t) is { HasWildcards: false })
        .ToArray();

    public IReadOnlyList<string> Terms => _terms;

    public bool Matches(Conversation conversation)
    {
        if (MatchesEverything) return true;
        if (_keys.Contains(conversation.Key)) return true;
        if (_wildcards.Any(w => MatchesPattern(conversation, w.Text, w.Number))) return true;
        return _plainTerms.Any(term => MatchesTerm(conversation, term));
    }

    /// <summary>Matches against a scan summary, which holds no records.</summary>
    public bool Matches(ContactSummary summary)
    {
        if (MatchesEverything) return true;
        if (_keys.Contains(summary.Key)) return true;

        foreach (var (text, number) in _wildcards)
        {
            if (text.IsMatch(summary.DisplayName) || text.IsMatch(summary.DisplayAddress)) return true;
            if (number.IsMatch(AddressKey.StripFormatting(summary.DisplayAddress))) return true;
        }

        foreach (var term in _plainTerms)
        {
            if (summary.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
            if (summary.DisplayAddress.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;

            var normalized = AddressKey.Normalize(term);
            if (normalized.Length == 0 || !normalized.Any(char.IsAsciiDigit)) continue;

            foreach (var participant in summary.Key.Split('~', StringSplitOptions.RemoveEmptyEntries))
                if (participant == normalized) return true;
        }

        return false;
    }

    private static bool MatchesPattern(Conversation conversation, SearchPattern pattern, SearchPattern number)
    {
        if (pattern.IsMatch(conversation.DisplayName)) return true;
        if (pattern.IsMatch(conversation.DisplayAddress)) return true;

        foreach (var record in conversation.Records)
            if (pattern.IsMatch(record.ContactName)) return true;

        bool IsNumberMatch(string? address) => number.IsMatch(AddressKey.StripFormatting(address));

        if (IsNumberMatch(conversation.DisplayAddress)) return true;
        foreach (var record in conversation.Records)
        {
            if (IsNumberMatch(record.Address) || IsNumberMatch(record.GroupSender)) return true;
            foreach (var addr in record.Addresses)
                if (IsNumberMatch(addr.Address)) return true;
        }

        return false;
    }

    private static bool MatchesTerm(Conversation conversation, string term)
    {
        // Name, as displayed and as stored on any individual record.
        if (conversation.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        if (conversation.DisplayAddress.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var record in conversation.Records)
        {
            if (record.ContactName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;
        }

        // Number, compared the same way conversations are grouped.
        var normalized = AddressKey.Normalize(term);
        if (normalized.Length == 0 || !normalized.Any(char.IsAsciiDigit)) return false;

        foreach (var participant in conversation.Key.Split('~', StringSplitOptions.RemoveEmptyEntries))
            if (participant == normalized) return true;

        foreach (var record in conversation.Records)
        {
            if (record.Address is { } address && AddressKey.Normalize(address) == normalized) return true;
            if (record.GroupSender is { } sender && AddressKey.Normalize(sender) == normalized) return true;
            foreach (var addr in record.Addresses)
                if (AddressKey.Normalize(addr.Address) == normalized) return true;
        }

        return false;
    }
}
