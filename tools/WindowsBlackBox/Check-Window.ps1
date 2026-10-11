param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$WorkDir)
# Windows black-box check for the mdview window (W1 onward). Run by the PM on the owner's desktop
# through WSL interop, via run.sh. Every assertion is on the real process, its real windows and the
# real file on disk; nothing talks to the app's internals.
#
# Two constraints from AGENTS.md "Validation" shape it:
# - A process launched from WSL cannot take the foreground, so no check depends on z-order or focus,
#   and no mouse or keyboard input is sent. Interaction goes through UI Automation patterns.
# - Run the exe from a local folder, not \\wsl.localhost: Windows asks for approval before running
#   an exe from that network-style path, and the check would sit waiting for a human.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class MdViewBlackBox {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  public static string Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r.l + "," + r.t + "," + (r.r - r.l) + "," + (r.b - r.t); }
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT p, uint flags);
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
  [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
  [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO i);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
  // "window WxH client WxH": with the Windows caption removed the client area fills the window.
  public static string Sizes(IntPtr h) { RECT w, c; GetWindowRect(h, out w); GetClientRect(h, out c);
    return (w.r - w.l) + "x" + (w.b - w.t) + " client " + (c.r - c.l) + "x" + (c.b - c.t); }
  // For 8 points 2px inside the window edge: which of *our* windows owns the point (the top-level, i.e. the
  // uncovered resize band, or the WebView2 child) and what WM_NCHITTEST answers. Independent of z-order.
  public static string HitTest(IntPtr h) {
    RECT w; GetWindowRect(h, out w); int mx = (w.l + w.r) / 2, my = (w.t + w.b) / 2;
    var cases = new[] { new { n = "left", x = w.l + 2, y = my, e = 10 }, new { n = "right", x = w.r - 3, y = my, e = 11 },
      new { n = "top", x = mx, y = w.t + 2, e = 12 }, new { n = "bottom", x = mx, y = w.b - 3, e = 15 },
      new { n = "top-left", x = w.l + 2, y = w.t + 2, e = 13 }, new { n = "top-right", x = w.r - 3, y = w.t + 2, e = 14 },
      new { n = "bottom-left", x = w.l + 2, y = w.b - 3, e = 16 }, new { n = "bottom-right", x = w.r - 3, y = w.b - 3, e = 17 } };
    var parts = new List<string>(); bool ok = true;
    foreach (var c in cases) {
      var cp = new POINT { x = c.x, y = c.y }; ScreenToClient(h, ref cp);
      var owner = ChildWindowFromPointEx(h, cp, 1);
      int hit = (int)SendMessage(h, 0x0084, IntPtr.Zero, (IntPtr)((c.y << 16) | (c.x & 0xFFFF)));
      bool match = owner == h && hit == c.e; ok &= match;
      parts.Add(c.n + "=" + hit + (owner == h ? "" : "(covered)") + (match ? "" : "!"));
    }
    return (ok ? "OK " : "BAD ") + string.Join(" ", parts);
  }
  // Is the maximized window's client area inside its monitor's work area (the classic custom-frame bug
  // is a maximized window spilling past the screen edge by the frame thickness)?
  public static string MaximizedFit(IntPtr h) {
    RECT c; GetClientRect(h, out c); var tl = new POINT { x = 0, y = 0 }; ClientToScreen(h, ref tl);
    var mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO)); GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
    bool inside = tl.x >= mi.rcWork.l && tl.y >= mi.rcWork.t && tl.x + c.r <= mi.rcWork.r && tl.y + c.b <= mi.rcWork.b;
    return (inside ? "OK " : "BAD ") + "client " + tl.x + "," + tl.y + " " + c.r + "x" + c.b + " work " + mi.rcWork.l + "," + mi.rcWork.t + "-" + mi.rcWork.r + "," + mi.rcWork.b;
  }
  // Text of a dialog's child controls (a MessageBox's message is a Static child), read via Win32.
  public static string DialogText(IntPtr dialog) {
    var parts = new List<string>();
    EnumChildWindows(dialog, (h, l) => { var t = new StringBuilder(2048); GetWindowText(h, t, 2048);
      if (t.Length > 0) parts.Add(t.ToString()); return true; }, IntPtr.Zero);
    return string.Join(" ", parts);
  }
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
  [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
  // Visible top-level windows of a process, as "hwnd|class|title".
  public static List<string> Windows(uint pid) {
    var found = new List<string>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { var t = new StringBuilder(512); var c = new StringBuilder(256);
        GetWindowText(h, t, 512); GetClassName(h, c, 256); found.Add(h.ToInt64() + "|" + c + "|" + t); }
      return true; }, IntPtr.Zero);
    return found;
  }
  // "inactive" when `top` is not its thread's active window: Windows reports keyboard focus only for
  // the active window, so focus is unmeasurable then. Otherwise the class of the focused window.
  public static string FocusClass(IntPtr top) {
    uint pid; uint thread = GetWindowThreadProcessId(top, out pid);
    var info = new GUITHREADINFO(); info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
    if (!GetGUIThreadInfo(thread, ref info) || info.hwndActive != top) return "inactive";
    if (info.hwndFocus == IntPtr.Zero) return "none";
    var c = new StringBuilder(256); GetClassName(info.hwndFocus, c, 256); return c.ToString();
  }
}
'@

