using System;
using System.Collections.Generic;
using System.Globalization;

namespace vault.Core
{
    public static class VaultText
    {
        private static string _language = "it";

        private static readonly Dictionary<string, Dictionary<string, string>> _texts =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["core.item.folder"] = Lang("Cartella", "Folder", "Carpeta", "Dossier"),
                ["core.item.file"] = Lang("File", "File", "Archivo", "Fichier"),

                ["core.error.vaultAlreadyOpen"] = Lang("Vault gia aperto.", "Vault already open.", "Vault ya abierto.", "Coffre deja ouvert."),
                ["core.error.passwordWrong"] = Lang("Password errata.", "Wrong password.", "Contrasena incorrecta.", "Mot de passe incorrect."),
                ["core.error.unsupportedVaultVersion"] = Lang("Versione vault non supportata.", "Unsupported vault version.", "Version de vault no compatible.", "Version du coffre non prise en charge."),
                ["core.error.passwordOrCorrupted"] = Lang("Password errata o vault corrotto.", "Wrong password or corrupted vault.", "Contrasena incorrecta o vault dañado.", "Mot de passe incorrect ou coffre corrompu."),
                ["core.error.invalidMoveDestinationSelf"] = Lang("Destinazione non valida: una cartella non puo essere spostata dentro se stessa.", "Invalid destination: a folder cannot be moved into itself.", "Destino no valido: una carpeta no puede moverse dentro de si misma.", "Destination non valide : un dossier ne peut pas etre deplace dans lui-meme."),
                ["core.error.invalidName"] = Lang("Nome non valido.", "Invalid name.", "Nombre no valido.", "Nom non valide."),
                ["core.error.itemNotFound"] = Lang("Elemento non trovato nel vault.", "Item not found in vault.", "Elemento no encontrado en el vault.", "Element introuvable dans le coffre."),
                ["core.error.invalidDestinationPath"] = Lang("Percorso destinazione non valido.", "Invalid destination path.", "Ruta de destino no valida.", "Chemin de destination non valide."),
                ["core.error.fileNotFound"] = Lang("File non trovato nel vault.", "File not found in vault.", "Archivo no encontrado en el vault.", "Fichier introuvable dans le coffre."),
                ["core.error.selectedIsFolder"] = Lang("La voce selezionata e una cartella. Usa lo spostamento o esportazione manuale dei file.", "Selected entry is a folder. Use move or manual export for files.", "La entrada seleccionada es una carpeta. Usa mover o exportacion manual para archivos.", "L'entree selectionnee est un dossier. Utilisez le deplacement ou l'export manuel pour les fichiers."),
                ["core.error.invalidNewPassword"] = Lang("Nuova password non valida.", "Invalid new password.", "Nueva contrasena no valida.", "Nouveau mot de passe non valide."),
                ["core.error.unsupportedStorageFormat"] = Lang("Formato vault non supportato.", "Unsupported vault format.", "Formato de vault no compatible.", "Format de coffre non pris en charge."),
                ["core.error.ultraNotSupportedInWeb"] = Lang(
                    "Formato ultra non supportato nella versione web.",
                    "Ultra format is not supported in the web version.",
                    "El formato ultra no es compatible en la version web.",
                    "Le format ultra n'est pas pris en charge dans la version web."),
                ["core.error.legacySizeLimit"] = Lang(
                    "Formato legacy selezionato: dimensione totale oltre il limite (~2 GB). Crea un vault in formato esteso o ultra per superare questo limite.",
                    "Legacy format selected: total size exceeds the limit (~2 GB). Create an extended or ultra vault to go beyond this limit.",
                    "Formato legacy seleccionado: tamano total por encima del limite (~2 GB). Crea un vault extendido o ultra para superar este limite.",
                    "Format legacy selectionne : taille totale au-dessus de la limite (~2 Go). Creez un coffre etendu ou ultra pour depasser cette limite."),
                ["core.error.pathNotFound"] = Lang("Percorso non trovato.", "Path not found.", "Ruta no encontrada.", "Chemin introuvable."),
                ["core.error.destinationFolderMissing"] = Lang("Cartella destinazione non trovata nel vault.", "Destination folder not found in vault.", "Carpeta de destino no encontrada en el vault.", "Dossier de destination introuvable dans le coffre."),
                ["core.error.fileTooLargeForFormat"] = Lang(
                    "\"{0}\" supera il limite tecnico del formato corrente (circa 2 GB per singolo file).",
                    "\"{0}\" exceeds the technical limit of the current format (about 2 GB per file).",
                    "\"{0}\" supera el limite tecnico del formato actual (aprox 2 GB por archivo).",
                    "\"{0}\" depasse la limite technique du format actuel (environ 2 Go par fichier)."),
                ["core.error.invalidPath"] = Lang("Percorso non valido.", "Invalid path.", "Ruta no valida.", "Chemin non valide."),
                ["core.default.fileName"] = Lang("file", "file", "archivo", "fichier"),
                ["core.default.newFolder"] = Lang("Nuova cartella", "New folder", "Nueva carpeta", "Nouveau dossier"),
                ["core.default.fileBin"] = Lang("file.bin", "file.bin", "file.bin", "file.bin"),
                ["core.error.noVaultOpen"] = Lang("No vault open.", "No vault open.", "No hay vault abierto.", "Aucun coffre ouvert."),
                ["core.error.tooManyAttempts"] = Lang("Troppi tentativi falliti. Riprova tra qualche minuto.", "Too many failed attempts. Try again in a few minutes.", "Demasiados intentos fallidos. Intenta de nuevo en unos minutos.", "Trop de tentatives echouees. Reessayez dans quelques minutes."),

