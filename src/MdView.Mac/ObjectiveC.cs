using System.Runtime.InteropServices;

namespace MdView.Mac;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint(double x, double y) { public double X = x, Y = y; }
[StructLayout(LayoutKind.Sequential)]
internal struct CGSize(double width, double height) { public double Width = width, Height = height; }
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect(CGPoint origin, CGSize size) { public CGPoint Origin = origin; public CGSize Size = size; }

internal static unsafe class ObjectiveC
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    internal static nint Nil => 0;

    [DllImport(ObjC)] internal static extern nint objc_getClass(string name);
    [DllImport(ObjC)] internal static extern nint sel_registerName(string name);
    [DllImport(ObjC)] internal static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);
    [DllImport(ObjC)] internal static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethod(nint cls, nint selector, delegate* unmanaged<nint, nint, void> implementation, string types);
    [DllImport(ObjC, EntryPoint = "class_addMethod")] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethod1(nint cls, nint selector, delegate* unmanaged<nint, nint, nint, void> implementation, string types);
    [DllImport(ObjC, EntryPoint = "class_addMethod")] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethod2(nint cls, nint selector, delegate* unmanaged<nint, nint, nint, nint, void> implementation, string types);
    [DllImport(ObjC, EntryPoint = "class_addMethod")] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethod3(nint cls, nint selector, delegate* unmanaged<nint, nint, nint, nint, nint, void> implementation, string types);
    [DllImport(ObjC, EntryPoint = "class_addMethod")] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethodBool(nint cls, nint selector, delegate* unmanaged<nint, nint, nint, byte> implementation, string types);
    [DllImport(ObjC, EntryPoint = "class_addMethod")] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethodPtr(nint cls, nint selector, delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint> implementation, string types);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool class_addProtocol(nint cls, nint protocol);
    [DllImport(ObjC)] internal static extern nint objc_getProtocol(string name);
    [DllImport(ObjC)] internal static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] internal static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send(nint receiver, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send(nint receiver, nint selector, nint a, nint b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send(nint receiver, nint selector, nint a, nint b, nint c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send(nint receiver, nint selector, nint a, nint b, nint c, nint d);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendULong(nint receiver, nint selector, nuint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendDouble(nint receiver, nint selector, double arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendRect(nint receiver, nint selector, CGRect rect);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendRect(nint receiver, nint selector, CGRect rect, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendScript(nint receiver, nint selector, nint source, nint injectionTime, byte mainFrameOnly);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendRectNUInt(nint receiver, nint selector, CGRect rect, nuint style, nuint backing, byte defer);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(nint receiver, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(nint receiver, nint selector, nint a, nint b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(nint receiver, nint selector, nint a, nint b, nint c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(nint receiver, nint selector, byte arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern byte SendBool(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern long SendLong(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendRead(nint receiver, nint selector, nint buffer, nuint maximum);

    internal static nint Class(string name) => objc_getClass(name);
    internal static nint Sel(string name) => sel_registerName(name);
    internal static nint New(string className) => Send(Send(Class(className), Sel("alloc")), Sel("init"));
    internal static nint String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + '\0');
        fixed (byte* pointer = bytes) return Send(Class("NSString"), Sel("stringWithUTF8String:"), (nint)pointer);
    }
    internal static string? GetString(nint value)
    {
        if (value == Nil) return null;
        var pointer = Send(value, Sel("UTF8String"));
        return pointer == Nil ? null : Marshal.PtrToStringUTF8(pointer);
    }
}
