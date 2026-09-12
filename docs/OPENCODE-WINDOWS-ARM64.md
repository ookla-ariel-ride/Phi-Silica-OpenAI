# OpenCode on Windows ARM64

How to get OpenCode working on a Snapdragon X laptop when the native ARM64 build refuses every
command with a `bun:ffi` error. The fix is to run the x64 build under Windows' x64 emulation, or to
pin the last ARM64 release that works. This page has the fix first, then the evidence, so it can be
re-applied without re-deriving it.

Last reviewed: 2026-09-12, OpenCode 1.18.30 installed with `npm i -g opencode-ai`, Samsung Galaxy
Book4 Edge (Snapdragon X Elite), Windows 11 ARM64 build 29648, PowerShell 7.6.5, Node 26.7.0.

## Symptom

Every `opencode` command exits 1 with the same message, including commands that never draw a
screen:

```text
Error: Unexpected error

Failed to initialize OpenTUI render library: bun:ffi dlopen() is not available in this build (TinyCC is disabled)
```

`opencode --version` alone works. There are no crash events in the Windows Application log, because
this is a clean error exit rather than a native fault. OpenCode's own log under
`~\.local\share\opencode\log` gets nothing, since the failure happens before logging starts.

## Cause

OpenCode is compiled with Bun. Its terminal renderer, OpenTUI, loads a native DLL through
`bun:ffi`. Stable Bun compiles `bun:ffi` out on `windows-aarch64` because TinyCC, which it used for
FFI, has no ARM64 backend. An OpenCode ARM64 binary built with such a Bun cannot start its
renderer, and the CLI initialises the renderer even for `auth list`.

On this machine only the 1.18.30 ARM64 artifact, published 2026-09-09, has the problem. The ARM64
builds of 1.18.26 through 1.18.29 run correctly. Upstream has known since March; the details are
under "Diagnosis".

## Fix

Two options. The first keeps the current version; the second stays native.

### Option A: run the x64 build under emulation

Install the x64 platform package into a scratch prefix, validate it, and copy its binary over the
one the npm shim runs. npm refuses to install a package whose `cpu` field says `x64` on an ARM64
host, and does so silently under `--silent`, so the platform override flags are required.

The block stops at the first failure and never touches the install until the package has been
checked. `npm` does not throw on failure in PowerShell, so its exit code is tested by hand. The
backup is a copy, so the original survives anything that goes wrong later. npm packages carry no
Authenticode signature, so the checks here are existence, architecture and a version round trip.

```powershell
$ErrorActionPreference = 'Stop'
$pkg = "$env:APPDATA\npm\node_modules\opencode-ai"
$ver = & "$env:APPDATA\npm\opencode.cmd" --version       # 1.18.30 at time of writing
$t   = "$env:TEMP\opencode-x64"

function Get-PeMachine([string]$Path) {
    $fs = [IO.File]::OpenRead($Path); $br = New-Object IO.BinaryReader($fs)
    $fs.Seek(0x3C, 'Begin') | Out-Null; $pe = $br.ReadInt32()
    $fs.Seek($pe + 4, 'Begin') | Out-Null; $m = $br.ReadUInt16(); $br.Close()
    switch ($m) { 0x8664 { 'x64' } 0xAA64 { 'ARM64' } 0x14C { 'x86' } default { '0x{0:X}' -f $m } }
}

# 1. Install the x64 platform package. npm reports failure through its exit code only.
New-Item -ItemType Directory -Force $t | Out-Null
npm install --prefix $t --no-audit --no-fund --cpu x64 --os win32 --force "opencode-windows-x64@$ver"
if ($LASTEXITCODE -ne 0) { throw "npm install failed, exit code $LASTEXITCODE" }

# 2. Validate before touching the install: the file exists and is an x64 image.
$x64 = "$t\node_modules\opencode-windows-x64\bin\opencode.exe"
if (-not (Test-Path $x64)) { throw "npm produced no binary at $x64; the --cpu and --os overrides are required on ARM64" }
if ((Get-PeMachine $x64) -ne 'x64') { throw "installed file is not an x64 image" }

# 3. Back up by copying, so the original is intact whatever happens next.
Get-Process opencode -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$bak = "$pkg\bin\arm64-$ver-backup"
New-Item -ItemType Directory -Force $bak | Out-Null
Copy-Item "$pkg\bin\opencode.exe" "$bak\opencode.exe" -Force

# 4. Replace the binary and confirm it matches the package and answers with the same version.
Copy-Item $x64 "$pkg\bin\opencode.exe" -Force
if ((Get-FileHash "$pkg\bin\opencode.exe").Hash -ne (Get-FileHash $x64).Hash) {
    throw "opencode.exe does not match the x64 package; restore from $bak"
}
$got = & "$env:APPDATA\npm\opencode.cmd" --version
if ($got -ne $ver) { throw "opencode --version answered '$got', expected '$ver'; restore from $bak" }
Remove-Item $t -Recurse -Force
```

