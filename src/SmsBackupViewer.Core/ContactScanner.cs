namespace SmsBackupViewer.Core;

/// <summary>What a scan keeps per conversation: one of these per contact, never anything
/// per record, so a very large backup set can still be surveyed.</summary>
public sealed class ContactSummary
{
    public required string Key { get; init; }
    public string DisplayName { get; set; } = "";
    public string DisplayAddress { get; set; } = "";

    /// <summary>
    /// Counts are the largest seen in any single backup, not a total across the set. Each
    /// backup holds the whole history, so summing would multiply everything by the number of
    /// files.
    /// </summary>
    public int Attachments { get; set; }
    public int Records { get; set; }
    public int SmsCount { get; set; }
    public int MmsCount { get; set; }
    public int CallCount { get; set; }

    public long FirstDateMs { get; set; } = long.MaxValue;
    public long LastDateMs { get; set; }

    public bool IsGroup => Key.Contains('~');

    public string Label => DisplayName.Contains(DisplayAddress, StringComparison.OrdinalIgnoreCase)
        ? DisplayName
        : $"{DisplayName}  [{AddressKey.Display(DisplayAddress)}]";
}

public readonly record struct ScanProgress(int FileIndex, int FileCount, string FileName, double FileFraction)
{
    public double Fraction => FileCount <= 0
        ? 0
        : Math.Min(1.0, (FileIndex - 1 + FileFraction) / FileCount);
}

/// <summary>
/// Surveys a set of backups and reports which conversations they contain.
///
/// Files are read one at a time and reduced to a summary per contact, so memory stays bounded
/// by the largest single file rather than by the size of the set. Payloads are never decoded.
/// </summary>
public static class ContactScanner
{
    public static List<ContactSummary> Scan(
        IReadOnlyList<string> files,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var summaries = new Dictionary<string, ContactSummary>(StringComparer.Ordinal);

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileIndex = i;
            var name = Path.GetFileName(files[fileIndex]);
            progress?.Report(new ScanProgress(fileIndex + 1, files.Count, name, 0));

            var readProgress = new Progress<ReadProgress>(p =>
                progress?.Report(new ScanProgress(fileIndex + 1, files.Count, name, p.Fraction)));

            var index = new BackupIndex();
            foreach (var record in new BackupReader(null, null, captureAttributes: false)
                         .Read(files[fileIndex], readProgress, cancellationToken))
            {
                index.Add(record);
            }
            index.Finalise();

            foreach (var conversation in index.Conversations)
            {
                if (!summaries.TryGetValue(conversation.Key, out var summary))
                    summaries[conversation.Key] = summary = new ContactSummary { Key = conversation.Key };

                if (summary.DisplayName.Length == 0)
                    summary.DisplayName = conversation.DisplayName;
                if (conversation.DisplayAddress.Length > summary.DisplayAddress.Length)
                    summary.DisplayAddress = conversation.DisplayAddress;

                summary.Attachments = Math.Max(summary.Attachments, conversation.AttachmentCount);
                summary.Records = Math.Max(summary.Records, conversation.TotalCount);
                summary.SmsCount = Math.Max(summary.SmsCount, conversation.SmsCount);
                summary.MmsCount = Math.Max(summary.MmsCount, conversation.MmsCount);
                summary.CallCount = Math.Max(summary.CallCount, conversation.CallCount);

                if (conversation.FirstDateMs > 0 && conversation.FirstDateMs < summary.FirstDateMs)
                    summary.FirstDateMs = conversation.FirstDateMs;
                if (conversation.LastDateMs > summary.LastDateMs)
                    summary.LastDateMs = conversation.LastDateMs;
            }

            index.Records.Clear();
        }

        foreach (var summary in summaries.Values)
            if (summary.FirstDateMs == long.MaxValue) summary.FirstDateMs = 0;

        return summaries.Values
            .OrderByDescending(s => s.Attachments)
            .ThenByDescending(s => s.Records)
            .ToList();
    }

    /// <summary>Finds backup files under a folder, or returns the file itself.</summary>
    public static List<string> FindBackups(string path, bool recurse)
    {
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path)) return [];

        return Directory.GetFiles(path, "*.xml",
                recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
    }
}