$results = [ordered]@{}
function Check($name, $passed, $detail) { $results[$name] = [ordered]@{ passed = [bool]$passed; detail = "$detail" } }
# For a check whose precondition the harness cannot control (see keyboard focus): not a pass, not a failure.
function Unmeasured($name, $detail) { $results[$name] = [ordered]@{ passed = $null; detail = "UNMEASURED: $detail" } }
function Windows($id) { [MdViewBlackBox]::Windows([uint32]$id) }
function Finish { Restore-OwnerConfig; $results | ConvertTo-Json -Depth 4; if (@($results.Values | Where-Object { $_.passed -eq $false }).Count) { exit 1 } else { exit 0 } }
$utf8 = New-Object System.Text.UTF8Encoding $false
# Every mdview window that closes writes its placement into the owner's real %APPDATA%\mdview\config.json
# (W3b). Back it up before the first window opens and restore it in Finish, on every path.
$config = Join-Path $env:APPDATA 'mdview\config.json'
$configBackup = if (Test-Path $config) { [IO.File]::ReadAllBytes($config) } else { $null }
function Restore-OwnerConfig {
  try { if ($null -ne $configBackup) { [IO.File]::WriteAllBytes($config, $configBackup) } elseif (Test-Path $config) { Remove-Item $config } } catch { }
}
$docA = Join-Path $WorkDir 'Black box alpha.md'
$docB = Join-Path $WorkDir 'Black box beta.md'
[IO.File]::WriteAllText($docA, "# Alpha`r`n`r`n- [ ] tick me`r`n`r`nAlpha body.`r`n", $utf8)
[IO.File]::WriteAllText($docB, "# Beta`r`n`r`nBeta body.`r`n", $utf8)

if (Get-Process mdview -ErrorAction SilentlyContinue) { Check 'precondition-no-mdview-running' $false 'another mdview.exe is running; it would capture the launch'; Finish }
Check 'precondition-no-mdview-running' $true 'none'

