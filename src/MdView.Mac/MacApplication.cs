using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MdView.App;
using MdView.Serving;

namespace MdView.Mac;

internal static unsafe class MacApplication
{
    private const string Origin = "mdview://app";
    private static readonly nint ApplicationClass = RegisterDelegate();
    private static readonly ConcurrentDictionary<nint, byte> Delegates = new();
    private static nint application;
    private static DocumentHost? host;
    private static Lifecycle? lifecycle;
    private static MacWindowLauncher? launcher;
    private static nint appDelegate;
    private static bool receivedOpen;

    internal static int Run(InstanceCoordinator coordinator, string? initialPath)
    {
        application = ObjectiveC.Send(ObjectiveC.Class("NSApplication"), ObjectiveC.Sel("sharedApplication"));
        ObjectiveC.SendULong(application, ObjectiveC.Sel("setActivationPolicy:"), 0);
        appDelegate = ObjectiveC.New("MdViewApplicationDelegate");
        Delegates.TryAdd(appDelegate, 0);
        ObjectiveC.SendVoid(application, ObjectiveC.Sel("setDelegate:"), appDelegate);
        BuildMenu();

        host = new DocumentHost(() => Origin, fileDialogs: new MacFileDialogProvider(), appendTitleBar: false, platform: "mac");
        launcher = new MacWindowLauncher(host, ShowWarning);
        var handOff = new HandOffPipeServer(host, accept: opened => lifecycle?.TryAcceptHandOff(opened) == true,
            reportError: ShowWarning);
        lifecycle = new Lifecycle(host, handOff, launcher, coordinator, launcher,
            gracePeriod: TimeSpan.Zero, reportMessage: ShowWarning, documentOrigin: Origin);

        if (initialPath is not null)
        {
            receivedOpen = true;
            if (!lifecycle.TryLaunch(host.RegisterDocument(initialPath))) return 1;
        }
        var mainContext = SynchronizationContext.Current;
        _ = lifecycle.Completion.ContinueWith(_ =>
            mainContext?.Post(_ => ObjectiveC.SendVoid(application, ObjectiveC.Sel("terminate:"), application), null),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        ObjectiveC.SendVoid(application, ObjectiveC.Sel("activateIgnoringOtherApps:"), (byte)1);
        ObjectiveC.SendVoid(application, ObjectiveC.Sel("run"));
        lifecycle.Dispose();
        return 0;
    }

    internal static void Report(string message) => Console.Error.WriteLine(message);
    internal static void ShowError(string message) => ShowAlert(message, 2);
    internal static void ShowWarning(string message) => ShowAlert(message, 0);

    private static void ShowAlert(string message, long style)
    {
        if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine(message); return; }
        var pool = ObjectiveC.objc_autoreleasePoolPush();
        try
        {
            var alert = ObjectiveC.New("NSAlert");
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setMessageText:"), ObjectiveC.String("mdview"));
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setInformativeText:"), ObjectiveC.String(message));
            ObjectiveC.Send(alert, ObjectiveC.Sel("setAlertStyle:"), (nint)style);
            ObjectiveC.SendLong(alert, ObjectiveC.Sel("runModal"));
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("release"));
        }
        finally { ObjectiveC.objc_autoreleasePoolPop(pool); }
    }

    private static nint RegisterDelegate()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        var cls = ObjectiveC.objc_allocateClassPair(ObjectiveC.Class("NSObject"), "MdViewApplicationDelegate", 0);
        ObjectiveC.class_addProtocol(cls, ObjectiveC.objc_getProtocol("NSApplicationDelegate"));
        ObjectiveC.class_addMethod1(cls, ObjectiveC.Sel("applicationDidFinishLaunching:"), &DidFinish, "v@:@");
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("application:openURLs:"), &OpenUrls, "v@:@@");
        ObjectiveC.class_addMethodBool(cls, ObjectiveC.Sel("applicationShouldTerminateAfterLastWindowClosed:"), &ShouldTerminate, "B@:@");
        ObjectiveC.class_addMethod1(cls, ObjectiveC.Sel("mdviewQuit:"), &Quit, "v@:@");
        ObjectiveC.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static void DidFinish(nint self, nint command, nint notification)
    {
        var context = SynchronizationContext.Current;
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => context?.Post(_ =>
        {
            if (receivedOpen || lifecycle?.IsShuttingDown == true) return;
            var path = MacFileDialogProvider.OpenPath();
            if (path is null) lifecycle?.Shutdown();
            else OpenPath(path);
        }, null), TaskScheduler.Default);
    }

    [UnmanagedCallersOnly]
    private static void OpenUrls(nint self, nint command, nint app, nint urls)
    {
        var pool = ObjectiveC.objc_autoreleasePoolPush();
        try
        {
            receivedOpen = true;
            var count = ObjectiveC.SendLong(urls, ObjectiveC.Sel("count"));
            for (long index = 0; index < count; index++)
            {
                var url = ObjectiveC.Send(urls, ObjectiveC.Sel("objectAtIndex:"), (nint)index);
                var path = ObjectiveC.GetString(ObjectiveC.Send(url, ObjectiveC.Sel("path")));
                if (path is not null) OpenPath(path);
            }
        }
        catch (Exception exception) { ShowWarning(exception.Message); }
        finally { ObjectiveC.objc_autoreleasePoolPop(pool); }
    }

    private static void OpenPath(string candidate)
    {
        if (!Program.TryValidatePath(candidate, out var path, out var error)) { ShowWarning(error); return; }
        if (host?.OpenFromHandOff(path, opened => lifecycle?.TryAcceptHandOff(opened) == true) != true)
            ShowWarning("The Markdown file could not be opened.");
    }

    [UnmanagedCallersOnly]
    private static byte ShouldTerminate(nint self, nint command, nint sender) => 1;

    [UnmanagedCallersOnly]
    private static void Quit(nint self, nint command, nint sender) =>
        launcher?.RequestExit(() => lifecycle?.Shutdown());

    private static void BuildMenu()
    {
        var main = ObjectiveC.New("NSMenu");
        AddSubmenu(main, "mdview", [
            ("About mdview", "orderFrontStandardAboutPanel:", ""), ("-", "", ""),
            ("Hide mdview", "hide:", "h"), ("Quit mdview", "mdviewQuit:", "q")]);
        AddSubmenu(main, "File", [("Close Window", "performClose:", "w")]);
        AddSubmenu(main, "Edit", [("Undo", "undo:", "z"), ("Redo", "redo:", "Z"), ("-", "", ""),
            ("Cut", "cut:", "x"), ("Copy", "copy:", "c"), ("Paste", "paste:", "v"), ("Select All", "selectAll:", "a")]);
        AddSubmenu(main, "Window", [("Minimize", "performMiniaturize:", "m")]);
        ObjectiveC.SendVoid(application, ObjectiveC.Sel("setMainMenu:"), main);
    }

    private static void AddSubmenu(nint main, string title, (string Label, string Action, string Key)[] entries)
    {
        var root = ObjectiveC.Send(ObjectiveC.Send(ObjectiveC.Class("NSMenuItem"), ObjectiveC.Sel("alloc")),
            ObjectiveC.Sel("initWithTitle:action:keyEquivalent:"), ObjectiveC.String(title), 0, ObjectiveC.String(""));
        var menu = ObjectiveC.Send(ObjectiveC.Send(ObjectiveC.Class("NSMenu"), ObjectiveC.Sel("alloc")),
            ObjectiveC.Sel("initWithTitle:"), ObjectiveC.String(title));
        foreach (var entry in entries)
        {
            if (entry.Label == "-") { ObjectiveC.SendVoid(menu, ObjectiveC.Sel("addItem:"), ObjectiveC.Send(ObjectiveC.Class("NSMenuItem"), ObjectiveC.Sel("separatorItem"))); continue; }
            var item = ObjectiveC.Send(ObjectiveC.Send(ObjectiveC.Class("NSMenuItem"), ObjectiveC.Sel("alloc")),
                ObjectiveC.Sel("initWithTitle:action:keyEquivalent:"), ObjectiveC.String(entry.Label),
                ObjectiveC.Sel(entry.Action), ObjectiveC.String(entry.Key));
            if (entry.Action == "mdviewQuit:") ObjectiveC.SendVoid(item, ObjectiveC.Sel("setTarget:"), appDelegate);
            ObjectiveC.SendVoid(menu, ObjectiveC.Sel("addItem:"), item);
        }
        ObjectiveC.SendVoid(root, ObjectiveC.Sel("setSubmenu:"), menu);
        ObjectiveC.SendVoid(main, ObjectiveC.Sel("addItem:"), root);
    }
}

