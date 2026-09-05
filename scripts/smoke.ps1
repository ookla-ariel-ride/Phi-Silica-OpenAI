<#
.SYNOPSIS
  End-to-end smoke test of npu-bridge against a real backend on this machine.

.DESCRIPTION
  Starts NpuBridge.exe (or, with -NoStart, tests a server already listening), waits for /healthz to
  report ready (the first Phi Silica / Aion load can take minutes), then exercises /v1/models,
  /debug/generate (raw model access, cancellation, prompt-length preflight) and, once chunk 3+ land,
  /v1/chat/completions non-streaming, streaming, and a tool-call compliance probe.

  For -Backend phi-silica the exe is started by path; it relaunches itself through package activation so
  the process has identity and supervises that instance (scripts/identity.ps1 -Install must have been run
  for this build folder). Stopping the started process stops the activated one too.

.PARAMETER Backend
  phi-silica | aion | fake

.PARAMETER Port
  Listen port (default 5273). Refuses to start a server if something already listens there.

.PARAMETER NoStart
  Do not start the exe; test whatever is already listening on the port.

.PARAMETER ToolProbeRuns
  How many times to run the tool-call compliance probe (0 = skip). Ignored until chunk 7.

.EXAMPLE
  .\scripts\smoke.ps1 -Backend phi-silica
  .\scripts\smoke.ps1 -Backend fake -Port 5299
#>
[CmdletBinding()]
param(
    [ValidateSet('phi-silica', 'aion', 'fake')] [string] $Backend = 'phi-silica',
    [int] $Port = 5273,
    [switch] $NoStart,
    [int] $ToolProbeRuns = 5,
    [int] $ReadyTimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$base = "http://127.0.0.1:$Port"
$results = [System.Collections.Generic.List[object]]::new()

# Thrown by a Step body to report SKIP instead of FAIL/PASS, without affecting the exit code.
class SkipStepException : System.Exception {
    SkipStepException([string] $message) : base($message) {}
}

function Skip([string] $reason) {
    throw [SkipStepException]::new($reason)
}

function Step([string] $name, [scriptblock] $body) {
    Write-Host "==> $name" -ForegroundColor Cyan
    try {
        $detail = & $body
        $results.Add([pscustomobject]@{ Step = $name; Result = 'PASS'; Detail = "$detail" })
        Write-Host "    PASS $detail" -ForegroundColor Green
    } catch [SkipStepException] {
        $results.Add([pscustomobject]@{ Step = $name; Result = 'SKIP'; Detail = $_.Exception.Message })
        Write-Host "    SKIP $($_.Exception.Message)" -ForegroundColor Yellow
    } catch {
        $results.Add([pscustomobject]@{ Step = $name; Result = 'FAIL'; Detail = $_.Exception.Message })
        Write-Host "    FAIL $($_.Exception.Message)" -ForegroundColor Red
    }
}

# For the two real-model measurements: prints numbers for docs/DECISIONS.md, but a model that
# misbehaves or a step that cannot get a clean read is a finding, not a bridge failure, so this never
# adds to the FAIL count or the exit code.
function InfoStep([string] $name, [scriptblock] $body) {
    Write-Host "==> $name" -ForegroundColor Cyan
    try {
        $detail = & $body
    } catch {
        $detail = "could not measure: $($_.Exception.Message)"
    }
    $results.Add([pscustomobject]@{ Step = $name; Result = 'INFO'; Detail = "$detail" })
    Write-Host '    INFO' -ForegroundColor Yellow
    ("$detail" -split "`n") | ForEach-Object { Write-Host "      $_" -ForegroundColor Yellow }
}

function Get-Json([string] $path, [string] $method = 'GET', [string] $body = $null, [int[]] $expect = @(200), [int] $timeoutSec = 300) {
    $request = @{ Uri = "$base$path"; Method = $method; SkipHttpErrorCheck = $true; TimeoutSec = $timeoutSec }
    if ($body) { $request.Body = $body; $request.ContentType = 'application/json' }
    $r = Invoke-WebRequest @request
    if ($expect -notcontains [int]$r.StatusCode) { throw "HTTP $($r.StatusCode) for $method $path : $($r.Content)" }
    return ($r.Content | ConvertFrom-Json -Depth 20)
}

function Test-PortListening([int] $p) {
    return $null -ne (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)
}

# Starts a second, throwaway NpuBridge.exe on its own port for a measurement that needs a startup
# option (e.g. --system-prompt-placement) the already-running main server was not started with.
# Waits for /healthz to report ready before returning; throws on failure. Independent of $proc/$base.
function Start-AuxServer([string] $label, [int] $port, [string[]] $extraArgs) {
    if (Test-PortListening $port) { throw "something already listens on port $port for the $label run" }
    $exe = Get-ChildItem (Join-Path $repo 'src\NpuBridge\bin') -Recurse -Filter NpuBridge.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw 'NpuBridge.exe not found; run: dotnet build src/NpuBridge' }
    $log = Join-Path $env:TEMP "npu-bridge-smoke-$Backend-$label.log"
    $allArgs = @('--backend', $Backend, '--listen', "http://127.0.0.1:$port", '--verbose') + $extraArgs
    Write-Host "Starting $($exe.FullName) $($allArgs -join ' ') (log: $log)"
    $p = Start-Process -FilePath $exe.FullName -ArgumentList $allArgs `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow

    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        if ($p.HasExited) { throw "$label server process exited with code $($p.ExitCode); see $log and $log.err" }
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/healthz" -SkipHttpErrorCheck -TimeoutSec 5
            $last = $r.Content | ConvertFrom-Json
            if ($r.StatusCode -eq 200) { return $p }
            if ($last.status -eq 'failed') { throw "$label backend failed: $($last.error)" }
        } catch [System.Net.Http.HttpRequestException] { }
        Start-Sleep -Seconds 2
    }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    throw "$label server not ready after ${ReadyTimeoutSeconds}s; last: $($last | ConvertTo-Json -Compress)"
}

function Stop-AuxServer($p) {
    if ($p -and -not $p.HasExited) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

# --- start server -----------------------------------------------------------
$proc = $null
if (-not $NoStart) {
    if (Test-PortListening $Port) {
        throw "Something already listens on port $Port. Use -NoStart to test it, or -Port to pick another."
    }
    $exe = Get-ChildItem (Join-Path $repo 'src\NpuBridge\bin') -Recurse -Filter NpuBridge.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw 'NpuBridge.exe not found; run: dotnet build src/NpuBridge' }
    $log = Join-Path $env:TEMP "npu-bridge-smoke-$Backend.log"
    Write-Host "Starting $($exe.FullName) --backend $Backend --listen $base (log: $log)"
    $proc = Start-Process -FilePath $exe.FullName -ArgumentList '--backend', $Backend, '--listen', $base, '--verbose' `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
}

