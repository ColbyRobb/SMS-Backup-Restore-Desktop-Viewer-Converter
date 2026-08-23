using SmsBackupViewer.Core;

namespace SmsBackupViewer.Core.Tests;

public class AddressKeyTests
{
    [Theory]
    [InlineData("+1 555-867-5309", "58675309")]
    [InlineData("5558675309", "58675309")]
    [InlineData("(555) 867-5309", "58675309")]
    [InlineData("555-867-5309", "58675309")]
    public void FormattingVariantsProduceTheSameKey(string address, string expected)
        => Assert.Equal(expected, AddressKey.Normalize(address));

    [Fact]
    public void ShortCodesCompareInFull()
        => Assert.Equal("262966", AddressKey.Normalize("262966"));

    [Fact]
    public void EmptyAddressProducesEmptyKey()
        => Assert.Equal("", AddressKey.Normalize(null));

    [Fact]
    public void GroupParticipantsAreSortedSoOrderDoesNotSplitTheThread()
    {
        var first = AddressKey.ThreadKey("+15558675309~+15551234567");
        var second = AddressKey.ThreadKey("+15551234567~+15558675309");
        Assert.Equal(first, second);
        Assert.Contains('~', first);
    }

    [Fact]
    public void DeviceOwnerPlaceholderIsExcludedFromGroupKeys()
    {
        var key = AddressKey.ThreadKey("+15558675309~insert-address-token");
        Assert.Equal(AddressKey.Normalize("+15558675309"), key);
        Assert.DoesNotContain('~', key);
    }

    [Fact]
    public void GroupOfOneCollapsesToASingleConversation()
        => Assert.Equal("58675309", AddressKey.ThreadKey("+1 555-867-5309~5558675309"));

    [Fact]
    public void RcsPlaceholderIsRecognised()
    {
        Assert.True(AddressKey.IsRcsPlaceholder("99887766@rcs.google.com"));
        Assert.False(AddressKey.IsRcsPlaceholder("+15558675309"));
    }
}

public class BackupReaderTests
{
    private static string Fixture(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return Path.Combine(directory!, "tests", "fixtures", name);
    }

    private static List<MessageRecord> ReadAll(string fixture)
        => new BackupReader().Read(Fixture(fixture)).ToList();

    [Fact]
    public void ReadsSmsAndMmsFromAMessagesFile()
    {
        var records = ReadAll("sms-20230611004105.xml");
        Assert.Equal(3, records.Count(r => r.Kind == RecordKind.Sms));
        Assert.Equal(3, records.Count(r => r.Kind == RecordKind.Mms));
    }

    [Fact]
    public void ReadsCallsFromACallLogFile()
    {
        var records = ReadAll("calls-20230618004105.xml");
        Assert.Equal(5, records.Count);
        Assert.All(records, r => Assert.Equal(RecordKind.Call, r.Kind));
        Assert.Contains(records, r => r.DurationSeconds == 125);
    }

    [Fact]
    public void MapsDirectionCodes()
    {
        var records = ReadAll("sms-20230611004105.xml");
        var sms = records.Where(r => r.Kind == RecordKind.Sms).ToList();
        Assert.Contains(sms, r => r.Direction == Direction.Received);
        Assert.Contains(sms, r => r.Direction == Direction.Sent);
        Assert.Contains(sms, r => r.Direction == Direction.Failed);
    }

    [Fact]
    public void UnknownTypeCodeIsPreservedRatherThanDiscarded()
    {
        // Type 6 (queued) appears only in the newest fixture.
        var records = ReadAll("sms-20260101004105.xml");
        var queued = records.Single(r => r.TypeCode == 6);
        Assert.Equal(Direction.Queued, queued.Direction);
        Assert.Equal("6", queued.AllAttributes["type"]);
    }

    [Fact]
    public void UndocumentedAttributesArePreservedInExtras()
    {
        var records = ReadAll("sms-20230611004105.xml");
        var tagged = records.Single(r => r.Extras.ContainsKey("carrier_tag"));
        Assert.Equal("telstra", tagged.Extras["carrier_tag"]);
    }

    [Fact]
    public void MappedAttributesAreNotDuplicatedIntoExtras()
    {
        var record = ReadAll("sms-20230611004105.xml").First(r => r.Kind == RecordKind.Sms);
        Assert.False(record.Extras.ContainsKey("body"));
        Assert.False(record.Extras.ContainsKey("address"));
        Assert.True(record.AllAttributes.ContainsKey("body"));
    }

    [Fact]
    public void MmsTextPartsBecomeTheBody()
    {
        var mms = ReadAll("sms-20230611004105.xml")
            .First(r => r.Kind == RecordKind.Mms && r.Parts.Any(p => p.IsText));
        Assert.Contains("Look at this photo", mms.Body);
    }

    [Fact]
    public void SmilPartsAreClassifiedSeparatelyFromAttachments()
    {
        var mms = ReadAll("sms-20230611004105.xml").First(r => r.Parts.Any(p => p.IsSmil));
        Assert.DoesNotContain(mms.Parts.Where(p => p.IsSmil), p => p.IsAttachment);
        Assert.Contains(mms.Parts, p => p.IsAttachment && p.IsImage);
    }

