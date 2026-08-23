using SmsBackupViewer.Core;
using SmsBackupViewer.Pdf;

namespace SmsBackupViewer.Core.Tests;

public class PdfExporterTests
{
    private static string FixtureFolder()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
            directory = Path.GetDirectoryName(directory);
        return Path.Combine(directory!, "tests", "fixtures");
    }

    private static string[] Fixtures()
        => Directory.GetFiles(FixtureFolder(), "*.xml").OrderBy(f => f).ToArray();

    /// <summary>Loads the fixtures with media decoded, as the viewer does.</summary>
    private static (BackupIndex Index, MediaExtractor Media, string Folder) Load()
    {
        var folder = Path.Combine(Path.GetTempPath(), "smsviewer-pdf-" + Guid.NewGuid().ToString("N"));
        var media = new MediaExtractor(Path.Combine(folder, "media"));
        var index = new BackupIndex();

        foreach (var file in Fixtures())
            foreach (var record in new BackupReader(media).Read(file))
                index.Add(record);

        index.Finalise();
        return (index, media, folder);
    }

    private static void Cleanup(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void ProducesAPdfPerConversation()
    {
        var (index, media, folder) = Load();
        var output = Path.Combine(folder, "out");
        try
        {
            var result = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = output,
                Sources = index.Sources,
            });

            Assert.NotEmpty(result.DocumentPaths);
            Assert.Equal(result.DocumentPaths.Count, index.Conversations.Count(c => c.Records.Count > 0));
            Assert.All(result.DocumentPaths, p => Assert.True(new FileInfo(p).Length > 1000));
        }
        finally { media.Dispose(); Cleanup(folder); }
    }

    [Fact]
    public void ProducesOneCombinedPdfWhenAsked()
    {
        var (index, media, folder) = Load();
        var output = Path.Combine(folder, "out");
        try
        {
            var result = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = output,
                Sources = index.Sources,
                SingleDocument = true,
                DocumentTitle = "All messages",
            });

            Assert.Single(result.DocumentPaths);
            Assert.True(result.ConversationsExported > 1);
        }
        finally { media.Dispose(); Cleanup(folder); }
    }

    [Fact]
    public void WrittenFileIsARealPdf()
    {
        var (index, media, folder) = Load();
        var output = Path.Combine(folder, "out");
        try
        {
            var result = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = output,
                SingleDocument = true,
            });

            var bytes = File.ReadAllBytes(result.DocumentPaths[0]);
            Assert.Equal("%PDF"u8.ToArray(), bytes[..4]);
        }
        finally { media.Dispose(); Cleanup(folder); }
    }

    [Fact]
    public void AttachmentsAreCopiedBesideTheDocument()
    {
        var (index, media, folder) = Load();
        var output = Path.Combine(folder, "out");
        try
        {
            var result = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = output,
                SingleDocument = true,
                CopyAttachments = true,
            });

            Assert.True(result.AttachmentsCopied > 0);
            Assert.True(Directory.Exists(result.AttachmentsFolder));

            // The video cannot be shown in the PDF, so its file must exist to be referenced.
            var copied = Directory.GetFiles(result.AttachmentsFolder);
            Assert.Contains(copied, f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));

            // And a companion listing so the folder makes sense on its own.
            Assert.True(File.Exists(Path.Combine(output, "attachments.csv")));
        }
        finally { media.Dispose(); Cleanup(folder); }
    }

    [Fact]
    public void ImagesAreEmbeddedInline()
    {
        var (index, media, folder) = Load();
        var output = Path.Combine(folder, "out");
        try
        {
            var result = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = output,
                SingleDocument = true,
                InlineImages = true,
            });

            Assert.True(result.ImagesEmbedded > 0);
        }
        finally { media.Dispose(); Cleanup(folder); }
    }

    [Fact]
    public void ExportsWithoutMediaStillProduceADocument()
    {
        // Opened without media loading: nothing to copy, but the transcript must still render
        // and still name every attachment.
        var index = new BackupIndex();
        foreach (var file in Fixtures())
            foreach (var record in new BackupReader().Read(file))
                index.Add(record);
        index.Finalise();

        var folder = Path.Combine(Path.GetTempPath(), "smsviewer-pdf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = folder,
                SingleDocument = true,
            });

            Assert.Single(result.DocumentPaths);
            Assert.Equal(0, result.AttachmentsCopied);
            Assert.True(result.AttachmentsMissing > 0);
        }
        finally { Cleanup(folder); }
    }

    [Fact]
    public void DateRangeLimitsWhatIsExported()
    {
        var (index, media, folder) = Load();
        var output = Path.Combine(folder, "out");
        try
        {
            var result = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = output,
                SingleDocument = true,
                From = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });

            var all = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = Path.Combine(folder, "all"),
                SingleDocument = true,
            });

            Assert.True(result.RecordsExported < all.RecordsExported);
        }
        finally { media.Dispose(); Cleanup(folder); }
    }

    [Fact]
    public void CallsCanBeExcluded()
    {
        var (index, media, folder) = Load();
        try
        {
            var withCalls = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = Path.Combine(folder, "with"),
                SingleDocument = true,
            });

            var without = new PdfExporter().Export(new PdfExportOptions
            {
                Conversations = index.Conversations.ToList(),
                OutputFolder = Path.Combine(folder, "without"),
                SingleDocument = true,
                IncludeCalls = false,
            });

            Assert.True(without.RecordsExported < withCalls.RecordsExported);
        }
        finally { media.Dispose(); Cleanup(folder); }
    }
}
