# mdview

mdview is a small Windows reading utility: double-click a Markdown file and it opens in a focused, Claude-artifact-style Brave window that reloads when the file changes.

## Install

Download the installer from GitHub Releases and run it. The installer is intentionally unsigned, so Windows SmartScreen will initially show **Windows protected your PC**. Click **More info**, then **Run anyway**. This is expected for the unsigned v1 installer.

The installer registers mdview as an available Markdown app. Windows requires you to confirm a default app yourself: the first double-click of a `.md` file may show the one-time **How do you want to open this file?** picker. Choose **mdview** there and Windows will retain the choice.

## Brave location override

For a non-standard Brave installation, create `%APPDATA%\mdview\config.json`:

```json
{
  "bravePath": "C:\\Tools\\Brave\\brave.exe"
}
```

If this configured path does not exist, mdview shows an error rather than silently choosing a different browser.

## Scope

v1 opens `.md` files only. More document formats are planned.

## Build from source

In a shell with the .NET 8 SDK available:

```powershell
dotnet publish src/MdView/MdView.csproj -c Release -r win-x64 -p:Version=1.0.0
```

The published single-file executable is at `src/MdView\bin\Release\net8.0-windows\win-x64\publish\mdview.exe`.
