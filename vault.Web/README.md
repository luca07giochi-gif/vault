# Cassaforte Web

Versione web separata dalla UI desktop (`vault.UI`).

## Stato attuale

- Apertura di vault esistenti da file `.vault`
- Password master
- Navigazione cartelle
- Download file dal vault
- Formati supportati: `legacy`, `extended`
- Formato `ultra`: bloccato nella versione web

## Cosa non fa (per ora)

- Creazione nuovi vault
- Modifica struttura/file del vault
- Cambio password/formato

## Avvio locale

```powershell
dotnet run --project vault.Web/vault.Web.csproj
```

## Publish statico

```powershell
dotnet publish vault.Web/vault.Web.csproj -c Release -o artifacts/publish/web
```

I file deployabili sono in:

`artifacts/publish/web/wwwroot`

## GitHub Pages

Workflow incluso:

`.github/workflows/deploy-web-pages.yml`

Dopo il push su `main`, GitHub Actions pubblica automaticamente la web app su Pages.
