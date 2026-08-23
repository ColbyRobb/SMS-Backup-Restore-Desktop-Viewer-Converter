using System.Windows;
using SmsBackupViewer.App.ViewModels;

namespace SmsBackupViewer.App;

public partial class ExtractWindow : Window
{
    private readonly ExtractViewModel _viewModel = new();

    public ExtractWindow(string? initialFolder = null)
    {
        InitializeComponent();
        DataContext = _viewModel;

        if (!string.IsNullOrEmpty(initialFolder))
            _viewModel.SourceFolder = initialFolder;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Do not leave an extraction running with no window to report to.
        if (_viewModel.IsBusy)
        {
            var answer = MessageBox.Show(
                "An extraction is still running. Stop it and close?",
                "Extract media", MessageBoxButton.YesNo, MessageBoxImage.Question);

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
