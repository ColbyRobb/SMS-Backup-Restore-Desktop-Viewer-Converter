using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SmsBackupViewer.Core;

namespace SmsBackupViewer.Pdf;

/// <summary>Where a part's file ended up, relative to the PDF.</summary>
public sealed record AttachmentLink(MmsPart Part, string? RelativePath, string? AbsolutePath);

/// <summary>
/// Renders conversations as a printed transcript: chat bubbles, sent to the right and received
/// to the left, with photographs shown inline.
///
/// A PDF cannot play video or audio, and cannot open a vCard. Those parts are drawn as reference
/// cards naming the file, its type and size, and the path it was copied to alongside the
/// document — so the transcript stays complete and each reference points at something real.
/// </summary>
public sealed class ConversationDocument(
    IReadOnlyList<Conversation> conversations,
    PdfExportOptions options,
    IReadOnlyDictionary<MmsPart, AttachmentLink> links,
    PdfExportResult result) : IDocument
{
    private readonly Dictionary<Conversation, List<MessageRecord>> _filtered = [];
    private int _recordNumber;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = options.DocumentTitle,
        Subject = options.Subtitle.Length > 0 ? options.Subtitle : options.Reference,
        Author = options.PreparedBy.Length > 0 ? options.PreparedBy : "SMS Backup Viewer",
        Creator = "SMS Backup Viewer",
        CreationDate = DateTimeOffset.Now,
    };

    public DocumentSettings GetSettings() => new()
    {
        CompressDocument = options.CompressDocument,
        ImageRasterDpi = options.ImageDpi,
        ImageCompressionQuality = options.ImageQuality switch
        {
            PdfImageQuality.Low => ImageCompressionQuality.Low,
            PdfImageQuality.Medium => ImageCompressionQuality.Medium,
            PdfImageQuality.VeryHigh => ImageCompressionQuality.VeryHigh,
            PdfImageQuality.Best => ImageCompressionQuality.Best,
            _ => ImageCompressionQuality.High,
        },
    };

    private PageSize Paper
    {
        get
        {
            var size = options.PaperSize switch
            {
                PdfPaperSize.Letter => PageSizes.Letter,
                PdfPaperSize.Legal => PageSizes.Legal,
                PdfPaperSize.A3 => PageSizes.A3,
                PdfPaperSize.A5 => PageSizes.A5,
                _ => PageSizes.A4,
            };
            return options.Orientation == PdfOrientation.Landscape ? size.Landscape() : size;
        }
    }

    private float Small => options.FontSize * 0.85f;
    private float Tiny => options.FontSize * 0.72f;

    public void Compose(IDocumentContainer container)
    {
        foreach (var conversation in conversations)
            _filtered[conversation] = Filtered(conversation).ToList();

        if (options.IncludeCoverPage)
        {
            container.Page(page =>
            {
                page.Size(Paper);
                page.Margin(options.Margin);
                page.DefaultTextStyle(t => t.FontFamily(PdfTheme.BodyFont)
                    .FontSize(options.FontSize).FontColor(PdfTheme.Text));
                page.Content().Element(CoverPage);
            });
        }

        container.Page(page =>
        {
            page.Size(Paper);
            page.Margin(options.Margin);
            page.DefaultTextStyle(t => t.FontFamily(PdfTheme.BodyFont)
                .FontSize(options.FontSize).FontColor(PdfTheme.Text));

            if (options.ShowRunningHeader) page.Header().Element(Header);
            page.Content().Element(Content);
            page.Footer().Element(Footer);
        });
    }

    // ------------------------------------------------------------------ cover

    private void CoverPage(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(0);

            column.Item().PaddingBottom(4).Text(options.DocumentTitle)
                .FontFamily(PdfTheme.HeadingFont).FontSize(options.FontSize * 2.6f)
                .SemiBold().FontColor(PdfTheme.Text);

            if (options.Subtitle.Length > 0)
            {
                column.Item().PaddingBottom(4).Text(options.Subtitle)
                    .FontFamily(PdfTheme.HeadingFont).FontSize(options.FontSize * 1.3f)
                    .FontColor(PdfTheme.Muted);
            }

            column.Item().PaddingTop(6).LineHorizontal(2).LineColor(PdfTheme.Brand);
            column.Item().PaddingTop(14).Element(CoverIdentity);
            column.Item().PaddingTop(12).Element(CoverScope);

            if (options.ListConversationsOnCover && conversations.Count > 0)
                column.Item().PaddingTop(12).Element(CoverConversations);

            if (options.ListSourcesOnCover && options.Sources.Count > 0)
                column.Item().PaddingTop(12).Element(CoverSources);

            column.Item().PaddingTop(12).Element(CoverExplanation);

            if (options.Notes.Length > 0)
                column.Item().PaddingTop(12).Element(CoverNotes);
        });
    }

    /// <summary>Who produced this, when, and under what reference.</summary>
    private void CoverIdentity(IContainer container)
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, options.TimeZone);
        var offset = options.TimeZone.GetUtcOffset(now);
        var offsetText = $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";

        var rows = new List<(string Label, string Value)>
        {
            ("Generated", $"{Format(now)}  ({options.TimeZone.Id}, {offsetText})"),
        };

        if (options.Reference.Length > 0) rows.Add(("Reference", options.Reference));
        if (options.PreparedBy.Length > 0) rows.Add(("Prepared by", options.PreparedBy));
        if (options.PreparedFor.Length > 0) rows.Add(("Prepared for", options.PreparedFor));

        rows.Add(("Produced with", "SMS Backup Viewer — offline reader for SMS Backup & Restore backups"));

        container.Element(c => LabelledRows(c, "Document", rows));
    }

    /// <summary>Exactly what is inside, and what was filtered out.</summary>
    private void CoverScope(IContainer container)
    {
        var records = _filtered.Values.SelectMany(r => r).ToList();
        var sms = records.Count(r => r.Kind == RecordKind.Sms);
        var mms = records.Count(r => r.Kind == RecordKind.Mms);
        var calls = records.Count(r => r.Kind == RecordKind.Call);
        var attachments = records.SelectMany(r => r.Parts).Count(p => p.IsAttachment);
        var images = records.SelectMany(r => r.Parts).Count(p => p.IsImage);

        var rows = new List<(string, string)>
        {
            ("Conversations", $"{_filtered.Count(kv => kv.Value.Count > 0):N0}"),
            ("Records", $"{records.Count:N0}  ({sms:N0} SMS, {mms:N0} MMS, {calls:N0} calls)"),
        };

        if (attachments > 0)
        {
            var bytes = records.SelectMany(r => r.Parts).Where(p => p.IsAttachment).Sum(p => p.DecodedBytes);
            rows.Add(("Attachments", $"{attachments:N0}  ({images:N0} images, {attachments - images:N0} other)" +
                                     (bytes > 0 ? $", {Bytes(bytes)}" : "")));
        }

        var dated = records.Where(r => r.DateMs > 0).ToList();
        if (dated.Count > 0)
        {
            var first = Convert(dated.Min(r => r.DateMs));
            var last = Convert(dated.Max(r => r.DateMs));
            var span = last - first;
            var spanText = span.TotalDays >= 365
                ? $"{span.TotalDays / 365.25:F1} years"
                : $"{span.TotalDays:F0} days";
            rows.Add(("Period covered", $"{first:d MMMM yyyy} to {last:d MMMM yyyy}  ({spanText})"));
        }

        var included = new List<string>();
        if (options.IncludeSms) included.Add("SMS");
        if (options.IncludeMms) included.Add("MMS");
        if (options.IncludeCalls) included.Add("calls");
        rows.Add(("Record types", included.Count == 0 ? "none" : string.Join(", ", included)));

        if (options.From is not null || options.To is not null)
        {
            var from = options.From is { } f ? Format(TimeZoneInfo.ConvertTime(f, options.TimeZone)) : "the beginning";
            var to = options.To is { } t ? Format(TimeZoneInfo.ConvertTime(t, options.TimeZone)) : "the end";
            rows.Add(("Date filter", $"{from} to {to}"));
        }

        if (!options.IncludeDeliveryReceipts) rows.Add(("Excluded", "delivery receipts"));

        rows.Add(("Order", options.SortOrder == PdfSortOrder.OldestFirst
            ? "oldest first" : "newest first"));
        rows.Add(("Timestamps", $"all shown in {options.TimeZone.DisplayName}"));

        container.Element(c => LabelledRows(c, "Contents", rows));
    }

    private void CoverConversations(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().Element(c => SectionTitle(c, "Conversations in this document"));

            column.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(4);
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(2);
                    columns.ConstantColumn(52);
                    columns.ConstantColumn(46);
                });

                foreach (var heading in new[] { "Contact", "Address", "Period", "Records", "Files" })
                {
                    table.Cell().Background(PdfTheme.Fill).Border(0.5f).BorderColor(PdfTheme.Border)
                        .Padding(4).Text(heading).FontSize(Tiny).SemiBold();
                }

                foreach (var (conversation, records) in _filtered.Where(kv => kv.Value.Count > 0)
                             .OrderBy(kv => kv.Key.FirstDateMs))
                {
                    var dated = records.Where(r => r.DateMs > 0).ToList();
                    var period = dated.Count > 0
                        ? $"{Convert(dated.Min(r => r.DateMs)):MMM yyyy} – {Convert(dated.Max(r => r.DateMs)):MMM yyyy}"
                        : "—";
                    var files = records.SelectMany(r => r.Parts).Count(p => p.IsAttachment);

                    Cell(table).Text(conversation.DisplayName).FontSize(Tiny);
                    Cell(table).Text(AddressKey.Display(conversation.DisplayAddress)).FontSize(Tiny);
                    Cell(table).Text(period).FontSize(Tiny);
                    Cell(table).AlignRight().Text($"{records.Count:N0}").FontSize(Tiny);
                    Cell(table).AlignRight().Text(files > 0 ? $"{files:N0}" : "—").FontSize(Tiny);
                }
            });
        });
    }

    /// <summary>The backups this came from, with hashes, so the document can be traced back.</summary>
    private void CoverSources(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().Element(c => SectionTitle(c, "Source backups"));
            column.Item().PaddingTop(2).Text(
                    "Each file is listed with a SHA-256 hash of its contents, so the backup this " +
                    "transcript came from can be identified exactly.")
                .FontSize(Tiny).FontColor(PdfTheme.Muted);

            foreach (var source in options.Sources)
            {
                column.Item().PaddingTop(6).Border(0.5f).BorderColor(PdfTheme.Border)
                    .Background(PdfTheme.Fill).Padding(6).Column(inner =>
                    {
                        inner.Item().Text(source.FileName).FontSize(Small).SemiBold();

                        var details = $"{Bytes(source.SizeBytes)} · {source.RecordCount:N0} records";
                        if (source.SmsCount + source.MmsCount + source.CallCount > 0)
                            details += $" ({source.SmsCount:N0} SMS, {source.MmsCount:N0} MMS, {source.CallCount:N0} calls)";
                        inner.Item().Text(details).FontSize(Tiny).FontColor(PdfTheme.Muted);

                        if (source.BackupTaken is { } taken)
                        {
                            inner.Item().Text($"Backup taken {Format(TimeZoneInfo.ConvertTime(taken, options.TimeZone))}")
                                .FontSize(Tiny).FontColor(PdfTheme.Muted);
                        }

                        inner.Item().Text($"Read {Format(TimeZoneInfo.ConvertTime(source.LoadedAt, options.TimeZone))}")
                            .FontSize(Tiny).FontColor(PdfTheme.Muted);

                        if (source.Sha256.Length > 0)
                        {
                            inner.Item().PaddingTop(2).Text($"SHA-256  {source.Sha256}")
                                .FontSize(Tiny * 0.92f).FontColor(PdfTheme.Muted);
                        }
                    });
            }
        });
    }

    private void CoverExplanation(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().Element(c => SectionTitle(c, "How to read this document"));

            var points = new List<string>
            {
                "Messages appear as bubbles. Messages you sent are on the right; messages you " +
                "received are on the left. Calls appear as centred markers showing direction and duration.",
                "Message text is reproduced exactly as stored in the backup, including spelling, " +
                "punctuation and emoji. Nothing has been corrected or reworded.",
                $"Every timestamp is shown in {options.TimeZone.DisplayName}. The backup stores times " +
                "as milliseconds since 1 January 1970 UTC; they have been converted for display only.",
            };

            if (options.CopyAttachments)
            {
                points.Add($"Photographs are shown in place. Video, audio and other files cannot be " +
                           $"displayed inside a PDF, so each is shown as a labelled reference and the file " +
                           $"itself is saved in the \"{options.AttachmentsFolderName}\" folder beside this " +
                           $"document. Keep that folder with the PDF for those references to resolve.");
            }
            else
            {
                points.Add("Photographs are shown in place. Video, audio and other files cannot be " +
                           "displayed inside a PDF and are shown as labelled references only, because " +
                           "attachments were not copied alongside this document.");
            }

            if (options.NumberRecords)
                points.Add("Each record carries a sequential number so an individual message can be cited.");

            points.Add("Attachments whose payload was not present in the backup are named but marked as not saved.");

            foreach (var point in points)
            {
                column.Item().PaddingTop(4).Row(row =>
                {
                    row.ConstantItem(12).Text("•").FontSize(Small).FontColor(PdfTheme.Brand);
                    row.RelativeItem().Text(point).FontSize(Small).LineHeight(1.3f);
                });
            }
        });
    }

    private void CoverNotes(IContainer container)
    {
        container.Border(0.5f).BorderColor(PdfTheme.Border).Background(PdfTheme.Fill)
            .Padding(8).Column(column =>
            {
                column.Item().Text("Notes").FontFamily(PdfTheme.HeadingFont)
                    .FontSize(Small).SemiBold();
                column.Item().PaddingTop(3).Text(options.Notes).FontSize(Small).LineHeight(1.3f);
            });
    }

    private void SectionTitle(IContainer container, string text)
    {
        container.BorderBottom(0.5f).BorderColor(PdfTheme.Border).PaddingBottom(3)
            .Text(text).FontFamily(PdfTheme.HeadingFont)
            .FontSize(options.FontSize * 1.05f).SemiBold();
    }

    private void LabelledRows(IContainer container, string title, IReadOnlyList<(string Label, string Value)> rows)
    {
        container.Column(column =>
        {
            column.Item().Element(c => SectionTitle(c, title));

            foreach (var (label, value) in rows)
            {
                column.Item().PaddingTop(3).Row(row =>
                {
                    row.ConstantItem(110).Text(label).FontSize(Small).FontColor(PdfTheme.Muted);
                    row.RelativeItem().Text(value).FontSize(Small);
                });
            }
        });
    }

    private static IContainer Cell(TableDescriptor table)
        => table.Cell().Border(0.5f).BorderColor(PdfTheme.Border).Padding(4);

    // --------------------------------------------------------------- content

    private void Header(IContainer container)
    {
        container.PaddingBottom(6).BorderBottom(1).BorderColor(PdfTheme.Border).PaddingBottom(5)
            .Row(row =>
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().Text(options.DocumentTitle)
                        .FontFamily(PdfTheme.HeadingFont).FontSize(options.FontSize * 1.15f).SemiBold();
                    column.Item().Text($"Times shown in {options.TimeZone.Id}")
                        .FontSize(Tiny).FontColor(PdfTheme.Muted);
                });

                if (options.ShowPageNumbers)
                {
                    row.ConstantItem(90).AlignRight().AlignMiddle().Text(text =>
                    {
                        text.DefaultTextStyle(t => t.FontSize(Tiny).FontColor(PdfTheme.Muted));
                        text.Span("Page ");
                        text.CurrentPageNumber();
                        text.Span(" of ");
                        text.TotalPages();
                    });
                }
            });
    }

    private void Footer(IContainer container)
    {
        container.PaddingTop(5).BorderTop(1).BorderColor(PdfTheme.Border).PaddingTop(4)
            .Row(row =>
            {
                row.RelativeItem()
                    .Text("Produced from SMS Backup & Restore XML backups. Content reproduced verbatim.")
                    .FontSize(Tiny * 0.95f).FontColor(PdfTheme.Muted);

                if (options.ShowPageNumbers && !options.ShowRunningHeader)
                {
                    row.ConstantItem(90).AlignRight().Text(text =>
                    {
                        text.DefaultTextStyle(t => t.FontSize(Tiny).FontColor(PdfTheme.Muted));
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                }
            });
    }

    private void Content(IContainer container)
    {
        container.PaddingVertical(8).Column(column =>
        {
            column.Spacing(0);
            var first = true;

            foreach (var conversation in conversations)
            {
                var records = _filtered[conversation];
                if (records.Count == 0) continue;

                result.ConversationsExported++;
                result.RecordsExported += records.Count;

                if (!first && options.PageBreakBetweenConversations) column.Item().PageBreak();
                else if (!first) column.Item().PaddingTop(18);
                first = false;

                column.Item().Element(c => ConversationHeader(c, conversation, records));

                foreach (var record in records)
                    column.Item().PaddingVertical(3).Element(c => Record(c, record));
            }
        });
    }

    private IEnumerable<MessageRecord> Filtered(Conversation conversation)
    {
        var records = conversation.Records.Where(record =>
        {
            var allowed = record.Kind switch
            {
                RecordKind.Sms => options.IncludeSms,
                RecordKind.Mms => options.IncludeMms,
                _ => options.IncludeCalls,
            };
            if (!allowed) return false;
            if (record.IsDeliveryReceipt && !options.IncludeDeliveryReceipts) return false;

            if (record.DateMs > 0)
            {
                if (options.From is { } from && record.DateMs < from.ToUnixTimeMilliseconds()) return false;
                if (options.To is { } to && record.DateMs > to.ToUnixTimeMilliseconds()) return false;
            }

            return true;
        });

        return options.SortOrder == PdfSortOrder.NewestFirst
            ? records.OrderByDescending(r => r.DateMs)
            : records.OrderBy(r => r.DateMs);
    }

    private void ConversationHeader(IContainer container, Conversation conversation, List<MessageRecord> records)
    {
        container.PaddingBottom(8).Column(column =>
        {
            column.Spacing(2);
            column.Item().Text(conversation.DisplayName)
                .FontFamily(PdfTheme.HeadingFont).FontSize(options.FontSize * 1.55f).SemiBold();

            var address = AddressKey.Display(conversation.DisplayAddress);
            if (!string.Equals(address, conversation.DisplayName, StringComparison.OrdinalIgnoreCase))
                column.Item().Text(address).FontSize(Small).FontColor(PdfTheme.Muted);

            var attachments = records.SelectMany(r => r.Parts).Count(p => p.IsAttachment);
            var summary = $"{records.Count:N0} record(s)";
            if (attachments > 0) summary += $" · {attachments:N0} attachment(s)";

            var dated = records.Where(r => r.DateMs > 0).ToList();
            if (dated.Count > 0)
                summary += $" · {Convert(dated.Min(r => r.DateMs)):d MMM yyyy} – {Convert(dated.Max(r => r.DateMs)):d MMM yyyy}";

            column.Item().Text(summary).FontSize(Tiny).FontColor(PdfTheme.Muted);
            column.Item().PaddingTop(5).LineHorizontal(1).LineColor(PdfTheme.Brand);
        });
    }

    private void Record(IContainer container, MessageRecord record)
    {
        _recordNumber++;

        if (record.Kind == RecordKind.Call)
        {
            CallMarker(container, record);
            return;
        }

        var outgoing = record.IsOutgoing;
        var aligned = outgoing ? container.AlignRight() : container.AlignLeft();

        aligned.MaxWidth(options.BubbleWidth)
            .Background(outgoing ? PdfTheme.BrandLight : PdfTheme.Fill)
            .Border(1).BorderColor(outgoing ? PdfTheme.Brand : PdfTheme.Border)
            .CornerRadius(6).Padding(8)
            .Column(column =>
            {
                column.Spacing(3);

                column.Item().Text(text =>
                {
                    text.DefaultTextStyle(t => t.FontSize(Tiny));
                    if (options.NumberRecords)
                        text.Span($"#{_recordNumber}  ").FontColor(PdfTheme.Muted);
                    text.Span(Sender(record)).SemiBold();
                    text.Span($"  ·  {Direction(record)}").FontColor(PdfTheme.Muted);
                });

                if (options.ShowAddresses && record.Address is { Length: > 0 } address)
                {
                    column.Item().Text(AddressKey.Display(address))
                        .FontSize(Tiny * 0.95f).FontColor(PdfTheme.Muted);
                }

                if (record.IsDeliveryReceipt)
                {
                    column.Item().Text("Delivery receipt").FontSize(Small).Italic().FontColor(PdfTheme.Muted);
                }
                else if (record.Body.Length > 0)
                {
                    column.Item().Text(record.Body).FontSize(options.FontSize).LineHeight(1.25f);
                }

                foreach (var part in record.Parts.Where(p => p.IsAttachment))
                    column.Item().PaddingTop(2).Element(c => Attachment(c, part));

                if (record.Body.Length == 0 && !record.IsDeliveryReceipt &&
                    !record.Parts.Any(p => p.IsAttachment))
                {
                    column.Item().Text("(no content)").FontSize(Small).Italic().FontColor(PdfTheme.Muted);
                }

                column.Item().Text(Timestamp(record)).FontSize(Tiny * 0.92f).FontColor(PdfTheme.Muted);
            });
    }

    private void CallMarker(IContainer container, MessageRecord record)
    {
        container.AlignCenter().Background(PdfTheme.White).Border(1).BorderColor(PdfTheme.Border)
            .CornerRadius(9).PaddingVertical(4).PaddingHorizontal(10)
            .Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(Small));
                if (options.NumberRecords)
                    text.Span($"#{_recordNumber}  ").FontColor(PdfTheme.Muted);
                text.Span(CallSummary(record)).SemiBold();
                text.Span($"  ·  {Timestamp(record)}").FontColor(PdfTheme.Muted);
            });
    }

    private void Attachment(IContainer container, MmsPart part)
    {
        links.TryGetValue(part, out var link);

        // Photographs are drawn in place; everything else gets a reference card, because a PDF
        // cannot play or open it.
        if (options.InlineImages && part.IsImage && link?.AbsolutePath is { } path && File.Exists(path))
        {
            container.Column(column =>
            {
                column.Spacing(2);
                column.Item().MaxHeight(options.MaxImageHeight).Image(path)
                    .FitArea().WithRasterDpi(options.ImageDpi);

                var caption = part.DisplayName;
                if (part.DecodedBytes > 0) caption += $" · {Bytes(part.DecodedBytes)}";
                if (link.RelativePath is { } relative) caption += $" · {relative}";
                column.Item().Text(caption).FontSize(Tiny * 0.92f).FontColor(PdfTheme.Muted);
            });
            result.ImagesEmbedded++;
            return;
        }

        container.Background(PdfTheme.White).Border(1).BorderColor(PdfTheme.Border)
            .CornerRadius(4).Padding(6).Column(column =>
            {
                column.Spacing(1);
                column.Item().Text(Label(part)).FontSize(Small).SemiBold();

                var details = part.ContentType ?? "unknown type";
                if (part.DecodedBytes > 0) details += $" · {Bytes(part.DecodedBytes)}";
                column.Item().Text(details).FontSize(Tiny).FontColor(PdfTheme.Muted);

                if (link?.RelativePath is { } relative)
                {
                    // A relative link resolves as long as the attachments folder travels with
                    // the PDF, which the cover page asks the reader to keep together.
                    column.Item().Hyperlink(relative)
                        .Text(relative).FontSize(Tiny).FontColor(PdfTheme.Link).Underline();
                }
                else
                {
                    column.Item().Text("Not saved — no decoded file was available for this attachment.")
                        .FontSize(Tiny * 0.92f).Italic().FontColor(PdfTheme.Muted);
                }
            });
    }

    private static string Label(MmsPart part)
    {
        var kind = part.IsVideo ? "Video" : part.IsAudio ? "Audio" : part.IsImage ? "Image" : "File";
        return $"{kind}: {part.DisplayName}";
    }

    private static string Sender(MessageRecord record)
    {
        if (!string.IsNullOrEmpty(record.GroupSender)) return record.GroupSender!;
        if (record.IsOutgoing) return "You";
        if (!string.IsNullOrWhiteSpace(record.ContactName) && record.ContactName != "(Unknown)")
            return record.ContactName!;
        return AddressKey.Display(record.Address);
    }

    private string Direction(MessageRecord record)
    {
        var name = record.Direction switch
        {
            Core.Direction.Received => "Received",
            Core.Direction.Sent => "Sent",
            Core.Direction.Draft => "Draft",
            Core.Direction.Outbox => "Outbox",
            Core.Direction.Failed => "Failed",
            Core.Direction.Queued => "Queued",
            Core.Direction.Missed => "Missed",
            Core.Direction.Voicemail => "Voicemail",
            Core.Direction.Rejected => "Rejected",
            Core.Direction.Blocked => "Blocked",
            _ => record.TypeCode is { } unknown ? $"Unknown (code {unknown})" : "Unknown",
        };

        return options.ShowRawTypeCodes && record.TypeCode is { } code && record.Direction != Core.Direction.Unknown
            ? $"{name} ({code})"
            : name;
    }

    private string CallSummary(MessageRecord record)
    {
        var seconds = record.DurationSeconds ?? 0;
        if (seconds <= 0) return $"{Direction(record)} call";

        var span = TimeSpan.FromSeconds(seconds);
        var duration = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
            : span.TotalMinutes >= 1
                ? $"{span.Minutes}m {span.Seconds}s"
                : $"{span.Seconds}s";
        return $"{Direction(record)} call · {duration}";
    }

    private DateTimeOffset Convert(long ms)
        => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), options.TimeZone);

    private string Timestamp(MessageRecord record)
        => record.DateMs > 0 ? Format(Convert(record.DateMs)) : "(no timestamp)";

    private string Format(DateTimeOffset value) => options.DateStyle switch
    {
        PdfDateStyle.Iso => value.ToString("yyyy-MM-dd HH:mm:ss"),
        PdfDateStyle.DayMonthYear => value.ToString("dd/MM/yyyy HH:mm:ss"),
        PdfDateStyle.MonthDayYear => value.ToString("MM/dd/yyyy h:mm:ss tt"),
        PdfDateStyle.Long => value.ToString("dddd, d MMMM yyyy 'at' HH:mm:ss"),
        _ => value.ToString("d MMM yyyy, HH:mm:ss"),
    };

    private static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{value} B" : $"{size:F1} {units[unit]}";
    }
}
