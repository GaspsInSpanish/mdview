using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MdView.Serving;

internal static class TaskToggleService
{
    private static readonly Regex Marker = new(@"^(?:[ \t]*[-*+] |[ \t]*\d+[.)] )[ \t]*\[(?<state> |x|X)\]", RegexOptions.CultureInvariant);

    internal static bool TryToggle(string path, int lineNumber, bool expectedChecked, out byte[] hash)
    {
        hash = [];
        var bytes = File.ReadAllBytes(path);
        var start = 0;
        for (var line = 1; line < lineNumber; line++) { var nl = Array.IndexOf(bytes, (byte)'\n', start); if (nl < 0) return false; start = nl + 1; }
        var end = Array.IndexOf(bytes, (byte)'\n', start); if (end < 0) end = bytes.Length;
        var length = end - start; if (length > 0 && bytes[start + length - 1] == (byte)'\r') length--;
        var text = System.Text.Encoding.UTF8.GetString(bytes, start, length);
        var match = Marker.Match(text);
        if (!match.Success || !Rendering.MarkdownRenderer.IsExpectedTask(System.Text.Encoding.UTF8.GetString(bytes), lineNumber, expectedChecked)) return false;
        var offset = start + System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, match.Groups["state"].Index));
        bytes[offset] = expectedChecked ? (byte)' ' : (byte)'x';
        hash = SHA256.HashData(bytes);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); } finally { try { File.Delete(temp); } catch (IOException) { } }
        return true;
    }
}
