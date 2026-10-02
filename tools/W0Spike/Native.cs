using System.Runtime.InteropServices;
internal static class Native
{
 internal const uint WM_DESTROY=2,WM_SIZE=5,WM_CLOSE=0x10,WM_NCHITTEST=0x84,WM_NCCALCSIZE=0x83,WM_APP=0x8000;
 internal const uint WS_OVERLAPPED=0,WS_SYSMENU=0x80000,WS_THICKFRAME=0x40000,WS_MINIMIZEBOX=0x20000,WS_MAXIMIZEBOX=0x10000;
 internal const int CW_USEDEFAULT=unchecked((int)0x80000000),SW_SHOW=5,SW_MINIMIZE=6,SW_MAXIMIZE=3,SW_RESTORE=9;
 internal const int HTLEFT=10,HTRIGHT=11,HTTOP=12,HTTOPLEFT=13,HTTOPRIGHT=14,HTBOTTOM=15,HTBOTTOMLEFT=16,HTBOTTOMRIGHT=17;
 internal const uint DWMWA_EXTENDED_FRAME_BOUNDS=9;
 internal static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2=new(-4);
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct WNDCLASSEX { internal uint cbSize,style; internal WndProc lpfnWndProc; internal int cbClsExtra,cbWndExtra; internal nint hInstance,hIcon,hCursor,hbrBackground; internal string? lpszMenuName,lpszClassName; internal nint hIconSm; }
 [StructLayout(LayoutKind.Sequential)] internal struct MSG { internal nint hwnd;internal uint message;internal nuint wParam;internal nint lParam;internal uint time;internal POINT pt;internal uint lPrivate; }
 [StructLayout(LayoutKind.Sequential)] internal struct RECT { internal int left,top,right,bottom;public override readonly string ToString()=>$"({left},{top})-({right},{bottom}) {right-left}x{bottom-top}"; }
 [StructLayout(LayoutKind.Sequential)] internal struct POINT { internal int x,y; }
 [StructLayout(LayoutKind.Sequential)] internal struct INPUT { internal uint type;internal MOUSEINPUT mi; }
 [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { internal int dx,dy;internal uint mouseData,dwFlags,time;internal nuint dwExtraInfo; }
 internal delegate nint WndProc(nint hwnd,uint msg,nuint wp,nint lp);
 [DllImport("user32",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern ushort RegisterClassExW(ref WNDCLASSEX value);
 [DllImport("user32",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
 [DllImport("user32")] internal static extern nint DefWindowProcW(nint hwnd,uint msg,nuint wp,nint lp);
 [DllImport("user32")] internal static extern bool DestroyWindow(nint hwnd);
 [DllImport("user32")] internal static extern void PostQuitMessage(int code);
 [DllImport("user32")] internal static extern int GetMessageW(out MSG msg,nint hwnd,uint min,uint max);
 [DllImport("user32")] internal static extern bool TranslateMessage(ref MSG msg);
 [DllImport("user32")] internal static extern nint DispatchMessageW(ref MSG msg);
 [DllImport("user32")] internal static extern bool PostMessageW(nint hwnd,uint msg,nuint wp,nint lp);
 [DllImport("user32")] internal static extern nint SendMessageW(nint hwnd,uint msg,nuint wp,nint lp);
 [DllImport("user32")] internal static extern nint WindowFromPoint(POINT point);
 [DllImport("user32")] internal static extern bool ScreenToClient(nint hwnd,ref POINT point);
 [DllImport("user32")] internal static extern nint ChildWindowFromPointEx(nint parent,POINT clientPoint,uint flags);
 internal const uint CWP_SKIPINVISIBLE=1;
 [DllImport("user32")] internal static extern bool SetForegroundWindow(nint hwnd);
 [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
 [DllImport("user32")] internal static extern bool ShowWindow(nint hwnd,int command);
 [DllImport("user32")] internal static extern bool IsZoomed(nint hwnd);
 [DllImport("user32")] internal static extern bool GetClientRect(nint hwnd,out RECT rect);
 [DllImport("user32")] internal static extern bool GetWindowRect(nint hwnd,out RECT rect);
 [DllImport("user32")] internal static extern uint GetDpiForWindow(nint hwnd);
 [DllImport("user32")] internal static extern nint GetThreadDpiAwarenessContext();
 [DllImport("user32")] internal static extern bool AreDpiAwarenessContextsEqual(nint first,nint second);
 [DllImport("user32")] internal static extern bool SetProcessDpiAwarenessContext(nint value);
 [DllImport("user32")] internal static extern bool ClientToScreen(nint hwnd,ref POINT point);
 [DllImport("user32")] internal static extern bool GetCursorPos(out POINT point);
 [DllImport("user32")] internal static extern bool SetCursorPos(int x,int y);
 [DllImport("user32",SetLastError=true)] internal static extern uint SendInput(uint count,INPUT[] inputs,int size);
 [DllImport("kernel32",CharSet=CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
 [DllImport("kernel32")] internal static extern uint GetCurrentProcessId();
 [DllImport("dwmapi")] internal static extern int DwmGetWindowAttribute(nint hwnd,uint attribute,out RECT value,uint size);
}
