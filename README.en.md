<div align="center">

<img src="Memo-wpf/Assets/appicon.png" alt="Memo" width="96" height="96" />

# Memo

**A lightweight Markdown memo that keeps every idea within reach on your desktop.**

Pop-out notes, reminders, global hotkeys, and edge docking · Built for Windows

<p>
  <img src="https://img.shields.io/badge/platform-Windows-0078D4?style=flat-square" alt="Windows" />
  <img src="https://img.shields.io/badge/version-0.0.1-4C8BF5?style=flat-square" alt="Version 0.0.1" />
  <img src="https://img.shields.io/badge/tech-.NET%208%20%2B%20WPF-512BD4?style=flat-square" alt=".NET 8 + WPF" />
  <img src="https://img.shields.io/badge/language-C%23-239120?style=flat-square" alt="C#" />
  <img src="https://img.shields.io/badge/docs-简中%20%2F%20繁中%20%2F%20EN-2EA043?style=flat-square" alt="Three languages" />
</p>

[简体中文](README.md) · **English** · [繁體中文](README.zh-TW.md)

</div>

---

## What is it?

Memo is a Windows desktop memo focused on quick capture and constant visibility. It is not a heavy knowledge base and it does not upload your notes to the cloud: open the app, write Markdown, then leave it at the edge of your screen or turn it into an independent desktop note.

Think of it as **a Markdown scratchpad that is always nearby, plus a set of movable desktop notes**.

## What it can do

| | Capability | Details |
| :---: | --- | --- |
| 📝 | **Quick capture** | Type in the top editor and press `Ctrl + Enter` to add a memo. Content with an empty first line is ignored. |
| ✨ | **Markdown editing** | The WYSIWYG editor supports headings, emphasis, strikethrough, lists, tasks, quotes, links, code, tables, separators, and images while storing Markdown source. |
| 💾 | **Auto-save** | Changes save after about 500 ms of inactivity. `Ctrl + Enter` saves immediately; `Esc` restores the content from the current edit session. |
| 🔀 | **Drag to reorder** | Drag cards to reorder them. Drop a card outside the main window to open it as a pop-out note. |
| 📌 | **Pop-out notes** | Every memo can become an independent editable window with pinning, taskbar visibility, timestamp switching, and close animations. |
| ⏰ | **Reminders** | Set reminders accurate to the second. They still fire while Memo is in the tray, and overdue reminders are triggered when the app starts again. |
| 🧲 | **Edge docking** | Dock the main window to any screen edge or corner as a small rounded tile, then drag it inward to restore it. |
| 🖥️ | **Desktop integration** | System tray, global hotkeys, always-on-top, borderless rounded windows, edge/corner resizing, and single-instance activation. |
| 🎨 | **Appearance controls** | Light, dark, or system theme; motion follows the system, stays on, or turns off. The dock tile size is adjustable too. |
| 🔒 | **Local storage** | Memos, settings, and images stay in `%AppData%/Memo/` on your computer. |

## Quick start

**1. Install the .NET 8 SDK**

Memo targets Windows and requires the .NET 8 SDK for source builds and development. Published Windows output can run directly.

**2. Start Memo**

From the repository root:

```powershell
dotnet run --project Memo-wpf/Memo.csproj
```

The app opens as a compact borderless window. Open **Settings → Tutorial** to review the current shortcuts and window gestures.

**3. Write your first memo**

Type in the top editor and press `Ctrl + Enter`. Double-click an existing card to edit it; changes auto-save after you stop typing. Drag a card to reorder it or drop it outside the main window to pop it out.

## Notes and reminders

Pop-out notes are useful for keeping one memo visible in your workspace. The title-bar pin toggles always-on-top for that note, the toolbar button shows or hides the Markdown toolbar, and clicking the timestamp switches between relative and full time.

Click the clock button to set a reminder. Pick a date from the calendar or type it directly, then select hours, minutes, and seconds with the wheel controls. Four quick offsets are available: **1 minute**, **10 minutes**, **30 minutes**, and **1 hour**. Reminders run on the in-app timer; after a full exit, Memo does not run in the background.

