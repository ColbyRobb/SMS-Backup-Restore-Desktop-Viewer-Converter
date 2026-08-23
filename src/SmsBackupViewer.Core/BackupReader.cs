using System.Xml;

namespace SmsBackupViewer.Core;

/// <summary>Progress during a read, reported often enough for a responsive bar.</summary>
public readonly record struct ReadProgress(long BytesRead, long TotalBytes, int RecordsRead)
{
    public double Fraction => TotalBytes <= 0 ? 0 : (double)BytesRead / TotalBytes;
}

/// <summary>
/// Streams a backup file into records.
///
/// Uses a pull parser throughout: backups reach several gigabytes and a single MMS payload can
/// be tens of megabytes, so nothing is buffered whole. Every attribute is optional on every
/// record — presence varies by phone, Android version and app version across a multi-year set —
/// so nothing here requires a field to exist, and anything not interpreted is kept in Extras.
/// </summary>
public sealed class BackupReader
{
    /// <summary>Attributes the viewer maps to typed fields, and so does not repeat in Extras.</summary>
    private static readonly HashSet<string> MappedSms = new(StringComparer.Ordinal)
        { "address", "date", "type", "body", "contact_name" };

    private static readonly HashSet<string> MappedMms = new(StringComparer.Ordinal)
        { "address", "date", "msg_box", "m_type", "contact_name" };

    private static readonly HashSet<string> MappedCall = new(StringComparer.Ordinal)
        { "number", "date", "type", "duration", "contact_name" };

    private readonly MediaExtractor? _media;

    /// <summary>
    /// Chooses where a part's payload should be written, given the record's position in the file
    /// and the part's position in the record. Returning null skips the payload entirely, so it is
    /// never decoded.
    ///
    /// Exporting works this way because a record's conversation is not always knowable while its
    /// parts are being read — an RCS message's real participants live in the addr elements that
    /// follow its parts — so the destination is decided by an earlier scanning pass and looked up
    /// here by position.
    /// </summary>
    private readonly Func<int, int, string?>? _resolveTarget;

    /// <summary>
    /// When false, the verbatim attribute dictionaries are not retained. A scanning pass over a
    /// very large backup set does not need them, and they are by far the largest per-record cost.
    /// </summary>
    private readonly bool _captureAttributes;

    private int _ordinal;
    private int _currentOrdinal;

    public BackupReader(
        MediaExtractor? media = null,
        Func<int, int, string?>? resolveTarget = null,
        bool captureAttributes = true)
    {
        _media = media;
        _resolveTarget = resolveTarget;
        _captureAttributes = captureAttributes;
    }

    private static readonly Dictionary<string, string> NoAttributes =
        new(StringComparer.Ordinal);

    private Dictionary<string, string> Keep(Dictionary<string, string> attributes) =>
        _captureAttributes ? attributes : NoAttributes;

    private Dictionary<string, string> KeepExtras(Dictionary<string, string> all, HashSet<string> mapped) =>
        _captureAttributes ? ExtrasFrom(all, mapped) : NoAttributes;

    /// <summary>Records that could not be attributed to any address, kept for the anomaly count.</summary>
    public int SkippedRecords { get; private set; }

