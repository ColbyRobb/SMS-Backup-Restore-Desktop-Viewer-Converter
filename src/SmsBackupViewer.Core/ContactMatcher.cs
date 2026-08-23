namespace SmsBackupViewer.Core;

/// <summary>
/// Decides which conversations a set of search terms refers to.
///
/// A term can be a contact name, a phone number in any formatting, or a fragment of either.
/// Numbers match on the same last-eight-digits rule used for conversation grouping, so
/// "5558675309" finds a thread stored as "+1 555-867-5309".
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

    public IReadOnlyList<string> Terms => _terms;

    public bool Matches(Conversation conversation)
    {
        if (MatchesEverything) return true;
        if (_keys.Contains(conversation.Key)) return true;
        return _terms.Any(term => MatchesTerm(conversation, term));
    }

    /// <summary>Matches against a scan summary, which holds no records.</summary>
    public bool Matches(ContactSummary summary)
    {
        if (MatchesEverything) return true;
        if (_keys.Contains(summary.Key)) return true;

        foreach (var term in _terms)
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
