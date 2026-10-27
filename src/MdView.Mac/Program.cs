using MdView.App;
using MdView.Serving;
using System.Runtime.InteropServices;

namespace MdView.Mac;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("mdview's Mac host can only run on macOS.");
            return 1;
        }
        try
        {
            NativeLibraryBootstrap.Load();
            SynchronizationContext.SetSynchronizationContext(new MacSynchronizationContext());
            string? initialPath = null;
            var validationError = string.Empty;
            if (args.Length > 1 || args.Length == 1 && !TryValidatePath(args[0], out initialPath, out validationError))
            {
                MacApplication.ShowError(args.Length > 1 ? "mdview expects at most one Markdown file path." : validationError);
                return 1;
            }

            using var coordinator = new InstanceCoordinator();
            if (initialPath is null && !coordinator.IsPrimary) return 0;
            if (initialPath is not null && !coordinator.IsPrimary)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                do
                {
                    var result = HandOffPipeClient.TrySendDetailedAsync(initialPath).GetAwaiter().GetResult();
                    if (result == HandOffResult.Accepted) return 0;
                    if (coordinator.TryBecomePrimary()) break;
                    if (DateTime.UtcNow >= deadline)
                    {
                        MacApplication.ShowError("Another mdview is running but did not accept the file.");
                        return 1;
                    }
                    Thread.Sleep(150);
                } while (true);
            }

            return MacApplication.Run(coordinator, initialPath);
        }
        catch (Exception exception)
        {
            MacApplication.ShowError($"mdview could not start: {exception.Message}");
            return 1;
        }
    }

    internal static bool TryValidatePath(string pathArgument, out string path, out string error)
    {
        path = string.Empty;
        try { path = Path.GetFullPath(pathArgument); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { error = "The file path is not valid."; return false; }
        if (!File.Exists(path)) { error = $"The file does not exist: {path}"; return false; }
        if (!DocumentKinds.TryGetKind(path, out _))
        { error = $"This file type is not supported: {Path.GetExtension(path)}"; return false; }
        error = string.Empty;
        return true;
    }
}

internal static class NativeLibraryBootstrap
{
    private static readonly List<nint> Handles = [];
    internal static void Load()
    {
        Handles.Add(NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"));
        Handles.Add(NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit"));
    }
}