    public IEnumerable<MessageRecord> Read(
        string path,
        IProgress<ReadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,   // no external entities
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            CheckCharacters = false,                  // real backups contain lone surrogates
        };

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan);
        using var reader = XmlReader.Create(stream, settings);

        var total = stream.Length;
        var count = 0;
        var lastReported = 0L;
        _ordinal = 0;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name is not ("sms" or "mms" or "call")) continue;
            _currentOrdinal = _ordinal++;

            MessageRecord? record = reader.Name switch
            {
                "sms" => ReadSms(reader),
                "mms" => ReadMms(reader),
                _ => ReadCall(reader),
            };

            if (record is null) continue;
            count++;

            if (progress is not null && stream.Position - lastReported > 4L * 1024 * 1024)
            {
                lastReported = stream.Position;
                progress.Report(new ReadProgress(stream.Position, total, count));
            }

            yield return record;
        }

        progress?.Report(new ReadProgress(total, total, count));
    }

    /// <summary>Reads every attribute of the current element into a dictionary.</summary>
    private static Dictionary<string, string> ReadAttributes(XmlReader reader)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!reader.HasAttributes) return attributes;

        for (var i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.Name == "data") continue;   // streamed separately, never materialised
            attributes[reader.Name] = reader.Value;
        }
        reader.MoveToElement();
        return attributes;
    }

    private static Dictionary<string, string> ExtrasFrom(
        Dictionary<string, string> all, HashSet<string> mapped)
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in all)
            if (!mapped.Contains(key))
                extras[key] = value;
        return extras;
    }

    private static string? Value(Dictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && value != "null" ? value : null;

    private static long DateOf(Dictionary<string, string> attributes) =>
        long.TryParse(Value(attributes, "date"), out var ms) ? ms : 0;

    private static int? IntOf(Dictionary<string, string> attributes, string name) =>
        int.TryParse(Value(attributes, name), out var value) ? value : null;

    private MessageRecord ReadSms(XmlReader reader)
    {
        var attributes = ReadAttributes(reader);
        var address = Value(attributes, "address");

        return new MessageRecord
        {
            Kind = RecordKind.Sms,
            Ordinal = _currentOrdinal,
            DateMs = DateOf(attributes),
            Address = address,
            ThreadKey = AddressKey.ThreadKey(address),
            ContactName = Value(attributes, "contact_name"),
            TypeCode = IntOf(attributes, "type"),
            Direction = SmsDirection(IntOf(attributes, "type")),
            Body = attributes.GetValueOrDefault("body") is { } body && body != "null" ? body : "",
            Extras = KeepExtras(attributes, MappedSms),
            AllAttributes = Keep(attributes),
        };
    }

    private MessageRecord ReadCall(XmlReader reader)
    {
        var attributes = ReadAttributes(reader);
        var number = Value(attributes, "number");

        return new MessageRecord
        {
            Kind = RecordKind.Call,
            Ordinal = _currentOrdinal,
            DateMs = DateOf(attributes),
            Address = number,
            ThreadKey = AddressKey.ThreadKey(number),
            ContactName = Value(attributes, "contact_name"),
            TypeCode = IntOf(attributes, "type"),
            Direction = CallDirection(IntOf(attributes, "type")),
            DurationSeconds = IntOf(attributes, "duration"),
            Body = "",
            Extras = KeepExtras(attributes, MappedCall),
            AllAttributes = Keep(attributes),
        };
    }

    /// <summary>
    /// Reads an MMS and its children.
    ///
    /// Attribute order matters: every attribute of the mms element is read before descending,
    /// because moving into the children advances the parser past them permanently.
    /// </summary>
    private MessageRecord ReadMms(XmlReader reader)
    {
        var attributes = ReadAttributes(reader);
        var isEmpty = reader.IsEmptyElement;

        var parts = new List<MmsPart>();
        var addresses = new List<MmsAddress>();

        if (!isEmpty)
        {
            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth <= depth) break;
                if (reader.NodeType != XmlNodeType.Element) continue;

                switch (reader.Name)
                {
                    case "part": parts.Add(ReadPart(reader, parts.Count)); break;
                    case "addr": addresses.Add(ReadAddr(reader)); break;
                    // A malformed file can drop us into the next record; stop rather than merge.
                    case "sms" or "mms" or "call": goto done;
                }
            }
        }
        done:

        var rawAddress = Value(attributes, "address");
        var messageType = IntOf(attributes, "m_type");

        // On some RCS threads the address attribute is a placeholder and the real participants
        // are only in the addr elements.
        var threadKey = AddressKey.IsRcsPlaceholder(rawAddress)
            ? AddressKey.ThreadKeyFromAddresses(addresses)
            : AddressKey.ThreadKey(rawAddress);
        if (threadKey.Length == 0) threadKey = AddressKey.ThreadKeyFromAddresses(addresses);

        var msgBox = IntOf(attributes, "msg_box");
        var sender = msgBox == 1
            ? addresses.FirstOrDefault(a => a.IsSender && !a.IsDeviceOwner)?.Address
            : null;

        // Text parts make up the body, in the order the file gives them.
        var text = string.Join("\n", parts
            .Where(p => p.IsText && !string.IsNullOrEmpty(p.Text) && p.Text != "null")
            .OrderBy(p => p.Seq ?? int.MaxValue)
            .Select(p => p.Text));

        return new MessageRecord
        {
            Kind = RecordKind.Mms,
            Ordinal = _currentOrdinal,
            DateMs = DateOf(attributes),
            Address = rawAddress,
            ThreadKey = threadKey,
            ContactName = Value(attributes, "contact_name"),
            TypeCode = msgBox,
            Direction = MmsDirection(msgBox),
            IsDeliveryReceipt = messageType == 134,
            GroupSender = sender,
            Body = text,
            Parts = parts,
            Addresses = addresses,
            Extras = KeepExtras(attributes, MappedMms),
            AllAttributes = Keep(attributes),
        };
    }

    private MmsPart ReadPart(XmlReader reader, int partIndex)
    {
        var attributes = ReadAttributes(reader);
        var contentType = Value(attributes, "ct");
        var fileName = Value(attributes, "cl") ?? Value(attributes, "name");

        var part = new MmsPart
        {
            Seq = IntOf(attributes, "seq"),
            ContentType = contentType,
            FileName = fileName,
            Text = attributes.GetValueOrDefault("text"),
            AllAttributes = Keep(attributes),
        };

        // Decode the payload straight to disk. Done last so the other attributes are already read.
        // Decoded last, so the part's other attributes have already been read.
        if (_media is not null && part.IsAttachment && reader.MoveToAttribute("data"))
        {
            if (_resolveTarget is not null)
            {
                // Export: write to the path the scanning pass chose, or skip without decoding.
                var target = _resolveTarget(_currentOrdinal, partIndex);
                if (target is not null &&
                    _media.DecodeTo((buffer, length) => reader.ReadValueChunk(buffer, 0, length), target)
                        is { } decoded)
                {
                    part.MediaPath = decoded.Path;
                    part.DecodedBytes = decoded.Bytes;
                    part.Sha256 = decoded.Sha256;
                }
            }
            else if (_media.Extract(
                         (buffer, length) => reader.ReadValueChunk(buffer, 0, length),
                         contentType, fileName) is { } result)
            {
                // Viewer: shared folder, identical payloads stored once.
                part.MediaPath = result.Path;
                part.DecodedBytes = result.Bytes;
            }
            reader.MoveToElement();
        }

        return part;
    }

    private static MmsAddress ReadAddr(XmlReader reader)
    {
        var attributes = ReadAttributes(reader);
        return new MmsAddress
        {
            Address = Value(attributes, "address") ?? "",
            Type = IntOf(attributes, "type"),
        };
    }

    // Unknown codes map to Unknown rather than throwing: new codes appear over time, and a
    // record with an unrecognised type must still be shown.
    private static Direction SmsDirection(int? type) => type switch
    {
        1 => Direction.Received,
        2 => Direction.Sent,
        3 => Direction.Draft,
        4 => Direction.Outbox,
        5 => Direction.Failed,
        6 => Direction.Queued,
        _ => Direction.Unknown,
    };

    private static Direction MmsDirection(int? msgBox) => msgBox switch
    {
        1 => Direction.Received,
        2 => Direction.Sent,
        3 => Direction.Draft,
        4 => Direction.Outbox,
        _ => Direction.Unknown,
    };

    private static Direction CallDirection(int? type) => type switch
    {
        1 => Direction.Received,
        2 => Direction.Sent,
        3 => Direction.Missed,
        4 => Direction.Voicemail,
        5 => Direction.Rejected,
        6 => Direction.Blocked,
        _ => Direction.Unknown,
    };
}
