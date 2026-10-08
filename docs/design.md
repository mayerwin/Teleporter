# Design decisions

Why Teleporter is built the way it is, so these choices are not re-argued. Update this file when a
decision changes. What the app does is in the [README](../README.md).

## Requirements

- **Zero idle cost.** No CPU when the window is closed: no polling, no hidden rendering.
- **Portable.** One `Teleporter.exe` plus a `Teleporter.data` folder beside it. No installer.
  Optional "Start with Windows" (per-user `Run` key, respects Task Manager's disable).
- **Generic.** Anything specific to one person's infrastructure (how SSH keys are obtained, proxies)
  is a connection provider or an add-on that lives outside this repo.
- **Reliable transfers.** Every file verified by hash before anything is set aside. Everything is
  copied, including files git ignores, unless the user opts out for a listed folder.
- **Nothing deleted silently.** Set-aside copies are deleted only after an explicit confirmation.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| UI stack | .NET 10 + Avalonia (Fluent), self-contained single file, compressed, partial trim | One portable exe with no runtime to install. Native DLLs unpack once to `%TEMP%\.net` (accepted). |
| Idle | The window is destroyed on close, not hidden. The status refresh runs only while it is open. One daily timer for set-aside expiry. | Hidden Avalonia windows can keep rendering. |
| SSH client | Git for Windows' `ssh.exe`, optionally with a private, memory-only `ssh-agent` from a provider | Windows' built-in agent persists keys to disk (DPAPI) and ignores `-t`. |
| Credentials | Pluggable connection provider. Built-in: plain OpenSSH (the user's agent and config). External: any command speaking a small JSON protocol. | Keeps private key handling out of the app. |
| Add-ons | Folders in `Teleporter.data/addons/<name>/` with `addon.json` and scripts run on the server | Never loaded into the app process, so they cannot crash it or break on update. |
| Server account | A dedicated account that owns all dev and agent processes | "Stop everything" ends every process of that account; nothing else on the server is touched. |
| Packages for agents | Optional rootless Toolbx container in that account, passwordless sudo inside it | Agents can install anything without admin rights over the server. |
| Agents | Claude Code `remote-control` per project as a systemd user service; Antigravity's `agy remote-control` daemon | Steerable from Claude Desktop, claude.ai, the phone and the Antigravity dashboard; independent of any editor. |
| Editors | `code --folder-uri vscode-remote://ssh-remote+<alias>/<path>` with Teleporter's own ssh config; the Antigravity IDE through its own Remote-SSH extension | Quick edits and short sessions; nothing long-running depends on an editor. |
| Line endings | Bytes are never converted. A git repo with `core.autocrlf=true` gets `core.autocrlf input` on the server, restored on the way back. | `git status` stays clean on both sides without touching file contents. |
| Exec bits | Recorded on the server and restored on the next trip; for git repos also restored from the index. | Windows file systems cannot hold them. |

## Claude Code sessions

`~/.claude/projects/<encoded-path>/` holds `<session>.jsonl`, a `<session>/` folder per session (tool
results, subagents) and `memory/`; `~/.claude/file-history/<session>/` holds edit checkpoints.

Moving them: stage copies with each record's top-level `cwd` rewritten to the new path (sub-folders
kept), transfer and verify them like project files, merge into the destination `~/.claude` without
overwriting (a clash is kept as `name.teleporter-<time>`), then set the originals aside.

The encoded folder name is the path with every non-alphanumeric character replaced by `-`. Windows
uses both `C--...` (CLI) and `c--...` (VS Code) for the same folder, so both are collected.
Antigravity history is not moved (its format is undocumented).

## Set-aside copies

- Local PC: moved to a parking folder chosen in settings (default outside Dropbox and OneDrive). A
  stub folder stays in the original place with an "Open on server" shortcut and a README.
- Server: moved to `~/.teleporter/parked/`.
- After 30 days (configurable) they are listed as "ready to delete"; deletion needs a click.
