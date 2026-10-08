# Teleporter: notes for coding agents

- Read [docs/design.md](docs/design.md) first: requirements and decisions with their reasons. Keep it
  current when a decision changes. Protocols for providers and add-ons are in the README.
- Build: `BUILD.bat` (publish) or `dotnet build src/Teleporter`. A running copy locks `dist\Teleporter.exe`;
  BUILD.bat kills it, and Dropbox can briefly lock the file too (retry).
- **Idle cost is a hard requirement.** Measure after any change that could add a timer, thread or
  long-lived object: close the window, wait 30 s, then CPU over 60 s must stay at 0 ms. Known traps,
  each measured: DirectComposition's vblank thread, the GPU driver's threads after a window opened,
  and a long-lived HttpClient's pool timer (see Program.cs and ServerService.cs).
- The window is destroyed on close, never hidden. Anything that runs periodically belongs to the
  window's lifetime.
- No secrets in logs, command lines or settings. Credentials come from a connection provider; sensitive
  data goes over stdin.
- Nothing specific to one person's infrastructure goes in this repo: that is what providers and add-ons
  are for.
- No em-dashes in anything committed (docs, comments, commit messages).
- `src/Teleporter/Transfer/helper.py` is embedded and uploaded to the server on first use. When you
  change it, bump its `VERSION` and `HelperVersion` in TransferService.cs together, or servers keep
  the old copy. Its path logic must match `Core/PathMap.cs` (the parity test runs both).
- Windows' `tar.exe` (bsdtar) reads `-T` file lists in the ANSI code page: never feed it a list of
  names; walk the folder with `--exclude` instead (see `LocalTarArgs`).
- Tests: `dotnet test`. End-to-end changes to transfers must be re-verified against a real server
  (send + bring back of a repo with an exec bit, CRLF files, a Unicode name and an empty folder).
