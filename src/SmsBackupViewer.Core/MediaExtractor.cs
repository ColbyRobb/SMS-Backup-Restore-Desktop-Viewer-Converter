using System.Security.Cryptography;

namespace SmsBackupViewer.Core;

/// <summary>A payload decoded to disk.</summary>
public readonly record struct DecodedPayload(string Path, long Bytes, string Sha256);

/// <summary>
/// Decodes an MMS part's base64 payload straight to disk.
///
/// A single attachment can be tens of megabytes of base64 on one line, so the value is never
/// materialised as a string: characters arrive in chunks and are decoded four at a time into a
/// file stream. Every payload is hashed as it is written, which is what makes duplicate
/// detection possible without re-reading anything.
/// </summary>
public sealed class MediaExtractor : IDisposable
{
    private readonly string _folder;
    private readonly Dictionary<string, string> _byHash = new(StringComparer.Ordinal);
    private readonly char[] _valid = new char[8192];
    private readonly char[] _carry = new char[4];
    private int _carryLength;

    public MediaExtractor(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public int FileCount => _byHash.Count;
    public long TotalBytes { get; private set; }

    /// <summary>
    /// Decodes into the shared folder under a content-hash name, storing identical payloads
    /// once. Used by the viewer, where the same photo may appear in many messages.
    /// </summary>
    public (string Path, long Bytes)? Extract(Func<char[], int, int> source, string? contentType, string? fileName)
    {
        var temp = Path.Combine(_folder, Path.GetRandomFileName());
        var decoded = DecodeToFile(source, temp);

        if (decoded.Bytes == 0)
        {
            TryDelete(temp);
            return null;
        }

        if (_byHash.TryGetValue(decoded.Sha256, out var existing))
        {
            TryDelete(temp);
            return (existing, decoded.Bytes);
        }

        var target = Path.Combine(_folder, decoded.Sha256[..16] + Extension(contentType, fileName));
        if (File.Exists(target)) TryDelete(target);
        File.Move(temp, target);

        _byHash[decoded.Sha256] = target;
        TotalBytes += decoded.Bytes;
        return (target, decoded.Bytes);
    }

    /// <summary>
    /// Decodes straight to a chosen path, with no deduplication and no staging copy.
    ///
    /// Used when exporting, where writing each payload once matters: staging and then copying
    /// would move every byte twice, which across a very large backup set is hours of needless
    /// disk traffic. The caller decides afterwards, using the returned hash, whether to keep it.
    /// </summary>
    public DecodedPayload? DecodeTo(Func<char[], int, int> source, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var decoded = DecodeToFile(source, targetPath);

        if (decoded.Bytes == 0)
        {
            TryDelete(targetPath);
            return null;
        }

        TotalBytes += decoded.Bytes;
        return decoded;
    }

    /// <summary>Streams base64 characters into a file, hashing as it goes.</summary>
    private DecodedPayload DecodeToFile(Func<char[], int, int> source, string path)
    {
        _carryLength = 0;
        long written;
        string hash;

        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                   bufferSize: 1 << 20, FileOptions.SequentialScan))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new char[8192];
            int read;

            while ((read = source(buffer, buffer.Length)) > 0)
            {
                var count = Sanitize(buffer, read);
                if (count == 0) continue;
                WriteDecoded(_valid, count, output, hasher);
            }

            // Any trailing characters that did not fill a group of four.
            if (_carryLength > 0)
            {
                try
                {
                    var tail = Convert.FromBase64String(
                        new string(_carry, 0, _carryLength).PadRight((_carryLength + 3) / 4 * 4, '='));
                    output.Write(tail);
                    hasher.AppendData(tail);
                }
                catch (FormatException)
                {
                    // Truncated payload. Keep what decoded rather than losing the whole part.
                }
                _carryLength = 0;
            }

