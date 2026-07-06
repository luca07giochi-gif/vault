import zipfile
import re
from bs4 import BeautifulSoup

# Header/localized labels that can appear in exports and should not be treated as usernames
HEADER_LABELS = {
    "followers",
    "following",
    "follower",
    "seguaci",
    "seguiti",
    "seguito",
    "segui",
}

def estrai_file_html_da_zip(zip_path):
    """Estrae il contenuto testuale dei file HTML specifici dallo ZIP."""
    try:
        with zipfile.ZipFile(zip_path, 'r') as zip_ref:
            base_path = 'connections/followers_and_following/'
            files_di_interesse = ['followers_1.html', 'following.html']
            contenuti = {}
            for file_html in files_di_interesse:
                percorso_completo = base_path + file_html
                if percorso_completo in zip_ref.namelist():
                    with zip_ref.open(percorso_completo) as f:
                        contenuto = f.read().decode('utf-8')
                        contenuti[file_html] = contenuto
                else:
                    print(f"Attenzione: {percorso_completo} non trovato nello zip {zip_path}.")
            return contenuti
    except Exception as e:
        # Rilanciamo l'eccezione per farla gestire alla UI
        raise e

def estrai_testo_da_html(contenuto_html):
    """Pulisce l'HTML e ritorna testo grezzo."""
    soup = BeautifulSoup(contenuto_html, 'html.parser')
    testo = soup.get_text(separator='\n')
    return testo.strip()

def estrai_usernames_da_html(contenuto_html):
    """Estrae gli username dagli href Instagram presenti nell'HTML."""
    soup = BeautifulSoup(contenuto_html, 'html.parser')
    usernames = []
    pattern = re.compile(r'^https?://(?:www\.)?instagram\.com/(.+)$', re.IGNORECASE)

    for a in soup.find_all('a', href=True):
        href = a["href"].strip()
        m = pattern.match(href)
        if not m:
            continue

        path = m.group(1)
        # Rimuove query/fragment
        path = path.split('?', 1)[0].split('#', 1)[0]
        # Gestisce il prefisso _u/
        if path.startswith("_u/"):
            path = path[3:]
        # Rimuove eventuali slash finali
        path = path.strip("/")

        # Accetta solo username validi Instagram
        if not re.fullmatch(r'[A-Za-z0-9._]+', path):
            continue

        usernames.append(path)

    return usernames

def pulisci_testo_lista(testo, tipo):
    """Filtra le righe per trovare solo i nomi utente."""
    righe = testo.splitlines()
    righe_pulite = []
    # Regex per nomi utenti Instagram: lettere, numeri, underscore, punti
    pattern_utente = re.compile(r'^[A-Za-z0-9._]+$')

    for r in righe:
        r = r.strip()
        if not r:
            continue
        if r.lower() in HEADER_LABELS:
            continue
        if tipo == "following" and "profiles you choose to see content from" in r.lower():
            continue
        if pattern_utente.match(r):
            righe_pulite.append(r)

    return righe_pulite

def estrai_utenti_da_testo_lista(lista_utenti):
    """Rimuove duplicati e ordina la lista."""
    utenti_set = set()
    utenti_ordinati = []
    for utente in lista_utenti:
        if utente not in utenti_set:
            utenti_set.add(utente)
            utenti_ordinati.append(utente)
    return utenti_ordinati, utenti_set

def estrai_utenti_da_html(contenuto_html, tipo):
    """Estrae utenti preferendo i link; fallback al testo."""
    lista_utenti = estrai_usernames_da_html(contenuto_html)
    if lista_utenti:
        return estrai_utenti_da_testo_lista(lista_utenti)

    testo = estrai_testo_da_html(contenuto_html)
    lista_utenti = pulisci_testo_lista(testo, tipo)
    return estrai_utenti_da_testo_lista(lista_utenti)

def estrai_followers_da_zip(percorso_zip):
    """Funzione helper per estrarre solo il set di followers (usata nel confronto)."""
    contenuti_html = estrai_file_html_da_zip(percorso_zip)

    if 'followers_1.html' not in contenuti_html:
        raise ValueError("followers_1.html non trovato nello ZIP")

    _, set_followers = estrai_utenti_da_html(contenuti_html['followers_1.html'], "followers")

    return set_followers

def trova_non_seguaci(seguo_set, mi_seguono_set):
    """Calcola la differenza tra chi segui e chi ti segue."""
    return [u for u in seguo_set if u not in mi_seguono_set]