$started = Get-Date
$p = Start-Process -FilePath $Exe -ArgumentList "`"$docA`"" -PassThru
$deadline = (Get-Date).AddSeconds(20); $titled = $null
while ((Get-Date) -lt $deadline -and -not $titled) { Start-Sleep -Milliseconds 300; $titled = Windows $p.Id | Where-Object { $_ -match '\|Black box alpha' } | Select-Object -First 1 }
Check 'window-opens-titled-from-page' ($null -ne $titled) ("after {0:N1}s: {1}" -f ((Get-Date) - $started).TotalSeconds, ((Windows $p.Id) -join ' ;; '))
if (-not $titled) { Stop-Process -Id $p.Id -ErrorAction SilentlyContinue; Finish }
$top = [IntPtr][long]($titled.Split('|')[0])

# Keys reach the page only if focus is inside WebView2, not on our own top-level window (W1 blocker).
Start-Sleep -Seconds 3
$focus = [MdViewBlackBox]::FocusClass($top)
# A process launched from WSL may not become the active window (another app keeps the foreground);
# then focus cannot be measured, which is reported as such rather than as a pass or a failure.
if ($focus -eq 'inactive') { Unmeasured 'keyboard-focus-in-page' 'the mdview window was not the active window, so Windows reports no focus for it' }
else { Check 'keyboard-focus-in-page' ($focus -match '^(Chrome_|Intermediate D3D)') "focus class = $focus" }

# Write path end to end: tick the task checkbox through UI Automation. The page's change handler
# POSTs /toggle; only a request the app accepts (origin + token) rewrites the file on disk.
# The menu bar's edit lock is also a checkbox to UI Automation ("Locked"/"Editing"): skip it, or
# the check unlocks editing, which disables the task boxes.
$isBox = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
$box = $null; $deadline = (Get-Date).AddSeconds(10)
while (-not $box -and (Get-Date) -lt $deadline) {
  $ua = [System.Windows.Automation.AutomationElement]::FromHandle($top)
  $box = @($ua.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isBox) | Where-Object { $_.Current.Name -notin @('Locked', 'Editing') }) | Select-Object -First 1
  if (-not $box) { Start-Sleep -Milliseconds 300 } }
if ($box) { $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
$deadline = (Get-Date).AddSeconds(10); $ticked = $false
while (-not $ticked -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $ticked = [IO.File]::ReadAllText($docA) -match '- \[x\] tick me' }
Check 'checkbox-writes-file' $ticked ("task checkbox found={0}; file now: {1}" -f ($null -ne $box), ([IO.File]::ReadAllText($docA) -replace "`r?`n", ' / '))

# Live reload end to end: an external edit must reach the page.
$marker = "Reload marker $(Get-Random)"
[IO.File]::AppendAllText($docA, "`r`n$marker`r`n", $utf8)
$isText = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$deadline = (Get-Date).AddSeconds(12); $seen = $false
while (-not $seen -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500
  try { $ua = [System.Windows.Automation.AutomationElement]::FromHandle($top); $seen = @($ua.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isText) | Where-Object { $_.Current.Name -match [regex]::Escape($marker) }).Count -gt 0 } catch { } }
Check 'external-edit-reloads-page' $seen "marker '$marker' visible in the page=$seen"

# The 30 s startup guard ends the process unless a window reported its page as connected.
Start-Sleep -Seconds ([Math]::Max(0, 33 - ((Get-Date) - $started).TotalSeconds))
$p.Refresh()
Check 'page-connected-past-startup-guard' (-not $p.HasExited) ("alive {0:N0}s after launch" -f ((Get-Date) - $started).TotalSeconds)

$second = Start-Process -FilePath $Exe -ArgumentList "`"$docB`"" -PassThru -Wait
$deadline = (Get-Date).AddSeconds(20); $both = @()
while ((Get-Date) -lt $deadline -and $both.Count -lt 2) { Start-Sleep -Milliseconds 300; $both = @(Windows $p.Id | Where-Object { $_ -match '\|Black box (alpha|beta)' }) }
Check 'second-launch-forwards-to-same-process' ($second.ExitCode -eq 0 -and $both.Count -eq 2) ("forwarder exit={0}; windows in primary: {1}" -f $second.ExitCode, ($both -join ' ;; '))

