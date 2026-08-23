using System.Reflection;
using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace SmsBackupViewer.Pdf;

/// <summary>
/// Palette and typography for the exported PDF, matching the application, so a printed
/// transcript looks like what was on screen.
/// </summary>
public static class PdfTheme
{
    public const string HeadingFont = "Raleway";
    public const string BodyFont = "Open Sans";

    public static readonly Color Brand = Color.FromHex("#03c4eb");
    public static readonly Color BrandLight = Color.FromHex("#e3f7fd");
    public static readonly Color Link = Color.FromHex("#1a73e8");
    public static readonly Color Text = Color.FromHex("#333333");
    public static readonly Color Muted = Color.FromHex("#777777");
    public static readonly Color Border = Color.FromHex("#cccccc");
    public static readonly Color Fill = Color.FromHex("#f5f5f5");
    public static readonly Color White = Color.FromHex("#ffffff");

    private static bool _initialised;
    private static readonly Lock Gate = new();

    /// <summary>
    /// Registers the bundled fonts and selects the Community licence.
    ///
    /// The fonts are embedded in this assembly rather than read from disk so an exported PDF
    /// looks the same wherever the tool runs, and nothing is fetched from a font service.
    /// </summary>
    public static void Initialise()
    {
        lock (Gate)
        {
            if (_initialised) return;

            QuestPDF.Settings.License = LicenseType.Community;

            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is not null) FontManager.RegisterFont(stream);
            }

            _initialised = true;
        }
    }
}
