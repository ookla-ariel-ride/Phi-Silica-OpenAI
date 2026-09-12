# Grok Build CLI on Windows ARM64

How to get xAI's `grok` CLI working on a Snapdragon X laptop when the native ARM64 build crashes
with a stack overflow the moment it opens a TLS connection. The fix is to run the x64 build under
Windows' x64 emulation. This page has the fix first, then the evidence, so it can be re-applied
without re-deriving it.

Last reviewed: 2026-09-12, Grok Build CLI 1.0.30, Samsung Galaxy Book4 Edge (Snapdragon X Elite),
Windows 11 ARM64 build 29648, PowerShell 7.6.5.

## Symptom

Running `grok`, `grok login`, `grok login --device-code` or `grok update --check` in PowerShell
prints one line and exits:

```text
thread 'main' (34592) has overflowed its stack
```

`grok --version` and `grok doctor` work. The Windows Application log gets one Application Error
event per attempt: exception `0xC00000FD` (`STATUS_STACK_OVERFLOW`), faulting module `grok.exe`,
the same fault offset every time. No `~\.grok\auth.json` is ever written because no login completes.

## Cause

The ARM64 build recurses without bound inside the rustls TLS client handshake, right after the TCP
connection to any xAI host succeeds. Commands that do not open an HTTPS connection are unaffected.
The x64 build of the same version does not have the problem. The evidence is under "Diagnosis"
below.

## Fix

Download the x64 build of the installed version, validate it, keep the ARM64 originals in a backup
folder, and copy the x64 file over both `grok.exe` and `agent.exe`. The two installed files are
byte identical, so both have to be swapped or the agent subprocess still crashes.

The block stops at the first failure and never touches the install until the download has passed
three checks. `curl.exe` does not throw on failure in PowerShell, so its exit code is tested by
hand. Backups are copies, so the originals survive anything that goes wrong later in the block.

```powershell
$ErrorActionPreference = 'Stop'
$bin = "$env:USERPROFILE\.grok\bin"
$ver = (& "$bin\grok.exe" --version) -replace '^grok (\S+).*', '$1'      # 1.0.30 at time of writing
$x64 = "$env:TEMP\grok-$ver-windows-x86_64.exe"

function Get-PeMachine([string]$Path) {
    $fs = [IO.File]::OpenRead($Path); $br = New-Object IO.BinaryReader($fs)
    $fs.Seek(0x3C, 'Begin') | Out-Null; $pe = $br.ReadInt32()
    $fs.Seek($pe + 4, 'Begin') | Out-Null; $m = $br.ReadUInt16(); $br.Close()
    switch ($m) { 0x8664 { 'x64' } 0xAA64 { 'ARM64' } 0x14C { 'x86' } default { '0x{0:X}' -f $m } }
}

# 1. Download. curl.exe reports failure through its exit code only.
curl.exe -fsSL -o $x64 "https://x.ai/cli/grok-$ver-windows-x86_64.exe"
if ($LASTEXITCODE -ne 0) { throw "download failed, curl exit code $LASTEXITCODE" }
# mirror, if x.ai refuses:
# curl.exe -fsSL -o $x64 "https://storage.googleapis.com/grok-build-public-artifacts/cli/grok-$ver-windows-x86_64.exe"

# 2. Validate before touching the install: plausible size, X.AI signature, x64 image.
if ((Get-Item $x64).Length -lt 50MB) { throw "download is too small to be the CLI" }
$sig = Get-AuthenticodeSignature $x64
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'CN=X\.AI LLC') {
    throw "signature check failed: $($sig.Status) / $($sig.SignerCertificate.Subject)"
}
if ((Get-PeMachine $x64) -ne 'x64') { throw "downloaded file is not an x64 image" }

# 3. Back up by copying, so the originals are intact whatever happens next.
Get-Process grok, agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$bak = "$bin\arm64-$ver-backup"
New-Item -ItemType Directory -Force $bak | Out-Null
Copy-Item "$bin\grok.exe"  "$bak\grok.exe"  -Force
Copy-Item "$bin\agent.exe" "$bak\agent.exe" -Force

# 4. Replace both binaries and confirm each now matches the validated download.
Copy-Item $x64 "$bin\grok.exe"  -Force
Copy-Item $x64 "$bin\agent.exe" -Force
$want = (Get-FileHash $x64).Hash
foreach ($f in 'grok.exe', 'agent.exe') {
    if ((Get-FileHash "$bin\$f").Hash -ne $want) { throw "$f does not match the download; restore from $bak" }
}
Remove-Item $x64
```

