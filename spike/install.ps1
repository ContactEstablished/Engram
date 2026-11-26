<#
.SYNOPSIS
  One-shot setup for the Phase 0 spike (README Part A1-A3, and Part B with -Wsl).

.DESCRIPTION
  Does everything that doesn't need you at the keyboard:
    1. Publishes the Native AOT engram.exe.
    2. Copies it to %LOCALAPPDATA%\Engram\bin and adds that folder to your USER PATH.
    3. Creates the sandbox repo (%USERPROFILE%\src\engram-sandbox) with spike/settings.local.json.
    4. With -Wsl: builds the Linux binary, installs it in WSL, and creates the sandbox there too.

  It never touches ~/.claude/settings.json. The only global change is the user PATH entry, and it asks
  before making it (skip the prompt with -Yes). Safe to re-run: it overwrites the binary and settings,
  and leaves an existing sandbox repo and PATH entry alone.

  Afterwards, start a session with spike/start-session.ps1 (Windows) or `engram-spike-session` (WSL).

.PARAMETER Yes        Don't ask for confirmation.
.PARAMETER SkipBuild  Reuse the binaries already under artifacts/ (faster re-install).
.PARAMETER Wsl        Also set up WSL.
.PARAMETER Distro     WSL distro name (default: your default distro).
.PARAMETER Sandbox    Windows sandbox path.
.PARAMETER ResetCaptures  Delete earlier spike captures (%USERPROFILE%\.engram\spike) first.
.PARAMETER DryRun     Print what would happen and change nothing.
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$SkipBuild,
    [switch]$Wsl,
    [string]$Distro,
    [string]$Sandbox = (Join-Path $env:USERPROFILE 'src\engram-sandbox'),
    [switch]$ResetCaptures,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$bin = Join-Path $env:LOCALAPPDATA 'Engram\bin'
$exeSrc = Join-Path $root 'artifacts\publish\Engram.Cli\release_win-x64\engram.exe'
$settings = Join-Path $root 'spike\settings.local.json'
$linuxSrc = Join-Path $root 'artifacts\publish\Engram.Cli\release_linux-x64\engram'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Invoke-Step([string]$Description, [scriptblock]$Action) {
    if ($DryRun) { Write-Host "[dry-run] $Description" -ForegroundColor DarkGray }
    else { Write-Host "  $Description"; & $Action }
}

function Confirm-Change([string]$Question) {
    if ($Yes -or $DryRun) { return $true }
    return (Read-Host "$Question [y/N]") -match '^(y|yes)$'
}

function New-Sandbox([string]$Path) {
    Invoke-Step "sandbox repo at $Path" {
        New-Item -ItemType Directory -Force (Join-Path $Path '.claude') | Out-Null
        Copy-Item $settings (Join-Path $Path '.claude\settings.local.json') -Force
        if (-not (Test-Path (Join-Path $Path '.git'))) {
            'hello from the sandbox' | Set-Content (Join-Path $Path 'notes.txt')
            git -C $Path init -q
            git -C $Path add .
            git -C $Path -c user.name=engram -c user.email=engram@localhost commit -qm init
        }
    }
}

if (-not (Test-Path $settings)) { throw "Missing $settings. Run this from the repo's spike folder." }

Write-Host "Engram Phase 0 install"
Write-Host "  repo:     $root"
Write-Host "  binary:   $bin\engram.exe  (and your user PATH)"
Write-Host "  sandbox:  $Sandbox"
if ($Wsl) { Write-Host "  WSL:      ~/.local/bin/engram and ~/src/engram-sandbox  (distro: $(if ($Distro) { $Distro } else { 'default' }))" }
if ($DryRun) { Write-Host "  (dry run: nothing will change)" -ForegroundColor Yellow }

# ---- Windows -------------------------------------------------------------------------------------
if (-not $SkipBuild) {
    Step 'Publish Native AOT engram.exe'
    Invoke-Step 'dotnet publish src/Engram.Cli -c Release -r win-x64' {
        Push-Location $root
        try {
            dotnet publish src/Engram.Cli -c Release -r win-x64 --nologo -v q
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
        }
        finally { Pop-Location }
    }
}
if (-not $DryRun -and -not (Test-Path $exeSrc)) { throw "Missing $exeSrc. Re-run without -SkipBuild." }

