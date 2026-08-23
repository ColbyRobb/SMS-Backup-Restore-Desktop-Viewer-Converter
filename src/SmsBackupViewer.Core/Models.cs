namespace SmsBackupViewer.Core;

public enum RecordKind { Sms, Mms, Call }

/// <summary>Direction of a message or call, normalised across the three record types.</summary>
public enum Direction { Unknown, Received, Sent, Draft, Outbox, Failed, Queued, Missed, Voicemail, Rejected, Blocked }

/// <summary>
/// One record from a backup. Typed fields cover the attributes the viewer interprets;
/// <see cref="Extras"/> holds every other attribute verbatim, so nothing in the file is lost
/// even when a phone or app version emits fields we have never seen.
/// </summary>
public sealed class MessageRecord
{
    public required RecordKind Kind { get; init; }

    /// <summary>Position of this record within its source file, counting from zero.</summary>
    public int Ordinal { get; init; }

    /// <summary>Java epoch milliseconds, exactly as stored.</summary>
    public long DateMs { get; init; }

    /// <summary>The address as written in the file, unmodified.</summary>
    public string? Address { get; init; }

    /// <summary>Normalised key used to group records into conversations.</summary>
    public string ThreadKey { get; init; } = "";

    public string? ContactName { get; init; }
    public Direction Direction { get; init; }

    /// <summary>The raw type code, kept because new codes appear over time.</summary>
    public int? TypeCode { get; init; }

    /// <summary>SMS body, or the concatenated text parts of an MMS. Never altered.</summary>
    public string Body { get; init; } = "";

    /// <summary>Call duration in seconds. Null for messages.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>True when this MMS is a delivery receipt (m_type 134) rather than a message.</summary>
    public bool IsDeliveryReceipt { get; init; }

    /// <summary>The sender of a received group or RCS message, from the type=137 addr element.</summary>
    public string? GroupSender { get; init; }

    public IReadOnlyList<MmsPart> Parts { get; init; } = [];
    public IReadOnlyList<MmsAddress> Addresses { get; init; } = [];

    /// <summary>Attributes present on the record that the viewer does not interpret.</summary>
    public IReadOnlyDictionary<string, string> Extras { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Every attribute that was on the element, including the interpreted ones.</summary>
    public IReadOnlyDictionary<string, string> AllAttributes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(DateMs);
    public bool IsOutgoing => Direction is Direction.Sent or Direction.Outbox or Direction.Queued or Direction.Draft;
    public bool HasAttachments => Parts.Any(p => p.IsAttachment);
}

/// <summary>One &lt;part&gt; of an MMS.</summary>
public sealed class MmsPart
{
    public int? Seq { get; init; }
    public string? ContentType { get; init; }

    /// <summary>The content location, the closest thing the format has to a filename.</summary>
    public string? FileName { get; init; }

    /// <summary>Text content, for text/plain parts.</summary>
    public string? Text { get; init; }

    /// <summary>Where the decoded payload was written, when media extraction was enabled.</summary>
    public string? MediaPath { get; set; }

    /// <summary>Size of the decoded payload in bytes.</summary>
    public long DecodedBytes { get; set; }

    /// <summary>SHA-256 of the decoded payload, computed while it was written.</summary>
    public string? Sha256 { get; set; }

    public IReadOnlyDictionary<string, string> AllAttributes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool IsSmil => string.Equals(ContentType, "application/smil", StringComparison.OrdinalIgnoreCase);
    public bool IsText => string.Equals(ContentType, "text/plain", StringComparison.OrdinalIgnoreCase);
    public bool IsAttachment => !IsSmil && !IsText;

    public bool IsImage => ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsVideo => ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsAudio => ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>A display name for the part, falling back to the content type.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FileName) && FileName != "null" ? FileName
        : !string.IsNullOrWhiteSpace(ContentType) ? ContentType
        : "attachment";
}

/// <summary>One &lt;addr&gt; of an MMS.</summary>
public sealed class MmsAddress
{
    public string Address { get; init; } = "";
    public int? Type { get; init; }

    public bool IsSender => Type == 137;
    public bool IsDeviceOwner => Address == BackupConstants.InsertAddressToken;

    public string RoleName => Type switch
    {
        129 => "BCC",
        130 => "CC",
        137 => "From",
        151 => "To",
        null => "Unknown",
        _ => $"Type {Type}",
    };
}

/// <summary>A set of records sharing a normalised address key.</summary>
public sealed class Conversation
{
    public required string Key { get; init; }

    /// <summary>Best contact name seen for this thread, or the address when there is none.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>A representative address as written in the file.</summary>
    public string DisplayAddress { get; set; } = "";

    public List<MessageRecord> Records { get; } = [];

    public long FirstDateMs { get; set; } = long.MaxValue;
    public long LastDateMs { get; set; } = long.MinValue;

    public int SmsCount { get; set; }
    public int MmsCount { get; set; }
    public int CallCount { get; set; }

    public int TotalCount => SmsCount + MmsCount + CallCount;
    public bool IsGroup => Key.Contains('~');
    public int AttachmentCount => Records.Sum(r => r.Parts.Count(p => p.IsAttachment));

    public DateTimeOffset FirstDate => DateTimeOffset.FromUnixTimeMilliseconds(FirstDateMs);
    public DateTimeOffset LastDate => DateTimeOffset.FromUnixTimeMilliseconds(LastDateMs);
}

public static class BackupConstants
{
    /// <summary>Placeholder the platform writes for the device owner's own address.</summary>
    public const string InsertAddressToken = "insert-address-token";

    /// <summary>Suffix Google Messages uses in place of real participants on some RCS threads.</summary>
    public const string RcsGoogleSuffix = "@rcs.google.com";

    /// <summary>Number of trailing digits compared when matching two addresses.</summary>
    public const int NumberComparisonLength = 8;
}
