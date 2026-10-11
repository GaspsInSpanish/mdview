using System.Text.Json.Nodes;

namespace MdView.Serving;

// X and Y are screen coordinates. Entries without the screen marker predate this definition and are ignored.
public readonly record struct SavedWindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

public sealed class WindowPlacementStore
{
    private const int MaximumDimension = 100_000;
    private readonly ConfigFileStore config;

    public WindowPlacementStore(string? stateDirectory = null)
    {
        var directory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mdview");
        config = new ConfigFileStore(Path.Combine(directory, "config.json"));
    }

    public SavedWindowPlacement? Read()
    {
        return config.Read(root =>
        {
            if (root?["window"] is not JsonObject window ||
                window["coordinateSpace"] is not JsonValue coordinateSpace ||
                !coordinateSpace.TryGetValue<string>(out var space) || space != "screen" ||
                !TryInt(window, "x", out var x) || !TryInt(window, "y", out var y) ||
                !TryInt(window, "width", out var width) || !TryInt(window, "height", out var height) ||
                window["maximized"] is not JsonValue maximizedValue ||
                !maximizedValue.TryGetValue<bool>(out var maximized) ||
                width < WindowPlacementRules.MinimumWidth || height < WindowPlacementRules.MinimumHeight ||
                width > MaximumDimension || height > MaximumDimension ||
                (long)x + width > int.MaxValue || (long)y + height > int.MaxValue)
                return (SavedWindowPlacement?)null;
            return new SavedWindowPlacement(x, y, width, height, maximized);
        });
    }

    public void Write(SavedWindowPlacement placement)
    {
        try
        {
            config.Update(root => root["window"] = new JsonObject
            {
                ["x"] = placement.X,
                ["y"] = placement.Y,
                ["width"] = placement.Width,
                ["height"] = placement.Height,
                ["maximized"] = placement.Maximized,
                ["coordinateSpace"] = "screen"
            }, skipIfUnreadable: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Placement is cosmetic; close must continue if its best-effort save cannot complete.
        }
    }

    private static bool TryInt(JsonObject value, string name, out int result)
    {
        result = 0;
        return value[name] is JsonValue json && json.TryGetValue(out result);
    }
}

public static class WindowPlacementRules
{
    public const int MinimumWidth = 300;
    public const int MinimumHeight = 200;

    public static bool CanRestore(SavedWindowPlacement placement, Func<int, int, int, int, bool> intersectsMonitor) =>
        placement.Width >= MinimumWidth && placement.Height >= MinimumHeight &&
        intersectsMonitor(placement.X, placement.Y, placement.Width, placement.Height);

    public static bool TryApply(Action apply)
    {
        try { apply(); return true; }
        catch { return false; }
    }

    public static (int X, int Y) WorkspaceToScreen(
        int x, int y, int monitorLeft, int monitorTop, int workLeft, int workTop) =>
        (x + workLeft - monitorLeft, y + workTop - monitorTop);

    public static SavedWindowPlacement Cascade(
        SavedWindowPlacement placement, IReadOnlyCollection<(int X, int Y)> occupiedTopLefts,
        int offset, int workLeft, int workTop, int workRight, int workBottom)
    {
        var maximumX = Math.Max(workLeft, workRight - placement.Width);
        var maximumY = Math.Max(workTop, workBottom - placement.Height);
        var x = Math.Clamp(placement.X, workLeft, maximumX);
        var y = Math.Clamp(placement.Y, workTop, maximumY);
        while (IsOccupied(x, y, occupiedTopLefts, offset) && x + offset <= maximumX && y + offset <= maximumY)
        {
            x += offset;
            y += offset;
        }
        if (IsOccupied(x, y, occupiedTopLefts, offset))
        {
            for (var candidateY = workTop; candidateY <= maximumY; candidateY += offset)
            {
                for (var candidateX = workLeft; candidateX <= maximumX; candidateX += offset)
                {
                    if (IsOccupied(candidateX, candidateY, occupiedTopLefts, offset)) continue;
                    return placement with { X = candidateX, Y = candidateY };
                }
            }
        }
        return placement with { X = x, Y = y };
    }

    private static bool IsOccupied(int x, int y, IReadOnlyCollection<(int X, int Y)> occupied, int offset) =>
        occupied.Any(point => Math.Abs((long)point.X - x) <= offset / 2 &&
            Math.Abs((long)point.Y - y) <= offset / 2);
}