            output.Flush();
            written = output.Length;
            hash = Convert.ToHexString(hasher.GetHashAndReset());
        }

        return new DecodedPayload(path, written, hash);
    }

    /// <summary>Copies base64 characters, dropping whitespace that some writers insert.</summary>
    private int Sanitize(char[] buffer, int length)
    {
        var count = 0;
        for (var i = 0; i < length; i++)
        {
            var c = buffer[i];
            if (c is ' ' or '\r' or '\n' or '\t') continue;
            _valid[count++] = c;
        }
        return count;
    }

    private void WriteDecoded(char[] chars, int count, Stream output, IncrementalHash hasher)
    {
        var offset = 0;

        // Complete the group left over from the previous chunk before doing anything else.
        while (_carryLength is > 0 and < 4 && offset < count)
            _carry[_carryLength++] = chars[offset++];

        if (_carryLength == 4)
        {
            try
            {
                var bytes = Convert.FromBase64CharArray(_carry, 0, 4);
                output.Write(bytes);
                hasher.AppendData(bytes);
            }
            catch (FormatException) { /* skip the malformed group */ }
            _carryLength = 0;
        }

        var remaining = count - offset;
        var whole = remaining / 4 * 4;
        if (whole > 0)
        {
            try
            {
                var produced = Convert.FromBase64CharArray(chars, offset, whole);
                output.Write(produced);
                hasher.AppendData(produced);
            }
            catch (FormatException)
            {
                // A malformed run. Decoding stops for this group but the file keeps what it has.
            }
            offset += whole;
        }

        while (offset < count)
            _carry[_carryLength++] = chars[offset++];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    /// <summary>
    /// Picks a file extension from the content type, falling back to the stored filename and
    /// finally to .bin. Unknown types still get extracted — they are named from whatever the
    /// backup called them rather than being skipped.
    /// </summary>
    public static string Extension(string? contentType, string? fileName)
    {
        var mapped = contentType?.Trim().ToLowerInvariant() switch
        {
            // Images
            "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" or "image/x-ms-bmp" => ".bmp",
            "image/heic" or "image/heif" => ".heic",
            "image/avif" => ".avif",
            "image/tiff" => ".tiff",
            "image/svg+xml" => ".svg",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",

            // Video
            "video/mp4" or "video/mpeg4" or "video/x-m4v" => ".mp4",
            "video/3gpp" => ".3gp",
            "video/3gpp2" => ".3g2",
            "video/quicktime" => ".mov",
            "video/x-matroska" => ".mkv",
            "video/webm" => ".webm",
            "video/x-msvideo" => ".avi",
            "video/x-ms-wmv" => ".wmv",
            "video/mpeg" => ".mpg",
            "video/ogg" => ".ogv",

            // Audio
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/aac" or "audio/mp4" or "audio/mp4a-latm" or "audio/x-m4a" => ".m4a",
            "audio/amr" or "audio/amr-wb" or "audio/3gpp" => ".amr",
            "audio/ogg" or "audio/vorbis" => ".ogg",
            "audio/opus" => ".opus",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/wav" or "audio/x-wav" or "audio/vnd.wave" => ".wav",
            "audio/midi" or "audio/x-midi" => ".mid",
            "audio/webm" => ".weba",

            // Contacts, calendar, documents
            "text/x-vcard" or "text/vcard" => ".vcf",
            "text/x-vcalendar" or "text/calendar" => ".ics",
            "application/pdf" => ".pdf",
            "text/plain" => ".txt",
            "text/html" => ".html",
            "text/xml" or "application/xml" => ".xml",
            "application/json" => ".json",
            "application/zip" => ".zip",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.android.package-archive" => ".apk",
            _ => null,
        };

        if (mapped is not null) return mapped;

        // Unrecognised type: trust the filename the backup carried.
        if (!string.IsNullOrWhiteSpace(fileName) && fileName != "null")
        {
            var fromName = Path.GetExtension(fileName);
            if (fromName.Length is > 1 and <= 8) return fromName.ToLowerInvariant();
        }

        // Last resort: derive something readable from the subtype, e.g. image/x-foo -> .foo
        var slash = contentType?.LastIndexOf('/') ?? -1;
        if (slash >= 0 && contentType!.Length > slash + 1)
        {
            var subtype = contentType[(slash + 1)..].Trim().ToLowerInvariant();
            if (subtype.StartsWith("x-")) subtype = subtype[2..];
            if (subtype.Length is > 0 and <= 6 && subtype.All(char.IsAsciiLetterOrDigit))
                return "." + subtype;
        }

        return ".bin";
    }

    public void Dispose() { }
}