internal sealed class MacFileDialogProvider : IFileDialogProvider
{
    public Task<string?> ShowOpenAsync(string initialDirectory, CancellationToken cancellationToken) =>
        Task.FromResult(OpenPath(initialDirectory));
    public Task<string?> ShowSaveAsAsync(string initialDirectory, string suggestedFileName, CancellationToken cancellationToken) =>
        Task.FromResult(SavePath(suggestedFileName, initialDirectory));
    private static string? SavePath(string suggestedName, string? initialDirectory)
    {
        var panel = ObjectiveC.Send(ObjectiveC.Class("NSSavePanel"), ObjectiveC.Sel("savePanel"));
        Configure(panel, initialDirectory);
        ObjectiveC.SendVoid(panel, ObjectiveC.Sel("setNameFieldStringValue:"), ObjectiveC.String(suggestedName));
        return ObjectiveC.SendLong(panel, ObjectiveC.Sel("runModal")) == 1
            ? ObjectiveC.GetString(ObjectiveC.Send(ObjectiveC.Send(panel, ObjectiveC.Sel("URL")), ObjectiveC.Sel("path"))) : null;
    }
    internal static string? OpenPath(string? initialDirectory = null)
    {
        var panel = ObjectiveC.Send(ObjectiveC.Class("NSOpenPanel"), ObjectiveC.Sel("openPanel"));
        Configure(panel, initialDirectory);
        ObjectiveC.SendVoid(panel, ObjectiveC.Sel("setAllowsMultipleSelection:"), (byte)0);
        return ObjectiveC.SendLong(panel, ObjectiveC.Sel("runModal")) == 1
            ? ObjectiveC.GetString(ObjectiveC.Send(ObjectiveC.Send(panel, ObjectiveC.Sel("URL")), ObjectiveC.Sel("path"))) : null;
    }
    private static void Configure(nint panel, string? initialDirectory)
    {
        var types = ObjectiveC.New("NSMutableArray");
        ObjectiveC.SendVoid(types, ObjectiveC.Sel("addObject:"), ObjectiveC.String("md"));
        ObjectiveC.SendVoid(types, ObjectiveC.Sel("addObject:"), ObjectiveC.String("markdown"));
        ObjectiveC.SendVoid(panel, ObjectiveC.Sel("setAllowedFileTypes:"), types);
        if (!string.IsNullOrEmpty(initialDirectory))
        {
            var url = ObjectiveC.Send(ObjectiveC.Class("NSURL"), ObjectiveC.Sel("fileURLWithPath:"), ObjectiveC.String(initialDirectory));
            if (url != 0) ObjectiveC.SendVoid(panel, ObjectiveC.Sel("setDirectoryURL:"), url);
        }
    }
}
