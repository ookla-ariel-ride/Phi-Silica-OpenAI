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

function Step([string] $name, [scriptblock] $body) {
    Write-Host "==> $name" -ForegroundColor Cyan
    try {
        $detail = & $body
        $results.Add([pscustomobject]@{ Step = $name; Result = 'PASS'; Detail = "$detail" })
        Write-Host "    PASS $detail" -ForegroundColor Green
    } catch {
        $results.Add([pscustomobject]@{ Step = $name; Result = 'FAIL'; Detail = $_.Exception.Message })
        Write-Host "    FAIL $($_.Exception.Message)" -ForegroundColor Red
    }
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

    # --- chat completions (chunk 3+) ----------------------------------------
    $chatAvailable = $true
    try { $null = Get-Json '/v1/chat/completions' 'POST' '{}' @(400) } catch { $chatAvailable = $false }
    if (-not $chatAvailable) {
        Write-Host '    (chat completions endpoint not built yet; skipping OpenAI generation steps)' -ForegroundColor Yellow
    } else {
        Step 'POST /v1/chat/completions (non-streaming)' {
            $body = @{ model = $Backend; messages = @(@{ role = 'user'; content = 'Reply with exactly the word PONG.' }) } | ConvertTo-Json -Depth 5
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $c = Get-Json '/v1/chat/completions' 'POST' $body
            $sw.Stop()
            $text = $c.choices[0].message.content
            if (-not $text) { throw "empty content: $($c | ConvertTo-Json -Compress)" }
            "in $($sw.ElapsedMilliseconds)ms finish=$($c.choices[0].finish_reason) usage=$($c.usage | ConvertTo-Json -Compress) text='$($text.Substring(0, [Math]::Min(80, $text.Length)))'"
        }

        Step 'POST /v1/chat/completions (streaming SSE)' {
            $body = @{ model = $Backend; stream = $true; messages = @(@{ role = 'user'; content = 'Count from 1 to 5, separated by spaces.' }) } | ConvertTo-Json -Depth 5
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $r = Invoke-WebRequest -Uri "$base/v1/chat/completions" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 300
            $sw.Stop()
            if ($r.Headers['Content-Type'] -notmatch 'text/event-stream') { throw "content-type $($r.Headers['Content-Type'])" }
            $lines = $r.Content -split "`n" | Where-Object { $_ -like 'data: *' }
            if ($lines[-1].Trim() -ne 'data: [DONE]') { throw "no [DONE]; last line: $($lines[-1])" }
            $chunks = $lines | Where-Object { $_ -ne 'data: [DONE]' } | ForEach-Object { ($_ -replace '^data: ', '') | ConvertFrom-Json }
            $text = -join ($chunks | ForEach-Object { $_.choices[0].delta.content })
            "$($chunks.Count) chunks in $($sw.ElapsedMilliseconds)ms text='$($text.Trim())'"
        }

        if ($ToolProbeRuns -gt 0) {
            Step "tool-call compliance probe ($ToolProbeRuns runs)" {
                $tools = @(@{ type = 'function'; function = @{ name = 'get_weather'; description = 'Get the current weather for a city'; parameters = @{ type = 'object'; properties = @{ city = @{ type = 'string' } }; required = @('city') } } })
                $body = @{ model = $Backend; tools = $tools; messages = @(@{ role = 'user'; content = 'What is the weather in Paris right now? Use the tool.' }) } | ConvertTo-Json -Depth 8
                $hits = 0
                for ($i = 0; $i -lt $ToolProbeRuns; $i++) {
                    $c = Get-Json '/v1/chat/completions' 'POST' $body
                    $tc = $c.choices[0].message.tool_calls
                    if ($c.choices[0].finish_reason -eq 'tool_calls' -and $tc -and $tc[0].function.name -eq 'get_weather') { $hits++ }
                }
                if ($hits -eq 0) { throw "0/$ToolProbeRuns runs produced a tool call" }
                "$hits/$ToolProbeRuns runs produced a well-formed get_weather call"
            }
        }
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
if ($failed -gt 0) { Write-Host "$failed step(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'All steps passed' -ForegroundColor Green
