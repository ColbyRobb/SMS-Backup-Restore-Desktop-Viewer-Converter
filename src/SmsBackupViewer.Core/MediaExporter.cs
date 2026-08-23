using System.Globalization;
using System.Text;

namespace SmsBackupViewer.Core;

/// <summary>What kinds of attachment to pull out.</summary>
public enum MediaSelection { Images, AllMedia, Everything }

public sealed class ExportOptions
{
    public required IReadOnlyList<string> Files { get; init; }
    public required string OutputFolder { get; init; }
    public ContactMatcher Matcher { get; init; } = new([]);
    public MediaSelection Selection { get; init; } = MediaSelection.Images;
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary>Put each conversation's files in its own subfolder.</summary>
    public bool FolderPerContact { get; init; } = true;

    /// <summary>Leave files that are already on disk alone, so an interrupted run can continue.</summary>
    public bool Resume { get; init; }
}

public readonly record struct ExportProgress(
    int FileIndex,
    int FileCount,
    string FileName,
    string Phase,
    long BytesDone,
    long BytesTotal,
    int FilesWritten,
    int DuplicatesSkipped)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Min(1.0, (double)BytesDone / BytesTotal);
}

public sealed class ExportResult
{
    public int FilesScanned { get; set; }
    public int ConversationsMatched { get; set; }
    public int FilesWritten { get; set; }
    public int DuplicatesSkipped { get; set; }
    public int AlreadyPresent { get; set; }
    public int Failed { get; set; }
    public long BytesWritten { get; set; }
    public List<string> ContactFolders { get; } = [];
    public string ManifestPath { get; set; } = "";
}

/// <summary>
/// Pulls attachments out of a set of backups for chosen conversations.
///
/// Backups are processed one file at a time, so memory stays bounded by the largest single file
/// rather than by the size of the whole set.
///
/// Each file is read twice: once to work out which records are wanted, and once to decode only
/// those payloads. Two passes are needed because a record's conversation is not always knowable
/// while its attachments are being read — an RCS message's real participants appear in the addr
/// elements that follow its parts.
///
/// The important saving is cross-file: consecutive backups each contain the entire message
/// history, so the same photograph is present in most files in the set. Duplicates are therefore
/// identified from message identity during the scan, before any payload is decoded. Decoding
/// first and discarding afterwards would write the same bytes once per backup.
/// </summary>
public sealed class MediaExporter
{
    private const string UnknownThread = "(unknown)";

    /// <summary>One attachment, identified without reading its payload.</summary>
    private readonly record struct AttachmentKey(string Thread, long Date, int Seq, string Type, string Name);

    private readonly HashSet<AttachmentKey> _seen = [];
    private readonly Dictionary<string, string> _folderByThread = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _hashesByThread = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedPaths = new(StringComparer.OrdinalIgnoreCase);

