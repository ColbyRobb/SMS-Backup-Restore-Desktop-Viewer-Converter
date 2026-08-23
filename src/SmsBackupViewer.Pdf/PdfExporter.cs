using System.Text;
using QuestPDF.Fluent;
using SmsBackupViewer.Core;

namespace SmsBackupViewer.Pdf;

/// <summary>
/// Writes conversations to PDF, and copies their attachments next to the document.
///
/// A PDF can show a photograph but cannot play a video, a voice note, or open a vCard. Those are
/// copied into an attachments folder and referenced from the transcript by name and relative
/// path, so the document stays complete and every reference points at a real file.
/// </summary>
public sealed class PdfExporter
{
    public PdfExportResult Export(
        PdfExportOptions options,
        IProgress<PdfExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PdfTheme.Initialise();

        var result = new PdfExportResult();
        Directory.CreateDirectory(options.OutputFolder);

        var conversations = options.Conversations
            .Where(c => c.Records.Count > 0)
            .OrderBy(c => c.FirstDateMs)
            .ToList();

        if (conversations.Count == 0) return result;

        // Attachments are copied first, so the document can reference paths that already exist.
        progress?.Report(new PdfExportProgress(0, conversations.Count, "", "copying attachments"));
        var links = CopyAttachments(conversations, options, result, cancellationToken);

        if (options.SingleDocument)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PdfExportProgress(0, conversations.Count, options.DocumentTitle, "rendering"));

            var path = UniquePath(options.OutputFolder, SafeName(options.DocumentTitle), ".pdf");
            new ConversationDocument(conversations, options, links, result).GeneratePdf(path);
            result.DocumentPaths.Add(path);
            result.DocumentBytes += new FileInfo(path).Length;

            progress?.Report(new PdfExportProgress(
                conversations.Count, conversations.Count, options.DocumentTitle, "done"));
        }
        else
        {
            for (var i = 0; i < conversations.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var conversation = conversations[i];
                progress?.Report(new PdfExportProgress(
                    i, conversations.Count, conversation.DisplayName, "rendering"));

                    var single = options.ForConversation(conversation);

                var name = SafeName(conversation.DisplayName);
                if (name.Length == 0) name = "conversation";
                var path = UniquePath(options.OutputFolder, name, ".pdf");

                new ConversationDocument([conversation], single, links, result).GeneratePdf(path);
                result.DocumentPaths.Add(path);
                result.DocumentBytes += new FileInfo(path).Length;
            }

            progress?.Report(new PdfExportProgress(
                conversations.Count, conversations.Count, "", "done"));
        }

        WriteAttachmentIndex(options, links, result);
        return result;
    }

    /// <summary>
    /// Copies each attachment beside the document, keeping one copy of identical payloads and
    /// naming files date-first so the folder reads chronologically.
    /// </summary>
    private static Dictionary<MmsPart, AttachmentLink> CopyAttachments(
        IReadOnlyList<Conversation> conversations,
        PdfExportOptions options,
        PdfExportResult result,
        CancellationToken cancellationToken)
    {
        var links = new Dictionary<MmsPart, AttachmentLink>();
        var folder = Path.Combine(options.OutputFolder, options.AttachmentsFolderName);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var conversation in conversations)
        foreach (var record in conversation.Records)
        foreach (var part in record.Parts)
        {
            if (!part.IsAttachment) continue;
            cancellationToken.ThrowIfCancellationRequested();

            var source = part.MediaPath;
            if (source is null || !File.Exists(source))
            {
                // The backup was opened without media loading, so there is nothing to copy.
                // The card still names the attachment rather than pretending it was not there.
                links[part] = new AttachmentLink(part, null, null);
                result.AttachmentsMissing++;
                continue;
            }

            var wanted = options.CopyAttachments && (options.CopyImagesToo || !part.IsImage);
            if (!wanted)
            {
                // Not copied, but still needed on disk to draw the photograph inline.
                links[part] = new AttachmentLink(part, null, source);
                continue;
            }

            // The viewer stores media under a content-hash name, so the same payload reaching
            // here twice is the same file and needs copying only once.
            if (bySource.TryGetValue(source, out var already))
            {
                links[part] = new AttachmentLink(part,
                    $"{options.AttachmentsFolderName}/{Path.GetFileName(already)}", already);
                continue;
            }

            Directory.CreateDirectory(folder);
            var target = UniquePath(folder, BuildName(record, part, options), Path.GetExtension(source), used);

            try
            {
                File.Copy(source, target, overwrite: true);
            }
            catch (IOException)
            {
                links[part] = new AttachmentLink(part, null, source);
                result.AttachmentsMissing++;
                continue;
            }

            bySource[source] = target;
            result.AttachmentsCopied++;
            result.AttachmentBytes += new FileInfo(target).Length;

            links[part] = new AttachmentLink(part,
                $"{options.AttachmentsFolderName}/{Path.GetFileName(target)}", target);
        }

        result.AttachmentsFolder = Directory.Exists(folder) ? folder : "";
        return links;
    }

    /// <summary>A companion listing, so the attachments folder can be read without the PDF.</summary>
    private static void WriteAttachmentIndex(
        PdfExportOptions options,
        Dictionary<MmsPart, AttachmentLink> links,
        PdfExportResult result)
    {
        if (result.AttachmentsCopied == 0 || !options.WriteAttachmentIndex) return;

        var rows = new StringBuilder();
        rows.AppendLine("file,content_type,bytes,original_name");

        foreach (var link in links.Values.Where(l => l.RelativePath is not null).DistinctBy(l => l.RelativePath))
        {
            rows.AppendLine(string.Join(',',
                Csv(link.RelativePath!),
                Csv(link.Part.ContentType ?? ""),
                link.Part.DecodedBytes.ToString(),
                Csv(link.Part.FileName ?? "")));
        }

        File.WriteAllText(Path.Combine(options.OutputFolder, "attachments.csv"), rows.ToString());
    }

    private static string BuildName(MessageRecord record, MmsPart part, PdfExportOptions options)
    {
        var stamp = record.DateMs > 0
            ? TimeZoneInfo.ConvertTime(record.Timestamp, options.TimeZone).ToString("yyyy-MM-dd_HHmmss")
            : "undated";

        var original = part.FileName;
        if (string.IsNullOrWhiteSpace(original) || original == "null")
            original = "attachment";

        return $"{stamp}_{SafeName(Path.GetFileNameWithoutExtension(original))}";
    }

    private static string UniquePath(string folder, string stem, string extension,
        HashSet<string>? used = null)
    {
        Directory.CreateDirectory(folder);
        var candidate = Path.Combine(folder, stem + extension);

        if ((used?.Add(candidate.ToLowerInvariant()) ?? true) && !File.Exists(candidate))
            return candidate;

        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem}_{i}{extension}");
            if ((used?.Add(candidate.ToLowerInvariant()) ?? true) && !File.Exists(candidate))
                return candidate;
        }
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var name = builder.ToString().Trim().TrimEnd('.');
        return name.Length > 80 ? name[..80].TrimEnd() : name;
    }

    private static string Csv(string value)
    {
        if (value.Length == 0) return "";
        var needsQuotes = value.Contains(',') || value.Contains('"');
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }
}
