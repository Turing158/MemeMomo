<div align="center">

<img src="MemeMomo-wpf/Assets/appicon.png" alt="MemeMomo" width="96" height="96" />

# MemeMomo

**A lightweight Markdown memo that keeps every idea within reach on your desktop.**

Task statuses, quick creation, pop-out notes, reminders, and edge docking · Built for Windows

<p>
  <img src="https://img.shields.io/badge/platform-Windows-0078D4?style=flat-square" alt="Windows" />
  <img src="https://img.shields.io/badge/version-1.0.0-4C8BF5?style=flat-square" alt="Version 1.0.0" />
  <img src="https://img.shields.io/badge/tech-.NET%208%20%2B%20WPF-512BD4?style=flat-square" alt=".NET 8 + WPF" />
  <img src="https://img.shields.io/badge/language-C%23-239120?style=flat-square" alt="C#" />
  <img src="https://img.shields.io/badge/docs-简中%20%2F%20繁中%20%2F%20EN-2EA043?style=flat-square" alt="Three languages" />
</p>

[简体中文](README.md) · **English** · [繁體中文](README.zh-TW.md)

</div>

---

## What is it?

MemeMomo is a Windows desktop memo focused on quick capture and constant visibility. It is not a heavy knowledge base and it does not upload your notes to the cloud: open the app, write Markdown, then leave it at the edge of your screen or turn it into an independent desktop note.

Think of it as **a Markdown scratchpad that is always nearby, plus a set of movable desktop notes**.

## What it can do

| | Capability | Details |
| :---: | --- | --- |
| 📝 | **Quick capture** | Type in the top editor and press `Ctrl + Enter` to add a memo. Content with an empty first line is ignored. |
| ✨ | **Markdown editing** | The WYSIWYG editor supports headings, emphasis, strikethrough, lists, tasks, quotes, links, code, tables, separators, and images while storing Markdown source. |
| 💾 | **Auto-save** | Changes save after about 500 ms of inactivity. `Ctrl + Enter` saves immediately; `Esc` restores the content from the current edit session. |
| 🔀 | **Drag to reorder** | Drag cards to reorder them. Drop a card outside the main window to open it as a pop-out note. |
| 📌 | **Pop-out notes** | Every memo can become an independent editable window with pinning, taskbar visibility, timestamp switching, and close animations. |
| ⏰ | **Reminders** | Set reminders accurate to the second for notes or active tasks. They still fire while the app is in the tray, and overdue reminders are triggered when it starts again. |
| 🧲 | **Edge docking** | Dock the main window to any screen edge or corner as a small rounded tile, then drag it inward to restore it. |
| 🖥️ | **Desktop integration** | System tray, global hotkeys, always-on-top, borderless rounded windows, edge/corner resizing, and single-instance activation. |
| 🎨 | **Appearance controls** | Light, dark, or system theme; motion follows the system, stays on, or turns off. The dock tile size is adjustable too. |
| 🔒 | **Local storage** | Memos, settings, and images stay in `%AppData%/MemeMomo/` on your computer. |

## New features and how to use them

- **Task memos and statuses**: Choose a regular note or task in the editor's type/status selector. The card context menu also lets you convert types, change status, complete tasks, or reopen them. Built-in categories are To do, In progress, On hold, Done, and Canceled. Under **Settings → Tasks and statuses → Manage statuses**, add custom statuses with colors and icons; set the default memo type and new-task status in the task settings. Closing a task cancels its reminder; set a new reminder after reopening it.
- **Filtering and sorting**: Filter the main list by memo type, active tasks, status category, or individual status, with a visible item count. Choose manual order or active tasks first. Drag reordering is available only in unfiltered manual order; clear filters to restore it.
- **Quick creation at the pointer**: Press `Ctrl + Alt + Q` to open a blank note draft, choose its type and status, then enter content and press `Ctrl + Enter` to save it as a memo. Subsequent edits auto-save. Closing an unsaved draft offers Save, Discard, or Cancel. `Ctrl + Alt + C` still adds clipboard text directly.
- **Markdown editing improvements**: Edit table cells directly and right-click to insert or delete rows and columns. Insert images from files, paste, drag and drop, or HTTPS URLs; edit image details and adjust display width. Code blocks support language selection and code copying. Use **Edit source** on the toolbar to modify Markdown directly.
- **Basic HTML display**: Common tags and inline styles support text color, font size, highlighting, subscript/superscript, paragraph alignment, image dimensions, links, and lists while preserving source. Scripts and full browser layouts are not supported. See the [HTML support reference (Chinese)](MemeMomo-wpf/Markdown/HTML-SUPPORT.md) for details.
- **Note appearance and languages**: Choose Simplified Chinese, Traditional Chinese, or English in Settings, and use the theme color or one of seven preset note backgrounds. Default note pinning, note docking, and main-window docking are configurable.

