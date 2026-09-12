---
description: PowerShell script traps on the Copilot+ PC
globs: ["scripts/**/*.ps1"]
priority: 60
---
- These scripts run on the target machine itself (Snapdragon X Elite, Windows 11 ARM64). Trust
  `RuntimeInformation.OSArchitecture`, not `uname`, which reports AMD64 under emulation.
- `smoke.ps1` reports with `Write-Host`; a transcript needs `6>&1`. Plain `>` captures nothing.
- Never `dotnet build` or `dotnet test` while a bridge server is up; the exe is locked and the copy
  fails. This holds across subagents too.
- Package identity is registered against the build output path. After the output folder or
  `packaging/AppxManifest.xml` changes, run `identity.ps1 -Install`, or the Phi Silica relaunch fails
  with "registered for <other folder>". `-Status` does not reveal this.
- A first-generation "the remote procedure call failed" is the runtime's known flake; re-run once. The
  same fault on a later generation is real, and "RPC server is unavailable" persisting past one retry
  means the model host crashed (D94): wait it out, nothing else clears it.
- A probe never sends more than 32,000 characters of system text, sets `temperature: 0`, honours
  `-Runs`, and controls the target tool's catalog position before anyone reads a number off it (D96).
  Cap any loop that grows a request; a `Max(1, $null)` once built a 9.5-million-character prompt.
