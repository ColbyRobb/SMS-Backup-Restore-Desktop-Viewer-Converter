using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SmsBackupViewer.Core;

namespace SmsBackupViewer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private BackupIndex _index = new();

    /// <summary>Folder of the most recently opened backup, offered to the extract window.</summary>
    public string? LoadedFolder { get; private set; }

    /// <summary>Provenance of the loaded backups, printed on a PDF export's cover page.</summary>
    public IReadOnlyList<SourceFile> Sources => _index.Sources;
    private CancellationTokenSource? _loadCancellation;
    private string _mediaFolder = "";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "No backup loaded. Open a file to begin.";
    [ObservableProperty] private string _fileSummary = "";
    [ObservableProperty] private string _provenanceText = "";

    [ObservableProperty] private string _conversationFilter = "";
    [ObservableProperty] private string _messageFilter = "";
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private bool _showSms = true;
    [ObservableProperty] private bool _showMms = true;
    [ObservableProperty] private bool _showCalls = true;
    [ObservableProperty] private bool _loadMedia = true;

    [ObservableProperty] private ConversationViewModel? _selectedConversation;
    [ObservableProperty] private RecordViewModel? _selectedRecord;

    public ObservableCollection<ConversationViewModel> Conversations { get; } = [];
    public ObservableCollection<RecordViewModel> Messages { get; } = [];

    /// <summary>Filtered view over the conversation list, driven by the search box.</summary>
    public ICollectionView ConversationsView { get; }

    public MainViewModel()
    {
        ConversationsView = CollectionViewSource.GetDefaultView(Conversations);
        ConversationsView.Filter = FilterConversation;
    }

    private bool FilterConversation(object item)
    {
        if (item is not ConversationViewModel conversation) return false;
        if (string.IsNullOrWhiteSpace(ConversationFilter)) return true;

        if (_conversationPattern is { HasWildcards: true } pattern)
            return pattern.IsMatch(conversation.DisplayName)
                || pattern.IsMatch(conversation.AddressText)
                // Let "555-???-5309" match "+1 (555) 867-5309".
                || _conversationNumberPattern!.IsMatch(AddressKey.StripFormatting(conversation.Conversation.DisplayAddress));

        var needle = ConversationFilter.Trim();
        return conversation.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || conversation.AddressText.Contains(needle, StringComparison.OrdinalIgnoreCase)
            // Let a search for "5558675309" match "+1 555-867-5309".
            || conversation.Key.Contains(AddressKey.Normalize(needle), StringComparison.OrdinalIgnoreCase);
    }

    private SearchPattern? _conversationPattern;
    private SearchPattern? _conversationNumberPattern;

    partial void OnConversationFilterChanged(string value)
    {
        _conversationPattern = SearchPattern.Parse(value);
        _conversationNumberPattern = SearchPattern.ParseNumber(value);
        ConversationsView.Refresh();
    }

    partial void OnSelectedConversationChanged(ConversationViewModel? value) => RebuildMessages();
    partial void OnMessageFilterChanged(string value) => RebuildMessages();
    partial void OnStartDateChanged(DateTime? value) => RebuildMessages();
    partial void OnEndDateChanged(DateTime? value) => RebuildMessages();
    partial void OnShowSmsChanged(bool value) => RebuildMessages();
    partial void OnShowMmsChanged(bool value) => RebuildMessages();
    partial void OnShowCallsChanged(bool value) => RebuildMessages();

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an SMS Backup & Restore XML file",
            Filter = "Backup files (*.xml)|*.xml|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        await LoadAsync(dialog.FileNames);
    }

    [RelayCommand]
    private void Cancel() => _loadCancellation?.Cancel();

    [RelayCommand]
    private void CloseFile()
    {
        _index = new BackupIndex();
        Conversations.Clear();
        Messages.Clear();
        SelectedConversation = null;
        SelectedRecord = null;
        FileSummary = "";
        ProvenanceText = "";
        StatusText = "No backup loaded. Open a file to begin.";
    }

    public async Task LoadAsync(IReadOnlyList<string> paths)
    {
        if (IsLoading) return;

        IsLoading = true;
        Progress = 0;
        Conversations.Clear();
        Messages.Clear();
        SelectedConversation = null;
        SelectedRecord = null;

        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        var index = new BackupIndex();

        // Media lands beside the first file, in a folder named after it, so nothing is written
        // anywhere unexpected and nothing leaves this machine.
        LoadedFolder = Path.GetDirectoryName(paths[0]);
        _mediaFolder = Path.Combine(
            LoadedFolder ?? ".",
            Path.GetFileNameWithoutExtension(paths[0]) + ".media");

        try
        {
            await Task.Run(() =>
            {
                using var media = LoadMedia ? new MediaExtractor(_mediaFolder) : null;

                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();

                    var info = new FileInfo(path);
                    var source = new SourceFile
                    {
                        Path = path,
                        FileName = info.Name,
                        SizeBytes = info.Length,
                        Sha256 = BackupIndex.ComputeSha256(path),
                    };

                    var progress = new Progress<ReadProgress>(p =>
                    {
                        Progress = p.Fraction * 100;
                        StatusText = $"Reading {info.Name} — {p.RecordsRead:N0} records";
                    });

                    var reader = new BackupReader(media);
                    foreach (var record in reader.Read(path, progress, token))
                    {
                        index.Add(record);
                        source.RecordCount++;
                        switch (record.Kind)
                        {
                            case RecordKind.Sms: source.SmsCount++; break;
                            case RecordKind.Mms: source.MmsCount++; break;
                            case RecordKind.Call: source.CallCount++; break;
                        }
                    }

                    if (media is not null)
                    {
                        source.AttachmentCount = media.FileCount;
                        source.AttachmentBytes = media.TotalBytes;
                    }
                    index.Sources.Add(source);
                }

                StatusText = "Grouping conversations…";
                index.Finalise();
            }, token);

            _index = index;
            PopulateConversations();
            StatusText = BuildStatus();
            FileSummary = BuildFileSummary();
            ProvenanceText = BuildProvenance();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Loading cancelled.";
            CloseFile();
        }
        catch (Exception ex)
        {
            // Show the failure without echoing file content.
            StatusText = $"Could not read the file: {ex.GetType().Name}. " +
                         "If it is stored online-only in OneDrive or Dropbox, make it available offline first.";
        }
        finally
        {
            IsLoading = false;
            Progress = 0;
            _loadCancellation?.Dispose();
            _loadCancellation = null;
        }
    }

    private void PopulateConversations()
    {
        foreach (var conversation in _index.Conversations.OrderByDescending(c => c.LastDateMs))
            Conversations.Add(new ConversationViewModel(conversation));

        ConversationsView.Refresh();
        if (Conversations.Count > 0) SelectedConversation = Conversations[0];
    }

    private void RebuildMessages()
    {
        Messages.Clear();
        SelectedRecord = null;
        if (SelectedConversation is null) return;

        var start = StartDate is { } from ? new DateTimeOffset(from.Date).ToUnixTimeMilliseconds() : (long?)null;
        var end = EndDate is { } to
            ? new DateTimeOffset(to.Date.AddDays(1).AddTicks(-1)).ToUnixTimeMilliseconds()
            : (long?)null;
        var pattern = SearchPattern.Parse(MessageFilter);

        foreach (var record in SelectedConversation.Conversation.Records)
        {
            var kindAllowed = record.Kind switch
            {
                RecordKind.Sms => ShowSms,
                RecordKind.Mms => ShowMms,
                _ => ShowCalls,
            };
            if (!kindAllowed) continue;

            if (start is { } s && record.DateMs > 0 && record.DateMs < s) continue;
            if (end is { } e && record.DateMs > 0 && record.DateMs > e) continue;

            if (pattern is not null && !pattern.IsMatch(record.Body) && !pattern.IsMatch(record.ContactName))
                continue;

            Messages.Add(new RecordViewModel(record));
        }

        StatusText = $"{Messages.Count:N0} of {SelectedConversation.TotalCount:N0} records shown " +
                     $"in {SelectedConversation.DisplayName}";
    }

    private string BuildStatus()
    {
        var range = _index.FirstDate is { } first && _index.LastDate is { } last
            ? $" spanning {first.ToLocalTime():d MMM yyyy} to {last.ToLocalTime():d MMM yyyy}"
            : "";
        return $"{_index.Records.Count:N0} records in {_index.Conversations.Count:N0} conversations{range}.";
    }

    private string BuildFileSummary()
    {
        var attachments = _index.Sources.Sum(s => s.AttachmentCount);
        var parts = new List<string>
        {
            $"{_index.SmsCount:N0} SMS",
            $"{_index.MmsCount:N0} MMS",
            $"{_index.CallCount:N0} calls",
        };
        if (attachments > 0)
            parts.Add($"{attachments:N0} attachments ({RecordViewModel.FormatBytes(_index.Sources.Sum(s => s.AttachmentBytes))})");
        if (_index.UnaddressedRecords > 0)
            parts.Add($"{_index.UnaddressedRecords:N0} without an address");
        return string.Join("  •  ", parts);
    }

    private string BuildProvenance()
    {
        var lines = _index.Sources.Select(s =>
            $"{s.FileName}  •  {RecordViewModel.FormatBytes(s.SizeBytes)}  •  SHA-256 {s.Sha256[..16]}…  " +
            $"•  {s.RecordCount:N0} records");
        var text = string.Join("\n", lines);
        if (_index.Sources.Any(s => s.AttachmentCount > 0))
            text += $"\nAttachments extracted to: {_mediaFolder}";
        return text;
    }
}

/// <summary>A conversation as shown in the left-hand list.</summary>
public sealed class ConversationViewModel(Conversation conversation)
{
    public Conversation Conversation { get; } = conversation;

    public string Key => Conversation.Key;
    public string DisplayName => Conversation.DisplayName;
    public string AddressText => AddressKey.Display(Conversation.DisplayAddress);
    public int TotalCount => Conversation.TotalCount;
    public bool IsGroup => Conversation.IsGroup;

    public string CountsText
    {
        get
        {
            var parts = new List<string>();
            if (Conversation.SmsCount > 0) parts.Add($"{Conversation.SmsCount:N0} SMS");
            if (Conversation.MmsCount > 0) parts.Add($"{Conversation.MmsCount:N0} MMS");
            if (Conversation.CallCount > 0) parts.Add($"{Conversation.CallCount:N0} calls");
            return string.Join(" • ", parts);
        }
    }

    public string LastDateText => Conversation.LastDateMs > 0
        ? Conversation.LastDate.ToLocalTime().ToString("d MMM yyyy")
        : "";
}