Step 'Install engram.exe'
if (-not (Confirm-Change "Copy engram.exe to $bin and add that folder to your user PATH?")) {
    Write-Host 'Cancelled. Nothing changed.'
    return
}
Invoke-Step "copy engram.exe to $bin" {
    New-Item -ItemType Directory -Force $bin | Out-Null
    Copy-Item $exeSrc $bin -Force
}
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -contains $bin) {
    Write-Host '  user PATH already contains it'
}
else {
    Invoke-Step 'add to user PATH' {
        $new = if ([string]::IsNullOrEmpty($userPath)) { $bin } else { "$userPath;$bin" }
        [Environment]::SetEnvironmentVariable('Path', $new, 'User')
        $env:Path = "$env:Path;$bin"
    }
}

Step 'Sandbox repo (Windows)'
if ($ResetCaptures) {
    $spikeDir = Join-Path $env:USERPROFILE '.engram\spike'
    Invoke-Step "delete old captures in $spikeDir" {
        if (Test-Path $spikeDir) { Remove-Item -Recurse -Force $spikeDir }
    }
}
New-Sandbox $Sandbox

# ---- WSL -----------------------------------------------------------------------------------------
if ($Wsl) {
    Step 'Build Linux binary'
    if (-not $SkipBuild) {
        Invoke-Step 'pwsh spike/publish-linux.ps1' {
            & (Join-Path $PSScriptRoot 'publish-linux.ps1')
        }
    }
    if (-not $DryRun -and -not (Test-Path $linuxSrc)) { throw "Missing $linuxSrc. Re-run without -SkipBuild." }

    Step 'Install in WSL'
    $wslArgs = if ($Distro) { @('-d', $Distro) } else { @() }
    $scriptWin = Join-Path $root 'artifacts\wsl-install.sh'
    $sh = @'
set -e
src="$1"; settings="$2"; reset="$3"
mkdir -p "$HOME/.local/bin"
cp "$src" "$HOME/.local/bin/engram"
chmod +x "$HOME/.local/bin/engram"
if [ "$reset" = "1" ]; then rm -rf "$HOME/.engram/spike"; fi
sandbox="$HOME/src/engram-sandbox"
mkdir -p "$sandbox/.claude"
cp "$settings" "$sandbox/.claude/settings.local.json"
if [ ! -d "$sandbox/.git" ]; then
  cd "$sandbox"
  git init -q
  echo "hello from the sandbox" > notes.txt
  git add .
  git -c user.name=engram -c user.email=engram@localhost commit -qm init
fi
cat > "$HOME/.local/bin/engram-spike-session" <<'EOS'
#!/usr/bin/env bash
# Starts the listener, sets the session variables, and runs claude in the WSL sandbox.
export PATH="$HOME/.local/bin:$PATH"
export ENGRAM_TOKEN=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
export ENGRAM_UNLISTED=should-not-appear
unset ENGRAM_DISABLE
engram spike listen --port 47613 > /tmp/engram-listen.log 2>&1 &
listener=$!
trap 'kill $listener 2>/dev/null' EXIT
cd "$HOME/src/engram-sandbox" && claude "$@"
EOS
chmod +x "$HOME/.local/bin/engram-spike-session"
echo "WSL: installed $("$HOME/.local/bin/engram" --version) and the sandbox at $sandbox"
'@
    Invoke-Step 'copy the binary, create the sandbox and engram-spike-session inside WSL' {
        [IO.File]::WriteAllText($scriptWin, ($sh -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        $scriptWsl = (wsl.exe @wslArgs -e wslpath -a $scriptWin).Trim()
        $binWsl = (wsl.exe @wslArgs -e wslpath -a $linuxSrc).Trim()
        $settingsWsl = (wsl.exe @wslArgs -e wslpath -a $settings).Trim()
        wsl.exe @wslArgs -e bash $scriptWsl $binWsl $settingsWsl $(if ($ResetCaptures) { '1' } else { '0' })
        if ($LASTEXITCODE -ne 0) { throw "WSL setup failed ($LASTEXITCODE)" }
    }
}

# ---- Next steps ----------------------------------------------------------------------------------
Step 'Done'
Write-Host @"

Next:
  1. Open a NEW terminal (so PATH is picked up) and check:  engram --version
  2. Windows session:  pwsh $root\spike\start-session.ps1
$(if ($Wsl) { "     WSL session:      wsl $(if ($Distro) { "-d $Distro " })-e bash -lc engram-spike-session   (or run engram-spike-session inside WSL)" })
  3. In Claude Code: accept the trust prompt, then work through the prompt table in
     spike\README.md section A5 (about 15 minutes). Then --continue / --fork-session / claude -p.
  4. Make fixtures:  see spike\README.md Part C.
"@