If the block throws at step 1 or 2, the install is untouched. If it throws at step 3 or 4, the
backup folder holds the original and "Revert option A" puts it back.

`opencode-windows-x64-baseline` is the build for CPUs without AVX2. Windows 11's emulator provides
AVX2 on this build, and the regular x64 package ran without complaint, so the baseline package was
not needed.

### Option B: pin the last working ARM64 release

```powershell
npm i -g opencode-ai@1.18.29
```

This stays native and needs no file swap, at the cost of whatever 1.18.30 changed. It also keeps
the second gap upstream describes: the `bun-pty` shell library ships an x64-only DLL, so shell
sessions inside the TUI may still fail on native ARM64 even though the renderer starts.

## Verify

```powershell
opencode --version    # 1.18.30 (option A) or 1.18.29 (option B)
opencode auth list    # lists ~\.local\share\opencode\auth.json without the FFI error
opencode models       # fetches the catalogue over HTTPS and prints model ids
```

`auth list` is the right smoke test. It is the smallest command that initialises the renderer, and
it needs no credentials.

## Gotchas

`npm update -g`, `npm i -g opencode-ai`, or anything that re-runs the package's `postinstall.mjs`
undoes option A. That script picks the platform package from `os.arch()` and copies its binary back
into `bin\`, so the ARM64 build and the error return. After any update, run `opencode auth list`
before trusting it.

Once upstream ships an ARM64 build compiled with Bun 1.4 or later, the ARM64 package should work
again and option A can be retired. Check a new release with `opencode auth list` before adopting
it.

The global install lives under `%APPDATA%\npm`. `Get-Command opencode` shows the shims there
(`opencode`, `opencode.cmd`, `opencode.ps1`); the real binary is
`%APPDATA%\npm\node_modules\opencode-ai\bin\opencode.exe`, which is what `opencode.cmd` runs.

## Revert option A

Copies the original back, confirms it landed, and only then removes the backup. `Move-Item` errors
are non-terminating by default, so a locked destination would otherwise let the block fall through
to the delete and lose the only copy.

```powershell
$ErrorActionPreference = 'Stop'
$pkg = "$env:APPDATA\npm\node_modules\opencode-ai"
$bak = Get-ChildItem "$pkg\bin\arm64-*-backup" -Directory | Select-Object -First 1
if (-not $bak) { throw "no backup folder under $pkg\bin" }
Get-Process opencode -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Copy-Item "$($bak.FullName)\opencode.exe" "$pkg\bin\opencode.exe" -Force
if ((Get-FileHash "$pkg\bin\opencode.exe").Hash -ne (Get-FileHash "$($bak.FullName)\opencode.exe").Hash) {
    throw "opencode.exe was not restored; backup left in place"
}
Remove-Item $bak.FullName -Recurse
```

## Diagnosis

How the cause was pinned down, with the commands, so the same steps work for the next release.

### Find the binary and check its architecture

```powershell
Get-Command opencode -All | Select-Object Name, CommandType, Source
Get-Content "$env:APPDATA\npm\opencode.cmd"      # names node_modules\opencode-ai\bin\opencode.exe
```

The `opencode-ai` package lists one optional dependency per platform. On this machine npm resolved
`opencode-windows-arm64`, and the package's `postinstall.mjs` copied its binary into `bin\`. Reading
the PE header with the `Get-PeMachine` helper from the fix block confirmed a native ARM64 file:

```powershell
Get-PeMachine "$env:APPDATA\npm\node_modules\opencode-ai\bin\opencode.exe"   # ARM64
```

### Reproduce with captured output

```powershell
$oc = "$env:APPDATA\npm\node_modules\opencode-ai\bin\opencode.exe"
& $oc auth list
& $oc models
& $oc --log-level DEBUG --print-logs run "reply with the word pong"
```

All three printed the OpenTUI error and exited 1. `--print-logs` produced nothing, which placed the
failure before OpenCode's logging starts. No new file appeared under `~\.local\share\opencode\log`;
the only log there was from 2026-09-01, and it showed a normal boot. So OpenCode had worked on this
machine eleven days earlier.

### Check the event log

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-7) } |
    Where-Object { $_.Message -match 'opencode|bun\.exe' } | Select-Object TimeCreated, ProviderName
```

