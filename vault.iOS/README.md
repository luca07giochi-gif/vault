# Cassaforte iOS (bootstrap)

Questo progetto e separato da:

- `vault.UI` (desktop Windows)
- `vault.Web` (browser)

Obiettivo: generare un `.ipa` dedicato iOS senza impattare desktop/web.

## Compatibilita formato vault

`vault.iOS` usa direttamente `vault.Core` (stessa libreria condivisa da desktop/web),
quindi lettura/scrittura del formato restano coerenti tra tutte le edizioni.

## Stato attuale

- Progetto iOS standalone (`net8.0-ios17.0`)
- Riferimento a `vault.Core`
- UI minima di bootstrap
- Workflow GitHub Actions per build `.ipa` artifact

## Build locale

Richiede macOS + Xcode + workload iOS:

```bash
dotnet workload install ios
dotnet publish vault.iOS/vault.iOS.csproj -f net8.0-ios17.0 -c Release -r ios-arm64 -p:BuildIpa=true -p:EnableCodeSigning=false -p:CheckEolWorkloads=false -p:CheckEolTargetFramework=false
```

## Build da GitHub Actions

Workflow: `.github/workflows/build-ios-ipa.yml`

Genera artifact `vault-ios-ipa` contenente il file `.ipa`.

Nota CI: il workflow usa SDK `.NET 8.0.100` e `--skip-manifest-update`
per restare compatibile con la versione Xcode del runner GitHub.
Il repository include anche `global.json` per evitare che il runner usi SDK 10.x.
Inoltre il progetto iOS forza il link dei framework `Security` e `CoreFoundation`
e disattiva `PublishTrimmed` per evitare errori di linker nella CI.