## Quick start

**1. Install the .NET 8 SDK**

MemeMomo targets Windows. Running or building from source requires the .NET 8 SDK. Framework-dependent releases require the .NET 8 Desktop Runtime; self-contained releases do not require a separate .NET installation.

**2. Start MemeMomo**

From the repository root:

```powershell
dotnet run --project MemeMomo-wpf/MemeMomo.csproj
```

The app opens as a compact borderless window. Open **Settings → Tutorial** to review the current shortcuts and window gestures.

**3. Write your first memo**

Type in the top editor and press `Ctrl + Enter`. Double-click an existing card to edit it; changes auto-save after you stop typing. In unfiltered manual order, drag a card to reorder it or drop it outside the main window to pop it out.

## Notes and reminders

Pop-out notes are useful for keeping one memo visible in your workspace. The title-bar pin toggles always-on-top for that note, the toolbar button shows or hides the Markdown toolbar, and clicking the timestamp switches between relative and full time.

Click the clock button to set a reminder. Pick a date from the calendar or type it directly, then select hours, minutes, and seconds with the wheel controls. Four quick offsets are available: **1 minute**, **10 minutes**, **30 minutes**, and **1 hour**. Reminders run on the in-app timer; after a full exit, MemeMomo does not run in the background.

Reminders use Windows notifications by default. In **Settings → Reminder mode**, choose an in-app window or both instead. Windows notifications respect Do not disturb. Completed or canceled tasks must be reopened before setting a reminder.

## Edge docking

Independent notes can also collapse into semicircular tabs at the left or right screen edge. Note docking has its own switch in Settings.

Drag the main window within about 40 pixels of a screen edge and it collapses into a small rounded tile. Move the tile along the edge, drag inward to restore the window at the cursor, or drag it into a corner to attach to two edges. The **Dock tile size** setting supports 30–75 pixels with live preview, and docking can be disabled entirely.

## Shortcuts

### Global hotkeys

| Action | Default | Details |
| --- | --- | --- |
| Toggle always-on-top | `Ctrl + Alt + T` | Toggle the most recently active main, pop-out, or tutorial window. |
| Toggle pop-out taskbar button | `Ctrl + Alt + B` | Show or hide the taskbar button for the most recently active note. |
| Minimize to tray | `Ctrl + Alt + M` | Hide the main window while leaving the app in the tray. |
| Show main window | `Ctrl + Alt + N` | Bring the main window back. |
| Quick creation | `Ctrl + Alt + Q` | Open a blank note draft at the pointer; save it to create a memo. |
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

## About and update checks

**Settings → About** shows the current version and links to the repository, release notes, issue tracker, and third-party notices. **Check for updates** queries GitHub Releases over the network. When **Update available** appears, click again to open the release page and download the update yourself. Failed checks can be retried; the app does not automatically download or install updates.

## Requirements

- Windows 10 or 11;
- .NET 8 SDK for source builds;
- WPF on .NET 8;
- No network connection, cloud sync, or account is required.

Everyday note-taking works offline. Manual update checks contact the GitHub API, remote images require a network connection, and project links open in your browser. Memo content is not uploaded to the cloud.

## Data storage

MemeMomo creates this directory:

```text
%AppData%/MemeMomo/
├── memos.json       # Memos, task status references, and reminders
├── task-statuses.json # Built-in and custom task statuses
├── settings.json    # Close behavior, hotkeys, docking, theme, and motion
└── assets/          # Local images, deduplicated by SHA-256
```

JSON uses camelCase names and indentation. Saves atomically replace files through temporary files and retain `.bak` backups; the first overwrite of an existing file after upgrading also keeps a `.pre-tasks.bak` copy. If reading fails, the app attempts to preserve the damaged file and recover from backup. Files that cannot be recovered are blocked from further saves and a problem is reported. Back up memos, task statuses, settings, and images together to preserve task status references.

## For developers

The main project is the WPF implementation in `MemeMomo-wpf/`, where all further development happens. `MemeMomo-avalonia/` is the earlier Avalonia implementation, no longer updated and kept only for reference. Build, run, and publish the project file directly:

```powershell
dotnet build MemeMomo-wpf/MemeMomo.csproj
dotnet run --project MemeMomo-wpf/MemeMomo.csproj
dotnet publish MemeMomo-wpf/MemeMomo.csproj -c Release -r win-x64 --self-contained false
```

Main dependencies are .NET 8 / C#, WPF, AvalonEdit 6.3.1.120, and Markdig 0.41.3.

```text
MemeMomo-wpf/
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
