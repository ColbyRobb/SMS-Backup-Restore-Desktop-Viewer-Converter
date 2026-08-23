using System.Windows;
using SmsBackupViewer.App.ViewModels;
using SmsBackupViewer.Core;

namespace SmsBackupViewer.App;

public partial class PdfExportWindow : Window
{
    private readonly PdfExportViewModel _viewModel;

    public PdfExportWindow(
        IEnumerable<Conversation> conversations,
        IReadOnlyList<SourceFile> sources,
        Conversation? preselected,
        string? suggestedFolder)
    {
        InitializeComponent();
        _viewModel = new PdfExportViewModel(conversations, sources, preselected, suggestedFolder);
        DataContext = _viewModel;
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => _viewModel.UpdateSelectionStatus();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            var answer = MessageBox.Show(
                "An export is still running. Stop it and close?",
                "Export to PDF", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _viewModel.CancelCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