                ["core.format.fileTooShort"] = Lang("File troppo corto.", "File too short.", "Archivo demasiado corto.", "Fichier trop court."),
                ["core.format.headerIncomplete"] = Lang("Header incompleto.", "Incomplete header.", "Encabezado incompleto.", "En-tete incomplet."),
                ["core.format.magicInvalid"] = Lang("Il file scelto non sembra essere un vault valido.", "The selected file does not appear to be a valid vault.", "El archivo seleccionado no parece ser un vault valido.", "Le fichier selectionne ne semble pas etre un coffre valide."),
                ["core.format.versionUnsupported"] = Lang("Versione non supportata.", "Unsupported version.", "Version no compatible.", "Version non prise en charge."),
                ["core.format.streamingNotLegacy"] = Lang("Questo vault usa il formato streaming e non supporta la lettura payload legacy.", "This vault uses streaming format and does not support legacy payload reading.", "Este vault usa formato streaming y no admite lectura de payload legacy.", "Ce coffre utilise le format streaming et ne prend pas en charge la lecture du payload legacy."),
                ["core.format.vaultTooLargeLegacy"] = Lang("Il vault e troppo grande per il formato legacy.", "Vault is too large for legacy format.", "El vault es demasiado grande para formato legacy.", "Le coffre est trop volumineux pour le format legacy."),
                ["core.format.payloadMissing"] = Lang("Payload mancante.", "Missing payload.", "Payload faltante.", "Payload manquant."),
                ["core.format.headerNotStreaming"] = Lang("Header non compatibile con formato streaming.", "Header incompatible with streaming format.", "Encabezado no compatible con formato streaming.", "En-tete non compatible avec le format streaming."),
                ["core.format.targetNotWritable"] = Lang("Lo stream di destinazione non e scrivibile.", "Target stream is not writable.", "El stream de destino no es escribible.", "Le flux de destination n'est pas inscriptible."),
                ["core.format.chunkSizeInvalid"] = Lang("Chunk size non valido.", "Invalid chunk size.", "Tamano de chunk no valido.", "Taille de chunk non valide."),
                ["core.format.sourceNotReadable"] = Lang("Lo stream sorgente non e leggibile.", "Source stream is not readable.", "El stream de origen no es legible.", "Le flux source n'est pas lisible."),
                ["core.format.chunkLengthIncomplete"] = Lang("Payload streaming corrotto (lunghezza chunk incompleta).", "Corrupted streaming payload (incomplete chunk length).", "Payload streaming corrupto (longitud de chunk incompleta).", "Payload streaming corrompu (longueur de chunk incomplete)."),
                ["core.format.chunkLengthInvalid"] = Lang("Payload streaming corrotto (lunghezza chunk non valida).", "Corrupted streaming payload (invalid chunk length).", "Payload streaming corrupto (longitud de chunk no valida).", "Payload streaming corrompu (longueur de chunk invalide)."),
                ["core.format.encryptedChunkIncomplete"] = Lang("Payload streaming corrotto (chunk cifrato incompleto).", "Corrupted streaming payload (incomplete encrypted chunk).", "Payload streaming corrupto (chunk cifrado incompleto).", "Payload streaming corrompu (chunk chiffre incomplet)."),
                ["core.format.decryptedChunkLengthInvalid"] = Lang("Payload streaming corrotto (lunghezza chunk decifrato non valida).", "Corrupted streaming payload (invalid decrypted chunk length).", "Payload streaming corrupto (longitud de chunk descifrado no valida).", "Payload streaming corrompu (longueur du chunk dechiffre invalide)."),
                ["core.format.trailingData"] = Lang("Payload streaming corrotto (dati extra inattesi).", "Corrupted streaming payload (unexpected extra data).", "Payload streaming corrupto (datos extra inesperados).", "Payload streaming corrompu (donnees supplementaires inattendues)."),

                ["core.crypto.vaultTooLargeForEncryption"] = Lang("Vault troppo grande per la cifratura corrente (limite tecnico circa 2 GB).", "Vault too large for current encryption (technical limit about 2 GB).", "Vault demasiado grande para el cifrado actual (limite tecnico aprox 2 GB).", "Coffre trop volumineux pour le chiffrement actuel (limite technique environ 2 Go)."),
                ["core.crypto.invalidCiphertext"] = Lang("Ciphertext non valido.", "Invalid ciphertext.", "Ciphertext no valido.", "Ciphertext non valide."),

