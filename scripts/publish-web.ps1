param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "vault.Web\vault.Web.csproj"
$publishDir = Join-Path $root "artifacts\publish\web"

Write-Host "Pulizia output precedente: $publishDir"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

Write-Host "Publish web ($Configuration)..."
dotnet publish $project `
  -c $Configuration `
  -o $publishDir

Write-Host "Publish web completato in: $publishDir"
Write-Host "Contenuto deploy statico: $publishDir\wwwroot"
