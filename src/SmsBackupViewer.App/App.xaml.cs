using System.Windows;

namespace SmsBackupViewer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--extract [folder]" opens the extraction window on its own, for when pulling media
        // out of a folder of backups is all you came to do.
        var extractIndex = Array.FindIndex(e.Args,
            a => a.Equals("--extract", StringComparison.OrdinalIgnoreCase));

        if (extractIndex >= 0)
        {
            var folder = extractIndex + 1 < e.Args.Length && !e.Args[extractIndex + 1].StartsWith('-')
                ? e.Args[extractIndex + 1]
                : null;

            var window = new ExtractWindow(folder);
            MainWindow = window;
            window.Show();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        // Files passed on the command line are opened straight away.
        var files = e.Args.Where(a => !a.StartsWith('-')).ToArray();
        if (files.Length > 0) main.OpenFiles(files);
    }
}