If the block throws at step 1 or 2, the install is untouched. If it throws at step 3 or 4, the
backup folder holds the originals and the "Revert" section puts them back.

Nothing else changes: no PATH edit, no registry, no system files. `~\.grok\bin` was already on the
user PATH from xAI's installer.

## Verify

```powershell
grok --version               # grok 1.0.30 (04b7ffed98c6) [stable]
grok update --check --json   # returns JSON; this command crashed before the swap
grok login                   # browser flow, or: grok login --device-code
Test-Path "$env:USERPROFILE\.grok\auth.json"   # True after login
```

`grok update --check --json` is the right smoke test. It is the smallest command that performs a
full TLS handshake, and it needs no account.

Run the binary from `~\.grok\bin`. Launched from some other folder, the x64 build opened the
interactive TUI even for `--version`. That is how it detects an installed layout, and it goes away
once the file is in place.

## Gotchas

`grok update` re-downloads the `windows-aarch64` build and the crash comes back. After any update,
run `grok update --check --json` before anything else; if it overflows, repeat the fix.

The `model-gateway` Claude Code plugin reads `~\.grok\auth.json` for its Grok route. A silent revert
shows up there as "Grok CLI auth is missing. Run `grok` and log in again."

There is no newer build to move to. On the review date `https://x.ai/cli/stable` and
`https://x.ai/cli/alpha` both answered `1.0.30`. The upstream repository `xai-org/grok-build` has
issues disabled; the only report channel is `/feedback` inside the TUI, which is unreachable on a
build that cannot log in. Report it from the x64 build.

## Revert

Copies the originals back, confirms both landed, and only then removes the backup. `Move-Item`
errors are non-terminating by default, so a locked destination would otherwise let the block fall
through to the delete and lose the only copy.

```powershell
$ErrorActionPreference = 'Stop'
$bin = "$env:USERPROFILE\.grok\bin"
$bak = Get-ChildItem "$bin\arm64-*-backup" -Directory | Select-Object -First 1
if (-not $bak) { throw "no backup folder under $bin" }
Get-Process grok, agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

foreach ($f in 'grok.exe', 'agent.exe') {
    Copy-Item "$($bak.FullName)\$f" "$bin\$f" -Force
    if ((Get-FileHash "$bin\$f").Hash -ne (Get-FileHash "$($bak.FullName)\$f").Hash) {
        throw "$f was not restored; backup left in place"
    }
}
Remove-Item $bak.FullName -Recurse
```

## Diagnosis

This is how the cause was pinned down, in the order it happened, with the commands. Each step is
cheap and reusable for the next tool that behaves this way.

### Find the binary and check its architecture

`Get-Command grok` points at `~\.grok\bin\grok.exe`. Read the PE header's machine field rather
than trusting the file name; `0xAA64` is ARM64 and `0x8664` is x64. `Get-PeMachine` is the helper
defined in the fix block above.

```powershell
Get-PeMachine "$env:USERPROFILE\.grok\bin\grok.exe"                      # ARM64
[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture       # Arm64
```

The installed binary was native ARM64 and validly signed by X.AI LLC. Note that Git Bash and other
emulated shells report `AMD64` from `uname`; only the .NET call is trustworthy for the OS.

### Read the crash records

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error'; StartTime = (Get-Date).AddHours(-3) } |
    Where-Object { $_.Message -match 'grok\.exe' } |
    Select-Object TimeCreated, @{ n = 'Msg'; e = { ($_.Message -split "`n")[0..6] -join ' | ' } } | Format-List