    public ExportResult Export(
        ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ExportResult();
        var matchedThreads = new HashSet<string>(StringComparer.Ordinal);

        Directory.CreateDirectory(options.OutputFolder);

        var sizes = options.Files.Select(f => new FileInfo(f).Length).ToArray();
        var bytesTotal = sizes.Sum() * 2;   // each file is read twice
        long bytesDone = 0;

        var manifest = new StringBuilder();
        var manifestPath = Path.Combine(options.OutputFolder, "manifest.csv");
        var manifestExists = options.Resume && File.Exists(manifestPath);
        if (!manifestExists)
        {
            manifest.AppendLine("contact,conversation_key,date_local,direction,record_type," +
                                "file_written,content_type,bytes,original_name,source_backup");
        }

        using var extractor = new MediaExtractor(options.OutputFolder);

        for (var i = 0; i < options.Files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Captured per iteration: progress callbacks run asynchronously, and a for-loop
            // variable is shared across iterations, so a late callback would read the wrong file.
            var fileIndex = i;
            var fileSize = sizes[i];
            var file = options.Files[fileIndex];
            var name = Path.GetFileName(file);
            var startOfFile = bytesDone;

            void Report(string phase, long fileBytes) => progress?.Report(new ExportProgress(
                fileIndex + 1, options.Files.Count, name, phase,
                startOfFile + fileBytes, bytesTotal, result.FilesWritten, result.DuplicatesSkipped));

            // ---- Pass one: decide what to take, without decoding anything ----
            Report("scanning", 0);

            var index = new BackupIndex();

            // Where each wanted payload will be written, keyed by its position in this file.
            var targets = new Dictionary<(int Ordinal, int PartIndex), string>();
            var displayNames = new Dictionary<string, string>(StringComparer.Ordinal);

            var scanProgress = new Progress<ReadProgress>(p => Report("scanning", p.BytesRead));
            foreach (var record in new BackupReader(null, null, captureAttributes: false)
                         .Read(file, scanProgress, cancellationToken))
            {
                index.Add(record);
            }
            index.Finalise();
            result.FilesScanned++;
            bytesDone = startOfFile + fileSize;

            foreach (var conversation in index.Conversations)
            {
                if (!options.Matcher.Matches(conversation)) continue;
                matchedThreads.Add(conversation.Key);
                displayNames[conversation.Key] = conversation.DisplayName;

                // The first backup that shows this conversation decides its folder name, so a
                // contact renamed partway through the set does not end up split across folders.
                if (!_folderByThread.TryGetValue(conversation.Key, out var folder))
                {
                    folder = options.FolderPerContact
                        ? Path.Combine(options.OutputFolder, SafeFolderName(conversation))
                        : options.OutputFolder;
                    _folderByThread[conversation.Key] = folder;
                }

                foreach (var record in conversation.Records)
                {
                    if (!InRange(record, options)) continue;

                    for (var p = 0; p < record.Parts.Count; p++)
                    {
                        var part = record.Parts[p];
                        if (!Selected(part, options.Selection)) continue;

                        // The same message re-backed-up in a later file must not be decoded
                        // again. Deciding this here, from metadata alone, is what keeps the run
                        // proportional to the unique media rather than to the whole set.
                        var key = new AttachmentKey(
                            conversation.Key,
                            record.DateMs,
                            part.Seq ?? p,
                            part.ContentType ?? "",
                            part.FileName ?? "");

                        if (!_seen.Add(key))
                        {
                            result.DuplicatesSkipped++;
                            continue;
                        }

                        var target = ChooseName(folder, record, part);
                        if (options.Resume && File.Exists(target))
                        {
                            result.AlreadyPresent++;
                            continue;
                        }

                        targets[(record.Ordinal, p)] = target;
                    }
                }
            }

            // The scan's records are no longer needed; only the target list carries forward.
            index.Records.Clear();

            if (targets.Count == 0)
            {
                bytesDone = startOfFile + fileSize * 2;
                Report("nothing to take", fileSize * 2);
                continue;
            }

            // ---- Pass two: decode only the wanted payloads, in one read ----
            var extractProgress = new Progress<ReadProgress>(p =>
                Report("extracting", fileSize + p.BytesRead));

            var reader = new BackupReader(
                extractor,
                (ordinal, partIndex) => targets.GetValueOrDefault((ordinal, partIndex)),
                captureAttributes: false);

            foreach (var record in reader.Read(file, extractProgress, cancellationToken))
            {
                foreach (var part in record.Parts)
                {
                    if (part.MediaPath is null || part.Sha256 is null) continue;

                    var thread = record.ThreadKey.Length > 0 ? record.ThreadKey : UnknownThread;

                    // A byte-identical payload already kept for this contact: the same photo
                    // resent within the thread. Keep one copy.
                    if (!_hashesByThread.TryGetValue(thread, out var hashes))
                        _hashesByThread[thread] = hashes = new HashSet<string>(StringComparer.Ordinal);

                    if (!hashes.Add(part.Sha256))
                    {
                        TryDelete(part.MediaPath);
                        result.DuplicatesSkipped++;
                        continue;
                    }

                    result.FilesWritten++;
                    result.BytesWritten += part.DecodedBytes;

                    var folder = Path.GetDirectoryName(part.MediaPath)!;
                    if (!result.ContactFolders.Contains(folder)) result.ContactFolders.Add(folder);

                    manifest.AppendLine(string.Join(',',
                        Csv(displayNames.GetValueOrDefault(thread, thread)),
                        Csv(thread),
                        Csv(record.DateMs > 0
                            ? record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                            : ""),
                        Csv(record.Direction.ToString()),
                        Csv(record.Kind.ToString()),
                        Csv(Path.GetRelativePath(options.OutputFolder, part.MediaPath)),
                        Csv(part.ContentType ?? ""),
                        part.DecodedBytes.ToString(CultureInfo.InvariantCulture),
                        Csv(part.FileName ?? ""),
                        Csv(name)));
                }
            }

            bytesDone = startOfFile + fileSize * 2;
            Report("done", fileSize * 2);
        }

        result.ConversationsMatched = matchedThreads.Count;
        result.ManifestPath = manifestPath;

        if (manifest.Length > 0)
        {
            if (manifestExists) File.AppendAllText(manifestPath, manifest.ToString());
            else File.WriteAllText(manifestPath, manifest.ToString());
        }

        return result;
    }

    private static bool Selected(MmsPart part, MediaSelection selection) => selection switch
    {
        MediaSelection.Images => part.IsImage,
        MediaSelection.AllMedia => part.IsImage || part.IsVideo || part.IsAudio,
        _ => part.IsAttachment,
    };

    private static bool InRange(MessageRecord record, ExportOptions options)
    {
        if (record.DateMs <= 0) return true;   // undated records are kept rather than dropped
        if (options.From is { } from && record.DateMs < from.ToUnixTimeMilliseconds()) return false;
        if (options.To is { } to && record.DateMs > to.ToUnixTimeMilliseconds()) return false;
        return true;
    }

    /// <summary>Date-first names so a folder sorts chronologically in Explorer.</summary>
    private string ChooseName(string folder, MessageRecord record, MmsPart part)
    {
        var stamp = record.DateMs > 0
            ? record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd_HHmmss")
            : "undated";

        var original = part.FileName;
        if (string.IsNullOrWhiteSpace(original) || original == "null")
            original = "attachment" + MediaExtractor.Extension(part.ContentType, null);
        else if (!Path.HasExtension(original))
            original += MediaExtractor.Extension(part.ContentType, null);

        var name = $"{stamp}_{SafeName(original!)}";
        var candidate = Path.Combine(folder, name);
        if (_usedPaths.Add(candidate.ToLowerInvariant())) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem}_{i}{extension}");
            if (_usedPaths.Add(candidate.ToLowerInvariant())) return candidate;
        }
    }

    private static string SafeFolderName(Conversation conversation)
    {
        var name = SafeName(conversation.DisplayName);
        if (string.IsNullOrWhiteSpace(name)) name = "unknown";
        if (name.Length > 60) name = name[..60].TrimEnd();

        var suffix = conversation.Key.Length >= 4 ? conversation.Key[^4..] : conversation.Key;
        return string.IsNullOrEmpty(suffix) ? name : $"{name} ({suffix})";
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        return builder.ToString().Trim().TrimEnd('.');
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private static string Csv(string value)
    {
        if (value.Length == 0) return "";
        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var escaped = value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }
}