    [Fact]
    public void DeliveryReceiptIsFlagged()
    {
        var records = ReadAll("sms-20230611004105.xml");
        Assert.Contains(records, r => r.IsDeliveryReceipt);
    }

    [Fact]
    public void RcsPlaceholderResolvesToTheRealParticipants()
    {
        var rcs = ReadAll("sms-20230611004105.xml")
            .Single(r => AddressKey.IsRcsPlaceholder(r.Address));

        // The thread key must come from the addr elements, not the placeholder.
        Assert.DoesNotContain("rcs.google.com", rcs.ThreadKey);
        Assert.Equal(AddressKey.Normalize("+15551234567"), rcs.ThreadKey);
        Assert.Equal("+15551234567", rcs.GroupSender);
    }

    [Fact]
    public void GroupMessageSenderComesFromTheFromAddress()
    {
        var group = ReadAll("sms-20230611004105.xml")
            .Single(r => r.Address?.Contains('~') == true && r.Direction == Direction.Received);
        Assert.Equal("+15558675309", group.GroupSender);
    }

    [Fact]
    public void EmojiSurviveParsing()
    {
        var records = ReadAll("sms-20230611004105.xml");
        Assert.Contains(records, r => r.Body.Contains("\U0001F600"));
    }

    [Fact]
    public void OlderFilesWithoutNewerAttributesStillParse()
    {
        var records = ReadAll("sms-20210105004105.xml");
        Assert.Equal(3, records.Count);
        // thread_id and seen arrived later; their absence must not break anything.
        Assert.All(records, r => Assert.False(r.Extras.ContainsKey("thread_id")));
    }
}

public class MediaExtractionTests
{
    private static string Fixture(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
            directory = Path.GetDirectoryName(directory);
        return Path.Combine(directory!, "tests", "fixtures", name);
    }

    [Fact]
    public void AttachmentsAreDecodedToDisk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "smsviewer-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var media = new MediaExtractor(folder);
            var records = new BackupReader(media).Read(Fixture("sms-20230611004105.xml")).ToList();

            var image = records.SelectMany(r => r.Parts).First(p => p.IsImage);
            Assert.NotNull(image.MediaPath);
            Assert.True(File.Exists(image.MediaPath));
            Assert.True(image.DecodedBytes > 0);
            Assert.EndsWith(".jpg", image.MediaPath);

            // A one-pixel PNG payload; verify the decode produced the real bytes.
            var bytes = File.ReadAllBytes(image.MediaPath!);
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void IdenticalPayloadsAreStoredOnce()
    {
        var folder = Path.Combine(Path.GetTempPath(), "smsviewer-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var media = new MediaExtractor(folder);
            // The same image appears in both the 2021 and 2023 fixtures.
            _ = new BackupReader(media).Read(Fixture("sms-20210105004105.xml")).ToList();
            var afterFirst = media.FileCount;
            _ = new BackupReader(media).Read(Fixture("sms-20230611004105.xml")).ToList();

            var onDisk = Directory.GetFiles(folder).Length;
            Assert.Equal(media.FileCount, onDisk);
            Assert.True(media.FileCount > afterFirst - 1);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}

public class BackupIndexTests
{
    private static string Fixture(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
            directory = Path.GetDirectoryName(directory);
        return Path.Combine(directory!, "tests", "fixtures", name);
    }

    [Fact]
    public void FormattingVariantsOfANumberLandInOneConversation()
    {
        var index = new BackupIndex();
        foreach (var record in new BackupReader().Read(Fixture("sms-20230611004105.xml")))
            index.Add(record);
        index.Finalise();

        // "+1 555-867-5309" and "5558675309" must be the same thread.
        var thread = index.Conversations.Single(c => c.Key == AddressKey.Normalize("5558675309"));
        Assert.True(thread.TotalCount >= 2);
    }

    [Fact]
    public void RecordsAreSortedByDateWithinAConversation()
    {
        var index = new BackupIndex();
        foreach (var record in new BackupReader().Read(Fixture("calls-20230618004105.xml")))
            index.Add(record);
        index.Finalise();

        foreach (var conversation in index.Conversations)
        {
            var dates = conversation.Records.Select(r => r.DateMs).ToList();
            Assert.Equal(dates.OrderBy(d => d).ToList(), dates);
        }
    }

    [Fact]
    public void MessagesAndCallsShareOneConversationPerContact()
    {
        var index = new BackupIndex();
        foreach (var record in new BackupReader().Read(Fixture("sms-20210105004105.xml")))
            index.Add(record);
        foreach (var record in new BackupReader().Read(Fixture("calls-20210105004105.xml")))
            index.Add(record);
        index.Finalise();

        var thread = index.Conversations.Single(c => c.Key == AddressKey.Normalize("5558675309"));
        Assert.True(thread.SmsCount > 0);
        Assert.True(thread.CallCount > 0);
    }

    [Fact]
    public void RecordsWithNoAddressAreKeptRatherThanDropped()
    {
        var index = new BackupIndex();
        foreach (var record in new BackupReader().Read(Fixture("calls-20230618004105.xml")))
            index.Add(record);
        index.Finalise();

        // The fixture has one call with an empty number.
        Assert.Equal(1, index.UnaddressedRecords);
        Assert.Equal(5, index.Records.Count);
    }
}
