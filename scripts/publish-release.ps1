param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "vault.UI\vault.UI.csproj"
$publishDir = Join-Path $root "artifacts\publish\$Runtime"

Write-Host "Pulizia output precedente: $publishDir"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

Write-Host "Publish self-contained ($Runtime, $Configuration)..."
dotnet publish $project `
  -c $Configuration `
  -r $Runtime `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:PublishTrimmed=false `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:DebugType=None `
  /p:DebugSymbols=false `
  -o $publishDir

Write-Host "Publish completato in: $publishDir"
