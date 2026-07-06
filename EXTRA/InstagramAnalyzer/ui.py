import tkinter as tk
from tkinter import filedialog, font as tkfont, messagebox, scrolledtext, ttk
import os
from datetime import datetime, timedelta

# IMPORTIAMO LE FUNZIONI DAGLI ALTRI FILE
from instagram_parser import (
    estrai_file_html_da_zip, 
    estrai_utenti_da_html,
    trova_non_seguaci, 
    estrai_followers_da_zip
)
from utils import (
    carica_tag_account,
    normalizza_tag,
    normalizza_username,
    salva_tag_account,
    trova_cartella_download,
)

class App(tk.Tk):
    def __init__(self):
        super().__init__()

        # --- Configurazione Finestra ---
        self.title("Analisi Instagram Pro")
        
        # Palette Colori Moderni (Flat Design)
        self.colors = {
            "bg_main": "#ECF0F1",      # Grigio chiaro sfondo
            "bg_card": "#FFFFFF",      # Bianco per le card
            "header": "#2C3E50",       # Blu scuro header
            "text_header": "#FFFFFF",  # Testo header
            "text_main": "#2C3E50",    # Testo principale scuro
            "primary": "#27AE60",      # Verde (Azione principale)
            "secondary": "#2980B9",    # Blu (Info/Liste)
            "alert": "#C0392B",        # Rosso (Non seguaci/Errori)
            "neutral": "#95A5A6",      # Grigio bottoni secondari
            "accent": "#F39C12"        # Arancione (Confronto)
        }
        
        self.configure(bg=self.colors["bg_main"])
        
        # Variabili di stato
        self.download_dir = trova_cartella_download()
        self.zip_files = []
        self.mostra_tutti = False
        self.lista_followers = []
        self.set_followers = set()
        self.lista_following = []
        self.set_following = set()
        self.lista_non_seguaci = []
        self.lista_non_seguaci_netti = []
        self.account_tags = carica_tag_account()
        self.popup_text_size = 10
        self.nome_file_corrente = ""
        self.nome_file_analizzato = ""

        # --- Layout Principale ---
        self.crea_interfaccia()
        self.carica_file_zip_instagram()
        
        if len(self.zip_files) == 1:
            self.listbox.selection_set(0)
            
        self.imposta_dimensione_finestra()

    def crea_interfaccia(self):
        # 1. HEADER
        header_frame = tk.Frame(self, bg=self.colors["header"], height=70)
        header_frame.pack(fill="x", side="top")
        
        lbl_app_title = tk.Label(header_frame, text="INSTAGRAM FOLLOWER ANALYZER", 
                                 font=("Segoe UI", 16, "bold"), 
                                 bg=self.colors["header"], fg=self.colors["text_header"])
        lbl_app_title.pack(side="left", padx=20, pady=15)
        
        btn_istruzioni = tk.Button(header_frame, text="?", font=("Segoe UI", 12, "bold"),
                                   bg=self.colors["secondary"], fg="white", 
                                   relief="flat", cursor="hand2",
                                   command=self.mostra_popup_istruzioni)
        btn_istruzioni.pack(side="right", padx=20, pady=15)

        # 2. CONTAINER PRINCIPALE
        main_container = tk.Frame(self, bg=self.colors["bg_main"])
        main_container.pack(fill="both", expand=True, padx=20, pady=20)

        # --- COLONNA SINISTRA ---
        left_column = tk.Frame(main_container, bg=self.colors["bg_main"])
        left_column.pack(side="left", fill="both", expand=True, padx=(0, 10))
        self.crea_card_files(left_column)

        # --- COLONNA DESTRA ---
        right_column = tk.Frame(main_container, bg=self.colors["bg_main"])
        right_column.pack(side="right", fill="both", expand=True, padx=(10, 0))
        self.crea_card_azioni(right_column)
        
        # 3. STATUS BAR
        self.status_bar = tk.Frame(self, bg="#BDC3C7", height=30)
        self.status_bar.pack(side="bottom", fill="x")
        self.lbl_stato = tk.Label(self.status_bar, text="Pronto.", 
                                  font=("Segoe UI", 9), bg="#BDC3C7", fg="#2C3E50")
        self.lbl_stato.pack(side="left", padx=10)

    def crea_card_files(self, parent):
        card = tk.Frame(parent, bg=self.colors["bg_card"], bd=1, relief="solid")
        card.pack(fill="both", expand=True)
        
        tk.Label(card, text="1. SELEZIONE FILE", font=("Segoe UI", 11, "bold"),
                 bg=self.colors["bg_card"], fg=self.colors["text_main"]).pack(pady=(15, 5), anchor="w", padx=15)
        
        self.lbl_filtri = tk.Label(card, 
            text="Mostrati solo file recenti (ultimi 7gg) in Download.",
            font=("Segoe UI", 9), bg=self.colors["bg_card"], fg="#7F8C8D", wraplength=350, justify="left")
        self.lbl_filtri.pack(anchor="w", padx=15, pady=(0, 10))

        list_frame = tk.Frame(card, bg="white", bd=1, relief="sunken")
        list_frame.pack(fill="both", expand=True, padx=15, pady=5)
        
        self.scrollbar = tk.Scrollbar(list_frame)
        self.scrollbar.pack(side="right", fill="y")
        
        self.listbox = tk.Listbox(list_frame, yscrollcommand=self.scrollbar.set,
                                  font=("Consolas", 10), bd=0, highlightthickness=0,
                                  selectbackground=self.colors["secondary"], selectforeground="white",
                                  height=10)
        self.listbox.pack(side="left", fill="both", expand=True)
        self.scrollbar.config(command=self.listbox.yview)

        btn_frame = tk.Frame(card, bg=self.colors["bg_card"])
        btn_frame.pack(fill="x", padx=15, pady=15)
        
        self.btn_toggle = tk.Button(btn_frame, text="Mostra Tutti", font=("Segoe UI", 9),
                                    bg=self.colors["neutral"], fg="white", relief="flat", cursor="hand2",
                                    command=self.toggle_mostra_file)
        self.btn_toggle.pack(side="left", fill="x", expand=True, padx=(0, 5))
        
        self.btn_seleziona_file = tk.Button(btn_frame, text="Sfoglia...", font=("Segoe UI", 9),
                                            bg=self.colors["neutral"], fg="white", relief="flat", cursor="hand2",
                                            command=self.seleziona_file_manualmente)
        self.btn_seleziona_file.pack(side="right", fill="x", expand=True, padx=(5, 0))

    def crea_card_azioni(self, parent):
        # ANALISI
        card_analizza = tk.Frame(parent, bg=self.colors["bg_card"], bd=1, relief="solid")
        card_analizza.pack(fill="x", pady=(0, 20))
        
        tk.Label(card_analizza, text="2. ANALISI & CONFRONTO", font=("Segoe UI", 11, "bold"),
                 bg=self.colors["bg_card"], fg=self.colors["text_main"]).pack(pady=(15, 10), anchor="w", padx=15)

        btn_container = tk.Frame(card_analizza, bg=self.colors["bg_card"])
        btn_container.pack(fill="x", padx=15, pady=(0, 15))

        self.btn_analizza = tk.Button(btn_container, text="ANALIZZA FILE SELEZIONATO",
                                      font=("Segoe UI", 11, "bold"),
                                      bg=self.colors["primary"], fg="white", 
                                      relief="flat", cursor="hand2", pady=8,
                                      command=self.analizza_zip_selezionato)
        self.btn_analizza.pack(fill="x", pady=(0, 10))

        self.btn_confronta = tk.Button(btn_container, text="CONFRONTA DUE ESPORTAZIONI",
                                       font=("Segoe UI", 10, "bold"),
                                       bg=self.colors["accent"], fg="white", 
                                       relief="flat", cursor="hand2",
                                       command=self.confronta_due_zip)
        self.btn_confronta.pack(fill="x")

        # RISULTATI
        self.card_risultati = tk.Frame(parent, bg=self.colors["bg_card"], bd=1, relief="solid")
        self.card_risultati.pack(fill="both", expand=True)
        
        tk.Label(self.card_risultati, text="3. RISULTATI", font=("Segoe UI", 11, "bold"),
                 bg=self.colors["bg_card"], fg=self.colors["text_main"]).pack(pady=(15, 5), anchor="w", padx=15)

        self.lbl_info_bottoni = tk.Label(self.card_risultati, text="Esegui un'analisi per vedere i dati.",
                                         font=("Segoe UI", 10), justify="left",
                                         bg=self.colors["bg_card"], fg="#7F8C8D")
        self.lbl_info_bottoni.pack(pady=10, padx=15, anchor="w")

        self.frame_bottoni_risultati = tk.Frame(self.card_risultati, bg=self.colors["bg_card"])
        self.frame_bottoni_risultati.pack(fill="x", padx=15, pady=10)

        self.btn_mostra_lista = tk.Button(self.frame_bottoni_risultati, text="Tutte le liste",
                                          font=("Segoe UI", 10, "bold"),
                                          bg=self.colors["secondary"], fg="white",
                                          relief="flat", cursor="hand2", pady=5,
                                          command=self.mostra_popup_followers_following)
        
        self.btn_mostra_non_seguaci = tk.Button(self.frame_bottoni_risultati, text="Non ricambiano + tag",
                                                font=("Segoe UI", 10, "bold"),
                                                bg=self.colors["alert"], fg="white",
                                                relief="flat", cursor="hand2", pady=5,
                                                command=self.mostra_non_seguaci)

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

    def crea_font_popup(self, family="Consolas", weight="normal"):
        return tkfont.Font(family=family, size=self.popup_text_size, weight=weight)

    def crea_controlli_dimensione_testo(self, parent, fonts, bg=None):
        bg = bg or self.colors["bg_card"]
        frame = tk.Frame(parent, bg=bg)

        tk.Label(frame, text="Testo", font=("Segoe UI", 9, "bold"),
                 bg=bg, fg=self.colors["text_main"]).pack(side="left", padx=(0, 8))

        def aggiorna_dimensione(delta):
            dimensione_corrente = fonts[0].cget("size")
            nuova_dimensione = max(8, min(24, dimensione_corrente + delta))
            if nuova_dimensione == dimensione_corrente:
                return

            for font in fonts:
                font.configure(size=nuova_dimensione)

            self.popup_text_size = nuova_dimensione

        tk.Button(frame, text="A-", font=("Segoe UI", 9, "bold"),
                  bg=self.colors["neutral"], fg="white", relief="flat", cursor="hand2",
                  width=3, command=lambda: aggiorna_dimensione(-1)).pack(side="left", padx=(0, 5))
        tk.Button(frame, text="A+", font=("Segoe UI", 9, "bold"),
                  bg=self.colors["secondary"], fg="white", relief="flat", cursor="hand2",
                  width=3, command=lambda: aggiorna_dimensione(1)).pack(side="left")

        return frame

    def crea_area_testo_sola_lettura(self, parent, contenuto, font):
        txt = scrolledtext.ScrolledText(parent, wrap=tk.WORD, font=font)
        txt.pack(expand=1, fill="both", padx=10, pady=10)
        txt.insert(tk.END, contenuto)
        txt.config(state=tk.DISABLED)
        return txt

    def ottieni_tag_utente(self, username):
        return self.account_tags.get(normalizza_username(username), [])

    def aggiorna_lista_non_seguaci_netti(self):
        self.lista_non_seguaci_netti = [
            utente for utente in self.lista_non_seguaci
            if not self.ottieni_tag_utente(utente)
        ]

    def aggiorna_info_risultati(self):
        if not self.nome_file_analizzato:
            return

        esclusi = len(self.lista_non_seguaci) - len(self.lista_non_seguaci_netti)
        info_testo = (
            f"Analisi completata per: {self.nome_file_analizzato}\n\n"
            f"Followers: {len(self.lista_followers)}\n"
            f"Following: {len(self.lista_following)}\n"
            f"Non ricambiano (lordo): {len(self.lista_non_seguaci)}\n"
            f"Non ricambiano (netto): {len(self.lista_non_seguaci_netti)}"
        )

        if esclusi:
            info_testo += f"\nEtichettati esclusi: {esclusi}"

        self.lbl_info_bottoni.config(
            text=info_testo,
            fg="#2C3E50",
            font=("Segoe UI", 10, "bold")
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
        if not os.path.isdir(self.download_dir):
            messagebox.showerror("Errore", f"La cartella Download non esiste:\n{self.download_dir}")
            return

        oggi = datetime.now()
        settimana_fa = oggi - timedelta(days=7)

        tutti_file = os.listdir(self.download_dir)
        zip_instagram = [f for f in tutti_file if f.lower().startswith("instagram") and f.lower().endswith(".zip")]

        if not self.mostra_tutti:
            filtrati = []
            for f in zip_instagram:
                full_path = os.path.join(self.download_dir, f)
                data_mod = datetime.fromtimestamp(os.path.getmtime(full_path))
                if data_mod >= settimana_fa:
                    filtrati.append(f)
            self.zip_files = filtrati
        else:
            self.zip_files = zip_instagram

        if not self.zip_files:
            self.listbox.insert(tk.END, "   (Nessun file Instagram*.zip recente trovato)")
            self.listbox.config(fg="#95A5A6") 
            self.lampeggia_errore(testo="Nessun file Instagram*.zip trovato nella cartella Download.")
            self.btn_analizza.config(state=tk.DISABLED, bg=self.colors["neutral"])
        else:
            self.listbox.config(fg="#2C3E50")
            for f in sorted(self.zip_files):
                self.listbox.insert(tk.END, f"📦 {f}")
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
        percorso = filedialog.askopenfilename(title="Seleziona file ZIP Instagram",
                                              filetypes=[("File ZIP", "*.zip")])
        if percorso:
            nome_file = os.path.basename(percorso)
            self.zip_files = [nome_file]
            self.listbox.config(fg="#2C3E50")
            self.listbox.delete(0, tk.END)
            self.listbox.insert(tk.END, f"📦 {nome_file}")
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
            font=("Segoe UI", 10)
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
            nome_file = testo_selezione.replace("📦 ", "")
            percorso_zip = os.path.join(self.download_dir, nome_file)
            self.nome_file_corrente = percorso_zip

        self.lbl_stato.config(text=f"Analisi in corso: {os.path.basename(percorso_zip)}...")
        self.update()

        try:
            self.nome_file_analizzato = os.path.basename(percorso_zip)
            contenuti_html = estrai_file_html_da_zip(percorso_zip)
            if not contenuti_html:
                self.lampeggia_errore("Errore ZIP o file HTML mancanti.")
                return

            if 'followers_1.html' not in contenuti_html or 'following.html' not in contenuti_html:
                self.lampeggia_errore("File HTML richiesti non trovati.")
                return

            self.lista_followers, self.set_followers = estrai_utenti_da_html(
                contenuti_html['followers_1.html'], "followers"
            )
            self.lista_following, self.set_following = estrai_utenti_da_html(
                contenuti_html['following.html'], "following"
            )

            self.lista_non_seguaci = trova_non_seguaci(self.set_following, self.set_followers)

            info_testo = (
                f"✅ Analisi completata per: {os.path.basename(percorso_zip)}\n\n"
                f"• Followers: {len(self.lista_followers)}\n"
                f"• Following: {len(self.lista_following)}\n"
                f"• Non ricambiano: {len(self.lista_non_seguaci)}"
            )
            self.lbl_info_bottoni.config(text=info_testo, fg="#2C3E50", font=("Segoe UI", 10, "bold"))

            if len(self.lista_followers) > 0 or len(self.lista_following) > 0:
                self.btn_mostra_lista.pack(side="left", fill="x", expand=True, padx=(0, 5))
                self.btn_mostra_non_seguaci.pack(side="right", fill="x", expand=True, padx=(5, 0))

            self.lbl_stato.config(text="Analisi completata con successo.", fg=self.colors["primary"])
        except Exception as e:
            messagebox.showerror("Errore Analisi", f"Si è verificato un errore:\n{e}")

    def confronta_due_zip(self):
        zip_vecchio = filedialog.askopenfilename(
            title="Seleziona il file ZIP PIÙ VECCHIO",
            initialdir=self.download_dir,
            filetypes=[("File ZIP Instagram", "*.zip")]
        )
        if not zip_vecchio: return

        zip_nuovo = filedialog.askopenfilename(
            title="Seleziona il file ZIP PIÙ RECENTE",
            initialdir=self.download_dir,
            filetypes=[("File ZIP Instagram", "*.zip")]
        )
        if not zip_nuovo: return

        try:
            followers_vecchi = estrai_followers_da_zip(zip_vecchio)
            followers_nuovi = estrai_followers_da_zip(zip_nuovo)
            hanno_smetto = sorted(followers_vecchi - followers_nuovi)

            popup = tk.Toplevel(self)
            popup.title("Confronto Esportazioni")
            popup.geometry("500x600")
            
            lbl_header = tk.Label(popup, text=f"Utenti persi: {len(hanno_smetto)}", 
                                  font=("Segoe UI", 12, "bold"), bg=self.colors["alert"], fg="white", pady=10)
            lbl_header.pack(fill="x")

            txt = scrolledtext.ScrolledText(popup, wrap=tk.WORD, font=("Consolas", 10))
            txt.pack(expand=1, fill="both", padx=10, pady=10)

            if hanno_smetto:
                txt.insert(tk.END, "\n".join(hanno_smetto))
            else:
                txt.insert(tk.END, "Nessun utente ha smesso di seguirti in questo intervallo.")
            
            txt.config(state=tk.DISABLED)

        except Exception as e:
            self.lampeggia_errore(f"Errore confronto: {e}")

    def mostra_popup_followers_following(self):
        popup = tk.Toplevel(self)
        popup.title("Liste Complete")
        popup.geometry("600x700")

        tab_control = ttk.Notebook(popup)
        tab_followers = ttk.Frame(tab_control)
        tab_following = ttk.Frame(tab_control)

        tab_control.add(tab_followers, text=f"Followers ({len(self.lista_followers)})")
        tab_control.add(tab_following, text=f"Following ({len(self.lista_following)})")
        tab_control.pack(expand=1, fill="both")

        def crea_area_testo(parent, dati):
            txt = scrolledtext.ScrolledText(parent, wrap=tk.WORD, font=("Consolas", 10))
            txt.pack(expand=1, fill="both", padx=10, pady=10)
            txt.insert(tk.END, "\n".join(dati))
            txt.config(state=tk.DISABLED)

        crea_area_testo(tab_followers, self.lista_followers)
        crea_area_testo(tab_following, self.lista_following)

    def mostra_non_seguaci(self):
        popup = tk.Toplevel(self)
        popup.title("Utenti che non ricambiano")
        popup.geometry("500x600")
        
        header_color = self.colors["alert"]
        lbl_title = tk.Label(popup, text=f"NON ti seguono: {len(self.lista_non_seguaci)}", 
                             font=("Segoe UI", 12, "bold"), bg=header_color, fg="white", pady=10)
        lbl_title.pack(fill="x")

        txt_non_seguaci = scrolledtext.ScrolledText(popup, wrap=tk.WORD, font=("Consolas", 10))
        txt_non_seguaci.pack(expand=1, fill="both", padx=10, pady=10)
        
        if self.lista_non_seguaci:
            txt_non_seguaci.insert(tk.END, "\n".join(self.lista_non_seguaci))
        else:
            txt_non_seguaci.insert(tk.END, "Ottimo! Tutti gli utenti che segui ti seguono a loro volta.")
        
        txt_non_seguaci.config(state=tk.DISABLED)

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
        popup.geometry("700x600")
        
        lbl_head = tk.Label(popup, text="ISTRUZIONI", font=("Segoe UI", 14, "bold"), bg=self.colors["header"], fg="white", pady=10)
        lbl_head.pack(fill="x")

        testo_parte1 = (
            "COME SCARICARE I DATI DA INSTAGRAM:\n\n"
            "Scaricare il file contenente le informazioni necessarie:\n" 
            "- profilo\n" "- menu a tendina (in alto a destra)\n" 
            "- centro gestione account\n" 
            "- le tue informazioni e autorizzazioni\n" 
            "- esporta le tue informazioni, attendi il caricamento della pagina\n" 
            "\n" 
            "- pulsante: crea esportazione\n" 
            "- esporta sul dispositivo\n" 
            "- personalizza informazioni\n" 
            "- cancella tutto (scritto in blu), per ogni sezione che incontri scorrendo in basso\n" 
            "\n" "- cerca la sezione contatti, seleziona solo la voce 'follower e persone/pagine seguite', poi salva\n" 
            "- intervallo di date: seleziona sempre, poi salva\n" 
            "- avvia esportazione\n" 
            "- attendi che il file venga generato (esci e rientra dall'applicazione e attendi almeno dieci minuti)\n" 
            "\n" "- ripeti i primi passaggi (da profilo fino a esporta le tue informazioni)\n" 
            "- pulsante scarica sotto attività attuale\n" 
            "- salva il file nella memoria del dispositivo\n" 
            "\n" "- condividi il file sul computer (puoi inviarlo tramite WhatsApp web, mail eccetera...)\n"
        )
        testo_parte2 = (
            "\n\nCOME USARE QUESTO PROGRAMMA:\n\n"
            "1. Il programma cerca automaticamente i file ZIP nella cartella Download.\n"
            "2. Seleziona il file dalla lista a sinistra.\n"
            "3. Premi 'ANALIZZA FILE SELEZIONATO'.\n"
            "4. Usa i bottoni colorati per vedere chi non ricambia il follow."
        )



        txt = scrolledtext.ScrolledText(popup, wrap=tk.WORD, font=("Segoe UI", 11), padx=15, pady=15)
        txt.pack(expand=True, fill="both")
        txt.insert(tk.END, testo_parte1 + testo_parte2)
        txt.config(state=tk.DISABLED)