## Edge docking

Drag the main window within about 40 pixels of a screen edge and it collapses into a small rounded tile. Move the tile along the edge, drag inward to restore the window at the cursor, or drag it into a corner to attach to two edges. The **Dock tile size** setting supports 30–75 pixels with live preview, and docking can be disabled entirely.

## Shortcuts

### Global hotkeys

| Action | Default | Details |
| --- | --- | --- |
| Toggle always-on-top | `Ctrl + Alt + T` | Toggle the most recently active main, pop-out, or tutorial window. |
| Toggle pop-out taskbar button | `Ctrl + Alt + B` | Show or hide the taskbar button for the most recently active note. |
| Minimize to tray | `Ctrl + Alt + M` | Hide the main window while leaving Memo in the tray. |
| Show main window | `Ctrl + Alt + N` | Bring the main window back. |
| Quick add clipboard | `Ctrl + Alt + C` | Add clipboard text as a memo; configurable in Settings. |

Global hotkeys can be changed under **Settings → Hotkey settings**. Press a new combination to capture it, `Esc` to clear it, or right-click to cancel. System-reserved and already-occupied combinations are rejected.

### Editor keys

| Context | Key | Action |
| --- | --- | --- |
| New memo | `Ctrl + Enter` | Create a memo. |
| Any editor state | `Enter` | Insert a line break. |
| Markdown editor | `Ctrl + B` / `Ctrl + I` / `Ctrl + K` | Insert bold, italic, or a link. |
| Existing memo | `Ctrl + Enter` | Save immediately and keep editing. |
| Existing memo | `Esc` | Restore the loaded snapshot. |

## Settings

Settings are saved automatically and applied immediately. You can configure the close action, clipboard quick add, pop-out duplication, edge docking, taskbar buttons, tray click behavior, dock tile size, theme, motion, global hotkeys, and the built-in tutorial. A reset action restores the defaults after confirmation.

## Requirements

- Windows 10 or 11;
- .NET 8 SDK for source builds;
- WPF on .NET 8;
- No network connection, cloud sync, or account is required.

Memo does not call external APIs. Unless you explicitly insert a remote image URL, it only handles content you enter or select locally.

## Data storage

Memo creates this directory:

```text
%AppData%/Memo/
├── memos.json       # Memos and reminders
├── settings.json    # Close behavior, hotkeys, docking, theme, and motion
└── assets/          # Local images, deduplicated by SHA-256
```

JSON is written asynchronously with camelCase names and indentation. A semaphore protects concurrent writes. If loading fails, Memo silently falls back to defaults; deleting these files resets the app on the next launch.

## For developers

The main project is the WPF implementation in `Memo-wpf/`, where all further development happens. `Memo-avalonia/` is the earlier Avalonia implementation, no longer updated and kept only for reference. Build, run, and publish the project file directly:

```powershell
dotnet build Memo-wpf/Memo.csproj
dotnet run --project Memo-wpf/Memo.csproj
dotnet publish Memo-wpf/Memo.csproj -c Release -r win-x64 --self-contained false
```

Main dependencies are .NET 8 / C#, WPF, AvalonEdit 6.3.1.120, and Markdig 0.41.3.

```text
Memo-wpf/
├── Views/                 # Main, pop-out, reminder, settings, tutorial, and tray windows
├── Components/            # Controls and custom dialogs
├── Editor/                # Markdown editor host
├── ViewModels/            # Main-window state and memo collection management
├── Models/                # Memo, settings, and hotkey models
├── Services/              # JSON storage, image, and edit coordination services
├── Markdown/              # Parsing, formatting, and summary projection
├── Behaviors/             # Drag-reorder and other interaction behaviors
├── Platform/Windows/      # Tray, global hotkeys, and single-instance integration
├── Infrastructure/        # Application startup and infrastructure
├── UI/                    # Theme, motion, window, docking, and text helpers
├── Utils/                 # Time formatting and shared utilities
├── Resources/             # XAML resource dictionaries
└── Assets/                # Application icons
```

---

<div align="center">

**Keep important things on your desktop, and on your own computer.**

</div>
