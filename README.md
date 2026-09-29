# mdview

mdview is a small Windows utility for reading Markdown. Double-click a `.md` file and it
opens in a clean, Claude-artifact-style window that reloads by itself whenever the file
changes on disk. When you want to change something, unlock the page and edit it in place.

## Install

1. Download `mdview-setup-<version>.exe` from
   [GitHub Releases](https://github.com/GaspsInSpanish/mdview/releases) and run it.
2. The installer is unsigned, so Windows SmartScreen will first say **Windows protected
   your PC**. Click **More info**, then **Run anyway**.
3. The installer registers mdview as a Markdown app, but Windows makes you confirm the
   default yourself. The first time you double-click a `.md` file you may see **How do
   you want to open this file?** Choose **mdview**, and Windows will remember it.

Upgrading: close every mdview window first (and end any leftover `mdview.exe` in Task
Manager), then run the new installer. Only one mdview runs at a time, so a copy that is
still running will keep opening your files.

## Browser

mdview shows documents in a chromeless app window of a Chromium browser it finds on your
PC, in this order:

1. **Brave**
2. **Google Chrome**
3. **Microsoft Edge** (included with Windows 10 and 11)

If none is found, the document opens as an ordinary tab in your default browser, with a
notice saying so.

To pick a browser yourself, create `%APPDATA%\mdview\config.json`:

```json
{
  "browserPath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"
}
```

Any Chromium browser works. If the path you set does not exist, mdview shows an error
rather than quietly using a different browser. (The older `bravePath` setting still works.)

## Reading

- **Live reload.** Save the file in any editor and the page updates on its own.
- **Folding.** Click a heading to collapse its section. **View → Collapse All / Expand
  All** does every section at once. Folding is for this window only and never changes
  the file.
- **Find.** **Edit → Find…** or Ctrl+F highlights every match, with a counter and
  wrap-around. It reaches into collapsed sections and opens them for you.
- **Theme.** **Theme → System, Light or Dark.** Your choice is remembered and applies to
  every open window at once.
- **Checkboxes.** Clicking a task-list checkbox (`- [ ]`) ticks it **in the file itself**,
  so the change survives a reload and shows up in git. If the file changed underneath you,
  the click is refused and the page reloads instead of overwriting the other change.

## Editing

Every document opens **Locked**, so you can read and click around without changing
anything by accident.

1. Click **Locked** at the right end of the menu bar, or press **Ctrl+E**. It changes to
   **Editing**.
2. Click any paragraph, heading, list, table or code block. It turns into its Markdown
   source, right where it was, with the cursor near where you clicked. Type your changes.
3. Click somewhere else, or press **Esc**, and the block renders again. To add text at
   the end of the document, click the dashed box at the bottom.
4. Press **Ctrl+S**, or use **File → Save**, to write the file.
5. Press **Ctrl+E** again to lock. If you have unsaved changes, mdview asks to save first.

While there are unsaved changes, the window title starts with `*` and the lock button
shows a dot. Closing the window with unsaved changes asks before discarding them.

Things that are part of the file but not visible on the page, such as YAML frontmatter,
link definitions (`[name]: https://…`) and footnote text, show as grey source lines while
you are editing, so you can edit those too.

**What mdview changes, and what it doesn't.** Only the blocks you actually edit are
written back, exactly as you typed them. Everything else in the file stays
byte-for-byte identical, including Windows line endings, so `git diff` shows only your
edit. mdview never reformats your Markdown.

**If another program changes the file while you have unsaved edits,** mdview does not
reload over your work. The lock button reads **Editing (changed on disk)**, and when you
save it asks whether to overwrite the other version or keep editing without saving.

**Limits.**
- Undo (Ctrl+Z) works inside the block you are editing. Once you leave a block, change it
  back by editing it again.
- There is no Discard button. To throw your changes away, close or reload the window and
  confirm leaving.
- Files that are not UTF-8 (for example UTF-16 or an old Windows code page) can be read
  but not unlocked, because saving would change their encoding. mdview tells you when this
  applies.

## Menu bar and shortcuts

| Menu | Command | Shortcut |
|---|---|---|
| File | New | |
| File | Open… | |
| File | Save (while editing) | Ctrl+S |
| File | Save As… | |
| File | Exit | Alt+F4 |
| Edit | Copy | Ctrl+C |
| Edit | Select All | Ctrl+A |
| Edit | Find… | Ctrl+F |
| View | Collapse All / Expand All | |
| Theme | System / Light / Dark | |
| (lock button) | Lock / unlock editing | Ctrl+E |

Open and Save As use the normal Windows file dialogs. Save As copies the file as it is on
disk to a new name and switches the window to the copy, like Notepad; if you have unsaved
edits, it offers to save them first. Press **Alt** to move to the menu bar with the
keyboard.

The menu bar is drawn in Windows 98 style because the app window has no menu bar of its
own. It follows your theme rather than being period-accurate grey, so it doesn't glare at
night.

## Scope

mdview opens `.md` files only. More document formats are planned.

## Build from source

In a shell with the .NET 8 SDK available:

```powershell
dotnet publish src/MdView/MdView.csproj -c Release -r win-x64 -p:Version=1.3.1
```

The single-file executable is written to
`src\MdView\bin\Release\net8.0-windows\win-x64\publish\mdview.exe`.
