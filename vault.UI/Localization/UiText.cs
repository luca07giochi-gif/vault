using System;
using System.Collections.Generic;
using System.Globalization;

namespace vault.UI.Localization
{
    public static class UiText
    {
        private static string _language = "it";

        private static readonly Dictionary<string, Dictionary<string, string>> _texts =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["lang.it"] = Lang("Italiano", "Italian", "Italiano", "Italien"),
                ["lang.en"] = Lang("Inglese", "English", "Ingles", "Anglais"),
                ["lang.es"] = Lang("Spagnolo", "Spanish", "Espanol", "Espagnol"),
                ["lang.fr"] = Lang("Francese", "French", "Frances", "Francais"),

                ["common.error"] = Lang("Errore", "Error", "Error", "Erreur"),
                ["common.cancel"] = Lang("Annulla", "Cancel", "Cancelar", "Annuler"),
                ["common.continue"] = Lang("Continua", "Continue", "Continuar", "Continuer"),
                ["common.confirm"] = Lang("Conferma", "Confirm", "Confirmar", "Confirmer"),
                ["common.apply"] = Lang("Applica", "Apply", "Aplicar", "Appliquer"),

                ["format.select"] = Lang("Seleziona", "Select", "Selecciona", "Selectionner"),
                ["format.legacy"] = Lang(
                    "Compatibile (legacy, max circa 2 GB totali)",
                    "Compatible (legacy, max about 2 GB total)",
                    "Compatible (legacy, max aprox 2 GB totales)",
                    "Compatible (legacy, max environ 2 Go au total)"),
                ["format.extended"] = Lang(
                    "Esteso (streaming, oltre 2 GB totali)",
                    "Extended (streaming, over 2 GB total)",
                    "Extendido (streaming, mas de 2 GB totales)",
                    "Etendu (streaming, plus de 2 Go au total)"),
                ["format.ultra"] = Lang(
                    "Ultra (file singolo oltre 2 GB)",
                    "Ultra (single file over 2 GB)",
                    "Ultra (archivo unico de mas de 2 GB)",
                    "Ultra (fichier unique de plus de 2 Go)"),
                ["format.note"] = Lang(
                    "Nota: legacy ed esteso limitano il singolo file a circa 2 GB; ultra supporta file singoli oltre 2 GB.",
                    "Note: legacy and extended limit a single file to about 2 GB; ultra supports single files over 2 GB.",
                    "Nota: legacy y extendido limitan el archivo unico a aprox 2 GB; ultra admite archivos unicos de mas de 2 GB.",
                    "Note: legacy et etendu limitent un fichier unique a environ 2 Go; ultra prend en charge des fichiers uniques de plus de 2 Go."),
                ["format.short.legacy"] = Lang("legacy", "legacy", "legacy", "legacy"),
                ["format.short.extended"] = Lang("esteso", "extended", "extendido", "etendu"),
                ["format.short.ultra"] = Lang("ultra", "ultra", "ultra", "ultra"),

