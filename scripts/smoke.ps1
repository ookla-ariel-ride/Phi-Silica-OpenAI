<#
.SYNOPSIS
  End-to-end smoke test of npu-bridge against a real backend on this machine.

.DESCRIPTION
  Starts NpuBridge.exe (or, with -NoStart, tests a server already listening), waits for /healthz to
  report ready (the first Phi Silica / Aion load can take minutes), then exercises /v1/models,
  /debug/generate (raw model access, cancellation, prompt-length preflight), /v1/chat/completions
  non-streaming and streaming (the SSE wire contract and the client-side cut) and, once chunk 7 lands,
  a tool-call compliance probe.

  It also takes the measurements docs/DECISIONS.md cites: the token estimate against the progress
  callbacks, which system-prompt placement this model obeys, whether cancelling a generation really
  stops the accelerator, and how an over-length prompt is refused -- how long it takes, and whether
  the verdict lands as an HTTP status or as an in-stream error frame (D52, D55). Those report numbers
  and never fail the run: a surprising number is a finding, not a broken bridge.

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

# Reads a server-sent-event response frame by frame rather than buffering it, because three of the
# things this script has to report are only visible while the stream is open: when the response headers
# arrive (the window in which a failure is still an ordinary HTTP status, D52), when the first chunk
# does, and whether the stream ended in a `data: {"error":...}` frame -- a failure that arrived after
# the headers were spent looks like HTTP 200 from outside and is invisible to a caller that only reads
# finish reasons. A reply that is not a stream at all is returned whole in Body, since that is a JSON
# error rather than a stream.
function Invoke-Sse([string] $path, [string] $body, [int] $timeoutSec = 300) {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($timeoutSec)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$base$path")
    $request.Content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')

    $chunks = [System.Collections.Generic.List[object]]::new()
    $frames = [System.Collections.Generic.List[string]]::new()
    $errors = [System.Collections.Generic.List[object]]::new()
    $keepAlives = 0
    $done = $false
    $firstChunkMs = $null
    $errorMs = $null
    $text = ''
    $reader = $null
    $response = $null
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $headerMs = $sw.Elapsed.TotalMilliseconds
        $status = [int]$response.StatusCode
        $contentType = $response.Content.Headers.ContentType.MediaType

        if ($status -ne 200 -or $contentType -ne 'text/event-stream') {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        else {
            $reader = [System.IO.StreamReader]::new($response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line.Length -eq 0) { continue }                       # the blank line between frames
                if ($line.StartsWith(':')) { $keepAlives++; continue }     # ': keep-alive', a comment
                if (-not $line.StartsWith('data: ')) { throw "unexpected SSE line: '$line'" }
                $payload = $line.Substring(6)
                if ($payload -eq '[DONE]') { $done = $true; continue }
                if ($null -eq $firstChunkMs) { $firstChunkMs = $sw.Elapsed.TotalMilliseconds }
                $frames.Add($payload)
                $frame = $payload | ConvertFrom-Json -Depth 20
                # A mid-stream failure: the status line was already spent, so the error travels as a
                # frame. Stamped separately because that is the moment the verdict arrived.
                if ($null -ne $frame.error) {
                    if ($null -eq $errorMs) { $errorMs = $sw.Elapsed.TotalMilliseconds }
                    $errors.Add($frame)
                }
                $chunks.Add($frame)
            }
        }
        $sw.Stop()

        $parsed = $chunks.ToArray()
        # The reply as the client sees it: every content delta, in order. The usage chunk contributes
        # nothing, having no choices at all.
        $content = -join ($parsed | ForEach-Object { $_.choices } | ForEach-Object { $_.delta.content })
        $ttft = if ($null -ne $firstChunkMs) { [Math]::Round($firstChunkMs, 1) } else { $null }
        $errAt = if ($null -ne $errorMs) { [Math]::Round($errorMs, 1) } else { $null }

        [pscustomobject]@{
            StatusCode   = $status
            ContentType  = $contentType
            Body         = $text
            Chunks       = $parsed
            Frames       = $frames.ToArray()
            ErrorFrames  = $errors.ToArray()
            Content      = $content
            KeepAlives   = $keepAlives
            Done         = $done
            HeaderMs     = [Math]::Round($headerMs, 1)
            FirstChunkMs = $ttft
            ErrorMs      = $errAt
            TotalMs      = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        }
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
    }
}

