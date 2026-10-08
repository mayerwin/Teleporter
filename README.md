<div align="center">

# Teleporter

**Your coding agents keep working while your PC is off.**

Teleport a project from your Windows PC to an always-on Linux server, let Claude Code and
Antigravity keep going there, steer them from your phone, and bring everything back when you are done.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Idle CPU: 0 ms](https://img.shields.io/badge/idle%20CPU-0%20ms%2Fmin-success)
![Single exe](https://img.shields.io/badge/install-none%20(single%20exe)-informational)

[Why](#why-teleporter) · [Features](#features) · [Quick start](#quick-start) · [How it works](#how-a-transfer-stays-safe) · [Extending](#extending-it) · [Build](#build-from-source)

</div>

---

## Why Teleporter?

Agents are slow and your PC is not always on. You start a long refactor with Claude Code, then you
need to leave, and the session dies when the PC sleeps. A server would be perfect, but moving a
project there by hand (files git ignores, exec bits, line endings, the agent's own history and
memory) is tedious and easy to get wrong.

Teleporter makes that one click, in both directions, and verifies every byte.

```
  Your PC (Windows)                                  Server (Linux, always on)
 ┌───────────────────┐   Send to server (SHA-256)   ┌──────────────────────────────┐
 │ my-project/       │ ───────────────────────────▶ │ ~/projects/my-project/       │
 │ + Claude sessions │                              │ + sessions, memory, history  │
 │ + memory          │ ◀─────────────────────────── │ claude remote-control  ◀──── phone, claude.ai,
 └───────────────────┘        Bring back            │ agy remote-control           │  Claude Desktop
         │                                          └──────────────────────────────┘
         └──── Open in VS Code / Antigravity (Remote-SSH), Terminal ─────▲
```

## Features

| | |
|---|---|
| 🚀 **One-click teleport** | Send any folder (git or not, ignored files included) to the server, or create a new project there. Bring it back to where it was. |
| 🧠 **Agents keep their memory** | Optionally moves the folder's Claude Code sessions, memory and edit checkpoints, rewriting paths so `claude --resume` just works on the other side. |
| 🤖 **Agents as background services** | Each project gets a `claude remote-control` systemd service, steerable from Claude Desktop, claude.ai or your phone. Antigravity's daemon is managed too. |
| 🔒 **Nothing lost, ever** | Every file is hashed before and after; the original is only set aside once the copy is verified, and nothing is deleted until you confirm. |
| 🪶 **Zero idle cost** | 0 ms CPU per minute and ~20 MB RAM with the window closed. One portable ~22 MB exe, no installer, no .NET install. |
| 🖥️ **Open anywhere** | Open the server copy in VS Code or the Antigravity IDE over Remote-SSH, or in a terminal (inside the server's dev container if you use one). |
| 📊 **Server at a glance** | Memory used by your account and by system services you pick, agent services, add-on status, and a safe **Stop everything** button. |
| 🧩 **Pluggable** | Credentials come from connection providers (bring your own vault), and add-ons inject per-agent environment such as a proxy. |

## Quick start

1. **Prepare the server.** Any Linux machine with `ssh`, `python3`, `tar` and `git`, plus a
   **dedicated account** for dev work. Everything Teleporter starts runs as that account, which is
   what makes Stop everything safe. Install Claude Code (`~/.local/bin/claude`) and/or the
   Antigravity CLI there.
2. **Get Teleporter.** [Build it](#build-from-source) (one command) and copy `Teleporter.exe`
   anywhere. It keeps its settings in a `Teleporter.data` folder next to itself.
3. **Connect.** Settings tab: host, user and port, using your own OpenSSH keys and agent (or an
   [external provider](#connection-providers)). Requires Git for Windows, whose `ssh.exe` is used.
4. **Teleport.** Projects tab, *Send a local folder to the server*, then *Start Claude agent*.
   Turn the PC off. Pick the session up from claude.ai or your phone.

Optional:
- **Toolbx container** (Settings): agents can `sudo dnf install` inside it without admin rights on
  the server.
- **VS Code**: Settings, "Set up VS Code for Teleporter" points Remote-SSH at Teleporter's ssh config
  and installs the Claude Code extension on the server automatically.
- **Local models**: if the server runs [llama-swap](https://github.com/mostlygeek/llama-swap), give
  its URL to see loaded models and unload them.

## How a transfer stays safe

1. **Pre-check.** Hashes every file and lists problems (locked files, names Windows cannot hold,
   names differing only by case, a folder already in the way) and warnings (links, a Claude session
   active in the last 2 minutes, paths over 260 characters). Problems block the move.
2. **Copy.** One `tar` stream over ssh into a staging folder next to the destination.
3. **Verify.** The destination hashes every file again; only an exact match (same files, sizes,
   SHA-256) is renamed into place.
4. **Set aside.** Only then is the source moved to a parking folder, leaving a stub with an
   "Open on server" launcher. Follow-up steps never undo a verified copy.

Handled for you: Linux exec bits (from git and from the last trip), Windows line endings in git repos
(`core.autocrlf` set on the server and restored on the way back), empty folders, Unicode names, and an
optional skip list for OS-specific folders (`node_modules`, `.venv`, `venv`).

## Extending it

Anything specific to your own infrastructure stays out of Teleporter and plugs in from outside.

### Connection providers

- **OpenSSH** (built in): your own keys and agent, host/user/port from the form.
- **External command**: `<command> prepare` prints one JSON object on stdout:

  ```json
  { "host": "server.example.com", "user": "dev", "port": 22,
    "sshPath": "C:/Program Files/Git/usr/bin/ssh.exe",
    "identityAgent": "C:/Users/me/AppData/Local/Temp/my-agent/agent.sock",
    "expiresAt": "2026-10-07T03:00:00Z" }
  ```

  `<command> release` stops whatever `prepare` started. Exit code 0 means success; stderr is shown to
  the user. Teleporter calls `prepare` on the first SSH need and `release` on exit. `.sh` runs with
  Git Bash, `.ps1` with PowerShell, `.cmd` with cmd. Typical use: load a key from a vault into a
  memory-only agent.

### Add-ons

A folder in `Teleporter.data/addons/<name>/` with an `addon.json`:

```json
{ "name": "my-proxy", "description": "Route Antigravity through a proxy",
  "agentEnv": { "antigravity": { "HTTPS_PROXY": "http://127.0.0.1:3128" } },
  "status": "status.sh", "prepare": "prepare.sh" }
```

- `agentEnv.<kind>`: environment added to every command Teleporter runs for that agent kind
  (`claude`, `antigravity`) and to the Antigravity daemon (a systemd drop-in).
- `status`: script run on the server (bash, as the server account); lines `TELEPORTER:key=value` are
  shown in the Server tab.
- `prepare`: script run before an agent of a kind the add-on touches is installed or started; a
  non-zero exit stops the action and shows stderr.

Add-ons are never loaded into the app process.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`).

```bat
BUILD.bat          :: output: dist\Teleporter.exe (self-contained, single file)
dotnet test        :: unit tests
```

Design decisions and their reasons: [docs/design.md](docs/design.md).

## Contributing

Issues and pull requests are welcome. Two rules keep the project healthy:
- **Idle cost is a hard requirement**: with the window closed, CPU over 60 s must stay at 0 ms.
- **Nothing specific to one person's infrastructure** goes in this repo; that is what providers and
  add-ons are for.

If Teleporter saves you a session, a ⭐ helps others find it.

## License

[MIT](LICENSE)
