using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SmsBackupViewer.Core;

namespace SmsBackupViewer.App.ViewModels;

/// <summary>One conversation offered for extraction, with a tick box.</summary>
public sealed partial class ContactChoice(ContactSummary summary) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public ContactSummary Summary { get; } = summary;
    public string Key => Summary.Key;
    public string Label => Summary.Label;
    public int Attachments => Summary.Attachments;
    public int Records => Summary.Records;

    public string CountsText => Summary.Attachments == 1
        ? "1 attachment"
        : $"{Summary.Attachments:N0} attachments";

    public string RangeText => Summary.LastDateMs > 0
        ? $"{DateTimeOffset.FromUnixTimeMilliseconds(Summary.FirstDateMs).ToLocalTime():MMM yyyy} – " +
          $"{DateTimeOffset.FromUnixTimeMilliseconds(Summary.LastDateMs).ToLocalTime():MMM yyyy}"
        : "";
}

public sealed partial class ExtractViewModel : ObservableObject
{
    private CancellationTokenSource? _cancellation;

    [ObservableProperty] private string _sourceFolder = "";
    [ObservableProperty] private string _outputFolder = "";
    [ObservableProperty] private bool _includeSubfolders;

    [ObservableProperty] private bool _imagesOnly = true;
    [ObservableProperty] private bool _allMedia;
    [ObservableProperty] private bool _allAttachments;

    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private bool _folderPerContact = true;
    [ObservableProperty] private bool _resume = true;

    [ObservableProperty] private bool _everyConversation = true;
    [ObservableProperty] private string _contactFilter = "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "Choose a folder of backups to begin.";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasScanned;

    public ObservableCollection<ContactChoice> Contacts { get; } = [];

    /// <summary>Backups found under the source folder, refreshed whenever it changes.</summary>
    public List<string> Files { get; private set; } = [];

    public bool CanRun => !IsBusy && Files.Count > 0 && OutputFolder.Length > 0;

    partial void OnIsBusyChanged(bool value) => RaiseCommandStates();
    partial void OnOutputFolderChanged(string value) => RaiseCommandStates();

    partial void OnSourceFolderChanged(string value) => RefreshFiles();
    partial void OnIncludeSubfoldersChanged(bool value) => RefreshFiles();