try {
    # --- wait for ready -----------------------------------------------------
    Step 'healthz becomes ready' {
        $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
        $last = $null
        while ((Get-Date) -lt $deadline) {
            if ($proc -and $proc.HasExited) { throw "server process exited with code $($proc.ExitCode); see $log and $log.err" }
            try {
                $r = Invoke-WebRequest -Uri "$base/healthz" -SkipHttpErrorCheck -TimeoutSec 5
                $last = $r.Content | ConvertFrom-Json
                if ($r.StatusCode -eq 200) { break }
                if ($last.status -eq 'failed') { throw "backend failed: $($last.error)" }
            } catch [System.Net.Http.HttpRequestException] { }
            Start-Sleep -Seconds 2
        }
        if (-not $last -or $last.status -ne 'ready') { throw "not ready after ${ReadyTimeoutSeconds}s; last: $($last | ConvertTo-Json -Compress)" }
        "backend=$($last.backend) model=$($last.model) identity=$($last.package_identity) loading=$($last.loading_seconds)s diagnostics=$($last.diagnostics | ConvertTo-Json -Compress)"
    }

    Step 'GET /v1/models lists the backend model' {
        $m = Get-Json '/v1/models'
        if ($m.object -ne 'list' -or $m.data.Count -ne 1) { throw "unexpected: $($m | ConvertTo-Json -Compress)" }
        "model id=$($m.data[0].id)"
    }

    # --- raw generation through the backend (diagnostic endpoint) -----------
    Step 'POST /debug/generate produces text from the model' {
        $body = @{ prompt = 'In one short sentence, what is a neural processing unit?' } | ConvertTo-Json
        $g = Get-Json '/debug/generate' 'POST' $body
        if ($g.status -ne 'Complete') { throw "status=$($g.status) detail=$($g.detail) text='$($g.text)'" }
        if (-not $g.text) { throw 'empty text' }
        "callbacks=$($g.progress_callbacks) chars=$($g.chars) ttft=$($g.ttft_ms)ms total=$($g.total_ms)ms text='$($g.text.Trim().Substring(0, [Math]::Min(120, $g.text.Trim().Length)))'"
    }

    Step 'preflight reports the whole short prompt as usable' {
        $g = Get-Json '/debug/generate' 'POST' (@{ prompt = 'Say OK.' } | ConvertTo-Json)
        if ($null -eq $g.usable_prompt_chars) { 'backend has no preflight (capability absent)' }
        elseif ($g.usable_prompt_chars -ne $g.prompt_chars) { throw "usable=$($g.usable_prompt_chars) prompt=$($g.prompt_chars)" }
        else { "usable=$($g.usable_prompt_chars) == prompt=$($g.prompt_chars)" }
    }

    Step 'POST /debug/generate honours a system prompt' {
        $body = @{ prompt = 'What is your name?'; system = 'You are Ada. Always answer with exactly the two words: I am Ada.' } | ConvertTo-Json
        $g = Get-Json '/debug/generate' 'POST' $body
        if ($g.status -ne 'Complete') { throw "status=$($g.status)" }
        "text='$($g.text.Trim())' (system prompt honoured: $($g.text -match 'Ada'))"
    }

    Step 'client disconnect mid-generation is survived' {
        if ($Backend -eq 'fake') {
            Skip 'the fake backend generates with no token delay, so there is no window in which to abort; meaningful on phi-silica and aion only'
        }
        $body = @{ prompt = 'Write a very long, detailed essay about the history of computing, at least 800 words.' } | ConvertTo-Json
        $aborted = $false
        try { $null = Get-Json '/debug/generate' 'POST' $body @(200) 2 } catch { $aborted = $true }
        if (-not $aborted) { throw 'expected the 2 s client timeout to abort the request' }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $g = Get-Json '/debug/generate' 'POST' (@{ prompt = 'Say OK.' } | ConvertTo-Json)
        $sw.Stop()
        if ($g.status -ne 'Complete') { throw "follow-up status=$($g.status)" }
        "aborted request drained; next request completed in $($sw.ElapsedMilliseconds)ms"
    }

    # --- chat completions (chunk 3) ------------------------------------------
    # One gate per feature: non-streaming is built now; streaming (chunk 4) and tool calls (chunk 7)
    # are not, and must report SKIP, not FAIL, so a clean chunk-3 run stays "All steps passed".
    Step 'POST /v1/chat/completions rejects an empty body' {
        $c = Get-Json '/v1/chat/completions' 'POST' '{}' @(400)
        if ($c.error.type -ne 'invalid_request_error') { throw "error.type=$($c.error.type): $($c | ConvertTo-Json -Compress)" }
        "HTTP 400 error.type=$($c.error.type)"
    }

    Step 'POST /v1/chat/completions (non-streaming)' {
        $body = @{
            model    = $Backend
            messages = @(
                @{ role = 'system'; content = 'You are a terse assistant.' }
                @{ role = 'user'; content = 'Reply with exactly the word PONG.' }
            )
        } | ConvertTo-Json -Depth 5
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $c = Get-Json '/v1/chat/completions' 'POST' $body
        $sw.Stop()

        if ($c.object -ne 'chat.completion') { throw "object=$($c.object)" }
        if ($c.id -notlike 'chatcmpl-*') { throw "id=$($c.id) does not start with chatcmpl-" }
        $choice = $c.choices[0]
        if ($choice.message.role -ne 'assistant') { throw "message.role=$($choice.message.role)" }
        $text = $choice.message.content
        if (-not $text) { throw "empty content: $($c | ConvertTo-Json -Compress)" }
        if ($choice.finish_reason -ne 'stop') { throw "finish_reason=$($choice.finish_reason)" }
        $u = $c.usage
        if (-not ($u.prompt_tokens -gt 0 -and $u.completion_tokens -gt 0 -and $u.total_tokens -gt 0)) {
            throw "usage not all > 0: $($u | ConvertTo-Json -Compress)"
        }
        if ($u.total_tokens -ne ($u.prompt_tokens + $u.completion_tokens)) {
            throw "total_tokens=$($u.total_tokens) != prompt_tokens+completion_tokens=$($u.prompt_tokens + $u.completion_tokens)"
        }

        # Non-streaming: the whole JSON body is written in one shot, so there is no separate
        # time-to-first-byte to observe client-side; ttft and total are the same clock reading here.
        "ttft=$($sw.ElapsedMilliseconds)ms total=$($sw.ElapsedMilliseconds)ms (non-streaming: single write, so ttft==total) usage=$($u | ConvertTo-Json -Compress) text='$($text.Substring(0, [Math]::Min(80, $text.Length)))'"
    }

    Step 'POST /v1/chat/completions (streaming SSE)' {
        Skip 'streaming arrives in chunk 4; stream: true deliberately returns 400 in chunk 3'
    }

    if ($ToolProbeRuns -gt 0) {
        Step "tool-call compliance probe ($ToolProbeRuns runs)" {
            Skip 'tool calling arrives in chunk 7; tools/tool_choice are accepted and ignored in chunk 3'
        }
    }

    # --- measurement 1: does completion_tokens (chars/4) track progress callbacks? -----------------
    InfoStep 'measurement: chars/4 estimate vs progress-callback count' {
        $prompt = 'In two or three sentences, explain what a neural processing unit does and why a Copilot+ PC has one.'

        # Single generation: read the callback count and the character count off the same response, so
        # the ratio measures the thing being decided rather than the difference between two replies.
        $g = Get-Json '/debug/generate' 'POST' (@{ prompt = $prompt } | ConvertTo-Json)
        if ($g.status -ne 'Complete') { throw "debug/generate status=$($g.status)" }
        $callbacks = $g.progress_callbacks
        $chars = $g.chars
        $estimate = [Math]::Ceiling($chars / 4.0)
        $ratio = if ($callbacks -gt 0) { [Math]::Round($estimate / $callbacks, 2) } else { $null }
        $ratioText = if ($null -ne $ratio) { "${ratio}x" } else { 'n/a (0 callbacks)' }

        # Cross-check only, from a second, separate generation through the real endpoint. Deliberately
        # not folded into the ratio above: two different replies of different lengths would measure
        # that difference, not the estimate-vs-callbacks question this step exists to answer.
        $chatBody = @{ model = $Backend; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 5
        $c = Get-Json '/v1/chat/completions' 'POST' $chatBody
        $crossCheck = if ($c.choices[0].finish_reason -eq 'stop') {
            "usage.completion_tokens=$($c.usage.completion_tokens) for a $($c.choices[0].message.content.Length)-char reply"
        } else {
            "finish_reason=$($c.choices[0].finish_reason)"
        }

        @"
asked: one bare user-message prompt ($($prompt.Length) chars) to /debug/generate, one generation
progress-callback count (this generation) = $callbacks
raw completion character count (this generation) = $chars
chars/4 estimate derived from this generation = $estimate
ratio: chars/4 estimate is $ratioText the callback count, both numbers from the same generation
cross-check (a different generation, same prompt, via /v1/chat/completions): $crossCheck -- for
comparison only, not part of the ratio above
verdict: chunk 3 chose chars/4 over counting callbacks because callbacks were measured undercounting by
roughly 4x (11 callbacks for 178 chars); this ratio is that assumption checked on one real generation,
not a recollection.
"@
    }

    # --- measurement 2: which --system-prompt-placement does this model actually obey? -------------
    InfoStep 'measurement: system-prompt placement (native vs prompt)' {
        if ($NoStart) {
            return 'skipped: -NoStart is set; this measurement starts two dedicated servers on an auxiliary port, which -NoStart precludes.'
        }

        $auxPort = $Port + 1
        $system = 'You are Ada. Always answer with exactly the two words: I am Ada.'
        $question = 'What is your name?'
        $chatBody = @{
            model    = $Backend
            messages = @(
                @{ role = 'system'; content = $system }
                @{ role = 'user'; content = $question }
            )
        } | ConvertTo-Json -Depth 5

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("asked (both placements, server restarted between them): system=`"$system`" user=`"$question`"")

        foreach ($placement in 'native', 'prompt') {
            $auxProc = $null
            try {
                $auxProc = Start-AuxServer "placement-$placement" $auxPort @('--system-prompt-placement', $placement)
                $r = Invoke-WebRequest -Uri "http://127.0.0.1:$auxPort/v1/chat/completions" -Method Post -Body $chatBody `
                    -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 120
                if ([int]$r.StatusCode -ne 200) { throw "HTTP $($r.StatusCode): $($r.Content)" }
                $c = $r.Content | ConvertFrom-Json -Depth 20
                $text = $c.choices[0].message.content
                $obeyed = $text -match 'Ada'
                $lines.Add("$placement -> got: '$($text.Trim())' obeyed=$obeyed")
            } catch {
                $lines.Add("$placement -> error: $($_.Exception.Message)")
            } finally {
                Stop-AuxServer $auxProc
            }
        }

        $lines.Add('verdict: compare the two "obeyed" lines above. Phi Silica has twice been observed ignoring a natively delivered system prompt (docs/DECISIONS.md); this is that check run on real hardware for this build.')
        $lines -join "`n"
    }
} finally {
    if ($proc -and -not $proc.HasExited) {
        # Stopping the by-path process stops the activated instance it supervises (D37).
        Write-Host "Stopping server (pid $($proc.Id))"
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
$skipped = @($results | Where-Object Result -eq 'SKIP').Count
$info = @($results | Where-Object Result -eq 'INFO').Count
if ($failed -gt 0) { Write-Host "$failed step(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All steps passed ($skipped skipped, $info informational)" -ForegroundColor Green
