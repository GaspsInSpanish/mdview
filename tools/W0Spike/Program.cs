using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

internal sealed record Check(string Name,bool Passed,string Detail);
internal sealed class Win32Context(nint hwnd):SynchronizationContext
{
 readonly ConcurrentQueue<(SendOrPostCallback Callback,object? State)> queue=new();
 public override void Post(SendOrPostCallback callback,object? state){queue.Enqueue((callback,state));Native.PostMessageW(hwnd,Native.WM_APP,0,0);}
 internal void Drain(){while(queue.TryDequeue(out var work))work.Callback(work.State);}
}
internal sealed class SpikeApp
{
 const int ResizeBandAt96Dpi=16;
 static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
 readonly bool auto,drag;readonly string? reportPath;readonly List<Check> checks=[];readonly Stopwatch startup=Stopwatch.StartNew();
 readonly string userData=Path.Combine(Path.GetTempPath(),$"mdview-w0-{Guid.NewGuid():N}");readonly Native.WndProc wndProc;
 nint hwnd;Win32Context? context;CoreWebView2Environment? environment;CoreWebView2Controller? controller;CoreWebView2? webview;long pingStarted;bool finished,shutdownComplete;
 readonly TaskCompletionSource browserExited=new();
 internal SpikeApp(string[] args){auto=args.Contains("--auto",StringComparer.OrdinalIgnoreCase);drag=args.Contains("--drag",StringComparer.OrdinalIgnoreCase);int i=Array.FindIndex(args,a=>a.Equals("--auto",StringComparison.OrdinalIgnoreCase));reportPath=i>=0&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal)?Path.GetFullPath(args[i+1]):null;wndProc=WindowProc;}
 internal int Run()
 {
  if((auto&&reportPath is null)||(!auto&&!Environment.GetCommandLineArgs().Contains("--interactive",StringComparer.OrdinalIgnoreCase)))return 2;
  try
  {
  _=Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);nint instance=Native.GetModuleHandleW(null);
  var wc=new Native.WNDCLASSEX{cbSize=(uint)Marshal.SizeOf<Native.WNDCLASSEX>(),lpfnWndProc=wndProc,hInstance=instance,lpszClassName="MdViewW0Spike"};
  if(Native.RegisterClassExW(ref wc)==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"RegisterClassExW failed");
  uint style=Native.WS_OVERLAPPED|Native.WS_SYSMENU|Native.WS_THICKFRAME|Native.WS_MINIMIZEBOX|Native.WS_MAXIMIZEBOX;
  hwnd=Native.CreateWindowExW(0,wc.lpszClassName!,"mdview W0 spike",style,Native.CW_USEDEFAULT,Native.CW_USEDEFAULT,900,600,0,0,instance,0);
  if(hwnd==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"CreateWindowExW failed");
  Add("window-created",true,$"raw top-level HWND 0x{hwnd:X}");
  context=new Win32Context(hwnd);SynchronizationContext.SetSynchronizationContext(context);Native.ShowWindow(hwnd,Native.SW_SHOW);
  Native.GetClientRect(hwnd,out var client);Native.GetWindowRect(hwnd,out var window);
  int windowW=window.right-window.left,windowH=window.bottom-window.top,borderX=windowW-client.right,borderY=windowH-client.bottom;bool captionGone=borderY<=borderX;
  Add("frame-caption-removed",captionGone,$"window={windowW}x{windowH}; client={client.right}x{client.bottom}; horizontal non-client={borderX}px; vertical non-client={borderY}px; caption removed={captionGone}; {ResizeBand()}px client resize band reserved for hit testing");
  uint dpi=Native.GetDpiForWindow(hwnd);bool pmv2=Native.AreDpiAwarenessContextsEqual(Native.GetThreadDpiAwarenessContext(),Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);Add("dpi",pmv2&&dpi>0,$"per-monitor-v2 active={pmv2}; GetDpiForWindow={dpi}");
  if(auto)StartWatchdog();InitializeAsync();
  while(Native.GetMessageW(out var msg,0,0,0)>0){Native.TranslateMessage(ref msg);Native.DispatchMessageW(ref msg);}return 0;
  }
  catch{EmergencyCleanup();throw;}
 }
 async void InitializeAsync()
 {
  try
  {
   CoreWebView2Environment env;try{env=await CoreWebView2Environment.CreateAsync(null,userData);environment=env;env.BrowserProcessExited+=(s,e)=>browserExited.TrySetResult();Add("webview-environment",true,$"BrowserVersionString={env.BrowserVersionString}");}catch(Exception ex){Add("webview-environment",false,Describe(ex));Finish();return;}
   try{controller=await env.CreateCoreWebView2ControllerAsync(hwnd);webview=controller.CoreWebView2;ResizeController();Add("webview-controller-hwnd",true,$"controller created for raw HWND 0x{hwnd:X}; bounds={controller.Bounds}; {ResizeBand()}px resize band excluded from child bounds");}catch(Exception ex){Add("webview-controller-hwnd",false,Describe(ex));Finish();return;}
   try{webview.Settings.IsNonClientRegionSupportEnabled=true;Add("non-client-region-setting",webview.Settings.IsNonClientRegionSupportEnabled,"API exists in SDK; runtime accepted setting=True");}catch(Exception ex){Add("non-client-region-setting",false,$"API exists in SDK; runtime rejected: {Describe(ex)}");}
   string nonce=Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)),html=ReadPage().Replace("{{NONCE}}",nonce,StringComparison.Ordinal);
   webview.AddWebResourceRequestedFilter("https://mdview.example/*",CoreWebView2WebResourceContext.All);
   webview.WebResourceRequested+=(s,e)=>{try{var stream=new MemoryStream(Encoding.UTF8.GetBytes(html));string headers=$"Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'";e.Response=env.CreateWebResourceResponse(stream,200,"OK",headers);Add("served-from-memory",true,"https://mdview.example/ intercepted; response is an in-memory stream with nonce-only CSP");}catch(Exception ex){Add("served-from-memory",false,Describe(ex));}};
   webview.NavigationCompleted+=(s,e)=>Add("startup-time",e.IsSuccess,$"{startup.ElapsedMilliseconds} ms to first NavigationCompleted; status={e.WebErrorStatus}");webview.WebMessageReceived+=OnMessage;
   webview.Navigate("https://mdview.example/");
  }catch(Exception ex){Add("harness-initialize",false,Describe(ex));Finish();}
 }
 async void OnMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
 {
  try
  {
   using JsonDocument json=JsonDocument.Parse(e.WebMessageAsJson);JsonElement root=json.RootElement;string type=root.GetProperty("type").GetString()??"";
   if(type=="command"){HandleCommand(root.GetProperty("command").GetString());return;}
   if(type=="page-ready")
   {if(auto)RunResizeHitTest(); // after the page is up: only now is the WebView2 child HWND shown and sized
    string app=root.GetProperty("appRegion").GetString()??"",webkit=root.GetProperty("webkitAppRegion").GetString()??"";Add("app-region-css",app=="drag"||webkit=="drag",$"appRegion={JsonSerializer.Serialize(app)}; -webkit-app-region={JsonSerializer.Serialize(webkit)}");bool nonce=root.GetProperty("nonceRan").GetBoolean(),inline=root.GetProperty("inlineHandlerRan").GetBoolean();Add("csp-enforced",nonce&&!inline,$"nonce script ran={nonce}; inline onerror ran={inline}");if(drag)await RunDragAsync(root.GetProperty("titleRect"));pingStarted=Stopwatch.GetTimestamp();webview!.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="ping",sent=pingStarted}));}
   else if(type=="pong"){double ms=Stopwatch.GetElapsedTime(pingStarted).TotalMilliseconds;Add("message-round-trip",true,$"{ms:F3} ms");if(auto)Finish();}
  }catch(Exception ex){Add("message-processing",false,Describe(ex));if(auto)Finish();}
 }
 async Task RunDragAsync(JsonElement rect)
 {
  await Task.Yield();Native.GetWindowRect(hwnd,out var before);Native.GetCursorPos(out var old);
  try{int band=ResizeBand();var p=new Native.POINT{x=band+(int)(rect.GetProperty("left").GetDouble()+rect.GetProperty("width").GetDouble()/2),y=band+(int)(rect.GetProperty("top").GetDouble()+rect.GetProperty("height").GetDouble()/2)};Native.ClientToScreen(hwnd,ref p);if(!Native.SetForegroundWindow(hwnd)){Add("drag-region-hit",false,"skipped: SetForegroundWindow failed");return;}nint under=Native.WindowFromPoint(p);Native.GetWindowThreadProcessId(under,out uint processId);if(processId!=Native.GetCurrentProcessId()){Add("drag-region-hit",false,"skipped: another window is at the press point");return;}Native.SetCursorPos(p.x,p.y);SendMouse(2);SendMouseMove(40,0);SendMouse(4);await Task.Delay(250);Native.GetWindowRect(hwnd,out var after);bool moved=after.left!=before.left||after.top!=before.top;Add("drag-region-hit",moved,$"before={before}; after={after}; SendInput drag 40px right");}catch(Exception ex){Add("drag-region-hit",false,Describe(ex));}finally{Native.SetCursorPos(old.x,old.y);}
 }
 static void SendMouse(uint flag){var input=new[]{new Native.INPUT{type=0,mi=new Native.MOUSEINPUT{dwFlags=flag}}};if(Native.SendInput(1,input,Marshal.SizeOf<Native.INPUT>())!=1)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"SendInput failed");}
 static void SendMouseMove(int x,int y){var input=new[]{new Native.INPUT{type=0,mi=new Native.MOUSEINPUT{dx=x,dy=y,dwFlags=1}}};if(Native.SendInput(1,input,Marshal.SizeOf<Native.INPUT>())!=1)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"SendInput move failed");}
 void HandleCommand(string? command){if(command=="minimize")Native.ShowWindow(hwnd,Native.SW_MINIMIZE);else if(command=="maximize")Native.ShowWindow(hwnd,Native.IsZoomed(hwnd)?Native.SW_RESTORE:Native.SW_MAXIMIZE);else if(command=="close")Finish();}
 nint WindowProc(nint h,uint msg,nuint wp,nint lp){if(msg==Native.WM_NCCALCSIZE)return 0;if(msg==Native.WM_NCHITTEST)return HitTest(h,lp);if(msg==Native.WM_SIZE)ResizeController();else if(msg==Native.WM_APP)context?.Drain();else if(msg==Native.WM_CLOSE){if(finished){Native.DestroyWindow(h);}else Finish();return 0;}else if(msg==Native.WM_DESTROY){Native.PostQuitMessage(0);return 0;}return Native.DefWindowProcW(h,msg,wp,lp);}
 nint HitTest(nint h,nint lp){int x=unchecked((short)((long)lp&0xffff)),y=unchecked((short)(((long)lp>>16)&0xffff)),band=ResizeBand(h);Native.RECT r=VisibleWindowRect(h);bool left=x<r.left+band,right=x>=r.right-band,top=y<r.top+band,bottom=y>=r.bottom-band;if(top&&left)return Native.HTTOPLEFT;if(top&&right)return Native.HTTOPRIGHT;if(bottom&&left)return Native.HTBOTTOMLEFT;if(bottom&&right)return Native.HTBOTTOMRIGHT;if(left)return Native.HTLEFT;if(right)return Native.HTRIGHT;if(top)return Native.HTTOP;if(bottom)return Native.HTBOTTOM;return Native.DefWindowProcW(h,Native.WM_NCHITTEST,0,lp);}
 void ResizeController(){if(controller is null||hwnd==0)return;Native.GetClientRect(hwnd,out var r);int band=ResizeBand();controller.Bounds=new System.Drawing.Rectangle(band,band,Math.Max(0,r.right-2*band),Math.Max(0,r.bottom-2*band));}
 int ResizeBand(nint target=default)=>Math.Max(ResizeBandAt96Dpi,(int)Math.Ceiling(ResizeBandAt96Dpi*Native.GetDpiForWindow(target==0?hwnd:target)/96.0));
 Native.RECT VisibleWindowRect(nint target=default){target=target==0?hwnd:target;if(Native.DwmGetWindowAttribute(target,Native.DWMWA_EXTENDED_FRAME_BOUNDS,out var visible,(uint)Marshal.SizeOf<Native.RECT>())==0)return visible;Native.GetWindowRect(target,out visible);return visible;}
 void RunResizeHitTest()
 {
  bool foreground=Native.SetForegroundWindow(hwnd);Native.RECT r=VisibleWindowRect();int midX=(r.left+r.right)/2,midY=(r.top+r.bottom)/2;
  (string Name,Native.POINT Point,int Expected)[] cases=[("left",new(){x=r.left+2,y=midY},Native.HTLEFT),("right",new(){x=r.right-3,y=midY},Native.HTRIGHT),("top",new(){x=midX,y=r.top+2},Native.HTTOP),("bottom",new(){x=midX,y=r.bottom-3},Native.HTBOTTOM),("top-left",new(){x=r.left+2,y=r.top+2},Native.HTTOPLEFT),("top-right",new(){x=r.right-3,y=r.top+2},Native.HTTOPRIGHT),("bottom-left",new(){x=r.left+2,y=r.bottom-3},Native.HTBOTTOMLEFT),("bottom-right",new(){x=r.right-3,y=r.bottom-3},Native.HTBOTTOMRIGHT)];
  // Which of *our* windows owns each point: the top-level (band reaches WM_NCHITTEST) or the WebView2
  // child (band is covered). Asked of our own HWND so another app on top cannot decide the result;
  // launched from WSL the spike cannot take the foreground, so WindowFromPoint would measure z-order.
  bool passed=true;var details=new List<string>();
  foreach(var item in cases){var clientPoint=item.Point;Native.ScreenToClient(hwnd,ref clientPoint);nint owner=Native.ChildWindowFromPointEx(hwnd,clientPoint,Native.CWP_SKIPINVISIBLE);int actual=(int)Native.SendMessageW(hwnd,Native.WM_NCHITTEST,0,MakePointLParam(item.Point));bool match=owner==hwnd&&actual==item.Expected;passed&=match;details.Add($"{item.Name} ({item.Point.x},{item.Point.y}): owner=0x{owner:X} {(owner==hwnd?"top-level":owner==0?"outside":"child")}, hit={HitName(actual)}, expected={HitName(item.Expected)}");}
  var titleTop=new Native.POINT{x=0,y=ResizeBand()};Native.ClientToScreen(hwnd,ref titleTop);int visibleGap=titleTop.y-r.top;
  Add("resize-hit-test",passed,$"SetForegroundWindow={foreground}; visible title-bar gap={visibleGap}px; {string.Join("; ",details)}");
 }
 static nint MakePointLParam(Native.POINT p)=>unchecked((nint)(((p.y&0xffff)<<16)|(p.x&0xffff)));
 static string HitName(int hit)=>hit switch{Native.HTLEFT=>"HTLEFT",Native.HTRIGHT=>"HTRIGHT",Native.HTTOP=>"HTTOP",Native.HTBOTTOM=>"HTBOTTOM",Native.HTTOPLEFT=>"HTTOPLEFT",Native.HTTOPRIGHT=>"HTTOPRIGHT",Native.HTBOTTOMLEFT=>"HTBOTTOMLEFT",Native.HTBOTTOMRIGHT=>"HTBOTTOMRIGHT",_=>$"{hit}"};
 void Add(string name,bool passed,string detail){lock(checks){int i=checks.FindIndex(c=>c.Name==name);var check=new Check(name,passed,detail);if(i>=0)checks[i]=check;else checks.Add(check);}}
 async void Finish()
 {
  if(finished)return;finished=true;string exitDetail;
  try{controller?.Close();controller=null;webview=null;Task completed=await Task.WhenAny(browserExited.Task,Task.Delay(TimeSpan.FromSeconds(5)));exitDetail=completed==browserExited.Task?"browser process exited":"timed out waiting 5 seconds for browser process exit";}catch(Exception ex){exitDetail=$"controller close/wait failed: {Describe(ex)}";}
  bool deleted=false;string deleteDetail="";
  for(int attempt=1;attempt<=5&&!deleted;attempt++){try{if(Directory.Exists(userData))Directory.Delete(userData,true);deleted=!Directory.Exists(userData);if(!deleted)deleteDetail=$"folder still exists after attempt {attempt}";}catch(Exception ex){deleteDetail=Describe(ex);}if(!deleted)await Task.Delay(100);}
  Add("profile-cleaned",deleted,$"{exitDetail}; folder gone={deleted}{(deleteDetail.Length==0?"":$"; {deleteDetail}")}");environment=null;
  if(auto){FillMissingChecks();WriteReport();}shutdownComplete=true;if(hwnd!=0){Native.DestroyWindow(hwnd);hwnd=0;}Native.PostQuitMessage(0);
 }
 void FillMissingChecks(){string[] expected=["window-created","frame-caption-removed","resize-hit-test","webview-environment","webview-controller-hwnd","non-client-region-setting","app-region-css","served-from-memory","csp-enforced","message-round-trip","startup-time","dpi","profile-cleaned"];if(drag)expected=[..expected,"drag-region-hit"];foreach(string name in expected)if(!checks.Any(c=>c.Name==name))Add(name,false,"not reached because a prerequisite failed");}
 void StartWatchdog()=>new Thread(()=>{Thread.Sleep(TimeSpan.FromSeconds(28));if(shutdownComplete)return;Add("watchdog",false,"28-second watchdog expired before shutdown completed");if(!checks.Any(c=>c.Name=="profile-cleaned"))Add("profile-cleaned",false,"hard deadline reached before profile cleanup completed");FillMissingChecks();WriteReport();if(hwnd!=0)Native.PostMessageW(hwnd,Native.WM_CLOSE,0,0);Thread.Sleep(1000);Environment.Exit(0);}){IsBackground=true,Name="W0 watchdog"}.Start();
 void WriteReport(){if(reportPath is null)return;try{string? dir=Path.GetDirectoryName(reportPath);if(!string.IsNullOrEmpty(dir))Directory.CreateDirectory(dir);lock(checks)File.WriteAllText(reportPath,JsonSerializer.Serialize(checks,JsonOptions));}catch{/* Unwritable requested output is handled by the hard watchdog. */}}
 void EmergencyCleanup(){try{controller?.Close();}catch{}if(hwnd!=0){Native.DestroyWindow(hwnd);hwnd=0;}Native.PostQuitMessage(0);try{if(Directory.Exists(userData))Directory.Delete(userData,true);}catch{}}
 static string ReadPage(){using Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("W0Spike.page.html")??throw new InvalidOperationException("Embedded page.html missing");using var reader=new StreamReader(stream);return reader.ReadToEnd();}
 static string Describe(Exception ex)=>$"{ex.GetType().Name}: {ex.Message}";
}
internal static class Program
{
 [STAThread]static int Main(string[] args)
 {string? report=null;int i=Array.FindIndex(args,a=>a.Equals("--auto",StringComparison.OrdinalIgnoreCase));if(i>=0&&i+1<args.Length)report=args[i+1];try{return new SpikeApp(args).Run();}catch(Exception ex){if(report is not null)try{File.WriteAllText(Path.GetFullPath(report),JsonSerializer.Serialize(new[]{new Check("harness-crash",false,$"{ex.GetType().Name}: {ex.Message}")},new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));}catch{}return 1;}}
}
