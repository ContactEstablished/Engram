<#
.SYNOPSIS
  Measures cold-start wall time of the AOT `engram spike capture` hook handler on Windows.

.DESCRIPTION
  Spawns the published engram.exe N times per scenario, writes a realistic hook payload to stdin,
  and times spawn-to-exit (roughly what Claude Code pays per command hook). Each run goes to a
  throwaway ENGRAM_HOME, so your real ~/.engram is never touched.

  Scenarios:
    first-run      one run of a freshly copied exe (Defender usually scans a new binary once)
    capture        valid PostToolUse payload, appended to <temp>/spike/PostToolUse.jsonl
    disabled       ENGRAM_DISABLE=1 (the recursion guard: exits before reading stdin)
    empty-stdin    no input (error path: logs to errors.log)
    git-bash       the same capture through Git Bash shell form (what a hook without "args" costs)
    baseline       cmd.exe /c exit, for the OS's own process-creation cost

.EXAMPLE
  pwsh spike/measure-coldstart.ps1
  pwsh spike/measure-coldstart.ps1 -Runs 100 -Exe "$env:LOCALAPPDATA\Engram\bin\engram.exe"
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\publish\Engram.Cli\release_win-x64\engram.exe'),
    [int]$Runs = 50
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path

$work = Join-Path ([IO.Path]::GetTempPath()) ("engram-coldstart-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

# A realistic ~2 KB PostToolUse payload.
$payload = @{
    session_id      = [Guid]::NewGuid().ToString()
    transcript_path = 'C:\Users\someone\.claude\projects\sandbox\t.jsonl'
    cwd             = 'C:\src\engram-sandbox'
    permission_mode = 'default'
    hook_event_name = 'PostToolUse'
    tool_name       = 'Bash'
    tool_input      = @{ command = 'dotnet test'; description = 'Run tests' }
    tool_response   = @{ stdout = ('Passed!  - Failed: 0, Passed: 78, Skipped: 1 ' * 30); stderr = ''; interrupted = $false }
    tool_use_id     = 'toolu_01ABC'
    duration_ms     = 4210
} | ConvertTo-Json -Compress -Depth 5
$payloadBytes = [Text.Encoding]::UTF8.GetBytes($payload)

function Invoke-Timed {
    param([string]$File, [string[]]$Arguments, [hashtable]$Env = @{}, [byte[]]$Stdin = @())

    $psi = [Diagnostics.ProcessStartInfo]::new($File)
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Environment['ENGRAM_HOME'] = $work
    $psi.Environment.Remove('ENGRAM_DISABLE') | Out-Null
    foreach ($k in $Env.Keys) { $psi.Environment[$k] = $Env[$k] }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    if ($Stdin.Length -gt 0) {
        try { $p.StandardInput.BaseStream.Write($Stdin, 0, $Stdin.Length) } catch [IO.IOException] { } # disabled path may exit first
    }
    try { $p.StandardInput.Close() } catch [IO.IOException] { }
    $stdout = $p.StandardOutput.ReadToEnd()
    $null = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    $sw.Stop()

    if ($p.ExitCode -ne 0) { throw "$File exited $($p.ExitCode)" }
    if ($File -eq $script:FreshExe -or $File -eq $Exe) {
        if ($stdout.Length -gt 0) { throw "engram wrote to stdout: $stdout" }
    }
    return $sw.Elapsed.TotalMilliseconds
}

function Get-Stats([double[]]$ms) {
    $s = $ms | Sort-Object
    $pick = { param($q) $s[[Math]::Max(0, [Math]::Ceiling($q * $s.Count) - 1)] }
    [pscustomobject]@{
        runs = $s.Count
        min  = [Math]::Round($s[0], 1)
        p50  = [Math]::Round((& $pick 0.50), 1)
        p95  = [Math]::Round((& $pick 0.95), 1)
        max  = [Math]::Round($s[-1], 1)
    }
}

$results = [ordered]@{}

# first-run: a brand-new copy of the binary
$script:FreshExe = Join-Path $work 'engram-fresh.exe'
Copy-Item $Exe $script:FreshExe
$results['first-run (fresh copy)'] = Get-Stats @(Invoke-Timed $script:FreshExe @('spike', 'capture') -Stdin $payloadBytes)

# warm the file cache, then measure
1..3 | ForEach-Object { Invoke-Timed $Exe @('spike', 'capture') -Stdin $payloadBytes | Out-Null }

$results['capture'] = Get-Stats (1..$Runs | ForEach-Object { Invoke-Timed $Exe @('spike', 'capture') -Stdin $payloadBytes })
$results['disabled (ENGRAM_DISABLE=1)'] = Get-Stats (1..$Runs | ForEach-Object { Invoke-Timed $Exe @('spike', 'capture') -Env @{ ENGRAM_DISABLE = '1' } -Stdin $payloadBytes })
$results['empty-stdin'] = Get-Stats (1..$Runs | ForEach-Object { Invoke-Timed $Exe @('spike', 'capture') })

$gitBash = 'C:\Program Files\Git\bin\bash.exe'
if (Test-Path $gitBash) {
    $bashPath = $Exe -replace '\\', '/'
    $results['git-bash shell form'] = Get-Stats (1..([Math]::Min($Runs, 20)) | ForEach-Object { Invoke-Timed $gitBash @('-c', "'$bashPath' spike capture") -Stdin $payloadBytes })
}

$results['baseline: cmd.exe /c exit'] = Get-Stats (1..$Runs | ForEach-Object { Invoke-Timed "$env:SystemRoot\System32\cmd.exe" @('/c', 'exit 0') })

# What engram itself recorded: process start -> line written.
$recorded = Get-Content (Join-Path $work 'spike\PostToolUse.jsonl') | ForEach-Object { ($_ | ConvertFrom-Json) }
$results['self-reported process_ms'] = Get-Stats ($recorded | ForEach-Object { [double]$_.process_ms })
$results['self-reported handler_ms'] = Get-Stats ($recorded | ForEach-Object { [double]$_.handler_ms })

$size = [Math]::Round((Get-Item $Exe).Length / 1KB)
Write-Host ""
Write-Host "engram.exe: $Exe ($size KB)"
Write-Host "machine:    $([Environment]::OSVersion.VersionString), $([Environment]::ProcessorCount) logical CPUs"
Write-Host ""
Write-Host "| scenario | runs | min ms | p50 ms | p95 ms | max ms |"
Write-Host "| --- | ---: | ---: | ---: | ---: | ---: |"
foreach ($k in $results.Keys) {
    $r = $results[$k]
    Write-Host "| $k | $($r.runs) | $($r.min) | $($r.p50) | $($r.p95) | $($r.max) |"
}

Remove-Item -Recurse -Force $work
