using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MdView.Mac;

internal sealed unsafe class MacSynchronizationContext : SynchronizationContext
{
    private static readonly ConcurrentDictionary<nint, SendOrPostCallback> Callbacks = new();
    private static long next;
    private readonly int thread = Environment.CurrentManagedThreadId;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        var key = (nint)Interlocked.Increment(ref next);
        Callbacks[key] = _ => callback(state);
        Dispatch.dispatch_async_f(Dispatch.MainQueue, key, &Invoke);
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (Environment.CurrentManagedThreadId == thread) { callback(state); return; }
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        Post(_ => { try { callback(state); } catch (Exception ex) { failure = ex; } finally { done.Set(); } }, null);
        done.Wait();
        if (failure is not null) throw new AggregateException(failure);
    }

    [UnmanagedCallersOnly]
    private static void Invoke(nint context)
    {
        nint pool = 0;
        try
        {
            if (!Callbacks.TryRemove(context, out var callback)) return;
            pool = ObjectiveC.objc_autoreleasePoolPush();
            callback(null);
        }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
        finally
        {
            if (pool != 0) try { ObjectiveC.objc_autoreleasePoolPop(pool); }
            catch (Exception exception) { MacApplication.Report(exception.Message); }
        }
    }
}

internal static unsafe class Dispatch
{
    private const string Lib = "/usr/lib/system/libdispatch.dylib";
    private static readonly nint Library = NativeLibrary.Load(Lib);
    internal static readonly nint MainQueue = NativeLibrary.GetExport(Library, "_dispatch_main_q");
    [DllImport(Lib)] internal static extern void dispatch_async_f(nint queue, nint context, delegate* unmanaged<nint, void> work);
}
