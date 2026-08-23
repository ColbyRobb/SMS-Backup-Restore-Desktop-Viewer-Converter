using SmsBackupViewer.Core;

namespace SmsBackupViewer.Core.Tests;

public class ContactMatcherAndExportTests
{
    private static string FixtureFolder()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
            directory = Path.GetDirectoryName(directory);
        return Path.Combine(directory!, "tests", "fixtures");
    }

    private static string[] Fixtures() => Directory.GetFiles(FixtureFolder(), "*.xml").OrderBy(f => f).ToArray();

    private static BackupIndex BuildIndex()
    {
        var index = new BackupIndex();
        foreach (var file in Fixtures())
            foreach (var record in new BackupReader().Read(file))
                index.Add(record);
        index.Finalise();
        return index;
    }

    private static Conversation Find(BackupIndex index, string term)
        => index.Conversations.First(c => new ContactMatcher([term]).Matches(c));

    [Fact]
    public void EmptyTermsMatchEverything()
    {
        var matcher = new ContactMatcher([]);
        Assert.True(matcher.MatchesEverything);
        Assert.All(BuildIndex().Conversations, c => Assert.True(matcher.Matches(c)));
    }

    [Fact]
    public void MatchesByContactName()
    {
        var index = BuildIndex();
        var matched = index.Conversations.Where(new ContactMatcher(["Alex Rivera"]).Matches).ToList();
        Assert.NotEmpty(matched);
        Assert.All(matched, c => Assert.Contains(
            c.Records.Select(r => r.ContactName ?? "").Append(c.DisplayName),
            n => n.Contains("Alex", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void MatchesPartialNameCaseInsensitively()
    {
        var index = BuildIndex();
        Assert.Contains(index.Conversations, new ContactMatcher(["rivera"]).Matches);
    }

    [Fact]
    public void MatchesNumberRegardlessOfFormatting()
    {
        var index = BuildIndex();

        var plain = index.Conversations.Where(new ContactMatcher(["5558675309"]).Matches).ToList();
        var formatted = index.Conversations.Where(new ContactMatcher(["+1 555-867-5309"]).Matches).ToList();
        var bracketed = index.Conversations.Where(new ContactMatcher(["(555) 867-5309"]).Matches).ToList();

        Assert.NotEmpty(plain);
        Assert.Equal(plain.Select(c => c.Key).OrderBy(k => k), formatted.Select(c => c.Key).OrderBy(k => k));
        Assert.Equal(plain.Select(c => c.Key).OrderBy(k => k), bracketed.Select(c => c.Key).OrderBy(k => k));
    }

    [Fact]
    public void MatchesRcsThreadByParticipantNumber()
    {
        // The RCS conversation's address attribute is a @rcs.google.com placeholder; the real
        // number is only in the addr elements, so this is the case a naive matcher misses.
        var index = BuildIndex();
        var matched = index.Conversations.Where(new ContactMatcher(["5551234567"]).Matches).ToList();
        Assert.Contains(matched, c => c.Records.Any(r => AddressKey.IsRcsPlaceholder(r.Address)));
    }

    [Fact]
    public void NonMatchingTermFindsNothing()
        => Assert.DoesNotContain(BuildIndex().Conversations, new ContactMatcher(["Nobody At All"]).Matches);

    [Fact]
    public void ExportsOnlyImagesByDefault()
    {
        var output = NewFolder();
        try
        {
            var result = new MediaExporter().Export(new ExportOptions
            {
                Files = Fixtures(),
                OutputFolder = output,
                Matcher = new ContactMatcher([]),
                Selection = MediaSelection.Images,
            });

            Assert.True(result.FilesWritten > 0);
            var written = Directory.GetFiles(output, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("manifest.csv", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.All(written, f => Assert.Contains(Path.GetExtension(f).ToLowerInvariant(),
                new[] { ".jpg", ".png", ".gif", ".webp", ".bmp" }));
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public void AllMediaIncludesVideo()
    {
        var output = NewFolder();
        try
        {
            new MediaExporter().Export(new ExportOptions
            {
                Files = Fixtures(),
                OutputFolder = output,
                Selection = MediaSelection.AllMedia,
            });

            Assert.Contains(Directory.GetFiles(output, "*.*", SearchOption.AllDirectories),
                f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public void ExportedFilesAreGroupedIntoPerContactFolders()
    {
        var output = NewFolder();
        try
        {
            var result = new MediaExporter().Export(new ExportOptions
            {
                Files = Fixtures(),
                OutputFolder = output,
                Selection = MediaSelection.AllMedia,
            });

            Assert.True(result.ContactFolders.Count > 1);
            Assert.All(result.ContactFolders, folder => Assert.True(Directory.Exists(folder)));
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public void FlatModeWritesEverythingIntoOneFolder()
    {
        var output = NewFolder();
        try
        {
            new MediaExporter().Export(new ExportOptions
            {
                Files = Fixtures(),
                OutputFolder = output,
                Selection = MediaSelection.AllMedia,
                FolderPerContact = false,
            });

            Assert.Empty(Directory.GetDirectories(output));
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public void DateRangeExcludesOlderMessages()
    {
        var output = NewFolder();
        try
        {
            var result = new MediaExporter().Export(new ExportOptions
            {
                Files = Fixtures(),
                OutputFolder = output,
                Selection = MediaSelection.Everything,
                From = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });

            // Only the 2026 fixture has attachments after this date.
            Assert.Equal(1, result.FilesWritten);
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public void ManifestHasOneRowPerWrittenFile()
    {
        var output = NewFolder();
        try
        {
            var result = new MediaExporter().Export(new ExportOptions
            {
                Files = Fixtures(),
                OutputFolder = output,
                Selection = MediaSelection.AllMedia,
            });

            var lines = File.ReadAllLines(result.ManifestPath);
            Assert.Equal(result.FilesWritten + 1, lines.Length);   // + header
            Assert.StartsWith("contact,conversation_key,date_local", lines[0]);
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public void NoMatchProducesNoOutputRatherThanEverything()
    {
        var output = NewFolder();
        try
        {
            var result = new MediaExporter().Export(new ExportOptions
            {
                Files = Fixtures(),
                OutputFolder = output,
                Matcher = new ContactMatcher(["Nobody At All"]),
                Selection = MediaSelection.Everything,
            });

            Assert.Equal(0, result.ConversationsMatched);
            Assert.Equal(0, result.FilesWritten);
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public void TheSameMessageInSeveralBackupsIsExtractedOnce()
    {
        // Consecutive backups each contain the whole history, so the same attachment appears in
        // many files. It must be written once, and recognised without decoding it again.
        var folder = NewFolder();
        var output = NewFolder();
        try
        {
            var source = Fixtures().First(f => Path.GetFileName(f).StartsWith("sms-2021"));
            var first = Path.Combine(folder, "sms-20210105004105.xml");
            var second = Path.Combine(folder, "sms-20210206004105.xml");
            File.Copy(source, first);
            File.Copy(source, second);

            var result = new MediaExporter().Export(new ExportOptions
            {
                Files = [first, second],
                OutputFolder = output,
                Selection = MediaSelection.Everything,
            });

            Assert.Equal(1, result.FilesWritten);
            Assert.True(result.DuplicatesSkipped > 0);
        }
        finally { Cleanup(folder); Cleanup(output); }
    }

    [Fact]
    public void ResumeLeavesExistingFilesAlone()
    {
        var output = NewFolder();
        try
        {
            var options = new ExportOptions
            {
                Files = Fixtures(),
                OutputFolder = output,
                Selection = MediaSelection.AllMedia,
            };

            var first = new MediaExporter().Export(options);
            Assert.True(first.FilesWritten > 0);

            // A fresh exporter, as a second run of the tool would be.
            var second = new MediaExporter().Export(new ExportOptions
            {
                Files = options.Files,
                OutputFolder = output,
                Selection = options.Selection,
                Resume = true,
            });

            Assert.Equal(0, second.FilesWritten);
            Assert.Equal(first.FilesWritten, second.AlreadyPresent);
        }
        finally { Cleanup(output); }
    }

    [Fact]
    public void UnknownContentTypesStillGetExtracted()
    {
        // Anything that is not smil or text is an attachment, whatever its type.
        Assert.Equal(".bin", MediaExtractor.Extension("application/x-unheard-of", null));
        Assert.Equal(".dat", MediaExtractor.Extension("application/x-unheard-of", "thing.dat"));
        Assert.Equal(".webp", MediaExtractor.Extension("image/webp", null));
        Assert.Equal(".mkv", MediaExtractor.Extension("video/x-matroska", null));
        Assert.Equal(".opus", MediaExtractor.Extension("audio/opus", null));
        Assert.Equal(".gif", MediaExtractor.Extension("image/gif", null));
    }

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "smsviewer-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Cleanup(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
