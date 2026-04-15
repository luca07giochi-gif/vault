# Cassaforte iOS (bootstrap)

Questo progetto e separato da:

- `vault.UI` (desktop Windows)
- `vault.Web` (browser)

Obiettivo: generare un `.ipa` dedicato iOS senza impattare desktop/web.

## Compatibilita formato vault

`vault.iOS` usa direttamente `vault.Core` (stessa libreria condivisa da desktop/web),
quindi lettura/scrittura del formato restano coerenti tra tutte le edizioni.

## Stato attuale

- Progetto iOS standalone (`net8.0-ios16.4`)
- Riferimento a `vault.Core`
- UI minima di bootstrap
- Workflow GitHub Actions per build `.ipa` artifact

## Build locale

Richiede macOS + Xcode + workload iOS:

```bash
dotnet workload install ios
dotnet publish vault.iOS/vault.iOS.csproj -f net8.0-ios16.4 -c Release -r ios-arm64 -p:BuildIpa=true -p:EnableCodeSigning=false -p:CheckEolWorkloads=false -p:CheckEolTargetFramework=false
```

## Build da GitHub Actions

Workflow: `.github/workflows/build-ios-ipa.yml`

Genera artifact `vault-ios-ipa` contenente il file `.ipa`.
Il workflow mantiene `ApplicationDisplayVersion` dal progetto iOS e incrementa automaticamente
`ApplicationVersion` usando `github.run_number`, quindi nella home dell'app compare un build number
diverso a ogni IPA generata.

Nota CI: il workflow usa runner `macos-14`, SDK `.NET 8.0.100` e `--skip-manifest-update`
per restare compatibile con la versione Xcode del runner GitHub.
Il repository include anche `global.json` per evitare che il runner usi SDK 10.x.
Il linking di `Security` e `CoreFoundation` avviene via `MtouchExtraArgs` (senza `NativeReference` assoluti).
Il file `ios-publish-binlog` e binario: per leggerlo usa MSBuild Structured Log Viewer.