Nothing. The error is a controlled exit, and the message itself names the cause.

### Find the upstream issue

The error text is distinctive enough to search the tracker directly:

```powershell
gh issue list -R anomalyco/opencode --state all --search "TinyCC" --limit 10
```

Open issues, all describing this error on Windows ARM64: #19130 (March 2026), #20767 (April),
#38520 (July) and #45875 (28 August 2026). The last one is the clearest write-up. It names two gaps:

- Stable Bun ships `bun:ffi` compiled out on `windows-aarch64`, so OpenTUI cannot load its DLL.
  Fixed by building with Bun 1.4.0 or later, which has an engine-native FFI. PR #44946 pins it.
- `bun-pty` 0.4.8 ships only an x64 `rust_pty.dll`, which an ARM64 process cannot load, so shell
  sessions through `#pty` fail. A Windows ARM64 build is proposed in `sursaone/bun-pty#46`.

### Bisect the ARM64 releases

Since the September 1 log showed a working boot, the question was which release broke. Each ARM64
platform package was installed to a scratch prefix and its binary run directly:

```powershell
foreach ($v in '1.18.26', '1.18.27', '1.18.28', '1.18.29') {
    $t = "$env:TEMP\oc-$v"; New-Item -ItemType Directory -Force $t | Out-Null
    npm install --prefix $t --no-audit --no-fund "opencode-windows-arm64@$v"
    & "$t\node_modules\opencode-windows-arm64\bin\opencode.exe" auth list
}
```

| `opencode-windows-arm64` | Published | `auth list` |
|---|---|---|
| 1.18.26 | on or before 2026-09-01 | works |
| 1.18.27 | 2026-09-02 | works |
| 1.18.28 | 2026-09-04 | works |
| 1.18.29 | 2026-09-04 | works |
| 1.18.30 | 2026-09-09 | fails with the FFI error |

Only the newest ARM64 artifact is broken. The release notes for the four builds did not mention
Bun, Windows or ARM, so the toolchain change was not announced.

### Confirm with the x64 build

The first attempt to install `opencode-windows-x64` produced no binary and no error, because npm
skips a package whose declared `cpu` does not match the host and `--silent` hid the warning. With
`--cpu x64 --os win32 --force` it installed, and the binary passed `--version`, `auth list` and
`models` under emulation. That was enough to apply option A.

## Related

- `docs/GROK-CLI-WINDOWS-ARM64.md`: the same shape of problem in xAI's Grok CLI, fixed the same way.
- `docs/CLIENTS.md`: OpenCode is one of the two agent clients wired to this bridge, so an OpenCode
  that cannot start blocks that test path.
- Upstream: `https://github.com/anomalyco/opencode/issues/45875`.