                ["main.windowTitle"] = Lang("Vault", "Vault", "Boveda", "Coffre"),
                ["main.title"] = Lang("Virtual Vault", "Virtual Vault", "Boveda Virtual", "Coffre Virtuel"),
                ["main.languageLabel"] = Lang("Lingua", "Language", "Idioma", "Langue"),
                ["main.group.create"] = Lang("Crea nuovo vault", "Create new vault", "Crear nueva boveda", "Creer un nouveau coffre"),
                ["main.group.open"] = Lang("Apri vault esistente", "Open existing vault", "Abrir boveda existente", "Ouvrir un coffre existant"),
                ["main.label.masterPassword"] = Lang("Password master", "Master password", "Contrasena maestra", "Mot de passe maitre"),
                ["main.label.confirmPassword"] = Lang("Conferma password", "Confirm password", "Confirmar contrasena", "Confirmer le mot de passe"),
                ["main.label.vaultFormat"] = Lang("Formato vault", "Vault format", "Formato de boveda", "Format du coffre"),
                ["main.button.createVault"] = Lang("Crea vault", "Create vault", "Crear boveda", "Creer coffre"),
                ["main.label.vaultFile"] = Lang("File vault", "Vault file", "Archivo vault", "Fichier vault"),
                ["main.button.browse"] = Lang("Sfoglia", "Browse", "Examinar", "Parcourir"),
                ["main.button.openVault"] = Lang("Apri vault", "Open vault", "Abrir boveda", "Ouvrir coffre"),
                ["main.quickOpenTitle"] = Lang("Apri vault selezionato", "Open selected vault", "Abrir boveda seleccionada", "Ouvrir le coffre selectionne"),
                ["main.label.file"] = Lang("File", "File", "Archivo", "Fichier"),
                ["main.button.showHome"] = Lang("Mostra home completa", "Show full home", "Mostrar inicio completo", "Afficher l'accueil complet"),
                ["main.tooltip.folderUp"] = Lang("Cartella superiore", "Upper folder", "Carpeta superior", "Dossier parent"),
                ["main.group.folderContent"] = Lang("Contenuto cartella", "Folder content", "Contenido de carpeta", "Contenu du dossier"),
                ["main.label.sort"] = Lang("Ordina", "Sort", "Ordenar", "Trier"),
                ["main.sort.name"] = Lang("Nome", "Name", "Nombre", "Nom"),
                ["main.sort.date"] = Lang("Data", "Date", "Fecha", "Date"),
                ["main.sort.size"] = Lang("Dimensione", "Size", "Tamano", "Taille"),
                ["main.sort.type"] = Lang("Tipo file", "File type", "Tipo de archivo", "Type de fichier"),
                ["main.sort.directionAsc"] = Lang("A-Z", "A-Z", "A-Z", "A-Z"),
                ["main.sort.directionDesc"] = Lang("Z-A", "Z-A", "Z-A", "Z-A"),
                ["main.sort.nameAsc"] = Lang("Nome A-Z", "Name A-Z", "Nombre A-Z", "Nom A-Z"),
                ["main.sort.nameDesc"] = Lang("Nome Z-A", "Name Z-A", "Nombre Z-A", "Nom Z-A"),
                ["main.sort.dateDesc"] = Lang("Data piu recente", "Newest date", "Fecha mas reciente", "Date la plus recente"),
                ["main.sort.dateAsc"] = Lang("Data piu vecchia", "Oldest date", "Fecha mas antigua", "Date la plus ancienne"),
                ["main.sort.sizeDesc"] = Lang("Dimensione maggiore", "Largest size", "Tamano mayor", "Taille la plus grande"),
                ["main.sort.sizeAsc"] = Lang("Dimensione minore", "Smallest size", "Tamano menor", "Taille la plus petite"),
                ["main.sort.typeAsc"] = Lang("Tipo file", "File type", "Tipo de archivo", "Type de fichier"),
                ["main.label.viewMode"] = Lang("Vista", "View", "Vista", "Affichage"),
                ["main.viewMode.list"] = Lang("Lista", "List", "Lista", "Liste"),
                ["main.viewMode.thumb"] = Lang("Anteprime", "Thumbnails", "Miniaturas", "Vignettes"),
                ["main.ctx.open"] = Lang("Apri", "Open", "Abrir", "Ouvrir"),
                ["main.ctx.export"] = Lang("Esporta", "Export", "Exportar", "Exporter"),
                ["main.ctx.rename"] = Lang("Rinomina", "Rename", "Renombrar", "Renommer"),
                ["main.ctx.move"] = Lang("Sposta", "Move", "Mover", "Deplacer"),
                ["main.ctx.delete"] = Lang("Elimina", "Delete", "Eliminar", "Supprimer"),
                ["main.ctx.addFile"] = Lang("Aggiungi file...", "Add file...", "Agregar archivo...", "Ajouter un fichier..."),
                ["main.ctx.newFolder"] = Lang("Nuova cartella", "New folder", "Nueva carpeta", "Nouveau dossier"),
                ["main.ctx.refresh"] = Lang("Aggiorna", "Refresh", "Actualizar", "Actualiser"),
                ["main.col.name"] = Lang("Nome", "Name", "Nombre", "Nom"),
                ["main.col.type"] = Lang("Tipo", "Type", "Tipo", "Type"),
                ["main.col.size"] = Lang("Dimensione", "Size", "Tamano", "Taille"),
                ["main.col.added"] = Lang("Aggiunto", "Added", "Agregado", "Ajoute"),
                ["main.dragHint"] = Lang(
                    "Suggerimento: trascina file esterni qui per aggiungerli. Trascina file/cartelle del vault su una cartella per spostarli.",
                    "Tip: drag external files here to add them. Drag vault files/folders onto a folder to move them.",
                    "Consejo: arrastra archivos externos aqui para agregarlos. Arrastra archivos/carpetas del vault sobre una carpeta para moverlos.",
                    "Astuce: glissez des fichiers externes ici pour les ajouter. Glissez des fichiers/dossiers du coffre sur un dossier pour les deplacer."),
                ["main.button.addFile"] = Lang("Aggiungi file", "Add file", "Agregar archivo", "Ajouter fichier"),
                ["main.button.newFolder"] = Lang("Nuova cartella", "New folder", "Nueva carpeta", "Nouveau dossier"),
                ["main.button.move"] = Lang("Sposta", "Move", "Mover", "Deplacer"),
                ["main.button.moveAll"] = Lang("Sposta tutti", "Move all", "Mover todo", "Tout deplacer"),
                ["main.button.rename"] = Lang("Rinomina", "Rename", "Renombrar", "Renommer"),
                ["main.button.remove"] = Lang("Rimuovi", "Remove", "Quitar", "Retirer"),
                ["main.button.export"] = Lang("Esporta", "Export", "Exportar", "Exporter"),
                ["main.button.open"] = Lang("Apri", "Open", "Abrir", "Ouvrir"),
                ["main.button.settings"] = Lang("Impostazioni vault", "Vault settings", "Configuracion de boveda", "Parametres du coffre"),
                ["main.button.closeVault"] = Lang("Chiudi vault", "Close vault", "Cerrar boveda", "Fermer coffre"),

