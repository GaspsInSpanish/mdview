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
function Finish { $results | ConvertTo-Json -Depth 4; if (@($results.Values | Where-Object { $_.passed -eq $false }).Count) { exit 1 } else { exit 0 } }
$utf8 = New-Object System.Text.UTF8Encoding $false
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

Start-Sleep -Seconds 3
$left = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -match 'mdview\\WebView2' })
Check 'no-webview2-processes-left' ($left.Count -eq 0) ("msedgewebview2 with the mdview profile: {0}" -f $left.Count)
$profile = Join-Path $env:LOCALAPPDATA 'mdview\WebView2'
Check 'profile-folder-location' (Test-Path $profile) $profile

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
