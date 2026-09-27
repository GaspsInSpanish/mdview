using System.Security.Cryptography;
using System.Text;

namespace MdView.Serving;

/// <summary>A document's decoded text plus what is needed to write it back unchanged.</summary>
internal sealed record DecodedDocument(string Text, string Hash, bool Editable, bool HasBom, string Newline);

internal enum SaveOutcome { Saved, Conflict, NotEditable }

/// <summary>
/// The edit-mode write path. The page sends the whole edited text; everything the page cannot
/// see — the byte-order mark, the encoding — is restored here from the file on disk. Line
/// endings are the page's job, because only the page knows which lines the user touched.
/// </summary>
internal static class DocumentEditService
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly object WriteGate = new();

    /// <summary>
    /// Decodes exactly as the reader always has (BOM detection, UTF-8 default). Only UTF-8
    /// that round-trips byte-for-byte is editable: a UTF-16 or legacy-codepage file would be
    /// silently transcoded by a save, so those stay read-only.
    /// </summary>
    internal static DecodedDocument Decode(byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        var isUtf16Or32 = !hasBom && bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
        string text;
        var editable = false;
        if (isUtf16Or32)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        else
        {
            var body = bytes.AsSpan(hasBom ? Utf8Bom.Length : 0);
            try
            {
                text = StrictUtf8.GetString(body);
                editable = true;
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.UTF8.GetString(body);
            }
        }
        return new DecodedDocument(text, hash, editable, hasBom, DominantNewline(text));
    }

    /// <summary>Reads a document with the same short retry the reader uses for locked files.</summary>
    internal static async Task<byte[]?> ReadBytesAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3) return null;
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1))).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> if the file still hashes to <paramref name="expectedHash"/>
    /// (the version the user started editing). <paramref name="force"/> skips only that check —
    /// the user has been told the file changed and chose to overwrite it.
    /// </summary>
    /// <exception cref="EncoderFallbackException">The text holds an unpaired surrogate.</exception>
    internal static SaveOutcome TrySave(string path, string text, string? expectedHash, bool force, out string newHash)
    {
        newHash = string.Empty;
        lock (WriteGate)
        {
            byte[]? current = null;
            try { current = File.ReadAllBytes(path); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }

            if (current is null)
            {
                if (!force) return SaveOutcome.Conflict;
            }
            else
            {
                var decoded = Decode(current);
                if (!force && !string.Equals(decoded.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return SaveOutcome.Conflict;
                if (!decoded.Editable) return SaveOutcome.NotEditable;
            }

            var body = StrictUtf8.GetBytes(text);
            var bytes = current is not null && current.AsSpan().StartsWith(Utf8Bom) ? [.. Utf8Bom, .. body] : body;
            WriteAtomically(path, bytes);
            newHash = Convert.ToHexString(SHA256.HashData(bytes));
            return SaveOutcome.Saved;
        }
    }

    /// <summary>Temp file in the same directory, then rename over the original, so a crash
    /// mid-write can never leave the user's document half-written.</summary>
    internal static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private static string DominantNewline(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            if (index > 0 && text[index - 1] == '\r') crlf++; else lf++;
        }
        return crlf > lf ? "\r\n" : "\n";
    }
}
