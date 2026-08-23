using SmsBackupViewer.Core;

namespace SmsBackupViewer.Pdf;

public enum PdfPaperSize { A4, Letter, Legal, A3, A5 }
public enum PdfOrientation { Portrait, Landscape }
public enum PdfSortOrder { OldestFirst, NewestFirst }
public enum PdfImageQuality { Low, Medium, High, VeryHigh, Best }

/// <summary>How timestamps are written in the transcript.</summary>
public enum PdfDateStyle
{
    /// <summary>7 Jun 2023, 14:30:22</summary>
    Default,
    /// <summary>2023-06-07 14:30:22</summary>
    Iso,
    /// <summary>07/06/2023 14:30:22</summary>
    DayMonthYear,
    /// <summary>06/07/2023 2:30:22 PM</summary>
    MonthDayYear,
    /// <summary>Wednesday, 7 June 2023 at 14:30:22</summary>
    Long,
}

public sealed class PdfExportOptions
{
    /// <summary>Conversations to export, already filtered by the caller.</summary>
    public required IReadOnlyList<Conversation> Conversations { get; init; }

    /// <summary>Folder to write the PDF and its attachments folder into.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>Provenance of the backups these records came from, printed on the cover page.</summary>
    public IReadOnlyList<SourceFile> Sources { get; init; } = [];

    // ---- Document ----

    /// <summary>One PDF for everything, or one per conversation.</summary>
    public bool SingleDocument { get; init; }

    /// <summary>Name used for the combined document.</summary>
    public string DocumentTitle { get; init; } = "Messages";

    /// <summary>Free text shown under the title on the cover page.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Free text identifying the matter, case or purpose. Blank hides the row.</summary>
    public string Reference { get; init; } = "";

    /// <summary>Who produced the document. Blank hides the row.</summary>
    public string PreparedBy { get; init; } = "";

    /// <summary>Who it was produced for. Blank hides the row.</summary>
    public string PreparedFor { get; init; } = "";

    /// <summary>Free text notes printed at the end of the cover page.</summary>
    public string Notes { get; init; } = "";

    // ---- Content ----

    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    public bool IncludeSms { get; init; } = true;
    public bool IncludeMms { get; init; } = true;
    public bool IncludeCalls { get; init; } = true;

    /// <summary>Delivery receipts carry no readable body; most transcripts read better without.</summary>
    public bool IncludeDeliveryReceipts { get; init; } = true;

    public PdfSortOrder SortOrder { get; init; } = PdfSortOrder.OldestFirst;

    /// <summary>Number each record sequentially, for referring to a specific message.</summary>
    public bool NumberRecords { get; init; }

    /// <summary>Print the raw type code beside the direction, e.g. "Sent (2)".</summary>
    public bool ShowRawTypeCodes { get; init; }

    /// <summary>Print the sender's address under their name on every bubble.</summary>
    public bool ShowAddresses { get; init; }

    /// <summary>Start each conversation on a fresh page in a combined document.</summary>
    public bool PageBreakBetweenConversations { get; init; } = true;

    // ---- Cover page ----

    public bool IncludeCoverPage { get; init; } = true;

    /// <summary>List every conversation in the document on the cover page.</summary>
    public bool ListConversationsOnCover { get; init; } = true;

    /// <summary>List the source backups and their SHA-256 hashes on the cover page.</summary>
    public bool ListSourcesOnCover { get; init; } = true;

    // ---- Appearance ----

    public PdfPaperSize PaperSize { get; init; } = PdfPaperSize.A4;
    public PdfOrientation Orientation { get; init; } = PdfOrientation.Portrait;

    /// <summary>Page margin in points.</summary>
    public float Margin { get; init; } = 36;

    /// <summary>Body text size in points. Everything else scales from it.</summary>
    public float FontSize { get; init; } = 9;

    /// <summary>Widest a message bubble may be, in points.</summary>
    public float BubbleWidth { get; init; } = 380;

    public PdfDateStyle DateStyle { get; init; } = PdfDateStyle.Default;

