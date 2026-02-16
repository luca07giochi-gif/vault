param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,
    [Parameter(Mandatory = $true)]
    [string]$CertPath,
    [Parameter(Mandatory = $true)]
    [string]$CertPassword,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$InstallSdkIfMissing
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $FilePath)) {
    throw "File da firmare non trovato: $FilePath"
}

if (-not (Test-Path $CertPath)) {
    throw "Certificato PFX non trovato: $CertPath"
}

function Find-SignTool {
    $cmd = Get-Command signtool -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $kitsPath = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $kitsPath) {
        $candidate = Get-ChildItem $kitsPath -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1

        if ($candidate) {
            return $candidate
        }
    }

    return $null
}

$signTool = Find-SignTool
if (-not $signTool -and $InstallSdkIfMissing) {
    Write-Host "SignTool non trovato. Provo a installare Windows SDK..." -ForegroundColor Yellow
    winget install --id Microsoft.WindowsSDK.10.0.18362 -e --silent --accept-package-agreements --accept-source-agreements
    $signTool = Find-SignTool
}

if (-not $signTool) {
    Write-Host "SignTool non disponibile." -ForegroundColor Yellow
    Write-Host "Installa Windows SDK e rilancia:"
    Write-Host "winget install --id Microsoft.WindowsSDK.10.0.18362 -e --silent"
    exit 1
}

Write-Host "Firma digitale: $FilePath"
& $signTool sign `
  /fd SHA256 `
  /f $CertPath `
  /p $CertPassword `
  /tr $TimestampUrl `
  /td SHA256 `
  $FilePath

Write-Host "Firma completata."
