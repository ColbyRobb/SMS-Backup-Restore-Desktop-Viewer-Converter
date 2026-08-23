using SmsBackupViewer.Core;

namespace SmsBackupViewer.App.ViewModels;

/// <summary>One row in the record inspector.</summary>
public sealed class DetailRow
{
    public required string Name { get; init; }
    public string Value { get; init; } = "";
    public bool IsHeader { get; init; }
    public bool IsUninterpreted { get; init; }
}

/// <summary>
/// Presents one record for display. The underlying <see cref="MessageRecord"/> is never
/// modified — everything here is formatting.
/// </summary>
public sealed class RecordViewModel(MessageRecord record)
{
    public MessageRecord Record { get; } = record;

    public bool IsOutgoing => Record.IsOutgoing;
    public bool IsCall => Record.Kind == RecordKind.Call;
    public bool IsSystemNote => Record.IsDeliveryReceipt;

    public string TimeText => Record.DateMs > 0
        ? Record.Timestamp.ToLocalTime().ToString("d MMM yyyy, h:mm:ss tt")
        : "(no timestamp)";

    public string ShortTimeText => Record.DateMs > 0
        ? Record.Timestamp.ToLocalTime().ToString("d MMM yyyy, h:mm tt")
        : "(no timestamp)";

    public string DirectionText => Record.Direction switch
    {
        Direction.Received => "Received",
        Direction.Sent => "Sent",
        Direction.Draft => "Draft",
        Direction.Outbox => "Outbox",
        Direction.Failed => "Failed",
        Direction.Queued => "Queued",
        Direction.Missed => "Missed",
        Direction.Voicemail => "Voicemail",
        Direction.Rejected => "Rejected",
        Direction.Blocked => "Blocked",
        _ => Record.TypeCode is { } code ? $"Unknown (code {code})" : "Unknown",
    };

    public string KindText => Record.Kind switch
    {
        RecordKind.Sms => "SMS",
        RecordKind.Mms => "MMS",
        _ => "Call",
    };

    /// <summary>Header line above a bubble: who, and when.</summary>
    public string SenderText
    {
        get
        {
            if (!string.IsNullOrEmpty(Record.GroupSender)) return Record.GroupSender;
            if (Record.IsOutgoing) return "You";
            if (!string.IsNullOrWhiteSpace(Record.ContactName) && Record.ContactName != "(Unknown)")
                return Record.ContactName!;
            return AddressKey.Display(Record.Address);
        }
    }

    public string BodyText
    {
        get
        {
            if (Record.IsDeliveryReceipt) return "Delivery receipt";
            if (IsCall) return CallSummary;
            if (!string.IsNullOrEmpty(Record.Body)) return Record.Body;
            return Record.Parts.Any(p => p.IsAttachment) ? "" : "(no text)";
        }
    }

    public bool HasBodyText => BodyText.Length > 0;

    private string CallSummary
    {
        get
        {
            var seconds = Record.DurationSeconds ?? 0;
            if (seconds <= 0) return $"{DirectionText} call";
            var span = TimeSpan.FromSeconds(seconds);
            var duration = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
                : span.TotalMinutes >= 1
                    ? $"{span.Minutes}m {span.Seconds}s"
                    : $"{span.Seconds}s";
            return $"{DirectionText} call • {duration}";
        }
    }

    /// <summary>Attachments worth showing: everything except SMIL layout and plain text.</summary>
    public IReadOnlyList<MmsPart> Attachments =>
        _attachments ??= Record.Parts.Where(p => p.IsAttachment).ToList();
    private IReadOnlyList<MmsPart>? _attachments;

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// Every attribute the file carried for this record, interpreted fields first and
    /// uninterpreted ones marked, so nothing in the backup is hidden.
    /// </summary>
    public IReadOnlyList<DetailRow> Details => _details ??= BuildDetails();
    private IReadOnlyList<DetailRow>? _details;

    private List<DetailRow> BuildDetails()
    {
        var rows = new List<DetailRow>
        {
            new() { Name = "Record", IsHeader = true },
            new() { Name = "Type", Value = KindText },
            new() { Name = "Direction", Value = DirectionText },
            new() { Name = "Raw type code", Value = Record.TypeCode?.ToString() ?? "(absent)" },
            new() { Name = "Date (local)", Value = TimeText },
            new() { Name = "Date (UTC)", Value = Record.DateMs > 0
                ? Record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "(absent)" },
            new() { Name = "Date (raw ms)", Value = Record.DateMs.ToString() },
            new() { Name = "Address", Value = Record.Address ?? "(absent)" },
            new() { Name = "Conversation key", Value = Record.ThreadKey },
            new() { Name = "Contact name", Value = Record.ContactName ?? "(absent)" },
        };

        if (Record.DurationSeconds is { } duration)
            rows.Add(new DetailRow { Name = "Duration (seconds)", Value = duration.ToString() });
        if (Record.GroupSender is { } sender)
            rows.Add(new DetailRow { Name = "Group sender", Value = sender });
        if (Record.IsDeliveryReceipt)
            rows.Add(new DetailRow { Name = "Delivery receipt", Value = "yes (m_type 134)" });

        // Everything the file actually contained, verbatim.
        if (Record.AllAttributes.Count > 0)
        {
            rows.Add(new DetailRow { Name = "All attributes in the file", IsHeader = true });
            foreach (var (name, value) in Record.AllAttributes.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                rows.Add(new DetailRow
                {
                    Name = name,
                    Value = value,
                    // Flag anything the viewer does not interpret, so it is visibly preserved
                    // rather than silently ignored.
                    IsUninterpreted = Record.Extras.ContainsKey(name),
                });
            }
        }

        if (Record.Addresses.Count > 0)
        {
            rows.Add(new DetailRow { Name = "Addresses", IsHeader = true });
            foreach (var address in Record.Addresses)
            {
                var label = address.IsDeviceOwner ? $"{address.Address}  (this device)" : address.Address;
                rows.Add(new DetailRow { Name = address.RoleName, Value = label });
            }
        }

        if (Record.Parts.Count > 0)
        {
            rows.Add(new DetailRow { Name = "Parts", IsHeader = true });
            foreach (var part in Record.Parts.OrderBy(p => p.Seq ?? int.MaxValue))
            {
                var description = part.ContentType ?? "(no content type)";
                if (part.DecodedBytes > 0) description += $"  •  {FormatBytes(part.DecodedBytes)}";
                if (part.IsSmil) description += "  •  layout metadata";
                rows.Add(new DetailRow { Name = $"seq {part.Seq?.ToString() ?? "-"}: {part.DisplayName}", Value = description });
            }
        }

        return rows;
    }

    public static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{value} B" : $"{size:F1} {units[unit]}";
    }
}
