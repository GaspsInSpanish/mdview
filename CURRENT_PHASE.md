# Current phase

No active implementation packet. W4 (Win98 frame) completed 2026-10-11, committed, not released
(W3 and W4 both unreleased; the owner chose to go straight from W3 to W4). Next is **W5** in
`projectoutline.md` §5b: the installer checks for and bootstraps the WebView2 runtime, and the
dormant browser-launch code (`BrowserLauncher`, `BrowserDiscovery`, `browserPath`/`bravePath`) is
deleted.

Windows acceptance: `tools/WindowsBlackBox/run.sh` (app black-box) and `run-probes.sh` (every
ServeProbe suite natively on Windows). The owner must close their mdview first.
