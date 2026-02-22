# Cassaforte iOS (bootstrap)

Questo progetto e separato da:

- `vault.UI` (desktop Windows)
- `vault.Web` (browser)

Obiettivo: generare un `.ipa` dedicato iOS senza impattare desktop/web.

## Compatibilita formato vault

`vault.iOS` usa direttamente `vault.Core` (stessa libreria condivisa da desktop/web),
quindi lettura/scrittura del formato restano coerenti tra tutte le edizioni.

## Stato attuale

- Progetto iOS standalone (`net9.0-ios`)
- Riferimento a `vault.Core`
- UI minima di bootstrap
- Workflow GitHub Actions per build `.ipa` artifact

## Build locale

Richiede macOS + Xcode + workload iOS:

```bash
dotnet workload install ios
dotnet publish vault.iOS/vault.iOS.csproj -f net9.0-ios -c Release -r ios-arm64 -p:BuildIpa=true -p:EnableCodeSigning=false
```

## Build da GitHub Actions

Workflow: `.github/workflows/build-ios-ipa.yml`

Genera artifact `vault-ios-ipa` contenente il file `.ipa`.
