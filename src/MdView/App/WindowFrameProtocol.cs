using System.Text.Json;
using System.Text.RegularExpressions;

namespace MdView.App;

public enum FrameMessageKind { Ignore, WindowCommand, FrameColors }

public sealed record FrameColors(string Face, string HiOuter, string HiInner, string LoInner, string LoOuter);

public sealed record FrameMessage(FrameMessageKind Kind, string? Command = null, FrameColors? Colors = null);

public readonly record struct FrameRectangle(int Left, int Top, int Right, int Bottom);

public static class AutoHideTaskbarInsets
{
    public static FrameRectangle Apply(FrameRectangle rectangle, bool left, bool top, bool right, bool bottom) =>
        new(rectangle.Left + (left ? 1 : 0), rectangle.Top + (top ? 1 : 0),
            rectangle.Right - (right ? 1 : 0), rectangle.Bottom - (bottom ? 1 : 0));
}

public static partial class WindowFrameProtocol
{
    public static FrameMessage Parse(string source, string json, string origin)
    {
        if (!source.StartsWith(origin + "/", StringComparison.Ordinal) &&
            !string.Equals(source, origin, StringComparison.Ordinal)) return new(FrameMessageKind.Ignore);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
                return new(FrameMessageKind.Ignore);
            if (type.GetString() == "window" && root.TryGetProperty("command", out var command) &&
                root.EnumerateObject().Count() == 2 &&
                command.GetString() is "minimize" or "maximize" or "restore" or "close")
                return new(FrameMessageKind.WindowCommand, command.GetString());
            if (type.GetString() != "frame-colors") return new(FrameMessageKind.Ignore);
            string[] names = ["face", "hiOuter", "hiInner", "loInner", "loOuter"];
            if (root.EnumerateObject().Count() != names.Length + 1) return new(FrameMessageKind.Ignore);
            var values = new string[5];
            for (var index = 0; index < names.Length; index++)
            {
                if (!root.TryGetProperty(names[index], out var value) || value.ValueKind != JsonValueKind.String ||
                    !ColorPattern().IsMatch(values[index] = value.GetString()!)) return new(FrameMessageKind.Ignore);
            }
            return new(FrameMessageKind.FrameColors, Colors: new(values[0], values[1], values[2], values[3], values[4]));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { return new(FrameMessageKind.Ignore); }
    }

    [GeneratedRegex(@"\A#[0-9A-Fa-f]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
}

public static class WindowFrameHitTest
{
    public const int Client = 1, Left = 10, Right = 11, Top = 12, TopLeft = 13,
        TopRight = 14, Bottom = 15, BottomLeft = 16, BottomRight = 17;

    public static int Test(int x, int y, int width, int height, int dpi, bool maximized)
    {
        if (maximized || width <= 0 || height <= 0) return Client;
        var band = Math.Max(4, (int)Math.Ceiling(4 * dpi / 96.0));
        var corner = Math.Max(8, (int)Math.Ceiling(8 * dpi / 96.0));
        var left = x < band; var right = x >= width - band;
        var top = y < band; var bottom = y >= height - band;
        if (top && x < corner || left && y < corner) return TopLeft;
        if (top && x >= width - corner || right && y < corner) return TopRight;
        if (bottom && x < corner || left && y >= height - corner) return BottomLeft;
        if (bottom && x >= width - corner || right && y >= height - corner) return BottomRight;
        if (left) return Left; if (right) return Right; if (top) return Top; if (bottom) return Bottom;
        return Client;
    }
}
