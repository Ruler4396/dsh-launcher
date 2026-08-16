# dsh-launcher

[简体中文](README.md) · [English](README.en.md)

[![build](https://github.com/Ruler4396/dsh-launcher/actions/workflows/build.yml/badge.svg)](https://github.com/Ruler4396/dsh-launcher/actions/workflows/build.yml)
[![license](https://img.shields.io/github/license/Ruler4396/dsh-launcher)](LICENSE)
[![release](https://img.shields.io/github/v/release/Ruler4396/dsh-launcher)](https://github.com/Ruler4396/dsh-launcher/releases)
[![featured on dsh-suite](https://img.shields.io/badge/featured%20on-dsh--suite-4d6bfe)](https://whyihaveyou.github.io/dsh-suite/)

> A lightweight Windows launcher for DeepSeek Harness: autostart at logon + a small WebView2 window instead of a full browser. Double-click to run — no command line needed.

| Light | Dark |
|---|---|
| ![Light mode](assets/screenshot-light.png) | ![Dark mode](assets/screenshot-dark.png) |

## Install

**Option 1: MSI installer (recommended for beginners)**

1. Download `dsh-launcher-<version>.msi` from [Releases](https://github.com/Ruler4396/dsh-launcher/releases)
2. Double-click and follow the wizard (choose **autostart** / desktop / start menu shortcuts, custom folder supported)
3. Install and uninstall prompt once for **UAC elevation** (per-machine install, default `%ProgramFiles%\dsh-launcher`)
4. Uninstall: Settings → Apps → dsh-launcher (or the "卸载 dsh-launcher" Start Menu entry)

**Option 2: portable ZIP**

1. Download `dsh-launcher-windows.zip` and extract anywhere
2. Run `DshWeb.exe`
3. Uninstall: delete the folder (use `uninstall-autostart.cmd` to remove autostart/shortcuts)

## Requirements

| Dependency | Why | How to get |
|---|---|---|
| **Node.js 18+** | required to run the dsh service (a global dsh install is optional — the launcher falls back to `npx -y @deepseek-ai/dsh`) | https://nodejs.org (LTS, default install) |
| **.NET Desktop Runtime 10** | required to run the shell | if double-click does nothing, run `winget install Microsoft.DotNet.DesktopRuntime.10` |
| **WebView2 Runtime** | renders the window | usually preinstalled on Windows 10/11; if missing the launcher auto-installs it silently on startup (E1006 only if that fails) |

## Features

- 🚀 **Autostart** — opens the launcher window at logon (the shell then starts the dsh service and waits until ready)
- 🪟 **Lightweight window** — WebView2 (~50–150MB, freed on close) instead of a full browser
- 🔌 **Auto-launch** — starts the service if not running and waits until ready (first run downloads components, with progress)
- 🔔 **Clear error prompts** — missing Node.js / download failures / port conflicts all show an explicit dialog
- 🎛️ **Node service lifetime** — switch always-on / tray-resident / follow-window in the dsh settings page (companion plugin); decides whether the node service keeps running after the window closes
- 🌗 **Theme follow** — the window title bar (custom-painted) and window/taskbar icons follow the dsh theme instantly (dark/light), no restart
- 📋 **Unified logging** — shell decisions and dsh service output share a single log file `~/.dsh\dsh-launcher\dsh.log` (JSON Lines + raw output, controlled by `DSH_LOG_LEVEL`; the go-to when startup fails)

## It won't start? Check by symptom

> Most "nothing happens" cases come from **missing dependencies**. First confirm Node.js and .NET are installed (see Requirements), then check the symptom.

**Symptom 1: double-click does nothing (no window, no dialog)**

Mostly a missing **.NET Desktop Runtime 10**. In PowerShell:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10
```

Then double-click again. Still nothing → see "View the log" below.

> Tip: during **MSI install** the prerequisite check offers an "Auto-install (A)" button that runs winget to install .NET for you.

**Symptom 2: dialog "Node.js not detected, cannot start the dsh service"**

Since v0.3.0 the launcher shows a one-time confirm and **auto-downloads a portable Node.js** (LTS, SHA256-verified, mirror fallback, installed to `%LOCALAPPDATA%\dsh-launcher\env\node\` without touching system settings). Or install [Node.js](https://nodejs.org) 18+ yourself (LTS, default options), then **reopen** dsh-launcher.

**Symptom 3: stuck on "starting dsh service… first run downloads components"**

The first run downloads the dsh components via npx — **it can take a few minutes** depending on your network. Wait:
- download finishes → the window opens automatically
- after 3 minutes a dialog explains the reason (slow download / network issue) and shows the log tail

For slow networks you can switch npm to a mirror and retry:

```powershell
npm config set registry https://registry.npmmirror.com
```

**Symptom 4: dialog "dsh service could not be reached" / "service unavailable"**

Open the log and check the last lines: `~/.dsh\dsh-launcher\dsh.log`

- `npm ERR` or network errors → **network/proxy issue**, retry or change network
- `'npx' is not recognized` → **Node.js is not installed properly**, reinstall (Symptom 2)
- `EADDRINUSE` → **port 3080 is taken**, see Symptom 5

**Symptom 5: port 3080 is used by another program**

Set `DSH_WEB_PORT` to another port and restart — the shell starts the dsh service on that port automatically (simplest, recommended):

```powershell
$env:DSH_WEB_PORT = "3090"
```

If you manage the service yourself, point `DSH_WEB_URL` at it instead (the shell will not auto-start the service — see [docs/DETAILS.md](docs/DETAILS.md)):

```powershell
$env:DSH_WEB_URL = "http://127.0.0.1:3090"
```

**View the log:**

```powershell
Get-Content "$env:USERPROFILE\.dsh\dsh-launcher\dsh.log" -Tail 30
```

The first log line tells you whether the service was started with a global `dsh` or the `npx` fallback (a `[start-dsh] using ...` line). The JSON Lines rows record every shell decision point (single instance, port probe, service start, readiness, window shown); error dialogs carry `[E####]` codes. Attach `~/.dsh\dsh-launcher\dsh.log` when reporting an issue.

## FAQ

**Q: MSI vs ZIP?**
Same contents. MSI adds a standard install/uninstall flow (recommended); ZIP is portable.

**Q: Can I choose the install folder? Will uninstall delete other files in the same folder?**
Yes to custom folder; uninstall only removes the app's own files and keeps a non-empty folder (verified).

**Q: The dsh service keeps using hundreds of MB of memory?**
dsh is a full service (with web UI); staying resident is by design (instant open). To save memory: dsh settings page → **"Node 服务驻留 / Node service lifetime"** → **follow-window** (service stops on window close and is auto-restarted next time).

## More

- Implementation / Security / Release policy / Building from source: [docs/DETAILS.md](docs/DETAILS.md)
- Changelog: [CHANGELOG.md](CHANGELOG.md)

## Disclaimer & License

Independent third-party tool, not affiliated with DeepSeek / DeepSeek AI. The window icon uses the DeepSeek brand mark, copyright of DeepSeek, used locally only. [MIT](LICENSE) © dsh-launcher contributors
