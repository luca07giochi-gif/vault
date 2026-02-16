# Creare l'app Windows installabile (Cassaforte)

## 1) Build Release dell'app

Da PowerShell, nella root del progetto:

```powershell
.\scripts\publish-release.ps1
```

Output:

`artifacts\publish\win-x64`

Questa e una build Windows self-contained pronta da distribuire.

## 2) Creare un vero installer (.exe - Inno Setup)

Il progetto include gia uno script Inno Setup:

- `installer\cassaforte.iss`
- `scripts\build-installer.ps1`

Per generare l'installer:

```powershell
.\scripts\build-installer.ps1
```

Se manca Inno Setup, installalo da:

https://jrsoftware.org/isdl.php

Poi rilancia lo script.

Output installer:

`artifacts\installer\Cassaforte-Setup-1.0.2.exe`

## 3) Firmare digitalmente l'installer (consigliato)

Script disponibile:

`scripts\sign-installer.ps1`

Esempio firma setup `.exe` con certificato `.pfx`:

```powershell
.\scripts\sign-installer.ps1 `
  -FilePath .\artifacts\installer\Cassaforte-Setup-1.0.2.exe `
  -CertPath C:\cert\codesign.pfx `
  -CertPassword "PASSWORD_CERTIFICATO"
```

In alternativa puoi firmare direttamente durante la build Inno:

```powershell
.\scripts\build-installer.ps1 `
  -Version 1.0.2 `
  -CertPath C:\cert\codesign.pfx `
  -CertPassword "PASSWORD_CERTIFICATO"
```

Nota SmartScreen: la firma riduce gli avvisi, ma la reputazione SmartScreen migliora nel tempo o con certificato EV.

## 4) MSIX (opzionale)

Per MSIX il modo piu semplice e usare **MSIX Packaging Tool**:

```powershell
winget install --id Microsoft.MSIXPackagingTool -e --silent
```

Poi:

1. Avvia `MSIX Packaging Tool`
2. Crea nuovo package da installer (`Cassaforte-Setup-1.0.2.exe`)
3. Completa wizard (nome package, publisher, versione)
4. Firma il `.msix` con certificato code-sign

Questo approccio e piu affidabile per desktop app WPF rispetto a una configurazione MSIX manuale da zero.

## 5) Cosa fa l'installer

- Installa in `Program Files\Cassaforte`
- Crea collegamento nel menu Start
- Opzionalmente crea icona desktop
- Registra associazione file `.vault` -> Cassaforte (doppio click)
- Supporta disinstallazione standard da Windows
