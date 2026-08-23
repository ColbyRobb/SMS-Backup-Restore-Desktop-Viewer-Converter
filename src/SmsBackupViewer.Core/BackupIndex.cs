using System.Security.Cryptography;

namespace SmsBackupViewer.Core;

/// <summary>Provenance of one loaded file, shown in the UI and carried onto exports.</summary>
public sealed class SourceFile
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset? BackupTaken { get; init; }
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.Now;
    public int RecordCount { get; set; }
    public int SmsCount { get; set; }
    public int MmsCount { get; set; }
    public int CallCount { get; set; }
    public int AttachmentCount { get; set; }
    public long AttachmentBytes { get; set; }
}

/// <summary>
/// Everything loaded from one or more backup files, grouped into conversations.
///
/// Records are held in memory; attachment payloads are not — those are written to a media
/// folder during the read and referenced by path.
/// </summary>
public sealed class BackupIndex
{
    private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

    public List<SourceFile> Sources { get; } = [];
    public List<MessageRecord> Records { get; } = [];
    public IReadOnlyCollection<Conversation> Conversations => _conversations.Values;

    /// <summary>Records with no usable address, which cannot be placed in any conversation.</summary>
    public int UnaddressedRecords { get; private set; }

    public int SmsCount => Records.Count(r => r.Kind == RecordKind.Sms);
    public int MmsCount => Records.Count(r => r.Kind == RecordKind.Mms);
    public int CallCount => Records.Count(r => r.Kind == RecordKind.Call);

    public DateTimeOffset? FirstDate => Records.Count == 0
        ? null
        : DateTimeOffset.FromUnixTimeMilliseconds(Records.Where(r => r.DateMs > 0).Min(r => r.DateMs));

    public DateTimeOffset? LastDate => Records.Count == 0
        ? null
        : DateTimeOffset.FromUnixTimeMilliseconds(Records.Max(r => r.DateMs));

    public void Add(MessageRecord record)
    {
        Records.Add(record);

        var key = record.ThreadKey;
        if (string.IsNullOrEmpty(key))
        {
            // Keep it rather than discard it — a record with no address is still evidence.
            key = "(unknown)";
            UnaddressedRecords++;
        }

        if (!_conversations.TryGetValue(key, out var conversation))
        {
            conversation = new Conversation
            {
                Key = key,
                DisplayAddress = record.Address ?? "(no address)",
                DisplayName = "",
            };
            _conversations[key] = conversation;
        }

        conversation.Records.Add(record);

        if (record.DateMs > 0)
        {
            if (record.DateMs < conversation.FirstDateMs) conversation.FirstDateMs = record.DateMs;
            if (record.DateMs > conversation.LastDateMs) conversation.LastDateMs = record.DateMs;
        }

        switch (record.Kind)
        {
            case RecordKind.Sms: conversation.SmsCount++; break;
            case RecordKind.Mms: conversation.MmsCount++; break;
            case RecordKind.Call: conversation.CallCount++; break;
        }

        // Prefer a real contact name over a placeholder, and a longer address over a shorter one
        // (a number with a country code is more useful than the same number without).
        var name = record.ContactName;
        if (!string.IsNullOrWhiteSpace(name) && name != "(Unknown)" && name != "null" &&
            (conversation.DisplayName.Length == 0 || conversation.DisplayName == "(Unknown)"))
        {
            conversation.DisplayName = name;
        }

        if (record.Address is { Length: > 0 } address && address.Length > conversation.DisplayAddress.Length)
            conversation.DisplayAddress = address;
    }

    /// <summary>Sorts each conversation's records by date. Call once after loading.</summary>
    public void Finalise()
    {
        foreach (var conversation in _conversations.Values)
        {
            conversation.Records.Sort((a, b) => a.DateMs.CompareTo(b.DateMs));
            if (conversation.DisplayName.Length == 0)
                conversation.DisplayName = AddressKey.Display(conversation.DisplayAddress);
            if (conversation.FirstDateMs == long.MaxValue) conversation.FirstDateMs = 0;
            if (conversation.LastDateMs == long.MinValue) conversation.LastDateMs = 0;
        }
        Records.Sort((a, b) => a.DateMs.CompareTo(b.DateMs));
    }

    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
