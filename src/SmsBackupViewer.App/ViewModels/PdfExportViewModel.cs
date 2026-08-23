using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SmsBackupViewer.Core;
using SmsBackupViewer.Pdf;

namespace SmsBackupViewer.App.ViewModels;

/// <summary>One conversation offered for PDF export.</summary>
public sealed partial class ConversationChoice(Conversation conversation) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public Conversation Conversation { get; } = conversation;
    public string DisplayName => Conversation.DisplayName;
    public string AddressText => AddressKey.Display(Conversation.DisplayAddress);
    public int TotalCount => Conversation.TotalCount;

    public string CountsText
    {
        get
        {
            var attachments = Conversation.AttachmentCount;
            var text = $"{Conversation.TotalCount:N0} records";
            if (attachments > 0) text += $" · {attachments:N0} attachments";
            return text;
        }
    }

    public string RangeText => Conversation.LastDateMs > 0
        ? $"{Conversation.FirstDate.ToLocalTime():MMM yyyy} – {Conversation.LastDate.ToLocalTime():MMM yyyy}"
        : "";
}

/// <summary>A named choice bound to a combo box.</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public sealed partial class PdfExportViewModel : ObservableObject
{
    private readonly IReadOnlyList<SourceFile> _sources;
    private CancellationTokenSource? _cancellation;

    // ---- Output ----
    [ObservableProperty] private string _outputFolder = "";
    [ObservableProperty] private bool _singleDocument;
    [ObservableProperty] private string _documentTitle = "Messages";

    // ---- Cover page ----
    [ObservableProperty] private bool _includeCoverPage = true;
    [ObservableProperty] private bool _listConversationsOnCover = true;
    [ObservableProperty] private bool _listSourcesOnCover = true;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _preparedBy = "";
    [ObservableProperty] private string _preparedFor = "";
    [ObservableProperty] private string _notes = "";

    // ---- Content ----
    [ObservableProperty] private bool _includeSms = true;
    [ObservableProperty] private bool _includeMms = true;
    [ObservableProperty] private bool _includeCalls = true;
    [ObservableProperty] private bool _includeDeliveryReceipts = true;
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private bool _numberRecords;
    [ObservableProperty] private bool _showRawTypeCodes;
    [ObservableProperty] private bool _showAddresses;
    [ObservableProperty] private bool _pageBreakBetweenConversations = true;

    // ---- Appearance ----
    [ObservableProperty] private Choice<PdfPaperSize> _paperSize;
    [ObservableProperty] private Choice<PdfOrientation> _orientation;
    [ObservableProperty] private Choice<PdfSortOrder> _sortOrder;
    [ObservableProperty] private Choice<PdfDateStyle> _dateStyle;
    [ObservableProperty] private TimeZoneInfo _timeZone = TimeZoneInfo.Local;
    [ObservableProperty] private double _margin = 36;
    [ObservableProperty] private double _fontSize = 9;
    [ObservableProperty] private double _bubbleWidth = 380;
    [ObservableProperty] private bool _showPageNumbers = true;
    [ObservableProperty] private bool _showRunningHeader = true;

    // ---- Images and attachments ----
    [ObservableProperty] private bool _inlineImages = true;
    [ObservableProperty] private double _maxImageHeight = 260;
    [ObservableProperty] private double _imageDpi = 144;
    [ObservableProperty] private Choice<PdfImageQuality> _imageQuality;
    [ObservableProperty] private bool _copyAttachments = true;
    [ObservableProperty] private bool _copyImagesToo = true;
    [ObservableProperty] private string _attachmentsFolderName = "attachments";
    [ObservableProperty] private bool _writeAttachmentIndex = true;
    [ObservableProperty] private bool _compressDocument = true;

    // ---- State ----
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private bool _hasFinished;

    public ObservableCollection<ConversationChoice> Conversations { get; } = [];

    public IReadOnlyList<Choice<PdfPaperSize>> PaperSizes { get; } =
    [
        new("A4", PdfPaperSize.A4),
        new("Letter", PdfPaperSize.Letter),
        new("Legal", PdfPaperSize.Legal),
        new("A3", PdfPaperSize.A3),
        new("A5", PdfPaperSize.A5),
    ];

    public IReadOnlyList<Choice<PdfOrientation>> Orientations { get; } =
    [
        new("Portrait", PdfOrientation.Portrait),
        new("Landscape", PdfOrientation.Landscape),
    ];

    public IReadOnlyList<Choice<PdfSortOrder>> SortOrders { get; } =
    [
        new("Oldest first", PdfSortOrder.OldestFirst),
        new("Newest first", PdfSortOrder.NewestFirst),
    ];

    public IReadOnlyList<Choice<PdfDateStyle>> DateStyles { get; } =
    [
        new("7 Jun 2023, 14:30:22", PdfDateStyle.Default),
        new("2023-06-07 14:30:22", PdfDateStyle.Iso),
        new("07/06/2023 14:30:22", PdfDateStyle.DayMonthYear),
        new("06/07/2023 2:30:22 PM", PdfDateStyle.MonthDayYear),
        new("Wednesday, 7 June 2023 at 14:30:22", PdfDateStyle.Long),
    ];

    public IReadOnlyList<Choice<PdfImageQuality>> ImageQualities { get; } =
    [
        new("Low — smallest file", PdfImageQuality.Low),
        new("Medium", PdfImageQuality.Medium),
        new("High", PdfImageQuality.High),
        new("Very high", PdfImageQuality.VeryHigh),
        new("Best — largest file", PdfImageQuality.Best),
    ];

    public IReadOnlyList<TimeZoneInfo> TimeZones { get; } =
        TimeZoneInfo.GetSystemTimeZones().OrderBy(z => z.BaseUtcOffset).ToList();

    public PdfExportViewModel(
        IEnumerable<Conversation> conversations,
        IReadOnlyList<SourceFile> sources,
        Conversation? preselected,
        string? suggestedFolder)
    {
        _sources = sources;
        _paperSize = PaperSizes[0];
        _orientation = Orientations[0];
        _sortOrder = SortOrders[0];
        _dateStyle = DateStyles[0];
        _imageQuality = ImageQualities[2];

        foreach (var conversation in conversations.OrderByDescending(c => c.LastDateMs))
        {
            Conversations.Add(new ConversationChoice(conversation)
            {
                IsSelected = preselected is null || ReferenceEquals(conversation, preselected),
            });
        }

        if (!string.IsNullOrEmpty(suggestedFolder))
            OutputFolder = Path.Combine(suggestedFolder, "pdf-export");

        // A PDF can only show a photograph if the payload was decoded when the backup was opened.
        var attachments = Conversations.Sum(c => c.Conversation.AttachmentCount);
        var decoded = Conversations
            .SelectMany(c => c.Conversation.Records)
            .SelectMany(r => r.Parts)
            .Count(p => p.IsAttachment && p.MediaPath is not null);

        if (attachments > 0 && decoded == 0)
        {
            WarningText = "This backup was opened without \"Load images and media\", so attachments " +
                          "cannot be embedded or copied. They will still be named in the transcript. " +
                          "Reopen the backup with that box ticked to include them.";
        }

        UpdateSelectionStatus();
    }

    public bool CanRun => !IsBusy && OutputFolder.Length > 0 && Conversations.Any(c => c.IsSelected);

    partial void OnIsBusyChanged(bool value) => RaiseStates();
    partial void OnOutputFolderChanged(string value) => RaiseStates();

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(CanRun));
        ExportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to save the PDF and its attachments",
            Multiselect = false,
        };
        if (OutputFolder.Length > 0 && Directory.Exists(OutputFolder))
            dialog.InitialDirectory = OutputFolder;

        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var choice in Conversations) choice.IsSelected = true;
        UpdateSelectionStatus();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var choice in Conversations) choice.IsSelected = false;
        UpdateSelectionStatus();
    }

    [RelayCommand]
    private void ResetAppearance()
    {
        PaperSize = PaperSizes[0];
        Orientation = Orientations[0];
        DateStyle = DateStyles[0];
        SortOrder = SortOrders[0];
        ImageQuality = ImageQualities[2];
        TimeZone = TimeZoneInfo.Local;
        Margin = 36;
        FontSize = 9;
        BubbleWidth = 380;
        MaxImageHeight = 260;
        ImageDpi = 144;
        ShowPageNumbers = true;
        ShowRunningHeader = true;
    }

    public void UpdateSelectionStatus()
    {
        var selected = Conversations.Count(c => c.IsSelected);
        var records = Conversations.Where(c => c.IsSelected).Sum(c => c.TotalCount);
        StatusText = $"{selected:N0} of {Conversations.Count:N0} conversation(s) selected — {records:N0} records.";
        RaiseStates();
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var chosen = Conversations.Where(c => c.IsSelected).Select(c => c.Conversation).ToList();
        if (chosen.Count == 0 || OutputFolder.Length == 0) return;

        IsBusy = true;
        HasFinished = false;
        Progress = 0;
        ResultText = "";

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var started = DateTime.UtcNow;

        var folderName = AttachmentsFolderName.Trim();
        if (folderName.Length == 0) folderName = "attachments";

        var options = new PdfExportOptions
        {
            Conversations = chosen,
            OutputFolder = OutputFolder,
            Sources = _sources,

            SingleDocument = SingleDocument,
            DocumentTitle = DocumentTitle.Trim().Length > 0 ? DocumentTitle.Trim() : "Messages",
            Subtitle = Subtitle.Trim(),
            Reference = Reference.Trim(),
            PreparedBy = PreparedBy.Trim(),
            PreparedFor = PreparedFor.Trim(),
            Notes = Notes.Trim(),

            From = StartDate is { } from
                ? new DateTimeOffset(from.Date, TimeZoneInfo.Local.GetUtcOffset(from.Date))
                : null,
            To = EndDate is { } to
                ? new DateTimeOffset(to.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(to.Date))
                : null,

            IncludeSms = IncludeSms,
            IncludeMms = IncludeMms,
            IncludeCalls = IncludeCalls,
            IncludeDeliveryReceipts = IncludeDeliveryReceipts,
            SortOrder = SortOrder.Value,
            NumberRecords = NumberRecords,
            ShowRawTypeCodes = ShowRawTypeCodes,
            ShowAddresses = ShowAddresses,
            PageBreakBetweenConversations = PageBreakBetweenConversations,

            IncludeCoverPage = IncludeCoverPage,
            ListConversationsOnCover = ListConversationsOnCover,
            ListSourcesOnCover = ListSourcesOnCover,

            PaperSize = PaperSize.Value,
            Orientation = Orientation.Value,
            Margin = (float)Margin,
            FontSize = (float)FontSize,
            BubbleWidth = (float)BubbleWidth,
            DateStyle = DateStyle.Value,
            TimeZone = TimeZone,
            ShowPageNumbers = ShowPageNumbers,
            ShowRunningHeader = ShowRunningHeader,

            InlineImages = InlineImages,
            MaxImageHeight = (float)MaxImageHeight,
            ImageDpi = (int)ImageDpi,
            ImageQuality = ImageQuality.Value,
            CopyAttachments = CopyAttachments,
            CopyImagesToo = CopyImagesToo,
            AttachmentsFolderName = folderName,
            WriteAttachmentIndex = WriteAttachmentIndex,
            CompressDocument = CompressDocument,
        };

        try
        {
            var progress = new Progress<PdfExportProgress>(p =>
            {
                Progress = p.Fraction * 100;
                StatusText = p.ConversationName.Length > 0
                    ? $"{p.Phase}: {p.ConversationName} ({p.ConversationIndex + 1} of {p.ConversationCount})"
                    : $"{p.Phase}…";
            });

            var result = await Task.Run(
                () => new PdfExporter().Export(options, progress, token), token);

            Progress = 100;
            HasFinished = true;

            var lines = new List<string>
            {
                $"{result.DocumentPaths.Count:N0} PDF(s), {FormatBytes(result.DocumentBytes)}, from " +
                $"{result.ConversationsExported:N0} conversation(s) and {result.RecordsExported:N0} record(s).",
            };
            if (result.ImagesEmbedded > 0)
                lines.Add($"{result.ImagesEmbedded:N0} image(s) shown inline.");
            if (result.AttachmentsCopied > 0)
                lines.Add($"{result.AttachmentsCopied:N0} attachment(s) copied to \"{folderName}\" " +
                          $"({FormatBytes(result.AttachmentBytes)}) and referenced from the transcript.");
            if (result.AttachmentsMissing > 0)
                lines.Add($"{result.AttachmentsMissing:N0} attachment(s) had no decoded file and were named only.");
            lines.Add($"Finished in {Duration(DateTime.UtcNow - started)}.");

            ResultText = string.Join("\n", lines);
            StatusText = $"Saved to {OutputFolder}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export cancelled. Any PDFs already written have been kept.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.GetType().Name}. {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private bool CanExport() => CanRun;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation?.Cancel();

    private bool CanCancel() => IsBusy;

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (OutputFolder.Length == 0 || !Directory.Exists(OutputFolder)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OutputFolder,
            UseShellExecute = true,
        });
    }

    private static string Duration(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{span.Minutes}m {span.Seconds}s"
        : $"{span.Seconds}s";

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{value} B" : $"{size:F1} {units[unit]}";
    }
}
