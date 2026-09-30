# ZCodeWallpaper — Frosted-glass wallpaper for ZCode

English | [简体中文](README.md)

A tiny tool that adds a frosted-glass wallpaper to the ZCode desktop client.

It does not modify any ZCode files, so ZCode upgrades are unaffected. Double-click the tray icon to tweak parameters — changes apply instantly.

![demo](docs/demo.png)

## How it works

ZCode is an Electron app. This tool launches ZCode with the `--remote-debugging-port` flag, then injects a frosted-glass wallpaper layer (`pointer-events: none`, so the UI stays fully interactive) into the renderer via the Chrome DevTools Protocol (CDP), and keeps the connection alive to support:

- Hot config reload (parameter changes apply immediately)
- Automatic injection into newly opened windows
- Automatic shutdown when ZCode exits

The injector itself is a single C# source file (about 900 lines) compiled with the **.NET Framework 4.8 compiler that ships with Windows**, producing an executable of about 96 KB that requires **no runtime installation** on the target machine.

## Usage

Requirements: Windows 10/11 and ZCode installed.

1. Download `ZCodeWallpaper.exe` from [Releases](../../releases) and put it in a permanent folder (e.g. `D:\ZCodeWallpaper`; the config file is generated next to it — avoid temp folders)
2. Fully quit ZCode first (including the tray icon)
3. Double-click `ZCodeWallpaper.exe` — on first run it creates a "ZCode 壁纸版" desktop shortcut and opens the settings window
4. Click "浏览..." (Browse) to pick an image — the wallpaper applies instantly

From then on, launch ZCode through the desktop shortcut instead of the original one.

> If you build from source, the binary has no embedded icon, but functionality is identical.

## Settings

| Setting | Description |
|---|---|
| Image | Pick a wallpaper via the native Windows file picker; clear it to disable the wallpaper |
| Opacity | 2% – 100%. Lower = fainter wallpaper, clearer UI (recommended 20 – 35). Note this is wallpaper "density": higher = more prominent wallpaper, more UI covered |
| Blur | 0 – 40px. Blurs the wallpaper image only, never the UI text (recommended 8 – 16). Blurred edges look soft, so pair with scale ≥105% |
| Brightness | 50% – 150% |
| Scale | 100% – 150%. Slightly above 100% prevents blurry edge bleed |
| Fit | cover = fill the window (may crop) / contain = show the whole image |
| Position | Top / Center / Bottom — controls the image focal point |

All changes apply instantly and are saved to `config.txt` next to the exe.

Tray context menu: Settings / Edit config file / Reload config / Exit.

## Building from source

```cmd
build.cmd
```

Requires Windows 10/11 (the built-in C# 5 compiler) — no SDK installation needed. Output goes to `dist\ZCodeWallpaper.exe`.

> If `dist\zcode.ico` exists it is embedded as the exe icon (the icon is not distributed in this repo; copy it from your ZCode installation).

## The tools/ folder

- `wallpaper-apply.js` — the wallpaper-layer JS injected into the page (reference copy of what the C# host embeds)
- `inspect.mjs` — attaches to a running ZCode and inspects the computed style of the wallpaper layer (debugging)
- `mock-cdp.mjs` — a mock CDP server for testing the injector without a ZCode installation

Requires Node.js 18+.

## Known limitations

- The wallpaper is an overlay-style "frosted watermark" on top of the UI (text always stays readable), not a background beneath the panels
- ZCode must be launched via this tool (or its shortcut) for the wallpaper to apply; launching from the official icon skips it
- The tool starts ZCode in debug mode; the debug port only listens on localhost (127.0.0.1), but in theory other local processes could reach it — skip this tool if that bothers you
- If a major ZCode update breaks it, simply run this tool again
- Tested with ZCode 3.14.4 (Electron 41 / Chromium 146); other versions are untested

## Uninstall

1. Tray icon → Exit
2. Delete the "ZCode 壁纸版" desktop shortcut
3. Delete the exe folder (config goes with it)

## Disclaimer

This is a personal third-party helper tool, not affiliated with the official ZCode team.
Wallpaper images belong to their authors — only use images you have the rights to.

## License

[MIT](LICENSE)
