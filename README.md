# mdview

mdview is a small Windows reading utility: double-click a Markdown file and it opens in a focused, Claude-artifact-style Brave window that reloads when the file changes.


## Clickable checkboxes

GFM task lists are interactive. Clicking a checkbox toggles `[ ]` / `[x]` **in the
source file** — the file is the source of truth, so the change survives a reload and
shows up in git.

mdview is otherwise still a reader: toggling an existing checkbox is the only thing it
will ever write. If the file changed underneath (say you also have it open in an
editor), the click is refused and the page reloads rather than overwriting your edit.
Line endings, indentation, trailing whitespace, and BOM are preserved byte-for-byte,
so a toggle in a CRLF file doesn't produce a whole-file diff.

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
