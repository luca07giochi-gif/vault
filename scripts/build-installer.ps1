param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "1.0.2",
    [string]$CertPath = "",
    [string]$CertPassword = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
$issPath = Join-Path $root "installer\cassaforte.iss"
if (-not (Test-Path $issPath)) {
    throw "File installer non trovato: $issPath"
}

$issContent = Get-Content -Raw $issPath
$newContent = [regex]::Replace(
    $issContent,
    '#define\s+AppVersion\s+"[^"]+"',
    ('#define AppVersion "{0}"' -f $Version))

if ($newContent -ne $issContent) {
    Set-Content -Path $issPath -Value $newContent
}

& $publishScript -Runtime $Runtime -Configuration $Configuration

$isccExe = $null
$cmd = Get-Command iscc -ErrorAction SilentlyContinue
if ($cmd) {
    $isccExe = $cmd.Source
}

if (-not $isccExe) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $isccExe = $candidate
            break
        }
    }
}

if (-not $isccExe) {
    Write-Host ""
    Write-Host "Inno Setup non trovato (comando 'iscc' assente)." -ForegroundColor Yellow
    Write-Host "Installa Inno Setup e rilancia questo script."
    Write-Host "Download: https://jrsoftware.org/isdl.php"
    exit 1
}

Write-Host "Creazione installer Inno Setup..."
Push-Location $root
try {
    & $isccExe $issPath
}
finally {
    Pop-Location
}

$outDir = Join-Path $root "artifacts\installer"
Write-Host "Installer creato in: $outDir"

if (-not [string]::IsNullOrWhiteSpace($CertPath)) {
    if ([string]::IsNullOrWhiteSpace($CertPassword)) {
        throw "Hai specificato CertPath ma non CertPassword."
    }

    $setupPath = Join-Path $outDir ("Cassaforte-Setup-{0}.exe" -f $Version)
    if (-not (Test-Path $setupPath)) {
        throw "Installer non trovato per la firma: $setupPath"
    }

    $signScript = Join-Path $PSScriptRoot "sign-installer.ps1"
    & $signScript `
      -FilePath $setupPath `
      -CertPath $CertPath `
      -CertPassword $CertPassword `
      -TimestampUrl $TimestampUrl
}
