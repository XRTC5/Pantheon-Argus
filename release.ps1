<#
.SYNOPSIS
    Builds the two release zips into ./release. Does not publish them.

.DESCRIPTION
    argus-<version>-win-x64.zip             about 1 MB; needs the .NET 9 Desktop Runtime
    argus-<version>-win-x64-standalone.zip  one exe with .NET inside; needs nothing

    Standalone is single-file but NOT compressed: a compressed bundle is
    unpacked into memory at every start, which costs RAM for the life of
    the process. The zip does the compressing instead.

.EXAMPLE
    ./release.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src/Argus/Argus.csproj'
$out = Join-Path $root 'release'
$stage = Join-Path $out 'stage'

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

function Publish([string]$name, [string[]]$extra) {
    $dir = Join-Path $stage $name
    & dotnet publish $proj -c Release -r win-x64 -o $dir --nologo -v q `
        -p:Version=$Version -p:DebugType=none @extra
    if ($LASTEXITCODE -ne 0) { throw "publish $name failed ($LASTEXITCODE)" }
    Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') $dir
    $zip = Join-Path $out "$name.zip"
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    '{0,-40} {1,8:N1} MB' -f "$name.zip", ((Get-Item $zip).Length / 1MB)
}

Publish "argus-$Version-win-x64" @('--self-contained', 'false')
Publish "argus-$Version-win-x64-standalone" @('--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=false')
