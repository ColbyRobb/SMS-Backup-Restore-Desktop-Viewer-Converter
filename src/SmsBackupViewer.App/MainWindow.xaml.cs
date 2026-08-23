using System.IO;
using System.Windows;
using SmsBackupViewer.App.ViewModels;

namespace SmsBackupViewer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>Opens backups passed on the command line.</summary>
    public async void OpenFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToArray();
        if (files.Length > 0) await _viewModel.LoadAsync(files);
    }

    private void OnExportPdf(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Conversations.Count == 0)
        {
            MessageBox.Show("Open a backup first.", "Export to PDF",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // The conversation on screen starts ticked, since exporting what you are looking at is
        // the common case.
        var window = new PdfExportWindow(
            _viewModel.Conversations.Select(c => c.Conversation),
            _viewModel.Sources,
            _viewModel.SelectedConversation?.Conversation,
            _viewModel.LoadedFolder) { Owner = this };

        window.Show();
    }

    private void OnExtractMedia(object sender, RoutedEventArgs e)
    {
        // Offer the folder of whatever is already open as a starting point.
        var window = new ExtractWindow(_viewModel.LoadedFolder) { Owner = this };
        window.Show();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var files = paths
            .Where(p => File.Exists(p) && Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length > 0) await _viewModel.LoadAsync(files);
    }
}
