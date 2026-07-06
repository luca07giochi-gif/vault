import json
import os


def normalizza_username(username):
    """Uniforma gli username per confronti e salvataggio."""
    return username.strip().lower()


def normalizza_tag(tag):
    """Ripulisce il testo del tag mantenendo il contenuto leggibile."""
    return " ".join(tag.strip().split())

def trova_cartella_download():
    """Tenta di localizzare la cartella Download dell'utente."""
    home = os.path.expanduser("~")
    possibili = [
        os.path.join(home, "Downloads"),
        os.path.join(home, "Download"),
        os.path.join(home, "Scaricati"),
    ]
    for p in possibili:
        if os.path.isdir(p):
            return p
    return home


def trova_cartella_dati_app():
    """Ritorna una cartella persistente per i dati dell'applicazione."""
    local_appdata = os.environ.get("LOCALAPPDATA")
    if local_appdata:
        base_dir = os.path.join(local_appdata, "InstagramAnalyzer")
    else:
        base_dir = os.path.join(os.path.expanduser("~"), ".instagram_analyzer")

    os.makedirs(base_dir, exist_ok=True)
    return base_dir


def percorso_file_tag():
    """Percorso del file JSON che contiene i tag persistenti."""
    return os.path.join(trova_cartella_dati_app(), "account_tags.json")


def carica_tag_account():
    """Carica i tag persistenti da disco."""
    percorso = percorso_file_tag()
    if not os.path.isfile(percorso):
        return {}

    with open(percorso, "r", encoding="utf-8") as f:
        contenuto = json.load(f)

    if not isinstance(contenuto, dict):
        return {}

    tags_per_account = {}
    for username, tags in contenuto.items():
        username_norm = normalizza_username(str(username))
        if not username_norm:
            continue

        if not isinstance(tags, list):
            continue

        tags_puliti = []
        tags_visti = set()
        for tag in tags:
            tag_norm = normalizza_tag(str(tag))
            if not tag_norm:
                continue

            chiave_tag = tag_norm.casefold()
            if chiave_tag in tags_visti:
                continue

            tags_visti.add(chiave_tag)
            tags_puliti.append(tag_norm)

        if tags_puliti:
            tags_per_account[username_norm] = sorted(tags_puliti, key=str.casefold)

    return tags_per_account


def salva_tag_account(tags_per_account):
    """Salva i tag persistenti su disco in formato JSON."""
    contenuto = {}

    for username, tags in tags_per_account.items():
        username_norm = normalizza_username(str(username))
        if not username_norm:
            continue

        tags_puliti = []
        tags_visti = set()
        for tag in tags:
            tag_norm = normalizza_tag(str(tag))
            if not tag_norm:
                continue

            chiave_tag = tag_norm.casefold()
            if chiave_tag in tags_visti:
                continue

            tags_visti.add(chiave_tag)
            tags_puliti.append(tag_norm)

        if tags_puliti:
            contenuto[username_norm] = sorted(tags_puliti, key=str.casefold)

    with open(percorso_file_tag(), "w", encoding="utf-8") as f:
        json.dump(contenuto, f, ensure_ascii=False, indent=2)
