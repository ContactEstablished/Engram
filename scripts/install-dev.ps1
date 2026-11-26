<#
.SYNOPSIS
  Builds and installs Engram from this repo for dogfooding (Windows, and WSL with -Wsl).

.DESCRIPTION
    1. Publishes engram.exe (Native AOT) and engram-worker.exe (self-contained single file).
    2. Stops a running worker, copies both to %LOCALAPPDATA%\Engram\bin, adds that folder to your USER PATH.
    3. Runs `engram install`: ~/.engram, default settings, the worker token, and the ENGRAM_TOKEN user variable.
    4. Puts hooks/settings.local.json into the sandbox repo's .claude/settings.local.json, and hooks/mcp.json
       into its .mcp.json (the engram MCP server; Claude Code asks you to approve it once).
    5. With -Wsl: the same inside WSL (~/.local/bin, ~/.engram, ~/.profile, ~/src/engram-sandbox).

  Never touches ~/.claude/settings.json. Asks before changing PATH, ENGRAM_TOKEN and ~/.profile unless -Yes.
  Safe to re-run; that's how you update.

.PARAMETER Yes        Don't ask for confirmation.
.PARAMETER SkipBuild  Reuse the binaries already under artifacts/.
.PARAMETER Wsl        Also install into WSL.
.PARAMETER Distro     WSL distro name (default: your default distro).
.PARAMETER Sandbox    Windows sandbox repo (default %USERPROFILE%\src\engram-sandbox).
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
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$bin = Join-Path $env:LOCALAPPDATA 'Engram\bin'
$pub = Join-Path $root 'artifacts\publish'
$winCli = Join-Path $pub 'Engram.Cli\release_win-x64\engram.exe'
$winWorker = Join-Path $pub 'Engram.Worker\release_win-x64\engram-worker.exe'
$linuxCli = Join-Path $pub 'Engram.Cli\release_linux-x64\engram'
$linuxWorker = Join-Path $pub 'Engram.Worker\release_linux-x64\engram-worker'
$hooks = Join-Path $root 'hooks\settings.local.json'
$mcpJson = Join-Path $root 'hooks\mcp.json'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Invoke-Step([string]$Description, [scriptblock]$Action) {
    if ($DryRun) { Write-Host "[dry-run] $Description" -ForegroundColor DarkGray }
    else { Write-Host "  $Description"; & $Action }
}

function Confirm-Change([string]$Question) {
    if ($Yes -or $DryRun) { return $true }
    return (Read-Host "$Question [y/N]") -match '^(y|yes)$'
}

function Invoke-Native([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed ($LASTEXITCODE)" }
}

Write-Host "Engram dev install"
Write-Host "  repo:     $root"
Write-Host "  binaries: $bin  (added to your user PATH)"
Write-Host "  sandbox:  $Sandbox  (hooks go in .claude\settings.local.json)"
if ($Wsl) { Write-Host "  WSL:      ~/.local/bin, ~/.engram, ~/src/engram-sandbox  (distro: $(if ($Distro) { $Distro } else { 'default' }))" }
if ($DryRun) { Write-Host '  (dry run: nothing will change)' -ForegroundColor Yellow }

# ---- Build ---------------------------------------------------------------------------------------
if (-not $SkipBuild) {
    Step 'Build'
    Invoke-Step 'publish engram.exe (Native AOT, win-x64)' {
        Invoke-Native 'publish engram' { dotnet publish (Join-Path $root 'src\Engram.Cli') -c Release -r win-x64 --nologo -v q }
    }
    Invoke-Step 'publish engram-worker.exe (self-contained, win-x64)' {
        Invoke-Native 'publish engram-worker' { dotnet publish (Join-Path $root 'src\Engram.Worker') -c Release -r win-x64 --self-contained --nologo -v q }
    }
    if ($Wsl) {
        Invoke-Step 'publish engram (linux-x64, single file; AOT needs a Linux build machine)' {
            Invoke-Native 'publish engram linux' {
                dotnet publish (Join-Path $root 'src\Engram.Cli') -c Release -r linux-x64 --self-contained --nologo -v q `
                    -p:PublishAot=false -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:PublishTrimmed=true `
                    "-p:NuGetLockFilePath=$root\artifacts\linux-x64.packages.lock.json"
            }
        }
        Invoke-Step 'publish engram-worker (self-contained, linux-x64)' {
            Invoke-Native 'publish engram-worker linux' { dotnet publish (Join-Path $root 'src\Engram.Worker') -c Release -r linux-x64 --self-contained --nologo -v q }
        }
    }
}

# ---- Windows -------------------------------------------------------------------------------------
Step 'Install on Windows'
if (-not (Confirm-Change "Copy engram.exe and engram-worker.exe to $bin and add it to your user PATH?")) {
    Write-Host 'Cancelled. Nothing changed.'
    return
}

$existing = Join-Path $bin 'engram.exe'
if (Test-Path $existing) {
    Invoke-Step 'stop the running worker (it locks its exe)' {
        & $existing worker stop 2>&1 | Out-Null   # older builds don't know this command; that's fine
        Get-Process engram-worker -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq (Join-Path $bin 'engram-worker.exe') } |
            ForEach-Object { $_.WaitForExit(10000) | Out-Null }
    }
}

Invoke-Step "copy binaries to $bin" {
    New-Item -ItemType Directory -Force $bin | Out-Null
    Copy-Item $winCli, $winWorker $bin -Force
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -contains $bin) {
    Write-Host '  user PATH already contains it'
}
else {
    Invoke-Step 'add to user PATH' {
        [Environment]::SetEnvironmentVariable('Path', ($(if ($userPath) { "$userPath;" }) + $bin), 'User')
    }
}