    /// <summary>Time zone every timestamp is rendered in, and which the cover page declares.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    public bool ShowPageNumbers { get; init; } = true;
    public bool ShowRunningHeader { get; init; } = true;

    // ---- Images and attachments ----

    /// <summary>Draw photographs inside the message bubbles.</summary>
    public bool InlineImages { get; init; } = true;

    /// <summary>Tallest an inline photograph may be, in points.</summary>
    public float MaxImageHeight { get; init; } = 260;

    /// <summary>Raster resolution for embedded photographs. Lower makes a smaller file.</summary>
    public int ImageDpi { get; init; } = 144;

    public PdfImageQuality ImageQuality { get; init; } = PdfImageQuality.High;

    /// <summary>
    /// Copy attachments next to the PDF. A PDF cannot play video or audio, so those are
    /// referenced by filename instead — without the folder, the references point at nothing.
    /// </summary>
    public bool CopyAttachments { get; init; } = true;

    /// <summary>Also copy photos that are already shown inline, so the originals are kept.</summary>
    public bool CopyImagesToo { get; init; } = true;

    /// <summary>Folder name, relative to the PDF, holding the copied attachments.</summary>
    public string AttachmentsFolderName { get; init; } = "attachments";

    /// <summary>Write attachments.csv listing what was copied.</summary>
    public bool WriteAttachmentIndex { get; init; } = true;

    /// <summary>Shrink the finished PDF. Costs a little time.</summary>
    public bool CompressDocument { get; init; } = true;

    /// <summary>Builds a copy of these options for a single conversation.</summary>
    public PdfExportOptions ForConversation(Conversation conversation) => new()
    {
        Conversations = [conversation],
        OutputFolder = OutputFolder,
        Sources = Sources,
        SingleDocument = true,
        DocumentTitle = conversation.DisplayName,
        Subtitle = Subtitle,
        Reference = Reference,
        PreparedBy = PreparedBy,
        PreparedFor = PreparedFor,
        Notes = Notes,
        From = From,
        To = To,
        IncludeSms = IncludeSms,
        IncludeMms = IncludeMms,
        IncludeCalls = IncludeCalls,
        IncludeDeliveryReceipts = IncludeDeliveryReceipts,
        SortOrder = SortOrder,
        NumberRecords = NumberRecords,
        ShowRawTypeCodes = ShowRawTypeCodes,
        ShowAddresses = ShowAddresses,
        PageBreakBetweenConversations = PageBreakBetweenConversations,
        IncludeCoverPage = IncludeCoverPage,
        ListConversationsOnCover = ListConversationsOnCover,
        ListSourcesOnCover = ListSourcesOnCover,
        PaperSize = PaperSize,
        Orientation = Orientation,
        Margin = Margin,
        FontSize = FontSize,
        BubbleWidth = BubbleWidth,
        DateStyle = DateStyle,
        TimeZone = TimeZone,
        ShowPageNumbers = ShowPageNumbers,
        ShowRunningHeader = ShowRunningHeader,
        InlineImages = InlineImages,
        MaxImageHeight = MaxImageHeight,
        ImageDpi = ImageDpi,
        ImageQuality = ImageQuality,
        CopyAttachments = CopyAttachments,
        CopyImagesToo = CopyImagesToo,
        AttachmentsFolderName = AttachmentsFolderName,
        WriteAttachmentIndex = WriteAttachmentIndex,
        CompressDocument = CompressDocument,
    };
}

public sealed class PdfExportResult
{
    public List<string> DocumentPaths { get; } = [];
    public int ConversationsExported { get; set; }
    public int RecordsExported { get; set; }
    public int ImagesEmbedded { get; set; }
    public int AttachmentsCopied { get; set; }
    public int AttachmentsMissing { get; set; }
    public long AttachmentBytes { get; set; }
    public long DocumentBytes { get; set; }
    public string AttachmentsFolder { get; set; } = "";
}

public readonly record struct PdfExportProgress(
    int ConversationIndex,
    int ConversationCount,
    string ConversationName,
    string Phase)
{
    public double Fraction => ConversationCount <= 0
        ? 0
        : Math.Min(1.0, (double)ConversationIndex / ConversationCount);
}