                ["main.msg.startupVaultMissing"] = Lang(
                    "Il file vault passato all'avvio non esiste piu:\n{0}",
                    "The vault file passed at startup no longer exists:\n{0}",
                    "El archivo vault pasado al inicio ya no existe:\n{0}",
                    "Le fichier vault passe au demarrage n'existe plus:\n{0}"),
                ["main.title.fileNotFound"] = Lang("File non trovato", "File not found", "Archivo no encontrado", "Fichier introuvable"),
                ["main.dialog.vaultFilter"] = Lang("File vault (*.vault)|*.vault", "Vault files (*.vault)|*.vault", "Archivos vault (*.vault)|*.vault", "Fichiers vault (*.vault)|*.vault"),
                ["main.dialog.vaultSelectTitle"] = Lang("Seleziona file vault (.vault)", "Select vault file (.vault)", "Selecciona archivo vault (.vault)", "Selectionner un fichier vault (.vault)"),
                ["main.msg.selectVaultExtension"] = Lang("Seleziona un file con estensione .vault", "Select a file with .vault extension", "Selecciona un archivo con extension .vault", "Selectionnez un fichier avec extension .vault"),
                ["main.title.invalidFormat"] = Lang("Formato non valido", "Invalid format", "Formato no valido", "Format non valide"),
                ["main.msg.enterCreatePassword"] = Lang("Inserisci una password master per il nuovo vault.", "Enter a master password for the new vault.", "Introduce una contrasena maestra para la nueva boveda.", "Entrez un mot de passe maitre pour le nouveau coffre."),
                ["main.msg.passwordMismatch"] = Lang("Le password non coincidono.", "Passwords do not match.", "Las contrasenas no coinciden.", "Les mots de passe ne correspondent pas."),
                ["main.dialog.defaultVaultName"] = Lang("vault.vault", "vault.vault", "vault.vault", "vault.vault"),
                ["main.msg.selectFormat"] = Lang("Seleziona prima un formato vault.", "Select a vault format first.", "Selecciona primero un formato de vault.", "Selectionnez d'abord un format de coffre."),
                ["main.progress.creatingVault"] = Lang("Creazione vault in corso...", "Creating vault...", "Creando vault...", "Creation du coffre..."),
                ["main.msg.vaultCreated"] = Lang("Vault creato e aperto con successo (formato {0}).", "Vault created and opened successfully ({0} format).", "Boveda creada y abierta con exito (formato {0}).", "Coffre cree et ouvert avec succes (format {0})."),
                ["main.msg.errorCreating"] = Lang("Errore durante la creazione: {0}", "Error while creating: {0}", "Error durante la creacion: {0}", "Erreur pendant la creation : {0}"),
                ["main.msg.selectVaultToOpen"] = Lang("Seleziona prima un file vault da aprire.", "Select a vault file to open first.", "Selecciona primero un archivo vault para abrir.", "Selectionnez d'abord un fichier vault a ouvrir."),
                ["main.msg.selectedFileMissing"] = Lang("Il file selezionato non esiste.", "Selected file does not exist.", "El archivo seleccionado no existe.", "Le fichier selectionne n'existe pas."),
                ["main.msg.selectedFileInvalid"] = Lang("Il file selezionato non e un file .vault valido.", "Selected file is not a valid .vault file.", "El archivo seleccionado no es un archivo .vault valido.", "Le fichier selectionne n'est pas un fichier .vault valide."),
                ["main.msg.enterOpenPassword"] = Lang("Inserisci la password master per aprire il vault.", "Enter the master password to open the vault.", "Introduce la contrasena maestra para abrir la boveda.", "Entrez le mot de passe maitre pour ouvrir le coffre."),
                ["main.progress.openingVault"] = Lang("Apertura vault in corso...", "Opening vault...", "Abriendo vault...", "Ouverture du coffre..."),
                ["main.msg.vaultOpened"] = Lang("Vault aperto con successo!", "Vault opened successfully!", "Boveda abierta con exito!", "Coffre ouvert avec succes !"),
                ["main.msg.remainingAttempts"] = Lang("{0}\nTentativi residui: {1}", "{0}\nRemaining attempts: {1}", "{0}\nIntentos restantes: {1}", "{0}\nTentatives restantes : {1}"),
                ["main.msg.unexpectedError"] = Lang("Errore imprevisto: {0}", "Unexpected error: {0}", "Error inesperado: {0}", "Erreur inattendue : {0}"),
                ["main.dialog.allFilesFilter"] = Lang("Tutti i file (*.*)|*.*", "All files (*.*)|*.*", "Todos los archivos (*.*)|*.*", "Tous les fichiers (*.*)|*.*"),
                ["main.progress.addingFiles"] = Lang("Aggiunta file al vault in corso...", "Adding files to vault...", "Agregando archivos al vault...", "Ajout des fichiers au coffre..."),
                ["main.progress.moving"] = Lang("Spostamento elementi in corso...", "Moving items...", "Moviendo elementos...", "Deplacement des elements..."),
                ["main.progress.loadingThumbnails"] = Lang("Caricamento anteprime in corso...", "Loading thumbnails...", "Cargando miniaturas...", "Chargement des vignettes..."),
                ["main.msg.itemsAdded"] = Lang("Elementi aggiunti: {0}", "Items added: {0}", "Elementos agregados: {0}", "Elements ajoutes : {0}"),
                ["main.msg.errorAddingFiles"] = Lang("Errore durante l'aggiunta file: {0}", "Error while adding files: {0}", "Error al agregar archivos: {0}", "Erreur pendant l'ajout des fichiers : {0}"),
                ["main.title.thumbnailManyItems"] = Lang("Anteprime: cartella grande", "Thumbnails: large folder", "Miniaturas: carpeta grande", "Vignettes : dossier volumineux"),
                ["main.msg.thumbnailManyItemsWarning"] = Lang(
                    "Questa cartella contiene {0} elementi.\nCaricare tutte le anteprime puo rallentare o bloccare temporaneamente l'app.\n\nVuoi comunque caricare le anteprime complete? (No = solo icone, piu veloce)",
                    "This folder contains {0} items.\nLoading all thumbnails may slow down or temporarily freeze the app.\n\nDo you still want to load full thumbnails? (No = icons only, faster)",
                    "Esta carpeta contiene {0} elementos.\nCargar todas las miniaturas puede ralentizar o bloquear temporalmente la app.\n\nQuieres cargar igualmente las miniaturas completas? (No = solo iconos, mas rapido)",
                    "Ce dossier contient {0} elements.\nCharger toutes les vignettes peut ralentir ou bloquer temporairement l'application.\n\nVoulez-vous quand meme charger les vignettes completes ? (Non = icones seulement, plus rapide)"),
                ["main.prompt.folderName"] = Lang("Nome della cartella", "Folder name", "Nombre de carpeta", "Nom du dossier"),
                ["main.msg.errorCreatingFolder"] = Lang("Errore durante la creazione cartella: {0}", "Error while creating folder: {0}", "Error al crear carpeta: {0}", "Erreur pendant la creation du dossier : {0}"),
                ["main.msg.selectAtLeastOneMove"] = Lang("Seleziona almeno un elemento da spostare.", "Select at least one item to move.", "Selecciona al menos un elemento para mover.", "Selectionnez au moins un element a deplacer."),
                ["main.msg.errorMoving"] = Lang("Errore durante lo spostamento: {0}", "Error while moving: {0}", "Error durante el movimiento: {0}", "Erreur pendant le deplacement : {0}"),
                ["main.msg.selectOneRename"] = Lang("Seleziona un elemento da rinominare.", "Select one item to rename.", "Selecciona un elemento para renombrar.", "Selectionnez un element a renommer."),
                ["main.msg.selectSingleRename"] = Lang("Per rinominare, seleziona un solo elemento.", "To rename, select only one item.", "Para renombrar, selecciona solo un elemento.", "Pour renommer, selectionnez un seul element."),
                ["main.msg.selectAtLeastOneList"] = Lang("Seleziona almeno un elemento dalla lista.", "Select at least one item from the list.", "Selecciona al menos un elemento de la lista.", "Selectionnez au moins un element de la liste."),
                ["main.msg.confirmDelete"] = Lang("Confermi l'eliminazione di {0} elemento/i dal vault?", "Confirm deletion of {0} item(s) from vault?", "Confirmas eliminar {0} elemento(s) del vault?", "Confirmez-vous la suppression de {0} element(s) du coffre ?"),
                ["main.title.confirmDelete"] = Lang("Conferma eliminazione", "Confirm deletion", "Confirmar eliminacion", "Confirmer la suppression"),
                ["main.msg.itemsRemoved"] = Lang("Elementi rimossi: {0}", "Items removed: {0}", "Elementos eliminados: {0}", "Elements supprimes : {0}"),
                ["main.msg.errorRemoving"] = Lang("Errore durante la rimozione: {0}", "Error while removing: {0}", "Error al eliminar: {0}", "Erreur pendant la suppression : {0}"),
                ["main.msg.selectAtLeastOneFile"] = Lang("Seleziona almeno un file dalla lista.", "Select at least one file from the list.", "Selecciona al menos un archivo de la lista.", "Selectionnez au moins un fichier de la liste."),
                ["main.msg.onlyFoldersUseMove"] = Lang("La selezione contiene solo cartelle. Per le cartelle usa 'Sposta'.", "Selection contains only folders. Use 'Move' for folders.", "La seleccion contiene solo carpetas. Para carpetas usa 'Mover'.", "La selection contient uniquement des dossiers. Utilisez 'Deplacer' pour les dossiers."),
                ["main.msg.exportSecurityWarning"] = Lang(
                    "Attenzione: i file esportati vengono salvati in chiaro sul disco.\nProteggi la cartella di destinazione e cancella i file quando non servono.\n\nContinuare?",
                    "Warning: exported files are saved in clear text on disk.\nProtect the destination folder and delete files when no longer needed.\n\nContinue?",
                    "Advertencia: los archivos exportados se guardan en claro en disco.\nProtege la carpeta de destino y elimina los archivos cuando no sean necesarios.\n\nContinuar?",
                    "Attention : les fichiers exportes sont en clair sur le disque.\nProtegez le dossier de destination et supprimez les fichiers quand ils ne sont plus necessaires.\n\nContinuer ?"),
                ["main.title.exportSecurityWarning"] = Lang("Avviso sicurezza esportazione", "Export security warning", "Aviso de seguridad de exportacion", "Avertissement de securite export"),
                ["main.default.exportFileName"] = Lang("file.bin", "file.bin", "file.bin", "file.bin"),
                ["main.progress.exporting"] = Lang("Esportazione file in corso...", "Exporting file...", "Exportando archivo...", "Export du fichier..."),
                ["main.msg.fileExported"] = Lang("File esportato con successo.", "File exported successfully.", "Archivo exportado con exito.", "Fichier exporte avec succes."),
                ["main.msg.foldersIgnored"] = Lang("Cartelle ignorate: {0}", "Folders ignored: {0}", "Carpetas ignoradas: {0}", "Dossiers ignores : {0}"),
                ["main.msg.filesExported"] = Lang("File esportati: {0}/{1}{2}", "Files exported: {0}/{1}{2}", "Archivos exportados: {0}/{1}{2}", "Fichiers exportes : {0}/{1}{2}"),
                ["main.msg.errorExporting"] = Lang("Errore durante l'esportazione: {0}", "Error while exporting: {0}", "Error durante la exportacion: {0}", "Erreur pendant l'export : {0}"),
                ["main.msg.onlyFoldersDoubleClick"] = Lang("La selezione contiene solo cartelle. Per navigare apri la cartella con doppio click.", "Selection contains only folders. Double-click a folder to navigate.", "La seleccion contiene solo carpetas. Haz doble clic en una carpeta para navegar.", "La selection contient uniquement des dossiers. Double-cliquez sur un dossier pour naviguer."),
                ["main.msg.confirmMultiOpen"] = Lang("Stai per aprire {0} file contemporaneamente. Continuare?", "You are about to open {0} files at once. Continue?", "Vas a abrir {0} archivos al mismo tiempo. Continuar?", "Vous allez ouvrir {0} fichiers en meme temps. Continuer ?"),
                ["main.title.confirmMultiOpen"] = Lang("Conferma apertura multipla", "Confirm multiple open", "Confirmar apertura multiple", "Confirmer l'ouverture multiple"),
                ["main.msg.cannotOpenFile"] = Lang("Impossibile aprire \"{0}\": {1}", "Cannot open \"{0}\": {1}", "No se puede abrir \"{0}\": {1}", "Impossible d'ouvrir \"{0}\" : {1}"),
                ["main.title.fileOpenError"] = Lang("Errore apertura file", "File open error", "Error al abrir archivo", "Erreur ouverture fichier"),
                ["main.msg.filesOpened"] = Lang("File aperti: {0}/{1}.{2}\n{3}", "Files opened: {0}/{1}.{2}\n{3}", "Archivos abiertos: {0}/{1}.{2}\n{3}", "Fichiers ouverts : {0}/{1}.{2}\n{3}"),
                ["main.msg.tempCleanup"] = Lang("Le copie temporanee verranno cancellate automaticamente quando possibile.", "Temporary copies will be deleted automatically when possible.", "Las copias temporales se eliminaran automaticamente cuando sea posible.", "Les copies temporaires seront supprimees automatiquement quand possible."),
                ["main.progress.updatingPassword"] = Lang("Aggiornamento password in corso...", "Updating password...", "Actualizando contrasena...", "Mise a jour du mot de passe..."),
                ["main.msg.passwordUpdated"] = Lang("Password aggiornata con successo.", "Password updated successfully.", "Contrasena actualizada con exito.", "Mot de passe mis a jour avec succes."),
                ["main.progress.convertingFormat"] = Lang("Conversione formato vault in corso...", "Converting vault format...", "Convirtiendo formato de vault...", "Conversion du format du coffre..."),
                ["main.msg.formatConverted"] = Lang("Formato vault convertito in {0}.", "Vault format converted to {0}.", "Formato de vault convertido a {0}.", "Format du coffre converti en {0}."),
                ["main.msg.noChangesApplied"] = Lang("Nessuna modifica applicata.", "No changes applied.", "No se aplicaron cambios.", "Aucun changement applique."),
                ["main.msg.errorSettings"] = Lang("Errore durante le impostazioni vault: {0}", "Error in vault settings: {0}", "Error en ajustes de boveda: {0}", "Erreur dans les parametres du coffre : {0}"),
                ["main.msg.autoLock"] = Lang("Vault chiuso automaticamente dopo 1 ora di inattivita.\nReinserisci la password per riaprirlo.", "Vault closed automatically after 1 hour of inactivity.\nEnter the password again to reopen it.", "Vault cerrado automaticamente despues de 1 hora de inactividad.\nIntroduce la contrasena de nuevo para abrirlo.", "Coffre ferme automatiquement apres 1 heure d'inactivite.\nEntrez de nouveau le mot de passe pour le rouvrir."),
                ["main.title.autoLock"] = Lang("Blocco automatico", "Automatic lock", "Bloqueo automatico", "Verrouillage automatique"),
                ["main.msg.operationNotCompleted"] = Lang("Operazione non completata: {0}", "Operation not completed: {0}", "Operacion no completada: {0}", "Operation non terminee : {0}"),
                ["main.msg.openedVaultPath"] = Lang("Vault aperto ({0}): {1}", "Opened vault ({0}): {1}", "Vault abierto ({0}): {1}", "Coffre ouvert ({0}) : {1}"),
                ["main.msg.renameError"] = Lang("Errore durante la rinomina: {0}", "Error while renaming: {0}", "Error al renombrar: {0}", "Erreur pendant le renommage : {0}"),
                ["main.msg.emptyVaultFile"] = Lang("Il file nel vault e vuoto.", "File in vault is empty.", "El archivo en el vault esta vacio.", "Le fichier dans le coffre est vide."),
                ["main.msg.fileLaunchFailed"] = Lang("Avvio del file non riuscito.", "File launch failed.", "No se pudo iniciar el archivo.", "Echec du lancement du fichier."),

