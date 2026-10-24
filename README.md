# AmbientBar

A tiny Windows player for long YouTube ambient tracks: audio only, no video, no browser tab.

It docks as a slim bar in the bottom-right corner, above the taskbar. It has a plain-text search, a short queue, and it hooks into Windows media controls, so the keyboard Play/Pause/Next keys, headset buttons and the Windows media flyout all work.

## Download

**[⬇ Download the latest AmbientBar.exe](https://github.com/vikpek/AmbientBar/releases/latest/download/AmbientBar.exe)**

It's a single self-contained exe, so nothing else needs to be installed. (All versions are on the [Releases page](https://github.com/vikpek/AmbientBar/releases).)

AmbientBar uses [yt-dlp](https://github.com/yt-dlp/yt-dlp) for search and audio streams. For the most reliable setup, install it with winget, which also installs the JS runtime (Deno) that yt-dlp now needs for YouTube:

```
winget install yt-dlp.yt-dlp
```

If yt-dlp isn't found, AmbientBar downloads its own copy to `%LOCALAPPDATA%\AmbientBar` on first use.

## Using it

| Action | How |
| --- | --- |
| Search | Click the title or the `^` button, type, press **Enter**. Pasting a YouTube link works too. |
| Queue a result | Click it. If nothing is playing, it starts right away. |
| Play something from the queue | Click it. |
| Remove from the queue | The `×` next to it. |
| Volume | Scroll the mouse wheel over the bar. |
| Repeat queue | The loop icon next to "QUEUE" (on by default). |
| Hide / show | Left-click the tray icon. Right-click it for more options. |
| Quit | The `×` on the bar, or **Quit** in the tray menu. |

The queue, the current track, the playback position and the volume are saved in `%APPDATA%\AmbientBar\state.json`, so a 10-hour track picks up where you left off. YouTube stream links expire after a few hours, and AmbientBar gets a fresh one and continues from the same spot.

## Building

Requires the .NET 10 SDK.

```
dotnet run --project src/AmbientBar
```

## Releasing a new version

```
git tag v0.2.0
git push origin v0.2.0
```

The `Release` GitHub Action builds a self-contained `AmbientBar.exe` and attaches it to a new release. The download link above always points to the newest one.
