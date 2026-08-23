namespace SmsBackupViewer.Core;

/// <summary>
/// Turns the many ways one phone number can be written into a single grouping key.
///
/// The rule comes from SyncTech's own viewer: strip spaces, dashes and brackets, then compare
/// the last eight characters. That is what makes "+1 555-867-5309" and "5558675309" the same
/// conversation. Numbers shorter than eight characters (short codes) compare in full.
/// </summary>
public static class AddressKey
{
    /// <summary>Normalises a single address. Group addresses must go through <see cref="ThreadKey"/>.</summary>
    public static string Normalize(string? address)
    {
        if (string.IsNullOrEmpty(address)) return "";

        Span<char> buffer = address.Length <= 128 ? stackalloc char[address.Length] : new char[address.Length];
        var length = 0;
        foreach (var c in address)
        {
            if (c is ' ' or '-' or '(' or ')') continue;
            buffer[length++] = c;
        }

        var trimmed = buffer[..length];
        return trimmed.Length > BackupConstants.NumberComparisonLength
            ? new string(trimmed[^BackupConstants.NumberComparisonLength..])
            : new string(trimmed);
    }

    /// <summary>
    /// Builds the conversation key for an address, handling tilde-joined group threads.
    ///
    /// Participants are normalised individually and then sorted before being rejoined. The sort
    /// matters: the same group can appear with its participants in different orders across
    /// messages, and without sorting one conversation fragments into several.
    /// </summary>
    public static string ThreadKey(string? address)
    {
        if (string.IsNullOrEmpty(address)) return "";
        if (!address.Contains('~')) return Normalize(address);

        var participants = address
            .Split('~', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p != BackupConstants.InsertAddressToken)
            .Select(Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        return participants.Length switch
        {
            0 => "",
            1 => participants[0],
            _ => string.Join('~', participants),
        };
    }

    /// <summary>
    /// Builds a thread key from an MMS's &lt;addr&gt; elements, used when the address attribute is
    /// the @rcs.google.com placeholder rather than the real participants.
    /// </summary>
    public static string ThreadKeyFromAddresses(IEnumerable<MmsAddress> addresses)
    {
        var participants = addresses
            .Select(a => a.Address)
            .Where(a => !string.IsNullOrEmpty(a) && a != BackupConstants.InsertAddressToken)
            .Select(Normalize)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();

        return participants.Length == 0 ? "" : string.Join('~', participants);
    }

    /// <summary>True when the address is Google's RCS placeholder rather than a real number.</summary>
    public static bool IsRcsPlaceholder(string? address) =>
        address?.Contains(BackupConstants.RcsGoogleSuffix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Presents an address for display, expanding group threads readably.</summary>
    public static string Display(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "(no address)";
        return address.Contains('~')
            ? string.Join(", ", address.Split('~', StringSplitOptions.RemoveEmptyEntries))
            : address;
    }
}