                ["loading.windowTitle"] = Lang("Operazione in corso", "Operation in progress", "Operacion en curso", "Operation en cours"),
                ["loading.inProgress"] = Lang("In corso...", "In progress...", "En curso...", "En cours..."),

                ["textInput.msg.enterValue"] = Lang("Inserisci un valore valido.", "Enter a valid value.", "Introduce un valor valido.", "Entrez une valeur valide."),

                ["move.windowTitle"] = Lang("Sposta elementi", "Move items", "Mover elementos", "Deplacer des elements"),
                ["move.instruction"] = Lang("Seleziona la cartella di destinazione. Puoi creare nuove cartelle annidate.", "Select destination folder. You can create nested folders.", "Selecciona la carpeta de destino. Puedes crear carpetas anidadas.", "Selectionnez le dossier de destination. Vous pouvez creer des dossiers imbriques."),
                ["move.button.newFolder"] = Lang("Nuova cartella", "New folder", "Nueva carpeta", "Nouveau dossier"),
                ["move.button.move"] = Lang("Sposta", "Move", "Mover", "Deplacer"),
                ["move.prompt.newFolderTitle"] = Lang("Nuova cartella", "New folder", "Nueva carpeta", "Nouveau dossier"),
                ["move.prompt.newFolderIn"] = Lang("Nuova cartella in: {0}", "New folder in: {0}", "Nueva carpeta en: {0}", "Nouveau dossier dans : {0}"),
                ["move.prompt.create"] = Lang("Crea", "Create", "Crear", "Creer"),
                ["move.msg.createFolderError"] = Lang("Impossibile creare la cartella: {0}", "Cannot create folder: {0}", "No se puede crear la carpeta: {0}", "Impossible de creer le dossier : {0}"),

