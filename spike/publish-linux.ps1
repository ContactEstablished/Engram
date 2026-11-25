<#
.SYNOPSIS
  Builds a linux-x64 `engram` for WSL from Windows, with no installs.

.DESCRIPTION
  Native AOT can't cross-compile from Windows to Linux, so this publishes a trimmed, self-contained,
  single-file ReadyToRun build instead. It's fine for capturing payloads; its cold start is slower
  than AOT, so treat WSL timings from it as indicative only. For true AOT timings, build inside WSL
  (needs the .NET SDK, clang and zlib1g-dev there; see spike/README.md).

  The restore lock file is redirected so the repo's packages.lock.json files stay unchanged.

  Output: artifacts/publish/Engram.Cli/release_linux-x64/engram
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    dotnet publish src/Engram.Cli -c Release -r linux-x64 --self-contained `
        -p:PublishAot=false -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:PublishTrimmed=true `
        "-p:NuGetLockFilePath=$root\artifacts\linux-x64.packages.lock.json"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    Write-Host "`nLinux binary: $root\artifacts\publish\Engram.Cli\release_linux-x64\engram"
}
finally {
    Pop-Location
}
