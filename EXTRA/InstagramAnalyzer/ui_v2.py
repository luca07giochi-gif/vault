import os
import tkinter as tk
from datetime import datetime, timedelta
from tkinter import filedialog, font as tkfont, messagebox, scrolledtext, ttk

from instagram_parser import (
    estrai_file_html_da_zip,
    estrai_followers_da_zip,
    estrai_utenti_da_html,
    trova_non_seguaci,
)
from utils import (
    carica_tag_account,
    normalizza_tag,
    normalizza_username,
    salva_tag_account,
    trova_cartella_download,
)


class App(tk.Tk):
    ZIP_PREFIX = "[ZIP] "

    def __init__(self):
        super().__init__()

        self.title("Analisi Instagram Pro")
        self.colors = {
            "bg_main": "#ECF0F1",
            "bg_card": "#FFFFFF",
            "header": "#2C3E50",
            "text_header": "#FFFFFF",
            "text_main": "#2C3E50",
            "primary": "#27AE60",
            "secondary": "#2980B9",
            "alert": "#C0392B",
            "neutral": "#95A5A6",
            "accent": "#F39C12",
        }
        self.configure(bg=self.colors["bg_main"])

        self.download_dir = trova_cartella_download()
        self.zip_files = []
        self.mostra_tutti = False
        self.popup_text_size = 10

        try:
            self.account_tags = carica_tag_account()
        except Exception as e:
            self.account_tags = {}
            messagebox.showwarning(
                "Tag non disponibili",
                f"I tag permanenti non sono stati caricati correttamente:\n{e}",
            )

        self.nome_file_corrente = ""
        self.nome_file_analizzato = ""
        self.lista_followers = []
        self.set_followers = set()
        self.lista_following = []
        self.set_following = set()
        self.lista_non_seguaci = []
        self.lista_non_seguaci_netti = []

        self.crea_interfaccia()
        self.carica_file_zip_instagram()

        if len(self.zip_files) == 1:
            self.listbox.selection_set(0)

        self.imposta_dimensione_finestra()

    def crea_interfaccia(self):
        header_frame = tk.Frame(self, bg=self.colors["header"], height=70)
        header_frame.pack(fill="x", side="top")

        tk.Label(
            header_frame,
            text="INSTAGRAM FOLLOWER ANALYZER",
            font=("Segoe UI", 16, "bold"),
            bg=self.colors["header"],
            fg=self.colors["text_header"],
        ).pack(side="left", padx=20, pady=15)

        tk.Button(
            header_frame,
            text="?",
            font=("Segoe UI", 12, "bold"),
            bg=self.colors["secondary"],
            fg="white",
            relief="flat",
            cursor="hand2",
            command=self.mostra_popup_istruzioni,
        ).pack(side="right", padx=20, pady=15)

        main_container = tk.Frame(self, bg=self.colors["bg_main"])
        main_container.pack(fill="both", expand=True, padx=20, pady=20)

        left_column = tk.Frame(main_container, bg=self.colors["bg_main"])
        left_column.pack(side="left", fill="both", expand=True, padx=(0, 10))
        self.crea_card_files(left_column)

        right_column = tk.Frame(main_container, bg=self.colors["bg_main"])
        right_column.pack(side="right", fill="both", expand=True, padx=(10, 0))
        self.crea_card_azioni(right_column)

        self.status_bar = tk.Frame(self, bg="#BDC3C7", height=30)
        self.status_bar.pack(side="bottom", fill="x")
        self.lbl_stato = tk.Label(
            self.status_bar,
            text="Pronto.",
            font=("Segoe UI", 9),
            bg="#BDC3C7",
            fg="#2C3E50",
        )
        self.lbl_stato.pack(side="left", padx=10)

    def crea_card_files(self, parent):
        card = tk.Frame(parent, bg=self.colors["bg_card"], bd=1, relief="solid")
        card.pack(fill="both", expand=True)

        tk.Label(
            card,
            text="1. SELEZIONE FILE",
            font=("Segoe UI", 11, "bold"),
            bg=self.colors["bg_card"],
            fg=self.colors["text_main"],
        ).pack(pady=(15, 5), anchor="w", padx=15)

        self.lbl_filtri = tk.Label(
            card,
            text="Mostrati solo file recenti (ultimi 7gg) in Download.",
            font=("Segoe UI", 9),
            bg=self.colors["bg_card"],
            fg="#7F8C8D",
            wraplength=350,
            justify="left",
        )
        self.lbl_filtri.pack(anchor="w", padx=15, pady=(0, 10))

        list_frame = tk.Frame(card, bg="white", bd=1, relief="sunken")
        list_frame.pack(fill="both", expand=True, padx=15, pady=5)

        self.scrollbar = tk.Scrollbar(list_frame)
        self.scrollbar.pack(side="right", fill="y")

        self.listbox = tk.Listbox(
            list_frame,
            yscrollcommand=self.scrollbar.set,
            font=("Consolas", 10),
            bd=0,
            highlightthickness=0,
            selectbackground=self.colors["secondary"],
            selectforeground="white",
            height=10,
        )
        self.listbox.pack(side="left", fill="both", expand=True)
        self.scrollbar.config(command=self.listbox.yview)
        self.configura_copia_listbox(self.listbox)

        btn_frame = tk.Frame(card, bg=self.colors["bg_card"])
        btn_frame.pack(fill="x", padx=15, pady=15)

        self.btn_toggle = tk.Button(
            btn_frame,
            text="Mostra Tutti",
            font=("Segoe UI", 9),
            bg=self.colors["neutral"],
            fg="white",
            relief="flat",
            cursor="hand2",
            command=self.toggle_mostra_file,
        )
        self.btn_toggle.pack(side="left", fill="x", expand=True, padx=(0, 5))

        self.btn_seleziona_file = tk.Button(
            btn_frame,
            text="Sfoglia...",
            font=("Segoe UI", 9),
            bg=self.colors["neutral"],
            fg="white",
            relief="flat",
            cursor="hand2",
            command=self.seleziona_file_manualmente,
        )
        self.btn_seleziona_file.pack(side="right", fill="x", expand=True, padx=(5, 0))

    def crea_card_azioni(self, parent):
        card_analizza = tk.Frame(parent, bg=self.colors["bg_card"], bd=1, relief="solid")
        card_analizza.pack(fill="x", pady=(0, 20))

        tk.Label(
            card_analizza,
            text="2. ANALISI & CONFRONTO",
            font=("Segoe UI", 11, "bold"),
            bg=self.colors["bg_card"],
            fg=self.colors["text_main"],
        ).pack(pady=(15, 10), anchor="w", padx=15)

        btn_container = tk.Frame(card_analizza, bg=self.colors["bg_card"])
        btn_container.pack(fill="x", padx=15, pady=(0, 15))

        self.btn_analizza = tk.Button(
            btn_container,
            text="ANALIZZA FILE SELEZIONATO",
            font=("Segoe UI", 11, "bold"),
            bg=self.colors["primary"],
            fg="white",
            relief="flat",
            cursor="hand2",
            pady=8,
            command=self.analizza_zip_selezionato,
        )
        self.btn_analizza.pack(fill="x", pady=(0, 10))

        self.btn_confronta = tk.Button(
            btn_container,
            text="CONFRONTA DUE ESPORTAZIONI",
            font=("Segoe UI", 10, "bold"),
            bg=self.colors["accent"],
            fg="white",
            relief="flat",
            cursor="hand2",
            command=self.confronta_due_zip,
        )
        self.btn_confronta.pack(fill="x")

        self.card_risultati = tk.Frame(parent, bg=self.colors["bg_card"], bd=1, relief="solid")
        self.card_risultati.pack(fill="both", expand=True)

        tk.Label(
            self.card_risultati,
            text="3. RISULTATI",
            font=("Segoe UI", 11, "bold"),
            bg=self.colors["bg_card"],
            fg=self.colors["text_main"],
        ).pack(pady=(15, 5), anchor="w", padx=15)

        self.lbl_info_bottoni = tk.Label(
            self.card_risultati,
            text="Esegui un'analisi per vedere i dati.",
            font=("Segoe UI", 10),
            justify="left",
            bg=self.colors["bg_card"],
            fg="#7F8C8D",
        )
        self.lbl_info_bottoni.pack(pady=10, padx=15, anchor="w")

        self.frame_bottoni_risultati = tk.Frame(self.card_risultati, bg=self.colors["bg_card"])
        self.frame_bottoni_risultati.pack(fill="x", padx=15, pady=10)

        self.btn_mostra_lista = tk.Button(
            self.frame_bottoni_risultati,
            text="Tutte le liste",
            font=("Segoe UI", 10, "bold"),
            bg=self.colors["secondary"],
            fg="white",
            relief="flat",
            cursor="hand2",
            pady=5,
            command=self.mostra_popup_followers_following,
        )

        self.btn_mostra_non_seguaci = tk.Button(
            self.frame_bottoni_risultati,
            text="Non ricambiano + tag",
            font=("Segoe UI", 10, "bold"),
            bg=self.colors["alert"],
            fg="white",
            relief="flat",
            cursor="hand2",
            pady=5,
            command=self.mostra_non_seguaci,
        )

        self.btn_mostra_lista.pack_forget()
        self.btn_mostra_non_seguaci.pack_forget()

    def imposta_dimensione_finestra(self):
        self.update_idletasks()
        width = 900
        height = 650
        x = (self.winfo_screenwidth() // 2) - (width // 2)
        y = (self.winfo_screenheight() // 2) - (height // 2)
        self.geometry(f"{width}x{height}+{x}+{y}")
        self.minsize(800, 600)

    def resetta_dati_analisi(self):
        self.nome_file_analizzato = ""
        self.lista_followers = []
        self.set_followers = set()
        self.lista_following = []
        self.set_following = set()
        self.lista_non_seguaci = []
        self.lista_non_seguaci_netti = []

    def formatta_voce_zip(self, nome_file):
        return f"{self.ZIP_PREFIX}{nome_file}"

    def estrai_nome_file_da_voce(self, voce_listbox):
        if voce_listbox.startswith(self.ZIP_PREFIX):
            return voce_listbox[len(self.ZIP_PREFIX):]
        return voce_listbox

    def crea_font_popup(self, family="Consolas", weight="normal"):
        return tkfont.Font(family=family, size=self.popup_text_size, weight=weight)

    def crea_controlli_dimensione_testo(self, parent, fonts, bg=None):
        bg = bg or self.colors["bg_card"]
        frame = tk.Frame(parent, bg=bg)

        tk.Label(
            frame,
            text="Testo",
            font=("Segoe UI", 9, "bold"),
            bg=bg,
            fg=self.colors["text_main"],
        ).pack(side="left", padx=(0, 8))

        def aggiorna_dimensione(delta):
            dimensione_corrente = fonts[0].cget("size")
            nuova_dimensione = max(8, min(24, dimensione_corrente + delta))
            if nuova_dimensione == dimensione_corrente:
                return

            for font in fonts:
                font.configure(size=nuova_dimensione)

            self.popup_text_size = nuova_dimensione

        tk.Button(
            frame,
            text="A-",
            font=("Segoe UI", 9, "bold"),
            bg=self.colors["neutral"],
            fg="white",
            relief="flat",
            cursor="hand2",
            width=3,
            command=lambda: aggiorna_dimensione(-1),
        ).pack(side="left", padx=(0, 5))

        tk.Button(
            frame,
            text="A+",
            font=("Segoe UI", 9, "bold"),
            bg=self.colors["secondary"],
            fg="white",
            relief="flat",
            cursor="hand2",
            width=3,
            command=lambda: aggiorna_dimensione(1),
        ).pack(side="left")

        return frame

    def crea_area_testo_sola_lettura(self, parent, contenuto, font):
        txt = scrolledtext.ScrolledText(parent, wrap=tk.WORD, font=font)
        txt.pack(expand=1, fill="both", padx=10, pady=10)
        txt.insert(tk.END, contenuto)
        self.configura_copia_testo(txt)
        txt.config(state=tk.DISABLED)
        return txt

    def copia_negli_appunti(self, testo):
        if not testo:
            return

        self.clipboard_clear()
        self.clipboard_append(testo)
        self.update_idletasks()

    def configura_copia_listbox(self, listbox):
        menu = tk.Menu(listbox, tearoff=0)

        def testo_selezionato():
            selezione = listbox.curselection()
            if not selezione:
                return ""
            return "\n".join(listbox.get(indice) for indice in selezione)

        def copia(event=None):
            testo = testo_selezionato()
            if testo:
                self.copia_negli_appunti(testo)
            return "break"

        def apri_menu(event):
            indice = listbox.nearest(event.y)
            if 0 <= indice < listbox.size():
                listbox.selection_clear(0, tk.END)
                listbox.selection_set(indice)

            menu.delete(0, tk.END)
            menu.add_command(label="Copia", command=copia)
            menu.tk_popup(event.x_root, event.y_root)

        listbox.bind("<Control-c>", copia)
        listbox.bind("<Control-C>", copia)
        listbox.bind("<Button-3>", apri_menu)

    def configura_copia_testo(self, widget):
        menu = tk.Menu(widget, tearoff=0)

        def copia_selezione(event=None):
            try:
                testo = widget.selection_get()
            except tk.TclError:
                testo = ""
            if testo:
                self.copia_negli_appunti(testo)
            return "break"

        def copia_tutto():
            testo = widget.get("1.0", "end-1c")
            if testo:
                self.copia_negli_appunti(testo)

        def apri_menu(event):
            menu.delete(0, tk.END)
            menu.add_command(label="Copia selezione", command=copia_selezione)
            menu.add_command(label="Copia tutto", command=copia_tutto)
            menu.tk_popup(event.x_root, event.y_root)

        widget.bind("<Control-c>", copia_selezione)
        widget.bind("<Control-C>", copia_selezione)
        widget.bind("<Button-3>", apri_menu)

    def configura_entry_copia_incolla(self, entry):
        menu = tk.Menu(entry, tearoff=0)

        def copia(event=None):
            try:
                testo = entry.selection_get()
            except tk.TclError:
                testo = entry.get()
            if testo:
                self.copia_negli_appunti(testo)
            return "break"

        def incolla(event=None):
            try:
                testo = self.clipboard_get()
            except tk.TclError:
                testo = ""
            if not testo:
                return "break"

            try:
                start = entry.index("sel.first")
                end = entry.index("sel.last")
                entry.delete(start, end)
                entry.insert(start, testo)
            except tk.TclError:
                entry.insert(tk.INSERT, testo)
            return "break"

        def apri_menu(event):
            menu.delete(0, tk.END)
            menu.add_command(label="Copia", command=copia)
            menu.add_command(label="Incolla", command=incolla)
            menu.tk_popup(event.x_root, event.y_root)

        entry.bind("<Control-c>", copia)
        entry.bind("<Control-C>", copia)
        entry.bind("<Control-v>", incolla)
        entry.bind("<Control-V>", incolla)
        entry.bind("<Button-3>", apri_menu)

    def ottieni_tag_utente(self, username):
        return self.account_tags.get(normalizza_username(username), [])

    def aggiorna_lista_non_seguaci_netti(self):
        self.lista_non_seguaci_netti = [
            utente for utente in self.lista_non_seguaci
            if not self.ottieni_tag_utente(utente)
        ]

    def ottieni_lista_non_seguaci_esclusi(self):
        return [
            utente for utente in self.lista_non_seguaci
            if self.ottieni_tag_utente(utente)
        ]

    def aggiorna_info_risultati(self):
        if not self.nome_file_analizzato:
            return

        esclusi = len(self.ottieni_lista_non_seguaci_esclusi())
        info_testo = (
            f"Analisi completata per: {self.nome_file_analizzato}\n\n"
            f"Followers: {len(self.lista_followers)}\n"
            f"Following: {len(self.lista_following)}\n"
            f"Non ricambiano (lordo): {len(self.lista_non_seguaci)}\n"
            f"Non ricambiano (netto): {len(self.lista_non_seguaci_netti)}\n"
            f"Esclusi con tag: {esclusi}"
        )

        self.lbl_info_bottoni.config(
            text=info_testo,
            fg="#2C3E50",
            font=("Segoe UI", 10, "bold"),
        )

    def salva_tag_permanenti(self):
        try:
            salva_tag_account(self.account_tags)
            return True
        except Exception as e:
            messagebox.showerror("Errore Tag", f"Impossibile salvare i tag:\n{e}")
            return False

    def carica_file_zip_instagram(self):
        self.listbox.delete(0, tk.END)
        self.resetta_dati_analisi()

        if not os.path.isdir(self.download_dir):
            messagebox.showerror("Errore", f"La cartella Download non esiste:\n{self.download_dir}")
            return

        oggi = datetime.now()
        settimana_fa = oggi - timedelta(days=7)

        tutti_file = os.listdir(self.download_dir)
        zip_instagram = [
            nome_file for nome_file in tutti_file
            if nome_file.lower().startswith("instagram") and nome_file.lower().endswith(".zip")
        ]

        if not self.mostra_tutti:
            filtrati = []
            for nome_file in zip_instagram:
                full_path = os.path.join(self.download_dir, nome_file)
                data_mod = datetime.fromtimestamp(os.path.getmtime(full_path))
                if data_mod >= settimana_fa:
                    filtrati.append(nome_file)
            self.zip_files = filtrati
        else:
            self.zip_files = zip_instagram

        if not self.zip_files:
            self.listbox.insert(tk.END, "   (Nessun file Instagram*.zip recente trovato)")
            self.listbox.config(fg="#95A5A6")
            self.lampeggia_errore("Nessun file Instagram*.zip trovato nella cartella Download.")
            self.btn_analizza.config(state=tk.DISABLED, bg=self.colors["neutral"])
        else:
            self.listbox.config(fg="#2C3E50")
            for nome_file in sorted(self.zip_files):
                self.listbox.insert(tk.END, self.formatta_voce_zip(nome_file))
            self.lbl_stato.config(text=f"{len(self.zip_files)} file trovati.", fg="black")
            self.btn_analizza.config(state=tk.NORMAL, bg=self.colors["primary"])

        self.nome_file_corrente = ""
        self.nascondi_risultati()

    def toggle_mostra_file(self):
        self.mostra_tutti = not self.mostra_tutti
        if self.mostra_tutti:
            self.btn_toggle.config(text="Mostra Recenti")
            self.lbl_filtri.config(text="Mostrati tutti i file instagram*.zip nella cartella Download.")
        else:
            self.btn_toggle.config(text="Mostra Tutti")
            self.lbl_filtri.config(text="Mostrati solo file recenti (ultimi 7gg).")
        self.carica_file_zip_instagram()

    def seleziona_file_manualmente(self):
        percorso = filedialog.askopenfilename(
            title="Seleziona file ZIP Instagram",
            filetypes=[("File ZIP", "*.zip")],
        )
        if percorso:
            nome_file = os.path.basename(percorso)
            self.resetta_dati_analisi()
            self.zip_files = [nome_file]
            self.listbox.config(fg="#2C3E50")
            self.listbox.delete(0, tk.END)
            self.listbox.insert(tk.END, self.formatta_voce_zip(nome_file))
            self.listbox.selection_set(0)
            self.nome_file_corrente = percorso
            self.lbl_stato.config(text=f"File selezionato: {nome_file}")
            self.btn_analizza.config(state=tk.NORMAL, bg=self.colors["primary"])
            self.nascondi_risultati()

    def nascondi_risultati(self):
        self.btn_mostra_lista.pack_forget()
        self.btn_mostra_non_seguaci.pack_forget()
        self.lbl_info_bottoni.config(
            text="Premi 'Analizza' per vedere i dati.",
            fg="#7F8C8D",
            font=("Segoe UI", 10),
        )

    def analizza_zip_selezionato(self):
        if self.nome_file_corrente and os.path.isfile(self.nome_file_corrente):
            percorso_zip = self.nome_file_corrente
        else:
            selezione = self.listbox.curselection()
            if not selezione:
                messagebox.showwarning("Attenzione", "Seleziona un file dalla lista prima di analizzare.")
                return

            testo_selezione = self.listbox.get(selezione[0])
            nome_file = self.estrai_nome_file_da_voce(testo_selezione)
            percorso_zip = os.path.join(self.download_dir, nome_file)
            self.nome_file_corrente = percorso_zip

        self.resetta_dati_analisi()
        self.nascondi_risultati()
        self.lbl_stato.config(text=f"Analisi in corso: {os.path.basename(percorso_zip)}...")
        self.update()

        try:
            self.nome_file_analizzato = os.path.basename(percorso_zip)
            contenuti_html = estrai_file_html_da_zip(percorso_zip)
            if not contenuti_html:
                self.lampeggia_errore("Errore ZIP o file HTML mancanti.")
                return

            if "followers_1.html" not in contenuti_html or "following.html" not in contenuti_html:
                self.lampeggia_errore("File HTML richiesti non trovati.")
                return

            self.lista_followers, self.set_followers = estrai_utenti_da_html(
                contenuti_html["followers_1.html"],
                "followers",
            )
            self.lista_following, self.set_following = estrai_utenti_da_html(
                contenuti_html["following.html"],
                "following",
            )

            self.lista_non_seguaci = trova_non_seguaci(self.set_following, self.set_followers)
            self.aggiorna_lista_non_seguaci_netti()
            self.aggiorna_info_risultati()

            if self.lista_followers or self.lista_following:
                self.btn_mostra_lista.pack(side="left", fill="x", expand=True, padx=(0, 5))
                self.btn_mostra_non_seguaci.pack(side="right", fill="x", expand=True, padx=(5, 0))

            self.lbl_stato.config(text="Analisi completata con successo.", fg=self.colors["primary"])
        except Exception as e:
            self.resetta_dati_analisi()
            self.nascondi_risultati()
            messagebox.showerror("Errore Analisi", f"Si e verificato un errore:\n{e}")

    def confronta_due_zip(self):
        zip_vecchio = filedialog.askopenfilename(
            title="Seleziona il file ZIP PIU VECCHIO",
            initialdir=self.download_dir,
            filetypes=[("File ZIP Instagram", "*.zip")],
        )
        if not zip_vecchio:
            return

        zip_nuovo = filedialog.askopenfilename(
            title="Seleziona il file ZIP PIU RECENTE",
            initialdir=self.download_dir,
            filetypes=[("File ZIP Instagram", "*.zip")],
        )
        if not zip_nuovo:
            return

        try:
            followers_vecchi = estrai_followers_da_zip(zip_vecchio)
            followers_nuovi = estrai_followers_da_zip(zip_nuovo)
            hanno_smetto = sorted(followers_vecchi - followers_nuovi)

            popup = tk.Toplevel(self)
            popup.title("Confronto Esportazioni")
            popup.geometry("560x640")
            popup.configure(bg=self.colors["bg_card"])

            tk.Label(
                popup,
                text=f"Utenti persi: {len(hanno_smetto)}",
                font=("Segoe UI", 12, "bold"),
                bg=self.colors["alert"],
                fg="white",
                pady=10,
            ).pack(fill="x")

            mono_font = self.crea_font_popup()
            toolbar = tk.Frame(popup, bg=self.colors["bg_card"])
            toolbar.pack(fill="x", padx=10, pady=(10, 0))
            self.crea_controlli_dimensione_testo(toolbar, [mono_font]).pack(side="right")

            contenuto = "\n".join(hanno_smetto)
            if not contenuto:
                contenuto = "Nessun utente ha smesso di seguirti in questo intervallo."
            self.crea_area_testo_sola_lettura(popup, contenuto, mono_font)
        except Exception as e:
            self.lampeggia_errore(f"Errore confronto: {e}")

    def mostra_popup_followers_following(self):
        popup = tk.Toplevel(self)
        popup.title("Liste Complete")
        popup.geometry("680x740")
        popup.configure(bg=self.colors["bg_card"])

        mono_font = self.crea_font_popup()
        toolbar = tk.Frame(popup, bg=self.colors["bg_card"])
        toolbar.pack(fill="x", padx=10, pady=(10, 0))
        self.crea_controlli_dimensione_testo(toolbar, [mono_font]).pack(side="right")

        tab_control = ttk.Notebook(popup)
        tab_followers = ttk.Frame(tab_control)
        tab_following = ttk.Frame(tab_control)

        tab_control.add(tab_followers, text=f"Followers ({len(self.lista_followers)})")
        tab_control.add(tab_following, text=f"Following ({len(self.lista_following)})")
        tab_control.pack(expand=1, fill="both", padx=10, pady=10)

        contenuto_followers = "\n".join(self.lista_followers) or "Nessun follower disponibile."
        contenuto_following = "\n".join(self.lista_following) or "Nessun following disponibile."

        self.crea_area_testo_sola_lettura(tab_followers, contenuto_followers, mono_font)
        self.crea_area_testo_sola_lettura(tab_following, contenuto_following, mono_font)

    def mostra_non_seguaci(self):
        popup = tk.Toplevel(self)
        popup.title("Non ricambiano")
        popup.geometry("780x720")
        popup.configure(bg=self.colors["bg_main"])

        mono_font = self.crea_font_popup()
        body_font = self.crea_font_popup(family="Segoe UI")

        header_frame = tk.Frame(popup, bg=self.colors["alert"])
        header_frame.pack(fill="x")

        lbl_header = tk.Label(
            header_frame,
            text="",
            font=("Segoe UI", 12, "bold"),
            bg=self.colors["alert"],
            fg="white",
            pady=10,
        )
        lbl_header.pack(anchor="w", padx=15)

        info_frame = tk.Frame(popup, bg=self.colors["bg_card"])
        info_frame.pack(fill="x", padx=15, pady=(12, 10))

        tk.Label(
            info_frame,
            text="Lordo: tutti. Netto: senza tag. Esclusi: account con almeno un tag.",
            font=("Segoe UI", 9),
            bg=self.colors["bg_card"],
            fg="#5D6D7E",
            justify="left",
        ).pack(side="left", anchor="w")

        self.crea_controlli_dimensione_testo(info_frame, [mono_font, body_font]).pack(side="right")

        contenuto_frame = tk.Frame(popup, bg=self.colors["bg_main"])
        contenuto_frame.pack(fill="both", expand=True, padx=15, pady=(0, 15))

        tab_control = ttk.Notebook(contenuto_frame)
        tab_control.pack(fill="both", expand=True, pady=(0, 12))

        tab_lordo = ttk.Frame(tab_control)
        tab_netto = ttk.Frame(tab_control)
        tab_esclusi = ttk.Frame(tab_control)
        tab_control.add(tab_lordo, text="Lordo")
        tab_control.add(tab_netto, text="Netto")
        tab_control.add(tab_esclusi, text="Esclusi")

        def crea_listbox_con_scroll(parent):
            frame = tk.Frame(parent, bg=self.colors["bg_card"])
            frame.pack(fill="both", expand=True, padx=10, pady=10)

            scrollbar = tk.Scrollbar(frame)
            scrollbar.pack(side="right", fill="y")

            listbox = tk.Listbox(
                frame,
                yscrollcommand=scrollbar.set,
                font=mono_font,
                bd=1,
                relief="solid",
                highlightthickness=0,
                selectbackground=self.colors["secondary"],
                selectforeground="white",
                exportselection=False,
            )
            listbox.pack(side="left", fill="both", expand=True)
            scrollbar.config(command=listbox.yview)
            return listbox

        listbox_lordo = crea_listbox_con_scroll(tab_lordo)
        listbox_netto = crea_listbox_con_scroll(tab_netto)
        listbox_esclusi = crea_listbox_con_scroll(tab_esclusi)
        self.configura_copia_listbox(listbox_lordo)
        self.configura_copia_listbox(listbox_netto)
        self.configura_copia_listbox(listbox_esclusi)

        gestione_frame = tk.Frame(contenuto_frame, bg=self.colors["bg_card"], bd=1, relief="solid")
        gestione_frame.pack(fill="x")

        tk.Label(
            gestione_frame,
            text="Gestione tag permanente",
            font=("Segoe UI", 11, "bold"),
            bg=self.colors["bg_card"],
            fg=self.colors["text_main"],
        ).pack(anchor="w", padx=15, pady=(12, 4))

        utente_selezionato_var = tk.StringVar(value="Seleziona un account dalle liste sopra.")
        tk.Label(
            gestione_frame,
            textvariable=utente_selezionato_var,
            font=("Segoe UI", 10),
            bg=self.colors["bg_card"],
            fg=self.colors["text_main"],
            justify="left",
        ).pack(anchor="w", padx=15, pady=(0, 8))

        area_tag_frame = tk.Frame(gestione_frame, bg=self.colors["bg_card"])
        area_tag_frame.pack(fill="x", padx=15, pady=(0, 12))

        colonna_tag = tk.Frame(area_tag_frame, bg=self.colors["bg_card"])
        colonna_tag.pack(side="left", fill="both", expand=True, padx=(0, 10))

        tk.Label(
            colonna_tag,
            text="Tag correnti",
            font=("Segoe UI", 9, "bold"),
            bg=self.colors["bg_card"],
            fg=self.colors["text_main"],
        ).pack(anchor="w", pady=(0, 5))

        tag_listbox = tk.Listbox(
            colonna_tag,
            height=4,
            font=body_font,
            bd=1,
            relief="solid",
            highlightthickness=0,
            exportselection=False,
        )
        tag_listbox.pack(fill="x")
        self.configura_copia_listbox(tag_listbox)

        colonna_azioni = tk.Frame(area_tag_frame, bg=self.colors["bg_card"])
        colonna_azioni.pack(side="right", fill="x", expand=True)

        tk.Label(
            colonna_azioni,
            text="Nuovo tag",
            font=("Segoe UI", 9, "bold"),
            bg=self.colors["bg_card"],
            fg=self.colors["text_main"],
        ).pack(anchor="w", pady=(0, 5))

        entry_tag = tk.Entry(colonna_azioni, font=body_font)
        entry_tag.pack(fill="x", pady=(0, 8))
        self.configura_entry_copia_incolla(entry_tag)

        btn_frame = tk.Frame(colonna_azioni, bg=self.colors["bg_card"])
        btn_frame.pack(fill="x")

        stato = {"utente": None}
        dati_liste = {"lordo": [], "netto": [], "esclusi": []}

        def aggiorna_header():
            esclusi = len(dati_liste["esclusi"])
            lbl_header.config(
                text=(
                    f"Lordo: {len(self.lista_non_seguaci)}   "
                    f"Netto: {len(self.lista_non_seguaci_netti)}   "
                    f"Esclusi con tag: {esclusi}"
                )
            )
            tab_control.tab(tab_lordo, text=f"Lordo ({len(self.lista_non_seguaci)})")
            tab_control.tab(tab_netto, text=f"Netto ({len(self.lista_non_seguaci_netti)})")
            tab_control.tab(tab_esclusi, text=f"Esclusi ({esclusi})")

        def aggiorna_pannello_tag():
            username = stato["utente"]
            tag_listbox.delete(0, tk.END)

            if not username:
                utente_selezionato_var.set("Seleziona un account dalle liste sopra.")
                return

            utente_selezionato_var.set(f"Account selezionato: {username}")
            for tag in self.ottieni_tag_utente(username):
                tag_listbox.insert(tk.END, tag)

        def popola_liste():
            dati_liste["lordo"] = list(self.lista_non_seguaci)
            dati_liste["netto"] = list(self.lista_non_seguaci_netti)
            dati_liste["esclusi"] = self.ottieni_lista_non_seguaci_esclusi()

            listbox_lordo.delete(0, tk.END)
            for username in dati_liste["lordo"]:
                tags = self.ottieni_tag_utente(username)
                descrizione = username
                if tags:
                    descrizione = f"{username}  [{', '.join(tags)}]"
                listbox_lordo.insert(tk.END, descrizione)

            listbox_netto.delete(0, tk.END)
            for username in dati_liste["netto"]:
                listbox_netto.insert(tk.END, username)

            listbox_esclusi.delete(0, tk.END)
            for username in dati_liste["esclusi"]:
                tags = self.ottieni_tag_utente(username)
                descrizione = username
                if tags:
                    descrizione = f"{username}  [{', '.join(tags)}]"
                listbox_esclusi.insert(tk.END, descrizione)

            if stato["utente"] and stato["utente"] not in self.lista_non_seguaci:
                stato["utente"] = None

            aggiorna_header()
            aggiorna_pannello_tag()

        def seleziona_da_lista(nome_lista):
            listbox_per_nome = {
                "lordo": listbox_lordo,
                "netto": listbox_netto,
                "esclusi": listbox_esclusi,
            }
            listbox_attiva = listbox_per_nome[nome_lista]
            selezione = listbox_attiva.curselection()
            if not selezione:
                return

            indice = selezione[0]
            if indice >= len(dati_liste[nome_lista]):
                return

            for nome, listbox in listbox_per_nome.items():
                if nome != nome_lista:
                    listbox.selection_clear(0, tk.END)
            stato["utente"] = dati_liste[nome_lista][indice]
            aggiorna_pannello_tag()

        def aggiungi_tag():
            username = stato["utente"]
            if not username:
                messagebox.showwarning("Tag", "Seleziona prima un account.")
                return

            tag = normalizza_tag(entry_tag.get())
            if not tag:
                messagebox.showwarning("Tag", "Inserisci un tag valido.")
                return

            chiave = normalizza_username(username)
            tag_esistenti = list(self.account_tags.get(chiave, []))
            if any(tag_attuale.casefold() == tag.casefold() for tag_attuale in tag_esistenti):
                messagebox.showinfo("Tag", "Questo tag e gia presente per l'account selezionato.")
                return

            nuovi_tag = sorted(tag_esistenti + [tag], key=str.casefold)
            self.account_tags[chiave] = nuovi_tag
            if not self.salva_tag_permanenti():
                if tag_esistenti:
                    self.account_tags[chiave] = tag_esistenti
                else:
                    self.account_tags.pop(chiave, None)
                return

            entry_tag.delete(0, tk.END)
            self.aggiorna_lista_non_seguaci_netti()
            self.aggiorna_info_risultati()
            popola_liste()

        def rimuovi_tag():
            username = stato["utente"]
            if not username:
                messagebox.showwarning("Tag", "Seleziona prima un account.")
                return

            selezione = tag_listbox.curselection()
            if not selezione:
                messagebox.showwarning("Tag", "Seleziona il tag da rimuovere.")
                return

            tag_da_rimuovere = tag_listbox.get(selezione[0])
            chiave = normalizza_username(username)
            tag_esistenti = list(self.account_tags.get(chiave, []))
            nuovi_tag = [tag for tag in tag_esistenti if tag != tag_da_rimuovere]

            if nuovi_tag:
                self.account_tags[chiave] = nuovi_tag
            else:
                self.account_tags.pop(chiave, None)

            if not self.salva_tag_permanenti():
                if tag_esistenti:
                    self.account_tags[chiave] = tag_esistenti
                return

            self.aggiorna_lista_non_seguaci_netti()
            self.aggiorna_info_risultati()
            popola_liste()

        tk.Button(
            btn_frame,
            text="Aggiungi tag",
            font=("Segoe UI", 9, "bold"),
            bg=self.colors["secondary"],
            fg="white",
            relief="flat",
            cursor="hand2",
            command=aggiungi_tag,
        ).pack(side="left", fill="x", expand=True, padx=(0, 5))

        tk.Button(
            btn_frame,
            text="Rimuovi tag",
            font=("Segoe UI", 9, "bold"),
            bg=self.colors["alert"],
            fg="white",
            relief="flat",
            cursor="hand2",
            command=rimuovi_tag,
        ).pack(side="right", fill="x", expand=True, padx=(5, 0))

        entry_tag.bind("<Return>", lambda event: aggiungi_tag())
        listbox_lordo.bind("<<ListboxSelect>>", lambda event: seleziona_da_lista("lordo"))
        listbox_netto.bind("<<ListboxSelect>>", lambda event: seleziona_da_lista("netto"))
        listbox_esclusi.bind("<<ListboxSelect>>", lambda event: seleziona_da_lista("esclusi"))

        popola_liste()

    def lampeggia_errore(self, testo, tempo_intervallo=500, ripetizioni=10):
        self.lbl_stato.config(text=testo)
        self._lampeggio_count = 0

        def toggle():
            if self._lampeggio_count >= ripetizioni:
                self.lbl_stato.config(fg="#2C3E50")
                return
            colore = "red" if self._lampeggio_count % 2 == 0 else "#2C3E50"
            self.lbl_stato.config(fg=colore)
            self._lampeggio_count += 1
            self.after(tempo_intervallo, toggle)

        toggle()

    def mostra_popup_istruzioni(self):
        popup = tk.Toplevel(self)
        popup.title("Guida all'uso")
        popup.geometry("760x620")
        popup.configure(bg=self.colors["bg_card"])

        tk.Label(
            popup,
            text="ISTRUZIONI",
            font=("Segoe UI", 14, "bold"),
            bg=self.colors["header"],
            fg="white",
            pady=10,
        ).pack(fill="x")

        body_font = self.crea_font_popup(family="Segoe UI")
        toolbar = tk.Frame(popup, bg=self.colors["bg_card"])
        toolbar.pack(fill="x", padx=10, pady=(10, 0))
        self.crea_controlli_dimensione_testo(toolbar, [body_font]).pack(side="right")

        testo_parte1 = (
            "COME SCARICARE I DATI DA INSTAGRAM:\n\n"
            "Scaricare il file contenente le informazioni necessarie:\n"
            "- profilo\n"
            "- menu a tendina (in alto a destra)\n"
            "- centro gestione account\n"
            "- le tue informazioni e autorizzazioni\n"
            "- esporta le tue informazioni, attendi il caricamento della pagina\n\n"
            "- pulsante: crea esportazione\n"
            "- esporta sul dispositivo\n"
            "- personalizza informazioni\n"
            "- cancella tutto, per ogni sezione che incontri scorrendo in basso\n\n"
            "- cerca la sezione contatti e seleziona solo 'follower e persone/pagine seguite'\n"
            "- intervallo di date: seleziona sempre\n"
            "- avvia esportazione\n"
            "- attendi che il file venga generato\n"
            "- scarica il file nella memoria del dispositivo o del PC\n"
        )

        testo_parte2 = (
            "\nCOME USARE QUESTO PROGRAMMA:\n\n"
            "1. Il programma cerca automaticamente i file ZIP nella cartella Download.\n"
            "2. Seleziona il file dalla lista a sinistra.\n"
            "3. Premi 'ANALIZZA FILE SELEZIONATO'.\n"
            "4. Apri 'Non ricambiano + tag' per vedere le liste Lordo, Netto ed Esclusi.\n"
            "5. Assegna uno o piu tag agli account da escludere in modo permanente.\n"
            "6. Usa A- e A+ nelle finestre dei risultati per cambiare la dimensione del testo.\n"
            "7. Nelle liste e nel campo tag puoi usare Ctrl+C, Ctrl+V e il tasto destro del mouse.\n"
        )

        self.crea_area_testo_sola_lettura(popup, testo_parte1 + testo_parte2, body_font)