                ["core.serializer.vaultInvalid"] = Lang("Vault non valido.", "Invalid vault.", "Vault no valido.", "Coffre non valide."),
                ["core.serializer.corruptWrongPassword"] = Lang("Password errata o vault corrotto.", "Wrong password or corrupted vault.", "Contrasena incorrecta o vault dañado.", "Mot de passe incorrect ou coffre corrompu."),
                ["core.serializer.invalidItemCount"] = Lang("Vault corrotto (conteggio elementi non valido).", "Corrupted vault (invalid item count).", "Vault corrupto (conteo de elementos no valido).", "Coffre corrompu (nombre d'elements non valide)."),
                ["core.serializer.fileIdIncomplete"] = Lang("Vault corrotto (ID file incompleto).", "Corrupted vault (incomplete file ID).", "Vault corrupto (ID de archivo incompleto).", "Coffre corrompu (ID de fichier incomplet)."),
                ["core.serializer.fileLengthInvalid"] = Lang("Vault corrotto (lunghezza file non valida).", "Corrupted vault (invalid file length).", "Vault corrupto (longitud de archivo no valida).", "Coffre corrompu (longueur de fichier invalide)."),
                ["core.serializer.chunkCountInvalid"] = Lang("Vault corrotto (numero chunk non valido).", "Corrupted vault (invalid chunk count).", "Vault corrupto (numero de chunks no valido).", "Coffre corrompu (nombre de chunks non valide)."),
                ["core.serializer.chunkSizeInvalid"] = Lang("Vault corrotto (dimensione chunk non valida).", "Corrupted vault (invalid chunk size).", "Vault corrupto (tamano de chunk no valido).", "Coffre corrompu (taille de chunk invalide)."),
                ["core.serializer.chunkIncomplete"] = Lang("Vault corrotto (chunk incompleto).", "Corrupted vault (incomplete chunk).", "Vault corrupto (chunk incompleto).", "Coffre corrompu (chunk incomplet)."),
                ["core.serializer.contentLengthMismatch"] = Lang("Vault corrotto (lunghezza contenuto non coerente).", "Corrupted vault (inconsistent content length).", "Vault corrupto (longitud de contenido incoherente).", "Coffre corrompu (longueur du contenu incoherente)."),
                ["core.serializer.folderContentInvalid"] = Lang("Vault corrotto (cartella con contenuto non valido).", "Corrupted vault (folder with invalid content).", "Vault corrupto (carpeta con contenido no valido).", "Coffre corrompu (dossier avec contenu non valide)."),
                ["core.serializer.fileContentIncomplete"] = Lang("Vault corrotto (contenuto file incompleto).", "Corrupted vault (incomplete file content).", "Vault corrupto (contenido de archivo incompleto).", "Coffre corrompu (contenu fichier incomplet)."),
                ["core.serializer.legacyIdIncomplete"] = Lang("Vault corrotto (ID legacy incompleto).", "Corrupted vault (incomplete legacy ID).", "Vault corrupto (ID legacy incompleto).", "Coffre corrompu (ID legacy incomplet)."),
                ["core.serializer.legacyContentIncomplete"] = Lang("Vault corrotto (contenuto legacy incompleto).", "Corrupted vault (incomplete legacy content).", "Vault corrupto (contenido legacy incompleto).", "Coffre corrompu (contenu legacy incomplet)."),
                ["core.serializer.contentInconsistent"] = Lang("Contenuto file non coerente.", "Inconsistent file content.", "Contenido de archivo incoherente.", "Contenu du fichier incoherent."),
                ["core.serializer.trailingData"] = Lang("Vault corrotto (dati extra inattesi).", "Corrupted vault (unexpected extra data).", "Vault corrupto (datos extra inesperados).", "Coffre corrompu (donnees supplementaires inattendues).")
            };

        public static string CurrentLanguage => _language;

        public static void SetLanguage(string? languageCode)
        {
            string normalized = (languageCode ?? string.Empty).Trim().ToLowerInvariant();
            _language = normalized is "en" or "es" or "fr" ? normalized : "it";
        }

        public static string T(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            if (!_texts.TryGetValue(key, out Dictionary<string, string>? byLang))
                return key;

            if (byLang.TryGetValue(_language, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;

            if (byLang.TryGetValue("it", out string? fallback) && !string.IsNullOrWhiteSpace(fallback))
                return fallback;

            return key;
        }

        public static string F(string key, params object[] args)
        {
            string template = T(key);
            return args == null || args.Length == 0
                ? template
                : string.Format(CultureInfo.CurrentCulture, template, args);
        }

        private static Dictionary<string, string> Lang(string it, string en, string es, string fr) =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["it"] = it,
                ["en"] = en,
                ["es"] = es,
                ["fr"] = fr
            };
    }
}