# Several files launched at once (W3a review B1): on Windows a pipe instance is freed only when both
# ends close, so a burst of secondaries is what kills a single-instance hand-off server. Two bursts of
# five distinct files, then one more launch to prove the hand-off still works afterwards.
$burstOk = $true; $burstDetail = @()
foreach ($round in 1..2) {
  $paths = foreach ($i in 1..5) { $f = Join-Path $WorkDir "Black box burst $round-$i.md"; [IO.File]::WriteAllText($f, "# Burst $round-$i`r`n", $utf8); $f }
  $launched = foreach ($f in $paths) { Start-Process -FilePath $Exe -ArgumentList "`"$f`"" -PassThru }
  $launched | ForEach-Object { $_.WaitForExit(15000) | Out-Null }
  $codes = ($launched | ForEach-Object { $_.ExitCode }) -join ','
  $deadline = (Get-Date).AddSeconds(25); $found = @()
  while ((Get-Date) -lt $deadline -and $found.Count -lt 5) { Start-Sleep -Milliseconds 400; $found = @(Windows $p.Id | Where-Object { $_ -match "\|Black box burst $round-" }) }
  $burstDetail += "round $round forwarder exits=[$codes] windows=$($found.Count)"
  if ($found.Count -ne 5 -or $codes -ne '0,0,0,0,0') { $burstOk = $false }
}
$after = Join-Path $WorkDir 'Black box after burst.md'; [IO.File]::WriteAllText($after, "# After`r`n", $utf8)
$last = Start-Process -FilePath $Exe -ArgumentList "`"$after`"" -PassThru -Wait
$deadline = (Get-Date).AddSeconds(20); $afterWindow = $null
while ((Get-Date) -lt $deadline -and -not $afterWindow) { Start-Sleep -Milliseconds 300; $afterWindow = Windows $p.Id | Where-Object { $_ -match '\|Black box after burst' } | Select-Object -First 1 }
$burstDetail += "after-burst forwarder exit=$($last.ExitCode) window=$($null -ne $afterWindow)"
Check 'burst-launches-each-open-once-and-handoff-survives' ($burstOk -and $last.ExitCode -eq 0 -and $afterWindow) ($burstDetail -join '; ')

# Nothing in the normal flow or the bursts should put a message box in front of the user (W3a review:
# transient hand-off failures used to raise a "temporarily unavailable" warning).
$dialogs = @(Windows $p.Id | Where-Object { $_ -match '\|#32770\|' })
Check 'no-warning-boxes-in-normal-use' ($dialogs.Count -eq 0) ("message boxes open in mdview: {0}" -f (($dialogs | ForEach-Object { [MdViewBlackBox]::DialogText([IntPtr][long]($_.Split('|')[0])) }) -join ' | '))

$browsers = @(Get-CimInstance Win32_Process -Filter "Name='brave.exe' OR Name='chrome.exe' OR Name='msedge.exe'" | Where-Object { $_.CommandLine -match '127\.0\.0\.1:7[78]' })
Check 'no-browser-launched' ($browsers.Count -eq 0) ("browser processes pointed at mdview: {0}" -f $browsers.Count)

$closedAt = Get-Date
foreach ($w in (Windows $p.Id)) { [void][MdViewBlackBox]::PostMessage([IntPtr][long]($w.Split('|')[0]), 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
$exited = $p.WaitForExit(25000)
Check 'closing-last-window-ends-process' $exited ("exited={0} after {1:N1}s" -f $exited, ((Get-Date) - $closedAt).TotalSeconds)
if (-not $exited) { Stop-Process -Id $p.Id -ErrorAction SilentlyContinue }

# mdview exits within a fraction of a second (W3a); WebView2's helper processes notice their host has
# gone a few seconds later. Wait for them, bounded: a real leak never drains.
$deadline = (Get-Date).AddSeconds(15)
do { Start-Sleep -Milliseconds 500; $left = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -match 'mdview\\WebView2' }) }
while ($left.Count -gt 0 -and (Get-Date) -lt $deadline)
Check 'no-webview2-processes-left' ($left.Count -eq 0) ("msedgewebview2 with the mdview profile after up to 15 s: {0}" -f $left.Count)
$profile = Join-Path $env:LOCALAPPDATA 'mdview\WebView2'
Check 'profile-folder-location' (Test-Path $profile) $profile

function Open-And-Wait($doc, $title) {
  $proc = Start-Process -FilePath $Exe -ArgumentList "`"$doc`"" -PassThru
  $deadline = (Get-Date).AddSeconds(20); $w = $null
  while ((Get-Date) -lt $deadline -and -not $w) { Start-Sleep -Milliseconds 300; $w = Windows $proc.Id | Where-Object { $_ -match "\|$title" } | Select-Object -First 1 }
  Start-Sleep -Milliseconds 800
  return @($proc, $(if ($w) { [IntPtr][long]($w.Split('|')[0]) } else { [IntPtr]::Zero }))
}
function Close-And-Wait($proc, $hwnd) { [void][MdViewBlackBox]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); [void]$proc.WaitForExit(15000) }
# Win98 frame (W4): caption removed, resize band at all 8 edge points, caption buttons in the page work
# through UI Automation (no mouse), and a maximized window stays inside the work area.
$frame = Open-And-Wait $docB 'Black box beta'
if ($frame[1] -eq [IntPtr]::Zero) { Check 'frame-caption-removed' $false 'window did not open' }
else {
  $h = $frame[1]
  $sizes = [MdViewBlackBox]::Sizes($h); $parts = $sizes -split ' client '
  Check 'frame-caption-removed' ($parts[0] -eq $parts[1]) "window $sizes"
  $hits = [MdViewBlackBox]::HitTest($h)
  Check 'frame-resize-band' ($hits.StartsWith('OK ')) $hits
  function Invoke-Caption($name) {
    $isButton = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $deadline = (Get-Date).AddSeconds(8); $b = $null
    while (-not $b -and (Get-Date) -lt $deadline) {
      $ua = [System.Windows.Automation.AutomationElement]::FromHandle($h)
      $b = @($ua.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isButton) | Where-Object { $_.Current.Name -eq $name }) | Select-Object -First 1
      if (-not $b) { Start-Sleep -Milliseconds 300 } }
    if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800; return $true }
    return $false
  }
  $found = Invoke-Caption 'Maximize'
  $zoomed = [MdViewBlackBox]::IsZoomed($h)
  $fit = if ($zoomed) { [MdViewBlackBox]::MaximizedFit($h) } else { 'not maximized' }
  Check 'caption-maximize' ($found -and $zoomed) "Maximize button found=$found; zoomed=$zoomed"
  Check 'maximized-inside-work-area' ($fit.StartsWith('OK ')) $fit
  $found = Invoke-Caption 'Restore'
  Check 'caption-restore' ($found -and -not [MdViewBlackBox]::IsZoomed($h)) "Restore button found=$found; zoomed=$([MdViewBlackBox]::IsZoomed($h))"
  $found = Invoke-Caption 'Minimize'
  $iconic = [MdViewBlackBox]::IsIconic($h)
  Check 'caption-minimize' ($found -and $iconic) "Minimize button found=$found; iconic=$iconic"
  [void][MdViewBlackBox]::ShowWindow($h, 9); Start-Sleep -Milliseconds 800   # SW_RESTORE
  $found = Invoke-Caption 'Close'
  $closed = $frame[0].WaitForExit(10000)
  Check 'caption-close' ($found -and $closed) "Close button found=$found; process exited=$closed"
  if (-not $closed) { Stop-Process -Id $frame[0].Id -ErrorAction SilentlyContinue }
}

# Remembered placement (W3b). This writes the owner's real %APPDATA%\mdview\config.json, so it is
# backed up first and restored afterwards whatever happens. Moving a window with SetWindowPos and
# maximizing it are window-manager calls, not user input.
try {
  $first = Open-And-Wait $docB 'Black box beta'
  if ($first[1] -eq [IntPtr]::Zero) { Check 'placement-restored' $false 'window did not open'; Check 'maximized-restored' $false 'window did not open' }
  else {
    [void][MdViewBlackBox]::SetWindowPos($first[1], [IntPtr]::Zero, 160, 120, 880, 640, 0x0014)  # NOZORDER|NOACTIVATE
    Start-Sleep -Milliseconds 500
    $set = [MdViewBlackBox]::Rect($first[1])
    Close-And-Wait $first[0] $first[1]
    $second = Open-And-Wait $docB 'Black box beta'
    $got = [MdViewBlackBox]::Rect($second[1])
    Check 'placement-restored' ($got -eq $set) "set=$set reopened=$got"
    [void][MdViewBlackBox]::ShowWindow($second[1], 3)  # SW_MAXIMIZE
    Start-Sleep -Milliseconds 500
    Close-And-Wait $second[0] $second[1]
    $third = Open-And-Wait $docB 'Black box beta'
    Check 'maximized-restored' ([MdViewBlackBox]::IsZoomed($third[1])) "reopened maximized=$([MdViewBlackBox]::IsZoomed($third[1]))"
    Close-And-Wait $third[0] $third[1]
  }
}
finally { Restore-OwnerConfig }

# WebView2 unavailable on the first launch: the user must get the download prompt *before* the
# process exits (W2b review R1: a prompt posted to the background UI thread died with the process).
# Pointing WebView2 at an empty browser folder makes environment creation fail.
$empty = Join-Path $WorkDir 'no-webview2'
New-Item -ItemType Directory -Path $empty -Force | Out-Null
$env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER = $empty
try { $q = Start-Process -FilePath $Exe -ArgumentList "`"$docB`"" -PassThru }
finally { Remove-Item Env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER }
$deadline = (Get-Date).AddSeconds(20); $dialog = $null
while (-not $dialog -and (Get-Date) -lt $deadline -and -not $q.HasExited) {
  Start-Sleep -Milliseconds 300; $q.Refresh()
  $dialog = Windows $q.Id | Where-Object { $_ -match '\|#32770\|' } | Select-Object -First 1 }
$prompt = ''
if ($dialog) {
  # The dialog window can be found before its controls are populated; read until the text appears.
  $handle = [IntPtr][long]($dialog.Split('|')[0])
  $deadline = (Get-Date).AddSeconds(5)
  while (-not $prompt -and (Get-Date) -lt $deadline) { $prompt = [MdViewBlackBox]::DialogText($handle); if (-not $prompt) { Start-Sleep -Milliseconds 200 } }
  [void][MdViewBlackBox]::PostMessage($handle, 0x0111, [IntPtr]7, [IntPtr]::Zero)  # WM_COMMAND IDNO
}
$gone = $q.WaitForExit(15000)
if (-not $gone) { Stop-Process -Id $q.Id -ErrorAction SilentlyContinue }
Check 'runtime-missing-prompts-before-exit' ($null -ne $dialog -and $prompt -match 'WebView2' -and $prompt -match 'download') ("dialog shown={0}; text='{1}'" -f ($null -ne $dialog), $prompt)
Check 'runtime-missing-then-exits' $gone "process exited after the prompt was answered No = $gone"
Finish