                ["openWarn.windowTitle"] = Lang("Avviso sicurezza apertura file", "File opening security warning", "Aviso de seguridad al abrir archivo", "Avertissement de securite ouverture fichier"),
                ["openWarn.body"] = Lang(
                    "Per aprire un file, il vault crea una copia temporanea in chiaro sul disco.\n\nLa copia viene cancellata automaticamente quando possibile, ma alcune applicazioni possono mantenere cache o lock per un certo periodo.",
                    "To open a file, the vault creates a temporary clear-text copy on disk.\n\nThe copy is deleted automatically when possible, but some applications may keep cache or locks for a while.",
                    "Para abrir un archivo, el vault crea una copia temporal en claro en disco.\n\nLa copia se elimina automaticamente cuando es posible, pero algunas aplicaciones pueden mantener cache o bloqueos por un tiempo.",
                    "Pour ouvrir un fichier, le coffre cree une copie temporaire en clair sur le disque.\n\nLa copie est supprimee automatiquement quand possible, mais certaines applications peuvent conserver des caches ou verrous pendant un certain temps."),
                ["openWarn.doNotShow"] = Lang("Non mostrare piu", "Do not show again", "No mostrar mas", "Ne plus afficher"),

                ["rename.windowTitle"] = Lang("Rinomina", "Rename", "Renombrar", "Renommer"),
                ["rename.title.folder"] = Lang("Rinomina cartella", "Rename folder", "Renombrar carpeta", "Renommer dossier"),
                ["rename.title.file"] = Lang("Rinomina file", "Rename file", "Renombrar archivo", "Renommer fichier"),
                ["rename.label.fileName"] = Lang("Nome file", "File name", "Nombre de archivo", "Nom du fichier"),
                ["rename.label.extension"] = Lang("Estensione", "Extension", "Extension", "Extension"),
                ["rename.hint.extension"] = Lang("Usa solo lettere e numeri (esempio: pdf, jpg, docx)", "Use only letters and numbers (example: pdf, jpg, docx)", "Usa solo letras y numeros (ejemplo: pdf, jpg, docx)", "Utilisez seulement lettres et chiffres (exemple : pdf, jpg, docx)"),
                ["rename.button.confirm"] = Lang("Conferma modifica", "Confirm change", "Confirmar cambio", "Confirmer modification"),
                ["rename.msg.nameEmpty"] = Lang("Il nome non puo essere vuoto.", "Name cannot be empty.", "El nombre no puede estar vacio.", "Le nom ne peut pas etre vide."),
                ["rename.msg.invalidChars"] = Lang("Il nome contiene caratteri non validi.", "Name contains invalid characters.", "El nombre contiene caracteres no validos.", "Le nom contient des caracteres invalides."),
                ["rename.msg.extensionTooLong"] = Lang("Estensione troppo lunga (massimo 16 caratteri).", "Extension too long (max 16 characters).", "Extension demasiado larga (maximo 16 caracteres).", "Extension trop longue (maximum 16 caracteres)."),
                ["rename.msg.extensionInvalid"] = Lang("L'estensione puo contenere solo lettere e numeri.", "Extension can contain only letters and numbers.", "La extension solo puede contener letras y numeros.", "L'extension peut contenir uniquement des lettres et des chiffres."),
                ["rename.msg.extensionChanged"] = Lang("Hai modificato l'estensione del file.\nIl file potrebbe non funzionare correttamente.\n\nContinuare?", "You changed the file extension.\nThe file may not work correctly.\n\nContinue?", "Has cambiado la extension del archivo.\nEl archivo podria no funcionar correctamente.\n\nContinuar?", "Vous avez modifie l'extension du fichier.\nLe fichier peut ne pas fonctionner correctement.\n\nContinuer ?"),
                ["rename.title.extensionWarning"] = Lang("Avviso estensione", "Extension warning", "Aviso de extension", "Avertissement extension"),
                ["rename.msg.fullNameInvalid"] = Lang("Il nome completo contiene caratteri non validi.", "Full name contains invalid characters.", "El nombre completo contiene caracteres no validos.", "Le nom complet contient des caracteres invalides."),

