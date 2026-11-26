<#
.SYNOPSIS
  Starts a Phase 0 capture session on Windows: session variables, HTTP listener, then claude.

.DESCRIPTION
  Sets ENGRAM_TOKEN (random, this terminal only) and ENGRAM_UNLISTED, clears ENGRAM_DISABLE, opens the
  listener in a second window (it inherits the variables so it can confirm the Authorization header),
  and runs `claude` in the sandbox repo. Extra arguments go to claude, e.g.:
    pwsh spike/start-session.ps1 --continue
    pwsh spike/start-session.ps1 --continue --fork-session

  Run spike/install.ps1 first.
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [string]$Sandbox = (Join-Path $env:USERPROFILE 'src\engram-sandbox'),
    [int]$Port = 47613,
    [switch]$NoListener,
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$ClaudeArgs
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command engram -ErrorAction SilentlyContinue)) { throw 'engram is not on PATH. Run spike/install.ps1, then open a new terminal.' }
if (-not (Test-Path (Join-Path $Sandbox '.claude\settings.local.json'))) { throw "No sandbox at $Sandbox. Run spike/install.ps1." }

$env:ENGRAM_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLower()
$env:ENGRAM_UNLISTED = 'should-not-appear'   # not in allowedEnvVars, so it must arrive empty
Remove-Item Env:ENGRAM_DISABLE -ErrorAction SilentlyContinue

if (-not $NoListener) {
    Start-Process pwsh -ArgumentList '-NoExit', '-Command', "engram spike listen --port $Port"
    Start-Sleep -Milliseconds 800
}

Set-Location $Sandbox
claude @ClaudeArgs
