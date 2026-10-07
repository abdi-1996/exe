param(
    [string]$Configuration = "Release",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $projectRoot "publish"
$artifacts = Join-Path $projectRoot "artifacts"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish, $artifacts | Out-Null

dotnet publish (Join-Path $projectRoot "AIStudio.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $publish "AIStudio.exe"
if (!(Test-Path $exe)) { throw "AIStudio.exe was not produced" }

if (!$SkipInstaller) {
    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (!$iscc) { throw "Inno Setup 6 was not found. Install it or use -SkipInstaller." }
    & $iscc (Join-Path $projectRoot "installer\AIStudio.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
}

Get-ChildItem $publish, $artifacts -Recurse -File | Select-Object FullName, Length