    partial void OnContactFilterChanged(string value)
    {
        if (value.Length > 0) EveryConversation = false;
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanRun));
        ScanCommand.NotifyCanExecuteChanged();
        ExtractCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void RefreshFiles()
    {
        Contacts.Clear();
        HasScanned = false;
        ResultText = "";

        Files = SourceFolder.Length > 0
            ? ContactScanner.FindBackups(SourceFolder, IncludeSubfolders)
            : [];

        StatusText = SourceFolder.Length == 0
            ? "Choose a folder of backups to begin."
            : Files.Count == 0
                ? "No .xml backup files found in that folder." +
                  (IncludeSubfolders ? "" : " Try including subfolders.")
                : $"{Files.Count:N0} backup file(s) found" +
                  $" ({FormatBytes(Files.Sum(f => new FileInfo(f).Length))}).";

        // Default the destination beside the source, but never overwrite a chosen one.
        if (OutputFolder.Length == 0 && Files.Count > 0)
            OutputFolder = Path.Combine(SourceFolder, "extracted-media");

        RaiseCommandStates();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder holding your backup files",
            Multiselect = false,
        };
        if (SourceFolder.Length > 0 && Directory.Exists(SourceFolder))
            dialog.InitialDirectory = SourceFolder;

        if (dialog.ShowDialog() == true) SourceFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to save the extracted media",
            Multiselect = false,
        };
        if (OutputFolder.Length > 0 && Directory.Exists(OutputFolder))
            dialog.InitialDirectory = OutputFolder;
        else if (SourceFolder.Length > 0 && Directory.Exists(SourceFolder))
            dialog.InitialDirectory = SourceFolder;

        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (Files.Count == 0) return;

        IsBusy = true;
        Progress = 0;
        ResultText = "";
        Contacts.Clear();

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                Progress = p.Fraction * 100;
                StatusText = $"Scanning {p.FileName} ({p.FileIndex} of {p.FileCount})…";
            });

            var found = await Task.Run(
                () => ContactScanner.Scan(Files, progress, token), token);

            foreach (var summary in found.Where(s => s.Attachments > 0))
                Contacts.Add(new ContactChoice(summary));

            HasScanned = true;
            StatusText = Contacts.Count == 0
                ? "No conversations with attachments were found in these backups."
                : $"{Contacts.Count:N0} conversation(s) have attachments. " +
                  "Tick the ones you want, or leave \"Every conversation\" ticked.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not scan those backups: {ex.GetType().Name}.";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private bool CanScan() => !IsBusy && Files.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private async Task ExtractAsync()
    {
        if (Files.Count == 0 || OutputFolder.Length == 0) return;

        IsBusy = true;
        Progress = 0;
        ResultText = "";

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        var selection = AllAttachments ? MediaSelection.Everything
            : AllMedia ? MediaSelection.AllMedia
            : MediaSelection.Images;

        // An explicit tick list beats a search term: it is unambiguous for group threads.
        var keys = EveryConversation
            ? []
            : Contacts.Where(c => c.IsSelected).Select(c => c.Key).ToList();

        var terms = EveryConversation || ContactFilter.Trim().Length == 0
            ? []
            : ContactFilter.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        if (!EveryConversation && keys.Count == 0 && terms.Count == 0)
        {
            StatusText = "Tick at least one conversation, type a name or number, " +
                         "or choose \"Every conversation\".";
            IsBusy = false;
            return;
        }

        var options = new ExportOptions
        {
            Files = Files,
            OutputFolder = OutputFolder,
            Matcher = new ContactMatcher(terms, keys),
            Selection = selection,
            From = StartDate is { } from ? new DateTimeOffset(from.Date, TimeZoneInfo.Local.GetUtcOffset(from.Date)) : null,
            To = EndDate is { } to
                ? new DateTimeOffset(to.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(to.Date))
                : null,
            FolderPerContact = FolderPerContact,
            Resume = Resume,
        };

        var started = DateTime.UtcNow;

        try
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                Progress = p.Fraction * 100;
                var elapsed = DateTime.UtcNow - started;
                var remaining = p.Fraction > 0.01
                    ? TimeSpan.FromSeconds(elapsed.TotalSeconds / p.Fraction - elapsed.TotalSeconds)
                    : TimeSpan.Zero;

                StatusText = $"[{p.FileIndex}/{p.FileCount}] {p.Phase} {p.FileName} — " +
                             $"{p.FilesWritten:N0} written" +
                             (remaining > TimeSpan.Zero ? $", about {Duration(remaining)} left" : "");
            });

            var result = await Task.Run(
                () => new MediaExporter().Export(options, progress, token), token);

            Progress = 100;

            if (result.ConversationsMatched == 0)
            {
                StatusText = "No conversation matched. Scan for contacts and tick the ones you want.";
                return;
            }

            var lines = new List<string>
            {
                $"{result.FilesWritten:N0} file(s) written ({FormatBytes(result.BytesWritten)})",
                $"from {result.ConversationsMatched:N0} conversation(s) across {result.FilesScanned:N0} backup(s)",
            };
            if (result.DuplicatesSkipped > 0)
                lines.Add($"{result.DuplicatesSkipped:N0} duplicate(s) skipped — the same attachment appears in several backups");
            if (result.AlreadyPresent > 0)
                lines.Add($"{result.AlreadyPresent:N0} already on disk and left alone");
            if (result.Failed > 0)
                lines.Add($"{result.Failed:N0} could not be decoded");
            lines.Add($"Finished in {Duration(DateTime.UtcNow - started)}.");

            ResultText = string.Join("\n", lines);
            StatusText = result.FilesWritten > 0
                ? $"Saved to {OutputFolder}"
                : "Nothing new to write.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Extraction cancelled. Files already written have been kept — " +
                         "tick Resume to carry on from here.";
        }
        catch (Exception ex)
        {
            StatusText = $"Extraction failed: {ex.GetType().Name}. {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private bool CanExtract() => CanRun;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation?.Cancel();

    private bool CanCancel() => IsBusy;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var contact in Contacts) contact.IsSelected = true;
        EveryConversation = false;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var contact in Contacts) contact.IsSelected = false;
    }

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

    private static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{value} B" : $"{size:F2} {units[unit]}";
    }
}