Invoke-Step 'engram install (settings, token, ENGRAM_TOKEN)' {
    $installArgs = @('install') + $(if ($Yes) { @('--yes') } else { @() })
    Invoke-Native 'engram install' { & (Join-Path $bin 'engram.exe') @installArgs }
}

Invoke-Step "check Ollama for semantic search (nomic-embed-text)" {
    $ollama = Get-Command ollama -ErrorAction SilentlyContinue
    if (-not $ollama) {
        Write-Host "  Ollama not found: search stays keyword-only. Install it from https://ollama.com, then: ollama pull nomic-embed-text" -ForegroundColor Yellow
    }
    elseif (-not ((& $ollama.Source list 2>$null) -match '^nomic-embed-text')) {
        Write-Host "  Run: ollama pull nomic-embed-text  (search stays keyword-only until the model is there)" -ForegroundColor Yellow
    }
    else {
        Write-Host "  Ollama has nomic-embed-text"
    }
}

Invoke-Step "hooks into $Sandbox\.claude\settings.local.json, MCP server into $Sandbox\.mcp.json" {
    New-Item -ItemType Directory -Force (Join-Path $Sandbox '.claude') | Out-Null
    Copy-Item $hooks (Join-Path $Sandbox '.claude\settings.local.json') -Force
    Copy-Item $mcpJson (Join-Path $Sandbox '.mcp.json') -Force
    if (-not (Test-Path (Join-Path $Sandbox '.git'))) {
        'hello from the sandbox' | Set-Content (Join-Path $Sandbox 'notes.txt')
        git -C $Sandbox init -q
        git -C $Sandbox add notes.txt
        git -C $Sandbox -c user.name=engram -c user.email=engram@localhost commit -qm init
    }
}

# ---- WSL -----------------------------------------------------------------------------------------
if ($Wsl) {
    Step 'Install in WSL'
    $wslArgs = if ($Distro) { @('-d', $Distro) } else { @() }
    $sh = @'
set -e
cli="$1"; worker="$2"; hooks="$3"; yes="$4"; mcp="$5"
# WSL forwards its localhost ports to Windows, so the WSL worker can't share the Windows worker's port.
port=47614
bin="$HOME/.local/bin"
mkdir -p "$bin"
# Phase 0 leftovers: the spike listener holds the port and the old binary.
pkill -f "engram spike listen" 2>/dev/null || true
if [ -x "$bin/engram" ]; then ENGRAM_PORT=$port "$bin/engram" worker stop >/dev/null 2>&1 || true; sleep 1; fi
# Copy then rename: replacing a running binary in place fails with "Text file busy".
for pair in "$cli:engram" "$worker:engram-worker"; do
  src="${pair%%:*}"; name="${pair##*:}"
  cp "$src" "$bin/.$name.new" && chmod +x "$bin/.$name.new" && mv -f "$bin/.$name.new" "$bin/$name"
done
if [ "$yes" = "1" ]; then "$bin/engram" install --yes; else "$bin/engram" install; fi
settings="$HOME/.engram/settings.json"
sed -i -E "s/\"port\": *[0-9]+/\"port\": $port/" "$settings"
grep -q "\"port\": $port" "$settings" || { echo "could not set worker.port=$port in $settings"; exit 1; }
sandbox="$HOME/src/engram-sandbox"
mkdir -p "$sandbox/.claude"
sed "s/127.0.0.1:47613/127.0.0.1:$port/g" "$hooks" > "$sandbox/.claude/settings.local.json"
cp "$mcp" "$sandbox/.mcp.json"
if [ ! -d "$sandbox/.git" ]; then
  cd "$sandbox"
  git init -q
  echo "hello from the sandbox" > notes.txt
  git add notes.txt
  git -c user.name=engram -c user.email=engram@localhost commit -qm init
fi
echo "WSL: engram $("$bin/engram" --version) installed; worker port $port; hooks in $sandbox/.claude/settings.local.json"
# Semantic search needs Ollama inside WSL; installing it needs sudo, so that is left to you.
if command -v ollama >/dev/null 2>&1; then
  ollama list 2>/dev/null | grep -q '^nomic-embed-text' && echo "WSL: Ollama has nomic-embed-text" || echo "WSL: run  ollama pull nomic-embed-text  to turn on semantic search"
else
  echo "WSL: Ollama is not installed; search stays keyword-only. To add it: curl -fsSL https://ollama.com/install.sh | sh  then  ollama pull nomic-embed-text"
fi
'@
    Invoke-Step 'copy binaries, run engram install, set up the sandbox inside WSL' {
        $scriptWin = Join-Path $root 'artifacts\wsl-install-dev.sh'
        [IO.File]::WriteAllText($scriptWin, ($sh -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        $toWsl = { param($p) (wsl.exe @wslArgs -e wslpath -a $p).Trim() }
        wsl.exe @wslArgs -e bash (& $toWsl $scriptWin) (& $toWsl $linuxCli) (& $toWsl $linuxWorker) (& $toWsl $hooks) $(if ($Yes) { '1' } else { '0' }) (& $toWsl $mcpJson)
        if ($LASTEXITCODE -ne 0) { throw "WSL install failed ($LASTEXITCODE)" }
    }
}

Step 'Done'
Write-Host @"

Next:
  1. Open a NEW terminal (PATH and ENGRAM_TOKEN are read at start), then:  engram status
  2. cd $Sandbox ; claude      (accept the trust prompt; /hooks lists Engram's hooks)
  3. Work normally for a few turns, exit, then run  engram status  again: sessions, prompts, events and jobs should grow.
$(if ($Wsl) { "  WSL: open a new WSL login shell, then the same in ~/src/engram-sandbox." })
"@
