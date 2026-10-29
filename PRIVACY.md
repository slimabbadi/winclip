# WinClip privacy

WinClip stores clipboard history locally. It has no account system, telemetry, or automatic crash reporting.

## What is stored

WinClip records supported clipboard text, images, copied file paths, and formatting. It also records the source application when available, timestamps, copy counts, and pin state.

History is stored in `%LOCALAPPDATA%\WinClip\history.db`. Clipboard contents, thumbnails, and formatting are encrypted using Windows DPAPI for the current Windows account. Source application names and timestamps are not encrypted. This protects the stored contents from casual inspection; it does not protect against software already running with access to your Windows account.

Preferences are stored in `settings.json` in the same directory. Diagnostic logs record failure types and stack traces, not clipboard text. Logs are local and are not submitted automatically. Velopack also maintains installation/update diagnostics and downloaded packages outside the history folder.

## Network activity

Automatic update checks are enabled by default. WinClip contacts GitHub Releases shortly after startup and about every 24 hours while running. Update checks and downloads expose ordinary request information, such as your IP address, to GitHub and its download hosts. Clipboard history is not included in those requests.

Turn off **Check for updates automatically** in Settings to stop scheduled checks. A check already in progress can finish. Manual **Check for updates…** still contacts GitHub when requested. Support links open external websites in your browser, subject to those websites' privacy practices. See [GitHub's privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

## Control and deletion

- **Pause recording** stops new capture for the current session. Restarting WinClip resumes recording.
- Ignored-app preferences and supported clipboard privacy markers exclude matching copies. These mechanisms cannot guarantee that every sensitive copy is identified.
- History limits trim unpinned items. Pinned items remain until explicitly deleted.
- **Clear history…** in the tray keeps pinned items.
- **Delete all local data…** in Settings, available in the next release after 0.2.0, removes all history including pins, preferences, and WinClip's diagnostic log after confirmation. It also turns off Start with Windows and quits. Cancel keeps the data. It leaves the current Windows clipboard, unrelated files, and installer/update diagnostics alone.
- Uninstalling WinClip preserves history and settings. Use the deletion control before uninstalling if you want them removed.

Deleting files does not guarantee forensic erasure from SSDs, backups, or filesystem snapshots. Other applications, Windows clipboard history, and Windows clipboard synchronization have their own access, storage, and deletion behavior; WinClip does not control those systems.

## Questions

Report privacy problems through [WinClip issues](https://github.com/slimabbadi/winclip/issues). Do not include clipboard contents, secrets, or your history database in a public report.
