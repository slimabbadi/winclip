# WinClip

A keyboard-first clipboard history for Windows, inspired by [Maccy](https://github.com/p0deje/Maccy) on macOS.

Press **Ctrl+Alt+V** anywhere, type a few letters, and press **Enter** to paste into the app you were using.

<p align="center">
  <img src="docs/popup.png" width="460" alt="The WinClip popup: a search box above recent clips, with a selected screenshot expanded to show its preview.">
</p>

## Features

- **Text, images and files.** Formatting from Word, browsers and Excel is kept. Screenshots show as thumbnails. Files copied in Explorer paste back as files.
- **Fast search.** Substring matches first, then fuzzy matches, so `gtrb` finds `git rebase`.
- **Pins.** Pinned items stay at the top and are never trimmed or cleared.
- **Pastes where you were typing.** The popup opens at the text cursor in apps that report it (otherwise at the mouse), and the chosen item is pasted into the app you came from.
- **Private by default.** History is encrypted with your Windows account, password-manager copies are skipped, and you can pause recording or exclude apps.
- **Small.** A tray app under 3 MB with no account, no network access and no telemetry.

## Install

WinClip is built for Windows 11 (Windows 10 should work but is untested) and needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build.

```powershell
git clone https://github.com/slimabbadi/winclip.git
cd winclip
pwsh scripts\publish.ps1
```

This builds `dist\WinClip.exe` and adds **WinClip** shortcuts to the Start Menu and Desktop. Double-click either one to start it; WinClip lives in the tray. Starting it again while it runs opens Settings.

The exe needs the .NET 10 Desktop Runtime, which the SDK includes. Pass `-SelfContained` for a larger exe that runs on machines without .NET.

## Keys

| Key | Action |
|---|---|
| Ctrl+Alt+V | Open history (change it in Settings) |
| Type | Filter |
| Up / Down | Move the selection |
| Enter | Paste into the previous app |
| Shift+Enter | Paste as plain text (file items paste their paths) |
| Ctrl+Enter | Copy without pasting |
| Ctrl+1 … Ctrl+9 | Paste that row |
| Alt+P | Pin or unpin |
| Alt+Del | Delete the item |
| Esc | Close |

Opening history from the tray icon copies instead of pasting, because clicking the tray takes focus away from your app.

## Settings

<img src="docs/settings.png" width="400" align="right" alt="WinClip settings: shortcut recorder, history size, longest copy to keep, apps to never record, and Start with Windows.">

Open **Settings…** from the tray icon menu. Changes apply when you click **Save**.

- **Shortcut:** click the box and press the new combination. If another app already uses it, Settings says so and keeps the old one.
- **History size** and **Longest copy to keep** limit what is stored.
- **Never record copies from** lists apps by process name.
- **Start with Windows** starts WinClip quietly when you sign in.

Settings are saved to `%LOCALAPPDATA%\WinClip\settings.json`.

<br clear="right">

## Privacy

- Copies marked private are never recorded. Most password managers mark them with `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore`, or `CanIncludeInClipboardHistory = 0`.
- **Pause recording** in the tray menu stops recording until you turn it off or restart WinClip.
- History lives in `%LOCALAPPDATA%\WinClip\history.db`. Text, images and formatting are encrypted with Windows DPAPI, so only your Windows account can read them. Duplicate detection uses a keyed hash, so the file doesn't reveal whether it contains a guessed value. Deleted items are overwritten, not just unlinked.
- Not encrypted: which app each item came from, and when it was copied.

## Known limits

- Windows blocks simulated keystrokes into apps running as administrator unless WinClip also runs as administrator. In those apps, the item is copied but not pasted.
- Chromium, Electron and most terminals don't report their text cursor, so the popup opens at the mouse there.
- When text and a picture are copied together, as Excel does, WinClip keeps the text and its formatting, not the picture.
- Images larger than 32 MB as PNG are skipped.

## Development

```powershell
dotnet build WinClip.slnx
dotnet test WinClip.slnx
dotnet run --project src/WinClip
```

| Path | Contents |
|---|---|
| `src/WinClip.Core` | History storage, search, settings. Platform-neutral and unit-tested. |
| `src/WinClip` | The WPF app: Win32 clipboard listener, global shortcut, paste, tray, popup, settings. |
| `tests/WinClip.Tests` | xUnit tests for Core. |
| `scripts/publish.ps1` | Builds `dist\WinClip.exe` and creates shortcuts. |
| `scripts/e2e-smoke.ps1` | End-to-end test on the real desktop. It types for you, so leave the keyboard alone while it runs. |
| `scripts/make-icon.ps1` | Regenerates the app icon. |

## License

[MIT](LICENSE)