                ["settings.windowTitle"] = Lang("Impostazioni Vault", "Vault Settings", "Configuracion de Vault", "Parametres du Coffre"),
                ["settings.header"] = Lang("Impostazioni vault", "Vault settings", "Configuracion de vault", "Parametres du coffre"),
                ["settings.passwordHeader"] = Lang("Cambio password (opzionale)", "Password change (optional)", "Cambio de contrasena (opcional)", "Changement de mot de passe (optionnel)"),
                ["settings.label.newPassword"] = Lang("Nuova password master", "New master password", "Nueva contrasena maestra", "Nouveau mot de passe maitre"),
                ["settings.label.confirmPassword"] = Lang("Conferma nuova password", "Confirm new password", "Confirmar nueva contrasena", "Confirmer le nouveau mot de passe"),
                ["settings.formatHeader"] = Lang("Conversione formato vault", "Vault format conversion", "Conversion de formato vault", "Conversion du format du coffre"),
                ["settings.label.currentFormat"] = Lang("Formato attuale: {0}", "Current format: {0}", "Formato actual: {0}", "Format actuel : {0}"),
                ["settings.label.newFormat"] = Lang("Nuovo formato", "New format", "Nuevo formato", "Nouveau format"),
                ["settings.msg.noChange"] = Lang("Nessuna modifica selezionata.", "No changes selected.", "No se seleccionaron cambios.", "Aucune modification selectionnee."),
                ["settings.msg.enterNewPassword"] = Lang("Inserisci una nuova password.", "Enter a new password.", "Introduce una nueva contrasena.", "Entrez un nouveau mot de passe."),
                ["settings.msg.passwordMismatch"] = Lang("Le password non coincidono.", "Passwords do not match.", "Las contrasenas no coinciden.", "Les mots de passe ne correspondent pas.")
            };

        public static string CurrentLanguage => _language;

        public static void SetLanguage(string? languageCode)
        {
            string normalized = (languageCode ?? string.Empty).Trim().ToLowerInvariant();
            _language = normalized is "en" or "es" or "fr" ? normalized : "it";
        }

        public static string Get(string key)
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

        public static string Format(string key, params object[] args)
        {
            string template = Get(key);
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