# The finish reasons carried by a stream's chunks, in order. Not simply choices[0] on every chunk: the
# usage chunk carries an empty choices array by design. The leading comma keeps the result an array
# when there is exactly one -- PowerShell would otherwise unroll it to a bare string, whose .Count is
# also 1 and whose [0] is its first character, so the callers' checks would read 's' for 'stop'.
function Get-FinishReasons($chunks) {
    $reasons = @($chunks |
        Where-Object { $_.choices.Count -gt 0 -and $_.choices[0].finish_reason } |
        ForEach-Object { $_.choices[0].finish_reason })
    return , $reasons
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

    # --- chat completions (chunks 3 and 4) -----------------------------------
    # One gate per feature: non-streaming (chunk 3) and streaming with the client-side cut (chunk 4)
    # are built now; tool calls (chunk 7) are not, and must report SKIP, not FAIL, so a clean run
    # stays "All steps passed".
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
        $body = @{
            model          = $Backend
            stream         = $true
            stream_options = @{ include_usage = $true }
            messages       = @(
                @{ role = 'system'; content = 'You are a terse assistant.' }
                @{ role = 'user'; content = 'Reply with exactly the word PONG.' }
            )
        } | ConvertTo-Json -Depth 5

        $s = Invoke-Sse '/v1/chat/completions' $body
        if ($s.StatusCode -ne 200) { throw "HTTP $($s.StatusCode): $($s.Body)" }
        if ($s.ContentType -ne 'text/event-stream') { throw "Content-Type=$($s.ContentType)" }
        if (-not $s.Done) { throw "stream did not end with the done marker: $($s.Frames -join ' | ')" }
        if ($s.Chunks.Count -lt 2) { throw "only $($s.Chunks.Count) chunk(s): $($s.Frames -join ' | ')" }

        # The role chunk opens the assistant message; OpenAI clients build the message from it.
        $first = $s.Chunks[0]
        if ($first.choices[0].delta.role -ne 'assistant') { throw "first chunk is not the role chunk: $($s.Frames[0])" }
        if ($first.id -notlike 'chatcmpl-*') { throw "id=$($first.id) does not start with chatcmpl-" }

        # One reply, one identity: a client stitching the chunks together must see the id, created and
        # model a non-streamed reply would have carried, on every chunk including the usage one.
        foreach ($c in $s.Chunks) {
            if ($c.object -ne 'chat.completion.chunk') { throw "object=$($c.object)" }
            if ($c.id -ne $first.id -or $c.created -ne $first.created -or $c.model -ne $first.model) {
                throw "identity drifted: id=$($c.id) created=$($c.created) model=$($c.model) vs first id=$($first.id) created=$($first.created) model=$($first.model)"
            }
        }

        if (-not $s.Content) { throw 'the content deltas concatenate to nothing' }

        $finishes = Get-FinishReasons $s.Chunks
        if ($finishes.Count -ne 1) { throw "$($finishes.Count) chunks carry a finish_reason, expected 1: $($finishes -join ',')" }
        if ($finishes[0] -ne 'stop') { throw "finish_reason=$($finishes[0])" }

        # stream_options.include_usage: exactly one usage chunk, last before the done marker, with an
        # empty choices array so a client indexing choices[0] on every chunk sees no phantom delta.
        $withUsage = @($s.Chunks | Where-Object { $null -ne $_.usage })
        if ($withUsage.Count -ne 1) { throw "$($withUsage.Count) usage chunks, expected exactly 1" }
        $last = $s.Chunks[-1]
        if ($null -eq $last.usage) { throw 'the usage chunk is not the last chunk before the done marker' }
        if ($last.choices.Count -ne 0) { throw "the usage chunk carries $($last.choices.Count) choice(s), expected none" }
        $u = $last.usage
        if (-not ($u.prompt_tokens -gt 0 -and $u.completion_tokens -gt 0 -and $u.total_tokens -eq ($u.prompt_tokens + $u.completion_tokens))) {
            throw "usage=$($u | ConvertTo-Json -Compress)"
        }

        # Streaming is the only place time-to-first-token is directly observable client-side. headers is
        # the D52 number: nothing is written until the first delta or the first keep-alive, so this is
        # how long a client waits on a status line.
        $preview = $s.Content.Trim()
        "chunks=$($s.Chunks.Count) ttft=$($s.FirstChunkMs)ms total=$($s.TotalMs)ms headers=$($s.HeaderMs)ms keep-alives=$($s.KeepAlives) usage=$($u | ConvertTo-Json -Compress) text='$($preview.Substring(0, [Math]::Min(80, $preview.Length)))'"
    }

    Step 'streaming client-side cut (max_tokens and stop)' {
        # D53: the cap is a character budget of max_tokens * 4, so usage.completion_tokens -- ceil of
        # chars/4 -- lands on the cap and never above it. Streaming is where the cut is hardest: text
        # already written cannot be recalled.
        $cap = 8
        $capChars = $cap * 4
        $capBody = @{
            model          = $Backend
            stream         = $true
            max_tokens     = $cap
            stream_options = @{ include_usage = $true }
            messages       = @(@{ role = 'user'; content = 'Write a detailed essay of at least 400 words about the history of computing.' })
        } | ConvertTo-Json -Depth 5

        $capped = Invoke-Sse '/v1/chat/completions' $capBody
        if ($capped.StatusCode -ne 200) { throw "max_tokens: HTTP $($capped.StatusCode): $($capped.Body)" }
        if (-not $capped.Done) { throw 'max_tokens: stream did not end with the done marker' }
        $capFinishes = Get-FinishReasons $capped.Chunks
        if ($capFinishes.Count -ne 1 -or $capFinishes[0] -ne 'length') {
            throw "max_tokens: finish_reason=$($capFinishes -join ',') expected exactly one 'length' (the model may have stopped on its own before the cap)"
        }
        if (-not $capped.Content) { throw 'max_tokens: no content before the cut' }
        if ($capped.Content.Length -gt $capChars) { throw "max_tokens: $($capped.Content.Length) chars streamed > budget of $capChars" }
        $capUsage = @($capped.Chunks | Where-Object { $null -ne $_.usage })[0].usage
        if ($null -eq $capUsage) { throw 'max_tokens: no usage chunk, though stream_options.include_usage was set' }
        if ($capUsage.completion_tokens -gt $cap) { throw "max_tokens: usage.completion_tokens=$($capUsage.completion_tokens) > max_tokens=$cap" }

        # The stop string is removed from the reply rather than never produced (D53), and the streaming
        # path has to hold text back to catch one that straddles two deltas.
        $stop = 'charlie'
        $ask = 'Reply with exactly this line and nothing else: alpha bravo charlie delta'
        $stopBody = @{
            model    = $Backend
            stream   = $true
            stop     = $stop
            messages = @(@{ role = 'user'; content = $ask })
        } | ConvertTo-Json -Depth 5

        $cut = Invoke-Sse '/v1/chat/completions' $stopBody
        if ($cut.StatusCode -ne 200) { throw "stop: HTTP $($cut.StatusCode): $($cut.Body)" }
        if (-not $cut.Done) { throw 'stop: stream did not end with the done marker' }
        $cutFinishes = Get-FinishReasons $cut.Chunks
        if ($cutFinishes.Count -ne 1 -or $cutFinishes[0] -ne 'stop') { throw "stop: finish_reason=$($cutFinishes -join ',') expected exactly one 'stop'" }
        if ($cut.Content.Contains($stop)) { throw "stop: the stop string reached the client: '$($cut.Content)'" }

        # A control run of the same prompt without the cut, so the detail can say whether there was
        # anything to truncate. A model that never emits the stop string would satisfy the assertion
        # above without the feature doing any work, and that is worth knowing rather than assuming.
        $controlBody = @{ model = $Backend; stream = $true; messages = @(@{ role = 'user'; content = $ask }) } | ConvertTo-Json -Depth 5
        $control = Invoke-Sse '/v1/chat/completions' $controlBody
        $confirmed = $control.StatusCode -eq 200 -and $control.Content.Contains($stop)
        $evidence = if ($confirmed) {
            "confirmed against a control run: without 'stop' the same prompt produced the string ($($control.Content.Length) chars, cut to $($cut.Content.Length))"
        }
        else {
            "not confirmed: the control run did not contain '$stop' either, so nothing needed truncating this time"
        }

        "max_tokens=$cap -> finish=length, $($capped.Content.Length) chars <= $capChars budget, completion_tokens=$($capUsage.completion_tokens); stop='$stop' -> finish=stop, absent from the reply, $evidence"
    }

    # The contract both shapes rest on: GenerationResult.Text is the concatenation of the deltas the
    # adapter delivered (ILanguageModelBackend). The fake honours it by construction, so dotnet test
    # cannot check a real adapter; this does. The adapter now returns the accumulated deltas on every
    # status and counts, in /healthz, every time the runtime's own text disagreed with them and every
    # callback that arrived after a completed generation ended. Those counters are the assertion. Whether the
    # two shapes' texts match on the wire is reported but not asserted: temperature 0 on this runtime
    # is not a documented promise of determinism, so a difference there is a finding about the model.
    Step 'both shapes return the deltas the adapter delivered (text contract)' {
        $prompt = 'Reply with exactly the word PONG.'
        $messages = @(
            @{ role = 'system'; content = 'You are a terse assistant.' }
            @{ role = 'user'; content = $prompt }
        )
        $json = Get-Json '/v1/chat/completions' 'POST' (@{ model = $Backend; temperature = 0; messages = $messages } | ConvertTo-Json -Depth 5)
        $sse = Invoke-Sse '/v1/chat/completions' (@{ model = $Backend; temperature = 0; stream = $true; messages = $messages } | ConvertTo-Json -Depth 5)
        if ($sse.StatusCode -ne 200 -or -not $sse.Done) { throw "stream: HTTP $($sse.StatusCode), done=$($sse.Done)" }

        $jsonText = $json.choices[0].message.content
        $sseText = $sse.Content
        $match = if ($jsonText -eq $sseText) { 'texts match' } else { "texts differ (json=$($jsonText.Length) chars, sse=$($sseText.Length) chars)" }

        $h = Get-Json '/healthz'
        $d = $h.diagnostics
        if ($Backend -eq 'fake') {
            Skip "$match; the fake backend keeps no text-contract counters"
        }
        if ($null -eq $d.text_mismatches -or $null -eq $d.late_deltas) {
            throw "healthz diagnostics lack text_mismatches/late_deltas: $($d | ConvertTo-Json -Compress)"
        }
        if ($d.text_mismatches -ne 0) { throw "the runtime's text disagreed with the delivered deltas $($d.text_mismatches) time(s) this run" }
        if ($d.late_deltas -ne 0) { throw "$($d.late_deltas) progress callback(s) arrived after the completion barrier this run" }

        "text_mismatches=0 late_deltas=0 over every completed generation so far (a callback after a cancelled one is exempt); $match"
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

    # --- measurement 3: does cancelling a generation actually stop the accelerator? ----------------
    InfoStep 'measurement: does the client-side cut stop the NPU, or only the client?' {
        # The open question since the research phase: the WinRT cancel is advisory, and nobody has
        # established whether the device stops mid-generation or runs to completion regardless. The cut
        # makes it measurable, because it cancels a real generation partway through -- and because the
        # handler does not return until that generation has actually ended (cancel, drain, dispose,
        # D51), the wall clock below is the device's time and not merely the client's.
        #
        # The control side is a *generous* cap rather than no cap at all. Letting the model run to its
        # natural end took 262 s on Phi Silica and made this one step longer than the rest of the suite
        # together, for no extra information: both requests are then cut the same way, one early and one
        # late, and the question -- does the device stop when the cut fires, or keep going -- is answered
        # by the gap between them just as well.
        $prompt = 'Write a detailed essay of at least 400 words about the history of computing.'
        $earlyCap = 4                   # 16 characters: the cut fires on the first delta or two
        $lateCap = 64                   # 256 characters: enough decode to time, nowhere near the essay
        $lateBody = @{ model = $Backend; stream = $true; max_tokens = $lateCap; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 5
        $earlyBody = @{ model = $Backend; stream = $true; max_tokens = $earlyCap; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 5

        $uncapped = Invoke-Sse '/v1/chat/completions' $lateBody
        if ($uncapped.StatusCode -ne 200) { throw "control (max_tokens=$lateCap): HTTP $($uncapped.StatusCode): $($uncapped.Body)" }
        $capped = Invoke-Sse '/v1/chat/completions' $earlyBody
        if ($capped.StatusCode -ne 200) { throw "early cut (max_tokens=$earlyCap): HTTP $($capped.StatusCode): $($capped.Body)" }

        $fullMs = $uncapped.TotalMs
        $cutMs = $capped.TotalMs
        $ratio = if ($fullMs -gt 0) { [Math]::Round($cutMs / $fullMs, 2) } else { $null }

        # Prompt processing cannot be cancelled: the early-cut request can never beat its own time to
        # first token. So the decode phase -- everything after it -- is where a working cancel shows up,
        # and that ratio is the one the verdict is read off when both are measurable.
        $fullDecode = if ($null -ne $uncapped.FirstChunkMs) { $fullMs - $uncapped.FirstChunkMs } else { $null }
        $cutDecode = if ($null -ne $capped.FirstChunkMs) { $cutMs - $capped.FirstChunkMs } else { $null }
        $decodeRatio = if ($null -ne $fullDecode -and $null -ne $cutDecode -and $fullDecode -gt 0) {
            [Math]::Round($cutDecode / $fullDecode, 2)
        }
        else { $null }

        $judged = if ($null -ne $decodeRatio) { $decodeRatio } else { $ratio }
        $verdict = if ($null -eq $judged) {
            'inconclusive: neither request took measurable time, so there is nothing to compare.'
        }
        elseif ($null -ne $ratio -and $ratio -gt 1) {
            # The decode ratio alone must not declare victory while the early cut took longer overall:
            # a verdict that contradicts the numbers printed above it is worse than no verdict.
            'inconclusive: the early cut did not finish sooner end to end, so the two runs are too close to separate -- time to first token is dominating and the decode figures are noise. On a backend whose generation takes seconds this cannot happen by accident; re-run before reading anything into it.'
        }
        elseif ($judged -le 0.5) {
            'cancellation really does stop the device. The early cut finished in a small fraction of the later one, and neither request is answered until its generation has ended, so the work stopped -- the NPU was not left running behind a returned response.'
        }
        elseif ($judged -ge 0.8) {
            "cancellation is advisory only. Cutting after 16 characters took about as long as cutting after 256, so the accelerator kept generating regardless of the cut; the cap saves the client's time and its token count, not the device's work."
        }
        else {
            'inconclusive: the early cut was faster but not decisively so. The device may be stopping at the next token boundary rather than at once; re-run before quoting this.'
        }

        $caveat = if ($Backend -eq 'fake') {
            "caveat: this is the fake backend, which generates with no per-token delay; the numbers exercise the measurement, they do not answer the hardware question."
        }
        else { $null }

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("asked: the same prompt twice on the streaming path ($($prompt.Length) chars), cut early (max_tokens=$earlyCap, a $($earlyCap * 4)-char budget) against a control cut late (max_tokens=$lateCap, $($lateCap * 4) chars). The control is a generous cap rather than no cap, so neither side runs a full-length generation.")
        $lines.Add("control (late cut):  $fullMs ms total, ttft $($uncapped.FirstChunkMs) ms, $($uncapped.Content.Length) chars, finish=$((Get-FinishReasons $uncapped.Chunks) -join ',')")
        $lines.Add("early cut:           $cutMs ms total, ttft $($capped.FirstChunkMs) ms, $($capped.Content.Length) chars, finish=$((Get-FinishReasons $capped.Chunks) -join ',')")
        $decodeText = if ($null -ne $decodeRatio) { "$decodeRatio of it counting only the decode phase after the first token" } else { 'decode phase not separately measurable' }
        $lines.Add("ratio: the early cut took $ratio of the control end to end, $decodeText")
        $lines.Add("verdict: $verdict")
        if ($caveat) { $lines.Add($caveat) }
        $lines -join "`n"
    }

    # --- measurement 4: how long does an over-length prompt take to be refused, and how? -----------
    InfoStep 'measurement: over-length prompt -- verdict latency and where the verdict lands (D52)' {
        # StreamingOptions.DefaultFirstKeepAliveDelay. Not settable from the command line, so this is
        # the number shipped code uses. D52 turns on which side of it the verdict lands: the streaming
        # path writes nothing until the first token or the first keep-alive, and once a keep-alive has
        # committed the headers a failure can only travel as an in-stream error frame.
        $firstKeepAliveMs = 1000

        # As big as is cheap to build rather than marginal, so the verdict is unambiguous. The last line
        # is short on purpose: the fake backend echoes it, and a backend with no context window would
        # otherwise answer with a megabyte of JSON.
        $filler = 'The quick brown fox jumps over the lazy dog. ' * 5000
        $prompt = "$filler`nSummarize the text above in one sentence."
        $body = @{ model = $Backend; stream = $true; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 5

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("asked: one user message of $($prompt.Length) chars on the streaming path")

        $s = Invoke-Sse '/v1/chat/completions' $body

        # Three ways this can end, and the difference between them is the whole point of the step. An
        # ordinary HTTP status means the verdict beat the header commit. An error frame means it did not
        # -- and that is invisible to anything reading only the status code or the finish reason, which
        # is how an earlier version of this step concluded a refused prompt had been accepted.
        $err = if ($s.ErrorFrames.Count -gt 0) { $s.ErrorFrames[0].error } else { $null }

        if ($s.StatusCode -ne 200) {
            $e = ($s.Body | ConvertFrom-Json).error
            $verdictMs = $s.HeaderMs
            $lines.Add("verdict: HTTP $($s.StatusCode) type=$($e.type) code=$($e.code) after $verdictMs ms, before a single byte was written -- the status line was still the server's to set")
            $lines.Add("message: '$($e.message)'")
            $margin = if ($verdictMs -lt ($firstKeepAliveMs * 0.2)) {
                "comfortable: the verdict lands in under a fifth of the ${firstKeepAliveMs} ms first keep-alive, so 1 s is not close to the edge"
            }
            elseif ($verdictMs -lt ($firstKeepAliveMs * 0.5)) {
                "adequate but not generous: the verdict uses more than a fifth of the ${firstKeepAliveMs} ms first keep-alive; do not lower that default"
            }
            elseif ($verdictMs -lt $firstKeepAliveMs) {
                "uncomfortable: the verdict uses more than half of the ${firstKeepAliveMs} ms first keep-alive, so a slower run would commit the headers and lose the status; raise the default"
            }
            else {
                "exceeded: the verdict took longer than the ${firstKeepAliveMs} ms first keep-alive, yet the status still arrived -- check the keep-alive path, this should not happen"
            }
            $lines.Add("margin: $margin")
        }
        elseif ($null -ne $err) {
            $over = if ($s.ErrorMs -gt 0) { [Math]::Round($s.ErrorMs / $firstKeepAliveMs, 1) } else { $null }
            $roleChunks = @($s.Chunks | Where-Object { $_.choices.Count -gt 0 -and $_.choices[0].delta.role })
            $lines.Add("verdict: an in-stream error frame after $($s.ErrorMs) ms -- type=$($err.type) code=$($err.code) param=$($err.param)")
            $lines.Add("message: '$($err.message)'")
            $lines.Add("headers: already committed at $($s.HeaderMs) ms by $($s.KeepAlives) keep-alive comment(s); role chunks before the error=$($roleChunks.Count); done marker=$($s.Done); HTTP status seen by the client=200")
            $lines.Add("margin: none. The verdict took ${over}x the ${firstKeepAliveMs} ms first keep-alive, so the headers were spent long before it arrived and no HTTP status could carry it. The stream ending in an error frame followed by the done marker is the specified behaviour for exactly this case -- it is the header deferral being unable to help, not the deferral failing.")
            if ($err.code -ne 'context_length_exceeded') {
                $lines.Add("note: the code is '$($err.code)', not 'context_length_exceeded' -- this backend did not report the prompt as too long, it reported a generic failure. See the preflight number in the cross-check below, and D55.")
            }
        }
        else {
            $finishes = (Get-FinishReasons $s.Chunks) -join ','
            $lines.Add("verdict: none. HTTP 200 after $($s.TotalMs) ms, finish=$finishes, $($s.Content.Length) chars generated, no error frame -- this backend generated a reply from the whole prompt, so there was no refusal to time (the fake has no context window).")
        }

        # Cross-check without the HTTP round trip: /debug/generate reports the server-side elapsed time
        # for the same prompt, and -- unlike the chat path -- calls the prompt-length preflight first.
        # That preflight number is the one that matters for chunk 5: it says how much of the prompt fits
        # even when the generation itself refuses with something other than PromptLargerThanContext.
        try {
            $g = Get-Json '/debug/generate' 'POST' (@{ prompt = $prompt } | ConvertTo-Json)
            $lines.Add("cross-check (/debug/generate, server-side clock, preflight included): status=$($g.status) detail='$($g.detail)' total=$($g.total_ms)ms preflight usable_prompt_chars=$($g.usable_prompt_chars) of $($g.prompt_chars)")
            if ($null -ne $g.usable_prompt_chars -and $g.usable_prompt_chars -lt $g.prompt_chars) {
                $lines.Add("preflight is decisive and cheap: it knew $($g.usable_prompt_chars) of $($g.prompt_chars) chars fit, whatever status the generation went on to report.")
            }
        }
        catch {
            $lines.Add("cross-check (/debug/generate): $($_.Exception.Message)")
        }

        $lines.Add("verdict for the log: D52 shipped a ${firstKeepAliveMs} ms first keep-alive as a reasoned default and said this script owed the measurement. The numbers above are it (D52, D55).")
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