Get-ChildItem "$env:LOCALAPPDATA\CrashDumps" | Where-Object Name -match grok
```

Seven events in a few minutes, all `0xC00000FD` in `grok.exe` at fault offset `0x60bcdc8`. The same
offset every time means the same code site every time, so this is deterministic and not a flake.
Windows Error Reporting had also written minidumps to `CrashDumps`, but no debugger was installed
to read them, and the trace log below made that unnecessary.

Do not confuse this with the `0xC0000409` fail-fasts from `WorkloadsSessionHost.exe` in the same
log, which `docs/DECISIONS.md` D94 describes. Those come from this bridge sending an oversized
system prompt to Phi Silica and have nothing to do with `grok`.

### Probe commands with a timeout

A TUI takes the console and a login blocks forever, so every probe ran through `Start-Process` with
redirected output and a bounded wait. A probe still running at the timeout counts as a pass for a
command that was going to wait on a person anyway.

```powershell
function Invoke-Probe([string]$Exe, [string[]]$Args, [hashtable]$Env = @{}, [int]$WaitMs = 30000) {
    $out = New-TemporaryFile; $err = New-TemporaryFile; $saved = @{}
    foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process'); [Environment]::SetEnvironmentVariable($k, $Env[$k], 'Process') }
    $p = Start-Process -FilePath $Exe -ArgumentList $Args -RedirectStandardOutput $out -RedirectStandardError $err -NoNewWindow -PassThru
    if (-not $p.WaitForExit($WaitMs)) { Stop-Process -Id $p.Id -Force; $code = 'TIMEOUT (still running)' } else { $code = '0x{0:X}' -f $p.ExitCode }
    foreach ($k in $Env.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
    "=== $($Args -join ' ') -> $code"; Get-Content $out | Select-Object -First 8; Get-Content $err | Select-Object -First 8
    Remove-Item $out, $err
}
$g = "$env:USERPROFILE\.grok\bin\grok.exe"
Invoke-Probe $g @('login', '--device-code')          # 0xC00000FD within a second
Invoke-Probe $g @('update', '--check', '--json')     # 0xC00000FD within a second
Invoke-Probe $g @('doctor')                          # exits 0
Invoke-Probe $g @('-p', '"reply pong"')              # exits 1, "Not signed in", no crash
```

Every command that opens an HTTPS connection died. Every command that does not was fine.

### Read the tool's own trace log

`grok` writes a log when `GROK_LOG_FILE` is set and honours `RUST_LOG`.

```powershell
Invoke-Probe $g @('update', '--check', '--json') @{ GROK_LOG_FILE = "$env:TEMP\grok-trace.log"; RUST_LOG = 'trace' }
Get-Content "$env:TEMP\grok-trace.log" -Tail 6
```

The last lines before the overflow were the same on every run:

```text
DEBUG reqwest::connect: starting new connection: https://x.ai/
DEBUG hyper_util::client::legacy::connect::http: connected to [2606:4700::6812:1350]:443
DEBUG rustls::client::hs: No cached session for DnsName("x.ai")
DEBUG rustls::client::hs: Not resuming any session
```

The TCP connection had succeeded and rustls was about to build the client hello. Nothing after that
line was ever logged. The overflowing thread was `main` when the blocking client ran the request and
`tokio-rt-worker` when the async client did, which rules out anything specific to one thread's
stack.

### Rule out the cheap explanations

An unreachable proxy stops the handshake from starting. With `HTTPS_PROXY=http://127.0.0.1:9` the
TUI came up and showed "error sending request" instead of crashing, so the crash needs a real
connection.

A bigger stack does not help. A copy of `grok.exe` with the PE optional header's
`SizeOfStackReserve` raised from 1 MB to 64 MB overflowed identically. That makes it unbounded
recursion, which no stack size fixes.

The machine's network settings were clean. `netsh winhttp show proxy` reported direct access, the
Internet Settings registry keys had no proxy or PAC, and no proxy variables were in the environment.

Whether a different TLS server also triggers it could not be isolated. The CLI opens its x.ai
connection first on every code path and dies there before any overridden host is reached.

### Confirm with the x64 build

The same version's x64 build, downloaded to a scratch folder and run under emulation, ran all three
crashing commands for the full wait without a fault. With the trace log on, it continued past the
point the ARM64 build died:

```text
DEBUG rustls::client::hs: Using ciphersuite TLS13_AES_256_GCM_SHA384
DEBUG rustls::client::tls13: TLS1.3 encrypted extensions: ServerExtensions { ... }
DEBUG rustls::client::hs: ALPN protocol is Some(b"h2")
WARN  ... Failed to fetch models error=RequestFailed { status: 400, ... "Incorrect API key provided" ...
```

Real TLS 1.3 sessions with `api.x.ai`, `auth.x.ai` and `cli-chat-proxy.grok.com`, and real HTTP
responses. That was enough to apply the swap.

## Related

- `docs/OPENCODE-WINDOWS-ARM64.md`: the same shape of problem in OpenCode, fixed the same way.
- `docs/CLIENTS.md`: how the agent clients are wired to this bridge. Grok Build is not one of them;
  it matters here only through the `model-gateway` plugin's Grok route.
- Installer reference: `https://x.ai/cli/install.ps1` reads the version from
  `https://x.ai/cli/<channel>` and downloads `grok-<version>-windows-<x86_64|aarch64>.exe`.
