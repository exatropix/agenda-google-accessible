using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Une ligne de la liste des jours.</summary>
    class DayItem
    {
        public DateTime Day;
        public List<EventInfo> Events = new List<EventInfo>();
        public override string ToString() { return EventText.DayLine(Day, Events.Count); }
    }

    /// <summary>Une ligne de la liste des rendez-vous du jour.</summary>
    class ApptItem
    {
        public EventInfo Event; // null = ligne « aucun rendez-vous »
        public DateTime Day;
        public string Text;
        public bool ShowCalendar;
        public override string ToString() { return Event == null ? Text : EventText.LineOnDay(Event, Day) + (ShowCalendar ? AlarmText.CalendarSuffix(Event) : ""); }
    }

    class MainForm : Form
    {
        readonly HttpClient http;
        readonly GoogleAuth auth;
        readonly CalendarApi api;

        readonly ListBox days, appts;
        readonly Label daysLabel, apptsLabel;
        readonly ToolStripStatusLabel status;
        ToolStripMenuItem accountItem, soundItem, alarmsItem, summaryItem, autoStartItem, appCredItem, distributeItem;
        readonly AlarmMonitor monitor;
        readonly NotifyIcon tray;
        readonly bool startHidden;
        bool reallyQuit;
        const int HotKeyId = 1, HotKeyNext = 2, HotKeyToday = 3;

        List<CalendarInfo> calendarList = new List<CalendarInfo>();
        CalendarInfo current;
        DateTime month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        bool connected, loading, soundOn;
        int loadGeneration;
        CancellationTokenSource signInCancel;
        ListBox lastList;

        readonly bool afterUpdate;
        ToolStripMenuItem speakAlarmsItem, autoUpdateItem, installItem;
        System.Windows.Forms.Timer updateTimer;
        // Dernier déplacement au clavier, pour pouvoir l'annuler (Ctrl+Z).
        EventInfo lastMoveBefore; CalendarInfo lastMoveCal;

        public MainForm(bool startHidden, bool afterUpdate = false)
        {
            this.startHidden = startHidden;
            this.afterUpdate = afterUpdate;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            auth = new GoogleAuth(http);
            api = new CalendarApi(http, auth);
            soundOn = Settings.Get("son", "1") == "1";
            monitor = new AlarmMonitor(api, () => calendarList, () => connected, ev => OpenFromAlert(ev));
            monitor.Enabled = Settings.Get("alarmes", "1") == "1";
            monitor.SummaryEnabled = Settings.Get("resume", "1") == "1";
            monitor.SpeakAlarms = Settings.Get("alarmes_voix", "1") == "1";
            monitor.Changed += UpdateTrayText;

            Text = "Agenda Google accessible";
            Font = Ui.BaseFont;
            ClientSize = new Size(1100, 700);
            MinimumSize = new Size(700, 450);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            // ----- Deux listes côte à côte : les jours, puis les rendez-vous du jour choisi -----
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            daysLabel = new Label { Text = "&Jours", AutoSize = true, Margin = new Padding(3, 3, 3, 4), TabIndex = 0 };
            days = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Jours", TabIndex = 1 };
            apptsLabel = new Label { Text = "&Rendez-vous du jour", AutoSize = true, Margin = new Padding(3, 3, 3, 4), TabIndex = 2 };
            appts = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, AccessibleName = "Rendez-vous", TabIndex = 3 };
            grid.Controls.Add(daysLabel, 0, 0);
            grid.Controls.Add(days, 0, 1);
            grid.Controls.Add(apptsLabel, 1, 0);
            grid.Controls.Add(appts, 1, 1);

            days.SelectedIndexChanged += (s, e) => OnDayChanged();
            days.KeyDown += OnDaysKeyDown;
            days.Enter += (s, e) => lastList = days;
            appts.Enter += (s, e) => lastList = appts;
            lastList = days;
            days.DoubleClick += (s, e) => FocusAppts();
            appts.KeyDown += OnApptsKeyDown;
            appts.DoubleClick += (s, e) => ShowDetail();

            var strip = new StatusStrip();
            status = new ToolStripStatusLabel { Text = "Démarrage…", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            strip.Items.Add(status);

            var menu = BuildMenu();
            MainMenuStrip = menu;
            Controls.Add(grid);
            Controls.Add(strip);
            Controls.Add(menu);

            // ----- Icône dans la zone de notification (près de l'horloge) -----
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("&Ouvrir l'agenda", null, (s, e) => ShowMain());
            trayMenu.Items.Add("&Résumé du jour", null, (s, e) => monitor.ShowSummary());
            trayMenu.Items.Add("&Prochaines alarmes", null, (s, e) => { ShowMain(); ShowUpcoming(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("&Quitter (plus d'alarmes)", null, (s, e) => QuitApp());
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Agenda Google accessible", ContextMenuStrip = trayMenu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };

            if (startHidden) { Opacity = 0; ShowInTaskbar = false; WindowState = FormWindowState.Minimized; }

            KeyDown += OnFormKeyDown;
            Shown += async (s, e) =>
            {
                if (startHidden) { Hide(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; Opacity = 1; }
                days.Focus();
                await Startup();
                monitor.Start();
                await monitor.RefreshNow();
            };
            FormClosing += OnClosing;
            FormClosed += (s, e) => { tray.Visible = false; tray.Dispose(); };
        }

        // ================= Arrière-plan : zone de notification, raccourci global =================

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkeys();
        }

        /// <summary>Enregistre les raccourcis globaux choisis dans Préférences (aucun par défaut).
        /// Renvoie ceux que Windows a refusés (déjà pris par une autre application).</summary>
        List<string> RegisterHotkeys()
        {
            var refused = new List<string>();
            foreach (int id in new[] { HotKeyId, HotKeyNext, HotKeyToday }) Native.UnregisterHotKey(Handle, id);
            var map = new[] { Tuple.Create(HotKeyId, GlobalShortcut.Open), Tuple.Create(HotKeyNext, GlobalShortcut.Next), Tuple.Create(HotKeyToday, GlobalShortcut.Today) };
            foreach (var t in map)
            {
                uint mods, vk;
                if (!GlobalShortcut.ToNative(t.Item2.Value, out mods, out vk)) continue;
                if (!Native.RegisterHotKey(Handle, t.Item1, mods, vk)) refused.Add(t.Item2.Value + " (" + t.Item2.Label + ")");
            }
            return refused;
        }

        void EditShortcuts()
        {
            bool arrows;
            using (var f = new ShortcutsForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) { days.Focus(); return; }
                foreach (var g in GlobalShortcut.All) g.Value = f.ValueOf(g);
                arrows = f.CtrlArrows;
            }
            Settings.Set("ctrl_fleches", arrows ? "1" : "0");
            var refused = RegisterHotkeys();
            if (Installer.IsInstalled) Installer.UpdateLaunchShortcut();
            UpdateShortcutMenus();
            days.Focus();
            if (refused.Count > 0)
                Ui.Error(this, "Windows a refusé ces raccourcis, sans doute déjà utilisés par une autre application :\r\n"
                    + string.Join("\r\n", refused) + "\r\n\r\nChoisissez une autre combinaison dans Préférences, Raccourcis clavier.");
            else SetStatus("Raccourcis clavier enregistrés.");
        }

        ToolStripMenuItem nextItem, todayItem;

        void UpdateShortcutMenus()
        {
            if (nextItem != null) nextItem.ShortcutKeyDisplayString = GlobalShortcut.Next.MenuText;
            if (todayItem != null) todayItem.ShortcutKeyDisplayString = GlobalShortcut.Today.MenuText;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, HotKeyId);
            Native.UnregisterHotKey(Handle, HotKeyNext);
            Native.UnregisterHotKey(Handle, HotKeyToday);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotKeyId) { ShowMain(); return; }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotKeyNext) { Speech.Say(monitor.NextEventSentence()); return; }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotKeyToday) { Speech.Say(monitor.TodaySentence()); return; }
            base.WndProc(ref m);
        }

        /// <summary>Affiche la fenêtre principale et lui donne le clavier.</summary>
        public void ShowMain()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Native.ForceForeground(this);
            (lastList ?? days).Focus();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!reallyQuit && e.CloseReason == CloseReason.UserClosing && monitor.Enabled)
            {
                // La fenêtre se ferme, mais l'agenda reste actif pour faire sonner les alarmes.
                e.Cancel = true;
                if (Settings.Get("info_fermeture") != "1")
                {
                    Settings.Set("info_fermeture", "1");
                    Ui.Info(this, "La fenêtre se ferme, mais l'agenda reste actif près de l'horloge pour faire sonner vos alarmes.\r\n\r\n"
                        + "Pour le rouvrir : son icône dans la zone de notification (Windows+B), son raccourci du menu Démarrer, ou le raccourci global que vous pouvez choisir dans Préférences, Raccourcis clavier.\r\n"
                        + "Pour le quitter vraiment : menu Fichier, « Quitter complètement » (Ctrl+Q). Il n'y aura alors plus d'alarmes.\r\n\r\n"
                        + "Ce message ne s'affichera plus.");
                }
                Hide();
                return;
            }
            if (signInCancel != null) signInCancel.Cancel();
        }

        public void QuitApp()
        {
            reallyQuit = true;
            Close();
        }

        void UpdateTrayText()
        {
            var next = monitor.Next();
            string t = next == null ? "Agenda : aucune alarme prévue"
                : "Agenda, alarme à " + EventInfo.TimeText(next.When) + " : " + EventText.Title(next.Event);
            tray.Text = t.Length > 63 ? t.Substring(0, 60) + "…" : t;
        }

        async void OpenFromAlert(EventInfo ev)
        {
            ShowMain();
            if (ev == null) { await GoToDay(DateTime.Today, false); return; }
            await LoadMonth(ev.Start.Date, e => e.Id == ev.Id);
            appts.Focus();
        }

        void ShowUpcoming()
        {
            var list = monitor.Upcoming(DateTime.Now, DateTime.Today.AddDays(2));
            string text = !monitor.Enabled ? "Les alarmes sont désactivées (menu Alarmes, « Activer les alarmes »).\r\n\r\n" : "";
            text += list.Count == 0 ? "Aucune alarme prévue d'ici demain soir." : string.Join("\r\n", list.Select(a => a.ToString() + "."));
            ShowText("Prochaines alarmes (" + list.Count + ")", text);
        }

        void ToggleAlarms()
        {
            monitor.Enabled = !monitor.Enabled;
            alarmsItem.Checked = monitor.Enabled;
            Settings.Set("alarmes", monitor.Enabled ? "1" : "0");
            SetStatus(monitor.Enabled ? "Alarmes activées." : "Alarmes désactivées : fermer la fenêtre quittera l'application.");
        }

        void ToggleSummary()
        {
            monitor.SummaryEnabled = !monitor.SummaryEnabled;
            summaryItem.Checked = monitor.SummaryEnabled;
            Settings.Set("resume", monitor.SummaryEnabled ? "1" : "0");
            SetStatus(monitor.SummaryEnabled ? "Résumé du matin activé." : "Résumé du matin désactivé.");
        }

        void ToggleAutoStart()
        {
            try
            {
                AutoStart.Enabled = !AutoStart.Enabled;
                autoStartItem.Checked = AutoStart.Enabled;
                SetStatus(AutoStart.Enabled ? "L'agenda démarrera avec Windows." : "L'agenda ne démarrera plus avec Windows.");
            }
            catch (Exception ex) { Ui.Error(this, "Impossible de modifier le démarrage automatique : " + ex.Message); }
        }

        void TestRing()
        {
            using (var f = new AlertForm("Test de la sonnerie", "Voici la sonnerie des alarmes. Échap pour l'arrêter.", true)) f.ShowDialog(this);
            days.Focus();
        }

        async void EditDefaultAlarms()
        {
            var list = calendarList.Where(c => c.AccessRole != "freeBusyReader").ToList();
            if (list.Count == 0) { Ui.Error(this, "Aucun agenda chargé. Vérifiez la connexion (menu Fichier) puis F5."); return; }
            List<int> minutes; List<CalendarInfo> targets;
            using (var f = new DefaultAlarmsForm(list))
            {
                if (f.ShowDialog(this) != DialogResult.OK) { days.Focus(); return; }
                minutes = f.Minutes; targets = f.Targets;
            }
            if (targets.Count == 0) { Ui.Info(this, "Aucun agenda coché : rien n'a été changé."); days.Focus(); return; }
            var failed = new List<string>();
            bool ok = await Guard(async () =>
            {
                foreach (var c in targets)
                {
                    SetStatus("Alarmes par défaut de " + c.Name + "…");
                    var rem = c.DefaultReminders.Where(r => r.Method != "popup").ToList();
                    rem.AddRange(minutes.Select(m => new Reminder { Method = "popup", Minutes = m }));
                    try { await api.SetDefaultReminders(c.Id, rem); c.DefaultReminders = rem; }
                    catch (ApiException) { failed.Add(c.Name); }
                }
            });
            if (!ok) return;
            await monitor.RefreshNow();
            await LoadMonth(SelectedDay, null);
            days.Focus();
            string what = minutes.Count == 0 ? "aucune alarme" : string.Join(", ", minutes.Select(AlarmText.Delay));
            Ui.Info(this, "Alarmes par défaut enregistrées : " + what + "."
                + (failed.Count > 0 ? "\r\n\r\nGoogle a refusé pour : " + string.Join(", ", failed) + "." : ""));
        }

        // ================= Identifiants Google et distribution =================

        /// <summary>Une copie reçue (avec agenda-identifiants.json) ne propose pas de refaire une version à distribuer :
        /// seule la copie de l'auteur, ou de quelqu'un qui utilise ses propres identifiants, le peut.</summary>
        void UpdateCredentialMenus()
        {
            bool hasAppFile = System.IO.File.Exists(GoogleAuth.AppCredentialsPath);
            appCredItem.Visible = hasAppFile;
            distributeItem.Visible = !auth.UsingAppCredentials && System.IO.File.Exists(GoogleAuth.CredentialsCopyPath);
        }

        /// <summary>Change de jeu d'identifiants : il faut alors se reconnecter (le jeton dépend de l'identifiant).</summary>
        async Task SwitchCredentials(string path, string mode)
        {
            string err = auth.LoadCredentials(path, mode == "perso");
            if (err != null) { Ui.Error(this, err); return; }
            Settings.Set("identifiants", mode);
            auth.SignOut();
            UpdateCredentialMenus();
            connected = false;
            calendarList.Clear(); current = null;
            days.Items.Clear(); appts.Items.Clear();
            UpdateTitle(); UpdateAccountButton();
            if (await SignIn()) await RefreshAll();
        }

        async void UsePersonalCredentials()
        {
            string path;
            using (var dlg = new OpenFileDialog { Title = "Choisir votre fichier d'identifiants Google (JSON)", Filter = "Fichier JSON (*.json)|*.json|Tous les fichiers|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            string err = GoogleAuth.CheckDesktopCredentials(path);
            if (err != null) { Ui.Error(this, err); return; }
            if (!Ui.Confirm(this, "Utiliser ces identifiants ? Il faudra vous reconnecter à Google.")) return;
            await SwitchCredentials(path, "perso");
        }

        async void UseAppCredentials()
        {
            if (!System.IO.File.Exists(GoogleAuth.AppCredentialsPath))
            {
                Ui.Info(this, "Cette copie de l'application n'est pas fournie avec des identifiants (fichier « " + GoogleAuth.AppCredentialsName + " » absent à côté du programme). Elle utilise vos propres identifiants.");
                return;
            }
            if (auth.UsingAppCredentials) { Ui.Info(this, "L'application utilise déjà les identifiants fournis avec elle."); return; }
            if (!Ui.Confirm(this, "Revenir aux identifiants fournis avec l'application ? Il faudra vous reconnecter à Google.")) return;
            await SwitchCredentials(GoogleAuth.AppCredentialsPath, "integres");
        }

        /// <summary>Crée un fichier zip prêt à donner : le programme, vos identifiants Google et un guide simple.
        /// Les personnes qui le reçoivent n'ont qu'à se connecter avec leur compte Google.</summary>
        void PrepareDistribution()
        {
            string cred = null;
            if (System.IO.File.Exists(GoogleAuth.CredentialsCopyPath)
                && MessageBox.Show(this, "Utiliser votre fichier d'identifiants Google actuel (celui avec lequel vous vous connectez) ?\r\n\r\nNon : choisir un autre fichier JSON.",
                    "Préparer une version à distribuer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                cred = GoogleAuth.CredentialsCopyPath;
            if (cred == null)
            {
                using (var dlg = new OpenFileDialog { Title = "Choisir le fichier d'identifiants Google à fournir (JSON)", Filter = "Fichier JSON (*.json)|*.json|Tous les fichiers|*.*" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    cred = dlg.FileName;
                }
            }
            string err = GoogleAuth.CheckDesktopCredentials(cred);
            if (err != null) { Ui.Error(this, err); return; }

            string zipPath;
            using (var dlg = new SaveFileDialog
            {
                Title = "Enregistrer la version à distribuer",
                Filter = "Archive zip (*.zip)|*.zip",
                FileName = "AgendaGoogleAccessible-1.6.2.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                zipPath = dlg.FileName;
            }
            try
            {
                if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
                string exe = Application.ExecutablePath;
                using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    const string dir = "AgendaGoogleAccessible/";
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, exe, dir + "AgendaGoogleAccessible.exe");
                    if (System.IO.File.Exists(exe + ".config"))
                        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, exe + ".config", dir + "AgendaGoogleAccessible.exe.config");
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, cred, dir + GoogleAuth.AppCredentialsName);
                    var guide = zip.CreateEntry(dir + "LISEZMOI.txt");
                    using (var w = new System.IO.StreamWriter(guide.Open(), new System.Text.UTF8Encoding(true)))
                        w.Write(HelpText.UserGuide.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                }
                Ui.Info(this, "Version à distribuer enregistrée :\r\n" + zipPath + "\r\n\r\n"
                    + "Les personnes qui la reçoivent n'ont rien à faire dans Google Cloud : elles décompressent, lancent le programme et se connectent avec leur compte Google.\r\n\r\n"
                    + "Rappels :\r\n"
                    + "- Votre projet Google Cloud doit être « En production » (Google Auth Platform, Audience). En mode Test, seules les adresses ajoutées comme testeurs peuvent se connecter.\r\n"
                    + "- Tant que Google n'a pas vérifié l'application, les utilisateurs voient un avertissement, et le projet est limité à 100 utilisateurs au total.\r\n"
                    + "- Ce zip contient votre identifiant client : donnez-le, mais ne le publiez pas dans le code source sur GitHub.");
            }
            catch (Exception ex) { Ui.Error(this, "Impossible de créer le fichier : " + ex.Message); }
        }

        /// <summary>L'agenda d'un rendez-vous (utile en vue « Tous mes agendas »).</summary>
        CalendarInfo CalOf(EventInfo ev)
        {
            return ev != null && ev.Calendar != null ? ev.Calendar : current;
        }

        /// <summary>Certaines actions portent sur un seul agenda : on le demande en vue « Tous mes agendas ».</summary>
        bool RequireSingle()
        {
            if (current != null && current.IsAll)
            {
                Ui.Info(this, "Vous affichez tous vos agendas. Choisissez d'abord l'agenda concerné avec Ctrl+G (menu Agenda, « Choisir l'agenda »).");
                return false;
            }
            return current != null;
        }

        // ================= Menus =================

        static ToolStripMenuItem Item(string text, Keys keys, string display, Action click)
        {
            var it = new ToolStripMenuItem(text);
            if (keys != Keys.None) it.ShortcutKeys = keys;
            if (display != null) it.ShortcutKeyDisplayString = display;
            if (click != null) it.Click += (s, e) => click();
            return it;
        }

        static ToolStripMenuItem MenuOf(string text, params ToolStripItem[] items)
        {
            var m = new ToolStripMenuItem(text);
            m.DropDownItems.AddRange(items);
            return m;
        }

        MenuStrip BuildMenu()
        {
            var ms = new MenuStrip();
            accountItem = Item("Se &connecter au compte Google", Keys.None, null, () => ToggleAccount());
            soundItem = Item("&Signal sonore sur les jours avec rendez-vous", Keys.None, null, () => ToggleSound());
            soundItem.Checked = soundOn;

            ms.Items.Add(MenuOf("&Fichier",
                accountItem,
                Item("&Actualiser", Keys.F5, null, async () => await RefreshAll()),
                new ToolStripSeparator(),
                Item("Utiliser mes &propres identifiants Google…", Keys.None, null, () => UsePersonalCredentials()),
                appCredItem = Item("Revenir aux identifiants &fournis avec l'application", Keys.None, null, () => UseAppCredentials()),
                distributeItem = Item("Préparer une version à &distribuer…", Keys.None, null, () => PrepareDistribution()),
                new ToolStripSeparator(),
                Item("&Fermer la fenêtre (les alarmes restent actives)", Keys.None, "Alt+F4", () => Close()),
                Item("&Quitter complètement", Keys.Control | Keys.Q, null, () => QuitApp())));

            ms.Items.Add(MenuOf("É&dition",
                MenuOf("&Prochains rendez-vous",
                    Item("&Demain", Keys.Control | Keys.Shift | Keys.J, null, () => ShowRange(1, 1, null)),
                    Item("Les &7 prochains jours", Keys.Control | Keys.Shift | Keys.S, null, () => ShowRange(0, 7, null)),
                    Item("Les &30 prochains jours", Keys.Control | Keys.Shift | Keys.M, null, () => ShowRange(0, 30, null)),
                    Item("&Chercher un mot dans les prochains rendez-vous…", Keys.Control | Keys.Shift | Keys.T, null, () => SearchUpcoming())),
                new ToolStripSeparator(),
                Item("&Rechercher dans cet agenda…", Keys.Control | Keys.F, null, () => Search(false)),
                Item("Rechercher dans &tous les agendas…", Keys.Control | Keys.Shift | Keys.F, null, () => Search(true))));

            ms.Items.Add(MenuOf("&Rendez-vous",
                Item("&Nouveau rendez-vous…", Keys.Control | Keys.N, null, () => NewEvent()),
                Item("Saisie ra&pide…", Keys.Control | Keys.Shift | Keys.N, null, () => QuickNewEvent()),
                Item("&Voir le détail", Keys.None, "Entrée", () => ShowDetail()),
                Item("&Modifier le rendez-vous…", Keys.Control | Keys.M, null, () => EditEvent(null)),
                Item("&Supprimer le rendez-vous…", Keys.Control | Keys.S, "Suppr ou Ctrl+S", () => DeleteEvent(null)),
                Item("D&upliquer le rendez-vous…", Keys.Control | Keys.Shift | Keys.D, null, () => DuplicateEvent()),
                new ToolStripSeparator(),
                MenuOf("Dé&placer le rendez-vous",
                    Item("Au jour &suivant", Keys.None, "Ctrl+Flèche droite", () => MoveSelected(TimeSpan.FromDays(1))),
                    Item("Au jour &précédent", Keys.None, "Ctrl+Flèche gauche", () => MoveSelected(TimeSpan.FromDays(-1))),
                    Item("Une heure plus &tard", Keys.None, "Ctrl+Flèche bas", () => MoveSelected(TimeSpan.FromHours(1))),
                    Item("Une heure plus tô&t", Keys.None, "Ctrl+Flèche haut", () => MoveSelected(TimeSpan.FromHours(-1))),
                    Item("&Annuler le dernier déplacement", Keys.Control | Keys.Z, null, () => UndoMove())),
                new ToolStripSeparator(),
                MenuOf("&Répondre à l'invitation",
                    Item("&Accepter", Keys.Control | Keys.Shift | Keys.A, null, () => Respond("accepted")),
                    Item("&Peut-être", Keys.Control | Keys.Shift | Keys.P, null, () => Respond("tentative")),
                    Item("&Refuser", Keys.Control | Keys.Shift | Keys.R, null, () => Respond("declined"))),
                Item("Re&joindre la visioconférence", Keys.Control | Keys.Shift | Keys.V, null, () => JoinMeeting())));

            ms.Items.Add(MenuOf("&Affichage",
                Item("&Aujourd'hui", Keys.Control | Keys.Home, "Ctrl+Origine", async () => await GoToDay(DateTime.Today, false)),
                Item("Aller à une &date…", Keys.Control | Keys.D, null, () => AskDate()),
                Item("Mois &suivant", Keys.Control | Keys.PageDown, "Ctrl+Page suivante", async () => await ShiftMonth(1)),
                Item("Mois &précédent", Keys.Control | Keys.PageUp, "Ctrl+Page précédente", async () => await ShiftMonth(-1)),
                Item("Passer des jours aux &rendez-vous", Keys.F6, null, () => SwitchList()),
                new ToolStripSeparator(),
                soundItem));

            alarmsItem = Item("&Activer les alarmes", Keys.None, null, () => ToggleAlarms());
            alarmsItem.Checked = monitor.Enabled;
            summaryItem = Item("Résumé du &matin", Keys.None, null, () => ToggleSummary());
            summaryItem.Checked = monitor.SummaryEnabled;
            autoStartItem = Item("Démarrer avec &Windows", Keys.None, null, () => ToggleAutoStart());
            speakAlarmsItem = Item("A&nnoncer les alarmes à la voix", Keys.None, null, () => ToggleSpeakAlarms());
            speakAlarmsItem.Checked = monitor.SpeakAlarms;
            autoStartItem.Checked = AutoStart.Enabled;
            ms.Items.Add(MenuOf("A&larmes",
                Item("&Prochaines alarmes…", Keys.Control | Keys.L, null, () => ShowUpcoming()),
                Item("&Résumé du jour", Keys.Control | Keys.R, null, () => monitor.ShowSummary()),
                Item("Alarmes par &défaut de mes agendas…", Keys.None, null, () => EditDefaultAlarms()),
                new ToolStripSeparator(),
                alarmsItem, speakAlarmsItem, summaryItem, autoStartItem,
                new ToolStripSeparator(),
                (nextItem = Item("&Dire le prochain rendez-vous", Keys.None, GlobalShortcut.Next.MenuText, () => Speech.Say(monitor.NextEventSentence()))),
                (todayItem = Item("Dire les rendez-vous d'a&ujourd'hui", Keys.None, GlobalShortcut.Today.MenuText, () => Speech.Say(monitor.TodaySentence()))),
                new ToolStripSeparator(),
                Item("&Tester la sonnerie", Keys.None, null, () => TestRing())));

            ms.Items.Add(MenuOf("&Préférences",
                Item("&Raccourcis clavier…", Keys.None, null, () => EditShortcuts())));

            ms.Items.Add(MenuOf("A&genda",
                Item("&Choisir l'agenda…", Keys.Control | Keys.G, null, () => ChooseCalendar()),
                new ToolStripSeparator(),
                Item("&Partager cet agenda…", Keys.None, null, () => ShareCalendar()),
                Item("S'&abonner à un agenda partagé avec moi…", Keys.None, null, () => AddSharedCalendar()),
                Item("C&réer un agenda…", Keys.None, null, () => CreateCalendar()),
                Item("Copier l'&identifiant de l'agenda", Keys.None, null, () => CopyCalendarId()),
                Item("Re&tirer cet agenda de ma liste…", Keys.None, null, () => RemoveCalendar())));

            ms.Items.Add(MenuOf("Aid&e",
                Item("&Aide générale", Keys.F1, null, () => ShowText("Aide", HelpText.General)),
                Item("&Touches de raccourci", Keys.Control | Keys.F1, null, () => ShowText("Touches de raccourci", HelpText.Shortcuts)),
                new ToolStripSeparator(),
                Item("Rechercher les &mises à jour…", Keys.None, null, () => CheckUpdates(true)),
                (autoUpdateItem = Item("&Vérifier les mises à jour automatiquement", Keys.None, null, () => ToggleAutoUpdate())),
                (installItem = Item("&Installer sur cet ordinateur…", Keys.None, null, () => OfferInstall(true))),
                Item("&Désinstaller…", Keys.None, null, () => UninstallFromMenu()),
                new ToolStripSeparator(),
                Item("À &propos", Keys.None, null, () => Ui.Info(this, "Agenda Google accessible, version " + AppInfo.VersionText + "\r\nPar Luc Rasschaert (Exatropic).\r\n\r\nGestion de vos agendas Google au clavier, avec NVDA ou JAWS."))));
            autoUpdateItem.Checked = Updater.AutoCheck;
            installItem.Visible = !Installer.IsInstalledCopy;
            return ms;
        }

        void ShowText(string title, string text)
        {
            var back = ActiveControl;
            using (var f = new TextViewForm(title, text)) f.ShowDialog(this);
            if (back != null) back.Focus();
        }

        void ToggleSound()
        {
            soundOn = !soundOn;
            soundItem.Checked = soundOn;
            Settings.Set("son", soundOn ? "1" : "0");
            SetStatus(soundOn ? "Signal sonore activé." : "Signal sonore désactivé.");
            if (soundOn) Beep.Appointment();
        }

        CalendarInfo CurrentCalendar { get { return current; } }
        DayItem CurrentDay { get { return days.SelectedItem as DayItem; } }
        DateTime SelectedDay { get { var d = CurrentDay; return d != null ? d.Day : DateTime.Today; } }

        void SetStatus(string text) { status.Text = text; }

        void UpdateTitle()
        {
            Text = current == null ? "Agenda Google accessible" : current.ToString() + " - Agenda Google accessible";
        }

        // ================= Démarrage et connexion =================

        async Task Startup()
        {
            bool appProvided;
            string credPath = GoogleAuth.ResolveCredentials(out appProvided);
            if (credPath == null)
            {
                var choice = MessageBox.Show(this, "Bienvenue !\r\n\r\nPour accéder à votre agenda, l'application a besoin de votre identifiant client Google "
                    + "(créé dans Google Cloud, voir le fichier LISEZMOI).\r\n\r\n"
                    + "Oui : choisir le fichier JSON téléchargé.\r\n"
                    + "Non : coller l'ID client et le code secret, copiés depuis la console Google Cloud.\r\n"
                    + "Annuler : plus tard.",
                    "Identifiant Google", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.Yes)
                {
                    using (var dlg = new OpenFileDialog { Title = "Choisir le fichier d'identifiants Google (JSON)", Filter = "Fichier JSON (*.json)|*.json|Tous les fichiers|*.*" })
                    {
                        if (dlg.ShowDialog(this) == DialogResult.OK) credPath = dlg.FileName;
                    }
                }
                else if (choice == DialogResult.No)
                {
                    credPath = AskClientIdAndSecret();
                }
                if (credPath == null)
                {
                    SetStatus("Pas d'identifiant Google. Utilisez le menu Fichier, « Se connecter au compte Google » quand vous l'aurez.");
                    UpdateAccountButton();
                    return;
                }
            }
            string err = auth.LoadCredentials(credPath, true);
            UpdateCredentialMenus();
            if (err != null)
            {
                Ui.Error(this, err);
                SetStatus("Fichier d'identifiants invalide.");
                return;
            }
            connected = auth.HasSavedToken;
            UpdateAccountButton();
            if (!connected)
            {
                if (!Visible) ShowMain();
                if (!await SignIn()) return;
            }
            if (Settings.Get("v13") != "1")
            {
                // Première ouverture de la version 1.3 : vue « Tous mes agendas » et démarrage avec Windows.
                Settings.Set("v13", "1");
                Settings.Set("agenda", "*");
                try { AutoStart.Enabled = true; autoStartItem.Checked = true; } catch { }
                Ui.Info(this, "Bienvenue dans l'Agenda Google accessible !\r\n\r\n"
                    + "- Tous vos agendas sont affichés ensemble (Ctrl+G pour n'en voir qu'un).\r\n"
                    + "- Alarmes : 1 heure, 30 minutes, 15 minutes avant ou à l'heure exacte, dans la fiche de chaque rendez-vous.\r\n"
                    + "- Un résumé de la journée chaque matin.\r\n"
                    + "- L'agenda démarre avec Windows et reste actif près de l'horloge pour sonner.\r\n"
                    + "- Menu Préférences, Raccourcis clavier : vous pouvez activer des raccourcis utilisables partout, pour faire dire le prochain rendez-vous ou ceux du jour par NVDA.\r\n\r\n"
                    + "F1 : aide complète. Ctrl+F1 : toutes les touches.");
            }
            await RefreshAll();
            AfterStartup();
        }

        // ================= Installation et mises à jour =================

        void AfterStartup()
        {
            AutoStart.Refresh();
            if (Installer.IsInstalledCopy) Installer.RefreshVersionInfo();
            if (afterUpdate)
            {
                string msg = "Mise à jour installée : version " + AppInfo.VersionText + ".";
                SetStatus(msg);
                Speech.Say(msg);
            }
            else if (!startHidden && !Installer.IsInstalledCopy && !Installer.IsInstalled && Settings.Get("installation_refusee") != "1")
                OfferInstall(false);
            updateTimer = new System.Windows.Forms.Timer { Interval = 3600 * 1000 };
            updateTimer.Tick += (s, e) => CheckUpdates(false);
            updateTimer.Start();
            CheckUpdates(false);
        }

        /// <summary>Propose d'installer l'agenda (dossier du programme, menu Démarrer, Bureau, désinstallation).</summary>
        void OfferInstall(bool fromMenu)
        {
            if (Installer.IsInstalledCopy) { Ui.Info(this, "L'agenda est déjà installé sur cet ordinateur."); return; }
            var r = MessageBox.Show(this,
                "Installer l'Agenda Google accessible sur cet ordinateur ?\r\n\r\n"
                + "Il sera rangé dans vos programmes, avec un raccourci dans le menu Démarrer et sur le Bureau, "
                + "il démarrera avec Windows et il se mettra à jour tout seul. Vous pourrez le désinstaller depuis les Paramètres de Windows, « Applications installées ».\r\n\r\n"
                + "Oui : installer.  Non : continuer sans installer (vous pourrez le faire plus tard, menu Aide).",
                "Installer l'agenda", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                if (!fromMenu) Settings.Set("installation_refusee", "1");
                days.Focus();
                return;
            }
            string err = Installer.Install(true);
            if (err != null) { Ui.Error(this, "L'installation n'a pas pu se faire : " + err); return; }
            Ui.Info(this, "L'agenda est installé. Il va redémarrer depuis son nouvel emplacement.\r\n\r\n"
                + "Pour le lancer plus tard : « Agenda Google accessible » dans le menu Démarrer ou sur le Bureau (une touche de lancement peut être choisie dans Préférences, Raccourcis clavier).\r\n"
                + "Vous pouvez supprimer le fichier téléchargé.");
            try { System.Diagnostics.Process.Start(Installer.InstalledExe, "/apresinstallation"); } catch { }
            QuitApp();
        }

        void UninstallFromMenu()
        {
            if (!Installer.IsInstalled)
            {
                Ui.Info(this, "L'agenda n'est pas installé : il suffit de supprimer son dossier. Vos rendez-vous restent dans votre agenda Google.");
                return;
            }
            try { System.Diagnostics.Process.Start(Installer.InstalledExe, "/desinstaller"); } catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        void ToggleAutoUpdate()
        {
            Updater.AutoCheck = !Updater.AutoCheck;
            autoUpdateItem.Checked = Updater.AutoCheck;
            SetStatus(Updater.AutoCheck ? "Mises à jour vérifiées automatiquement." : "Vérification automatique des mises à jour désactivée.");
        }

        bool checkingUpdate;

        /// <summary>Cherche une nouvelle version sur GitHub (une fois par jour au plus, sauf demande manuelle).</summary>
        async void CheckUpdates(bool manual)
        {
            if (checkingUpdate) return;
            // Vérification automatique toutes les 4 heures au plus.
            long last;
            long.TryParse(Settings.Get("maj_derniere_heure", "0"), out last);
            if (!manual && (!Updater.AutoCheck || (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalHours < 4)) return;
            checkingUpdate = true;
            try
            {
                if (manual) SetStatus("Recherche des mises à jour…");
                UpdateInfo info;
                try { info = await Updater.Check(); }
                catch (Exception ex)
                {
                    if (manual) Ui.Error(this, "Impossible de vérifier les mises à jour : " + ex.Message);
                    return;
                }
                Settings.Set("maj_derniere_heure", DateTime.UtcNow.Ticks.ToString());
                if (info == null)
                {
                    if (manual) Ui.Info(this, "Vous avez la dernière version (" + AppInfo.VersionText + ").");
                    return;
                }
                if (!manual && Settings.Get("maj_ignoree") == info.Tag) return;
                if (!Visible) ShowMain();
                string notes = info.Notes.Length > 1500 ? info.Notes.Substring(0, 1500) + "…" : info.Notes;
                var r = MessageBox.Show(this,
                    "Une nouvelle version de l'agenda est disponible : " + info.Version.ToString(3) + " (vous avez la " + AppInfo.VersionText + ").\r\n\r\n"
                    + notes.Replace("**", "") + "\r\n\r\n"
                    + "Oui : installer maintenant (l'agenda redémarre tout seul).\r\nNon : pas maintenant.\r\nAnnuler : ne plus proposer cette version.",
                    "Mise à jour disponible", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
                if (r == DialogResult.Cancel) { Settings.Set("maj_ignoree", info.Tag); return; }
                if (r != DialogResult.Yes) return;
                SetStatus("Téléchargement de la version " + info.Version.ToString(3) + "…");
                Speech.Say("Téléchargement de la mise à jour.");
                try { await Updater.Install(info); }
                catch (Exception ex) { Ui.Error(this, "La mise à jour n'a pas pu être téléchargée : " + ex.Message); return; }
                QuitApp();
            }
            finally { checkingUpdate = false; }
        }

        void ToggleSpeakAlarms()
        {
            monitor.SpeakAlarms = !monitor.SpeakAlarms;
            speakAlarmsItem.Checked = monitor.SpeakAlarms;
            Settings.Set("alarmes_voix", monitor.SpeakAlarms ? "1" : "0");
            SetStatus(monitor.SpeakAlarms ? "Les alarmes seront aussi annoncées à la voix." : "Les alarmes ne seront plus annoncées à la voix.");
        }

        /// <summary>Demande l'ID client et le code secret, puis les enregistre comme un fichier JSON Google classique.</summary>
        string AskClientIdAndSecret()
        {
            string id, secret;
            using (var d = new InputDialog("ID client Google",
                "Dans la console Google Cloud, rubrique Google Auth Platform puis Clients, copiez l'ID client (il se termine par .apps.googleusercontent.com) et collez-le ici avec Ctrl+V.",
                "&ID client :", ""))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Length == 0) return null;
                id = d.Value;
            }
            if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                Ui.Error(this, "Cet ID client ne semble pas correct : il doit se terminer par .apps.googleusercontent.com.");
                return null;
            }
            using (var d = new InputDialog("Code secret du client Google",
                "Collez ici le code secret du client (il commence en général par GOCSPX). Il n'est visible qu'au moment où vous le créez dans la console.",
                "&Code secret :", ""))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Length == 0) return null;
                secret = d.Value;
            }
            try
            {
                System.IO.Directory.CreateDirectory(GoogleAuth.DataDir);
                string path = System.IO.Path.Combine(GoogleAuth.DataDir, "credentials.json");
                var content = new Dictionary<string, object>
                {
                    { "installed", new Dictionary<string, object>
                        {
                            { "client_id", id },
                            { "client_secret", secret },
                            { "auth_uri", "https://accounts.google.com/o/oauth2/auth" },
                            { "token_uri", "https://oauth2.googleapis.com/token" },
                            { "redirect_uris", new[] { "http://localhost" } }
                        }
                    }
                };
                System.IO.File.WriteAllText(path, Json.Write(content));
                return path;
            }
            catch (Exception ex)
            {
                Ui.Error(this, "Impossible d'enregistrer l'identifiant : " + ex.Message);
                return null;
            }
        }

        async Task<bool> SignIn()
        {
            Ui.Info(this, "Votre navigateur va s'ouvrir sur la page de connexion Google.\r\n\r\n"
                + "Choisissez votre compte et acceptez l'accès à l'agenda. Si Google affiche « Google n'a pas validé cette application », "
                + (auth.UsingAppCredentials
                    ? "activez le lien « Paramètres avancés » puis « Accéder à Agenda accessible » : c'est normal tant que Google n'a pas terminé sa vérification de l'application.\r\n\r\n"
                    : "activez le lien « Paramètres avancés » puis « Accéder à … » : c'est votre propre application.\r\n\r\n")
                + "Revenez ensuite ici : la suite se fait toute seule.");
            SetStatus("En attente de la connexion dans le navigateur…");
            signInCancel = new CancellationTokenSource();
            try
            {
                await auth.SignInInteractive(signInCancel.Token);
                connected = true;
                UpdateAccountButton();
                SetStatus("Connecté à Google.");
                Activate();
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                connected = false;
                UpdateAccountButton();
                SetStatus("Non connecté.");
                Activate();
                Ui.Error(this, "La connexion à Google n'a pas abouti : " + ex.Message + "\r\n\r\nVous pouvez réessayer avec le menu Fichier, « Se connecter au compte Google ».");
                return false;
            }
            finally { signInCancel = null; }
        }

        void UpdateAccountButton()
        {
            accountItem.Text = connected ? "Se dé&connecter du compte Google" : "Se &connecter au compte Google";
        }

        async void ToggleAccount()
        {
            if (connected)
            {
                if (!Ui.Confirm(this, "Se déconnecter du compte Google ? Il faudra se reconnecter au prochain lancement.")) return;
                auth.SignOut();
                connected = false;
                calendarList.Clear(); current = null;
                days.Items.Clear(); appts.Items.Clear();
                UpdateTitle();
                UpdateAccountButton();
                SetStatus("Déconnecté.");
            }
            else
            {
                bool appProvided;
                if (GoogleAuth.ResolveCredentials(out appProvided) == null) { await Startup(); return; }
                if (await SignIn()) await RefreshAll();
            }
        }

        /// <summary>Exécute une action réseau en gérant les erreurs de façon lisible.</summary>
        async Task<bool> Guard(Func<Task> action)
        {
            if (!connected)
            {
                Ui.Error(this, "Vous n'êtes pas connecté à Google. Utilisez le menu Fichier, « Se connecter au compte Google ».");
                return false;
            }
            UseWaitCursor = true;
            try
            {
                await action();
                return true;
            }
            catch (ReconnectRequiredException ex)
            {
                connected = false;
                UpdateAccountButton();
                SetStatus("Non connecté.");
                if (Ui.Confirm(this, ex.Message + "\r\n\r\nSe reconnecter maintenant ?") && await SignIn())
                    await RefreshAll();
                return false;
            }
            catch (HttpRequestException ex)
            {
                SetStatus("Problème de connexion Internet.");
                Ui.Error(this, "Impossible de joindre Google. Vérifiez votre connexion Internet.\r\n\r\nDétail : " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return false;
            }
            catch (TaskCanceledException)
            {
                SetStatus("Délai dépassé.");
                Ui.Error(this, "Google n'a pas répondu à temps. Réessayez.");
                return false;
            }
            catch (ApiException ex)
            {
                SetStatus("Erreur Google.");
                Ui.Error(this, ExplainApiError(ex));
                return false;
            }
            catch (Exception ex)
            {
                SetStatus("Erreur.");
                Ui.Error(this, ex.Message);
                return false;
            }
            finally { UseWaitCursor = false; }
        }

        static string ExplainApiError(ApiException ex)
        {
            switch ((int)ex.Status)
            {
                case 403:
                    if (ex.Message != null && ex.Message.IndexOf("has not been used", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "L'API Google Agenda n'est pas activée dans votre projet Google Cloud. Activez « Google Calendar API » (voir LISEZMOI).\r\n\r\nDétail : " + ex.Message;
                    return "Action refusée par Google : vous n'avez pas les droits nécessaires sur cet agenda.\r\n\r\nDétail : " + ex.Message;
                case 404: return "Élément introuvable (il a peut-être été supprimé, ou l'agenda n'est pas partagé avec vous).\r\n\r\nDétail : " + ex.Message;
                case 409: return "Cet élément existe déjà.\r\n\r\nDétail : " + ex.Message;
                case 429: return "Trop de demandes envoyées à Google. Patientez un instant puis réessayez.";
                default: return "Google a renvoyé une erreur (" + (int)ex.Status + ") : " + ex.Message;
            }
        }

        // ================= Chargements =================

        async Task RefreshAll()
        {
            await LoadCalendars(current != null ? current.Id : Settings.Get("agenda", "*"));
            await LoadMonth(SelectedDay, null);
        }

        async Task LoadCalendars(string selectId)
        {
            await Guard(async () =>
            {
                SetStatus("Chargement des agendas…");
                calendarList = await api.ListCalendars();
                current = selectId == "*" ? CalendarInfo.All : calendarList.FirstOrDefault(c => c.Id == selectId) ?? calendarList.FirstOrDefault();
                if (current != null) Settings.Set("agenda", current.Id);
                UpdateTitle();
                SetStatus(calendarList.Count + " agenda(s).");
            });
        }

        /// <summary>Charge le mois qui contient <paramref name="day"/> et sélectionne ce jour.
        /// Si <paramref name="selectEvent"/> est donné, le rendez-vous correspondant est sélectionné dans la liste de droite.</summary>
        async Task LoadMonth(DateTime day, Func<EventInfo, bool> selectEvent)
        {
            var cal = current;
            if (cal == null) return;
            day = day.Date;
            var first = new DateTime(day.Year, day.Month, 1);
            int count = DateTime.DaysInMonth(day.Year, day.Month);
            int generation = ++loadGeneration;
            loading = true;
            try
            {
                await Guard(async () =>
                {
                    SetStatus("Chargement de " + first.ToString("MMMM yyyy", EventInfo.Fr) + "…");
                    var targets = cal.IsAll ? calendarList.Where(c => c.AccessRole != "freeBusyReader").ToList() : new List<CalendarInfo> { cal };
                    var list = new List<EventInfo>();
                    foreach (var c in targets)
                    {
                        try
                        {
                            foreach (var ev in await api.ListEvents(c.Id, first, first.AddDays(count), null)) { ev.Calendar = c; list.Add(ev); }
                        }
                        catch (ApiException) when (cal.IsAll) { }
                    }
                    if (generation != loadGeneration) return;
                    month = first;
                    string monthName = first.ToString("MMMM yyyy", EventInfo.Fr);
                    days.BeginUpdate();
                    days.Items.Clear();
                    int total = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var d = first.AddDays(i);
                        var item = new DayItem { Day = d };
                        item.Events = list.Where(ev => EventText.OccursOn(ev, d))
                            .OrderBy(ev => ev.AllDay && ev.Start.Date < d ? 0 : ev.AllDay ? 1 : 2)
                            .ThenBy(ev => ev.Start).ToList();
                        total += item.Events.Count;
                        days.Items.Add(item);
                    }
                    days.EndUpdate();
                    days.AccessibleName = "Jours de " + monthName;
                    daysLabel.Text = "&Jours de " + monthName + " (" + EventText.Count(list.Count) + ")";
                    SetStatus(cal.Name + " : " + EventText.Count(list.Count) + " en " + monthName + ".");
                    days.SelectedIndex = day.Day - 1; // déclenche OnDayChanged
                    if (soundOn && CurrentDay != null && CurrentDay.Events.Count > 0) Beep.Appointment();
                    if (selectEvent != null)
                    {
                        int idx = -1;
                        for (int i = 0; i < appts.Items.Count; i++)
                        {
                            var a = appts.Items[i] as ApptItem;
                            if (a != null && a.Event != null && selectEvent(a.Event)) { idx = i; break; }
                        }
                        if (idx >= 0) appts.SelectedIndex = idx;
                    }
                });
            }
            finally { if (generation == loadGeneration) loading = false; }
        }

        void OnDayChanged()
        {
            var d = CurrentDay;
            appts.BeginUpdate();
            appts.Items.Clear();
            if (d != null)
            {
                bool showCal = current != null && current.IsAll;
                foreach (var ev in d.Events) appts.Items.Add(new ApptItem { Event = ev, Day = d.Day, ShowCalendar = showCal });
                string dayText = EventInfo.DayText(d.Day, d.Day.Year != DateTime.Today.Year).ToLower(EventInfo.Fr);
                if (d.Events.Count == 0) appts.Items.Add(new ApptItem { Day = d.Day, Text = "Aucun rendez-vous le " + dayText + "." });
                appts.AccessibleName = "Rendez-vous du " + dayText + ", " + EventText.Count(d.Events.Count);
                apptsLabel.Text = "&Rendez-vous du " + dayText;
                appts.SelectedIndex = 0;
                if (soundOn && d.Events.Count > 0 && !loading) Beep.Appointment();
            }
            appts.EndUpdate();
        }

        async Task GoToDay(DateTime day, bool focusAppts)
        {
            day = day.Date;
            if (day.Year == month.Year && day.Month == month.Month && days.Items.Count > 0)
                days.SelectedIndex = day.Day - 1;
            else
                await LoadMonth(day, null);
            if (focusAppts) appts.Focus(); else days.Focus();
        }

        async Task ShiftMonth(int direction)
        {
            var target = month.AddMonths(direction);
            int dayNum = Math.Min(SelectedDay.Day, DateTime.DaysInMonth(target.Year, target.Month));
            bool inAppts = lastList == appts;
            await LoadMonth(new DateTime(target.Year, target.Month, dayNum), null);
            if (inAppts) appts.Focus(); else days.Focus();
        }

        async void AskDate()
        {
            using (var d = new InputDialog("Aller à une date", "Exemples : 25/12/2026, 25/12, demain.", "&Date (jj/mm/aaaa) :", Ui.FormatDate(SelectedDay)))
            {
                if (d.ShowDialog(this) != DialogResult.OK) { days.Focus(); return; }
                DateTime date;
                if (!Ui.TryParseDate(d.Value, out date)) { Ui.Error(this, "Cette date n'est pas valide. Exemple : 25/12/2026."); days.Focus(); return; }
                await GoToDay(date, false);
            }
        }

        // ================= Listes et clavier =================

        void FocusAppts() { appts.Focus(); }

        void SwitchList()
        {
            if (lastList == appts) days.Focus(); else appts.Focus();
        }

        async void OnDaysKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control || e.Alt) return;
            if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; FocusAppts(); }
            else if (e.KeyCode == Keys.Delete) { e.Handled = true; DeleteEvent(null); }
            else if (e.KeyCode == Keys.Down && days.SelectedIndex == days.Items.Count - 1 && days.Items.Count > 0 && !loading)
            {
                e.Handled = true;
                await LoadMonth(month.AddMonths(1), null);
            }
            else if (e.KeyCode == Keys.Up && days.SelectedIndex == 0 && !loading)
            {
                e.Handled = true;
                var prev = month.AddMonths(-1);
                await LoadMonth(prev.AddDays(DateTime.DaysInMonth(prev.Year, prev.Month) - 1), null);
            }
        }

        void OnApptsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control || e.Alt) return;
            if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; ShowDetail(); }
            else if (e.KeyCode == Keys.Delete) { e.Handled = true; DeleteEvent(null); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; e.SuppressKeyPress = true; days.Focus(); }
        }

        /// <summary>Ctrl+Flèches dans la liste des jours ou des rendez-vous : décaler le rendez-vous.
        /// Traité ici, avant que la liste ne s'en serve pour se déplacer elle-même.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((ActiveControl == appts || ActiveControl == days) && Settings.Get("ctrl_fleches", "1") == "1")
            {
                TimeSpan? delta = null;
                if (keyData == (Keys.Control | Keys.Right)) delta = TimeSpan.FromDays(1);
                else if (keyData == (Keys.Control | Keys.Left)) delta = TimeSpan.FromDays(-1);
                else if (keyData == (Keys.Control | Keys.Down)) delta = TimeSpan.FromHours(1);
                else if (keyData == (Keys.Control | Keys.Up)) delta = TimeSpan.FromHours(-1);
                if (delta != null) { MoveSelected(delta.Value); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            // Les raccourcis principaux sont portés par les menus.
            if (e.Shift && e.KeyCode == Keys.F6) { e.Handled = true; SwitchList(); }
        }

        // ================= Rendez-vous =================

        /// <summary>Le rendez-vous visé : celui sélectionné à droite si on y est,
        /// sinon celui du jour choisi (on demande lequel s'il y en a plusieurs).</summary>
        EventInfo PickEvent(string verb)
        {
            if (lastList == appts)
            {
                var a = appts.SelectedItem as ApptItem;
                if (a != null && a.Event != null) return a.Event;
            }
            var d = CurrentDay;
            if (d == null) return null;
            string dayText = EventInfo.DayText(d.Day, d.Day.Year != DateTime.Today.Year).ToLower(EventInfo.Fr);
            if (d.Events.Count == 0) { Ui.Info(this, "Aucun rendez-vous le " + dayText + "."); return null; }
            if (d.Events.Count == 1) return d.Events[0];
            var items = d.Events.Select(ev => (object)new ApptItem { Event = ev, Day = d.Day, ShowCalendar = current != null && current.IsAll }).ToList();
            using (var dlg = new ChooseDialog("Choisir le rendez-vous", "Rendez-vous à " + verb + ", " + dayText + " :", items, 0))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                var a = dlg.Selected as ApptItem;
                return a != null ? a.Event : null;
            }
        }

        void ShowDetail()
        {
            if (current == null) return;
            var a = appts.SelectedItem as ApptItem;
            EventInfo ev = lastList == appts && a != null ? a.Event : null;
            if (ev == null && lastList != appts) ev = PickEvent("lire");
            if (ev == null) return;
            var cal = CalOf(ev);
            DialogResult r;
            using (var f = new TextViewForm("Détail du rendez-vous", EventText.Detail(ev, cal), cal.CanEdit))
                r = f.ShowDialog(this);
            if (r == TextViewForm.Modify) EditEvent(ev);
            else if (r == TextViewForm.Delete) DeleteEvent(ev);
            else appts.Focus();
        }

        void NewEvent(EventInfo prefill = null)
        {
            var cal = current;
            if (cal == null) return;
            List<CalendarInfo> choices = null;
            if (cal.IsAll)
            {
                choices = calendarList.Where(c => c.CanEdit).ToList();
                if (choices.Count == 0) { Ui.Error(this, "Aucun de vos agendas ne permet d'ajouter des rendez-vous."); return; }
                cal = (prefill != null && prefill.Calendar != null ? choices.FirstOrDefault(c => c.Id == prefill.Calendar.Id) : null)
                    ?? choices.FirstOrDefault(c => c.Id == Settings.Get("agenda_nouveau")) ?? choices.FirstOrDefault(c => c.Primary) ?? choices[0];
            }
            else if (!cal.CanEdit) { Ui.Error(this, "L'agenda « " + cal.Name + " » est en lecture seule : vous ne pouvez pas y ajouter de rendez-vous."); return; }
            var back = ActiveControl;
            using (var f = new EventForm(cal, null, prefill != null ? prefill.Start : SelectedDay, false, choices, prefill))
            {
                if (f.ShowDialog(this) != DialogResult.OK) { if (back != null) back.Focus(); return; }
                var ev = f.Result;
                var target = ev.Calendar ?? cal;
                if (choices != null) Settings.Set("agenda_nouveau", target.Id);
                SaveAndReload(async () => await api.CreateEvent(target.Id, ev), ev.Start,
                    e => e.Summary == ev.Summary && e.Start == ev.Start, "Rendez-vous ajouté" + (choices != null ? " dans « " + target.Name + " »." : "."));
            }
        }

        void EditEvent(EventInfo ev)
        {
            if (current == null) return;
            if (ev == null) ev = PickEvent("modifier");
            if (ev == null) return;
            var cal = CalOf(ev);
            if (!cal.CanEdit) { Ui.Error(this, "L'agenda « " + cal.Name + " » est en lecture seule : vous ne pouvez pas modifier ce rendez-vous."); return; }
            using (var f = new EventForm(cal, ev, ev.Start, false))
            {
                if (f.ShowDialog(this) != DialogResult.OK) { appts.Focus(); return; }
                var updated = f.Result;
                bool guestsChanged = f.GuestsChanged;
                SaveAndReload(async () => await api.UpdateEvent(cal.Id, updated, guestsChanged), updated.Start,
                    e => e.Id == updated.Id, "Rendez-vous modifié.");
            }
        }

        async void DeleteEvent(EventInfo ev)
        {
            if (current == null) return;
            bool fromAppts = lastList == appts;
            if (ev == null) ev = PickEvent("supprimer");
            if (ev == null) return;
            var cal = CalOf(ev);
            if (!cal.CanEdit) { Ui.Error(this, "L'agenda « " + cal.Name + " » est en lecture seule : vous ne pouvez pas supprimer ce rendez-vous."); return; }
            string targetId = ev.Id;
            if (!string.IsNullOrEmpty(ev.RecurringEventId))
            {
                var choice = MessageBox.Show(this, "Ce rendez-vous se répète : " + ev + ".\r\n\r\n"
                    + "Oui : supprimer seulement cette occurrence.\r\n"
                    + "Non : supprimer TOUTE la série (toutes les dates).\r\n"
                    + "Annuler : ne rien supprimer.",
                    "Supprimer un rendez-vous répété", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
                if (choice == DialogResult.Cancel) { if (fromAppts) appts.Focus(); return; }
                if (choice == DialogResult.No)
                {
                    if (!Ui.Confirm(this, "Dernière confirmation : supprimer toutes les dates de « " + EventText.Title(ev) + " » ?")) { if (fromAppts) appts.Focus(); return; }
                    targetId = ev.RecurringEventId;
                }
            }
            else if (!Ui.Confirm(this, "Supprimer le rendez-vous : " + ev + " ?")) { if (fromAppts) appts.Focus(); return; }
            var day = SelectedDay;
            bool ok = await Guard(async () => await api.DeleteEvent(cal.Id, targetId, ev.Attendees.Count > 0));
            if (ok)
            {
                await LoadMonth(day, null);
                SetStatus("Rendez-vous supprimé. " + status.Text);
                await monitor.RefreshNow();
            }
            if (fromAppts) appts.Focus(); else days.Focus();
        }

        async void SaveAndReload(Func<Task> save, DateTime day, Func<EventInfo, bool> selectWhere, string done)
        {
            bool ok = await Guard(save);
            if (ok)
            {
                await LoadMonth(day, selectWhere);
                SetStatus(done + " " + status.Text);
                appts.Focus();
                await monitor.RefreshNow();
            }
            appts.Focus();
        }

        // ================= Saisie rapide, copie, déplacement =================

        void QuickNewEvent()
        {
            if (current == null) return;
            string text;
            using (var d = new InputDialog("Saisie rapide",
                "Écrivez le rendez-vous en une phrase : le titre, puis le jour, l'heure et le lieu. Exemples :\r\n"
                + "« Dentiste mardi 14h30 Rennes », « Réunion demain de 18h à 20h salle des fêtes », « Anniversaire de Paul 12 mars ».\r\n"
                + "La fiche du rendez-vous s'ouvre ensuite déjà remplie : vérifiez-la, puis enregistrez.",
                "&Rendez-vous :", ""))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Trim().Length == 0) { days.Focus(); return; }
                text = d.Value;
            }
            var r = QuickEntry.Parse(text, DateTime.Now);
            SetStatus("Compris : " + r.Summary);
            NewEvent(r.Event);
        }

        void DuplicateEvent()
        {
            if (current == null) return;
            var ev = PickEvent("dupliquer");
            if (ev == null) return;
            var copy = ev.Clone();
            copy.Id = null;
            copy.RecurringEventId = null;
            copy.Calendar = CalOf(ev);
            NewEvent(copy);
        }

        EventInfo SelectedForAction(string verb)
        {
            if (lastList == appts)
            {
                var a = appts.SelectedItem as ApptItem;
                return a != null ? a.Event : null;
            }
            return PickEvent(verb);
        }

        /// <summary>Décale le rendez-vous sélectionné d'un jour ou d'une heure (Ctrl+flèches), sans ouvrir la fiche.</summary>
        async void MoveSelected(TimeSpan delta)
        {
            if (current == null) return;
            if (loading) { Speech.Say("Patientez, l'agenda se met à jour."); return; }
            var ev = SelectedForAction("déplacer");
            if (ev == null) { Speech.Say("Aucun rendez-vous à déplacer ici."); return; }
            var cal = CalOf(ev);
            if (!cal.CanEdit) { Ui.Error(this, "L'agenda « " + cal.Name + " » est en lecture seule : ce rendez-vous ne peut pas être déplacé."); return; }
            if (ev.AllDay && delta.TotalDays != Math.Round(delta.TotalDays))
            {
                Speech.Say("Rendez-vous sur toute la journée : on ne peut le décaler que d'un jour, avec Ctrl+flèche droite ou gauche.");
                return;
            }
            var moved = ev.Clone();
            moved.Start = ev.Start + delta;
            moved.End = ev.End + delta;
            bool ok = await Guard(async () => await api.UpdateEvent(cal.Id, moved, false));
            if (!ok) { appts.Focus(); return; }
            lastMoveBefore = ev.Clone();
            lastMoveCal = cal;
            await LoadMonth(moved.Start, e => e.Id == moved.Id);
            appts.Focus();
            string said = "Déplacé : " + EventText.LineOnDay(moved, moved.Start.Date) + ", " + EventInfo.DayText(moved.Start, false).ToLower(EventInfo.Fr) + ". Ctrl+Z pour annuler.";
            SetStatus(said);
            Speech.Say(said);
            await monitor.RefreshNow();
        }

        async void UndoMove()
        {
            if (lastMoveBefore == null) { Speech.Say("Aucun déplacement à annuler."); return; }
            var before = lastMoveBefore;
            var cal = lastMoveCal;
            lastMoveBefore = null;
            bool ok = await Guard(async () => await api.UpdateEvent(cal.Id, before, false));
            if (!ok) return;
            await LoadMonth(before.Start, e => e.Id == before.Id);
            appts.Focus();
            string said = "Déplacement annulé : " + EventText.LineOnDay(before, before.Start.Date) + ".";
            SetStatus(said);
            Speech.Say(said);
            await monitor.RefreshNow();
        }

        // ================= Invitations et visioconférence =================

        async void Respond(string response)
        {
            if (current == null) return;
            var ev = SelectedForAction("répondre");
            if (ev == null) return;
            if (!ev.IsInvitation) { Ui.Info(this, "Ce rendez-vous n'est pas une invitation reçue : il n'y a pas de réponse à donner."); return; }
            var cal = CalOf(ev);
            bool ok = await Guard(async () => await api.RespondInvitation(cal.Id, ev.Id, response));
            if (!ok) { appts.Focus(); return; }
            await LoadMonth(ev.Start, e => e.Id == ev.Id);
            appts.Focus();
            string said = "Réponse envoyée : " + EventInfo.ResponseText(response) + ", pour « " + EventText.Title(ev) + " ».";
            SetStatus(said);
            Speech.Say(said);
            await monitor.RefreshNow();
        }

        void JoinMeeting()
        {
            if (current == null) return;
            var ev = SelectedForAction("rejoindre");
            if (ev == null) return;
            Uri link;
            if (ev.MeetLink == null || !Uri.TryCreate(ev.MeetLink, UriKind.Absolute, out link) || link.Scheme != Uri.UriSchemeHttps)
            { Ui.Info(this, "Ce rendez-vous n'a pas de lien de visioconférence utilisable."); return; }
            if (!Ui.Confirm(this, "Ouvrir la visioconférence de « " + EventText.Title(ev) + " » dans votre navigateur ?\r\n\r\n" + ev.MeetLink)) { appts.Focus(); return; }
            try { System.Diagnostics.Process.Start(link.AbsoluteUri); }
            catch (Exception ex) { Ui.Error(this, "Impossible d'ouvrir le lien : " + ex.Message); }
        }

        // ================= Prochains rendez-vous =================

        void SearchUpcoming()
        {
            string term;
            using (var d = new InputDialog("Chercher dans les prochains rendez-vous",
                "Un mot du titre, du lieu ou de la description. Vous choisirez ensuite la période.", "&Mot à chercher :", Settings.Get("recherche_proche", "")))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Trim().Length == 0) { days.Focus(); return; }
                term = d.Value.Trim();
            }
            Settings.Set("recherche_proche", term);
            var periods = new object[] { "Les 7 prochains jours", "Les 30 prochains jours", "Les 3 prochains mois", "L'année à venir" };
            int[] lengths = { 7, 30, 92, 366 };
            int choice;
            using (var d = new ChooseDialog("Période", "&Chercher « " + term + " » dans :", periods, 1))
            {
                if (d.ShowDialog(this) != DialogResult.OK) { days.Focus(); return; }
                choice = Array.IndexOf(periods, d.Selected);
            }
            ShowRange(0, lengths[Math.Max(0, choice)], term);
        }

        /// <summary>Liste d'un seul tenant des rendez-vous d'une période (dans l'agenda affiché, ou tous).</summary>
        async void ShowRange(int startOffset, int length, string term)
        {
            if (!connected || calendarList.Count == 0) { Ui.Error(this, "Aucun agenda chargé. Vérifiez la connexion (menu Fichier) puis F5."); return; }
            bool all = current == null || current.IsAll;
            var targets = all ? calendarList.Where(c => c.AccessRole != "freeBusyReader").ToList() : new List<CalendarInfo> { current };
            DateTime from = startOffset == 0 ? DateTime.Now : DateTime.Today.AddDays(startOffset);
            DateTime to = DateTime.Today.AddDays(startOffset + length);
            var hits = new List<SearchHit>();
            bool ok = await Guard(async () =>
            {
                foreach (var cal in targets)
                {
                    SetStatus("Lecture de " + cal.Name + "…");
                    try
                    {
                        foreach (var ev in await api.ListEvents(cal.Id, from, to, term))
                        { ev.Calendar = cal; hits.Add(new SearchHit { Event = ev, Calendar = cal, ShowCalendar = all && !cal.Primary }); }
                    }
                    catch (ApiException) when (all) { }
                }
            });
            if (!ok) { days.Focus(); return; }
            hits = hits.OrderBy(h => h.Event.AllDay ? h.Event.Start.Date : h.Event.Start).ThenBy(h => h.Event.AllDay ? 0 : 1).ToList();
            string period = startOffset == 1 && length == 1 ? "demain"
                : length == 7 ? "dans les 7 prochains jours" : length == 30 ? "dans les 30 prochains jours"
                : length == 92 ? "dans les 3 prochains mois" : "dans l'année à venir";
            string title = EventText.Count(hits.Count) + " " + period + (term != null ? " pour « " + term + " »" : "");
            title = char.ToUpper(title[0]) + title.Substring(1);
            SetStatus(title + ".");
            SearchHit go = null;
            using (var f = new SearchResultsForm(title, hits))
                if (f.ShowDialog(this) == DialogResult.OK) go = f.GoToHit;
            if (go == null) { days.Focus(); return; }
            await LoadMonth(go.Event.Start, e => e.Id == go.Event.Id);
            appts.Focus();
        }

        // ================= Recherche =================

        async void Search(bool allCalendars)
        {
            if (current == null && !allCalendars) return;
            if (current != null && current.IsAll) allCalendars = true;
            string term;
            using (var d = new InputDialog(allCalendars ? "Rechercher dans tous les agendas" : "Rechercher dans " + current.Name,
                "Un mot ou une partie du titre, du lieu ou de la description. La recherche couvre l'année passée et les deux années à venir.",
                "&Rechercher :", Settings.Get("recherche", "")))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Length == 0) { days.Focus(); return; }
                term = d.Value;
            }
            Settings.Set("recherche", term);
            var targets = allCalendars ? calendarList.Where(c => c.AccessRole != "freeBusyReader").ToList() : new List<CalendarInfo> { current };
            var hits = new List<SearchHit>();
            var skipped = new List<string>();
            bool ok = await Guard(async () =>
            {
                DateTime from = DateTime.Today.AddYears(-1), to = DateTime.Today.AddYears(2);
                foreach (var cal in targets)
                {
                    SetStatus("Recherche dans " + cal.Name + "…");
                    try
                    {
                        foreach (var ev in await api.ListEvents(cal.Id, from, to, term))
                        { ev.Calendar = cal; hits.Add(new SearchHit { Event = ev, Calendar = cal, ShowCalendar = allCalendars }); }
                    }
                    catch (ApiException) when (allCalendars) { skipped.Add(cal.Name); }
                }
            });
            if (!ok) { days.Focus(); return; }
            hits = hits.OrderBy(h => h.Event.Start).ToList();
            string title = hits.Count + " résultat" + (hits.Count > 1 ? "s" : "") + " pour « " + term + " »" + (allCalendars ? " dans tous les agendas" : "");
            SetStatus(title + (skipped.Count > 0 ? " (agendas non consultables : " + string.Join(", ", skipped) + ")" : "") + ".");
            SearchHit go = null;
            using (var f = new SearchResultsForm(title, hits))
            {
                if (f.ShowDialog(this) == DialogResult.OK) go = f.GoToHit;
            }
            if (go == null) { days.Focus(); return; }
            if (current == null || (!current.IsAll && go.Calendar.Id != current.Id))
            {
                current = go.Calendar;
                Settings.Set("agenda", current.Id);
                UpdateTitle();
                month = DateTime.MinValue; // force le rechargement
            }
            var target = go.Event;
            if (target.Start.Year == month.Year && target.Start.Month == month.Month && days.Items.Count > 0)
            {
                days.SelectedIndex = target.Start.Day - 1;
                for (int i = 0; i < appts.Items.Count; i++)
                {
                    var a = appts.Items[i] as ApptItem;
                    if (a != null && a.Event != null && a.Event.Id == target.Id) { appts.SelectedIndex = i; break; }
                }
            }
            else await LoadMonth(target.Start, e => e.Id == target.Id);
            appts.Focus();
        }

        // ================= Choix de l'agenda =================

        async void ChooseCalendar()
        {
            if (calendarList.Count == 0) { Ui.Error(this, "Aucun agenda chargé. Vérifiez la connexion (menu Fichier) puis F5."); return; }
            var choices = new List<CalendarInfo> { CalendarInfo.All };
            choices.AddRange(calendarList);
            int idx = current != null ? Math.Max(0, choices.FindIndex(c => c.Id == current.Id)) : 0;
            CalendarInfo chosen;
            using (var d = new ChooseDialog("Choisir l'agenda", "&Agendas :", choices.Cast<object>(), idx))
            {
                if (d.ShowDialog(this) != DialogResult.OK) { days.Focus(); return; }
                chosen = d.Selected as CalendarInfo;
            }
            if (chosen == null) return;
            current = chosen;
            Settings.Set("agenda", current.Id);
            UpdateTitle();
            await LoadMonth(SelectedDay, null);
            days.Focus();
        }

        // ================= Agendas et partage =================

        void ShareCalendar()
        {
            if (!RequireSingle()) return;
            var cal = CurrentCalendar;
            if (!connected) { Ui.Error(this, "Vous n'êtes pas connecté à Google."); return; }
            if (!cal.IsOwner)
            {
                Ui.Error(this, "Vous n'êtes pas propriétaire de l'agenda « " + cal.Name + " » (" + CalendarInfo.RoleText(cal.AccessRole) + "). Seul son propriétaire peut le partager.");
                return;
            }
            using (var f = new ShareForm(api, cal)) f.ShowDialog(this);
            days.Focus();
        }

        async void AddSharedCalendar()
        {
            string id;
            using (var d = new InputDialog("S'abonner à un agenda partagé avec moi",
                "Saisissez l'adresse Gmail de la personne qui vous a partagé son agenda principal, ou l'identifiant d'agenda qu'elle vous a donné (il se termine souvent par @group.calendar.google.com).",
                "&Adresse ou identifiant de l'agenda :", ""))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Length == 0) return;
                id = d.Value;
            }
            bool ok = await Guard(async () =>
            {
                try { await api.AddCalendarToList(id); }
                catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound || ex.Status == HttpStatusCode.Forbidden)
                {
                    throw new Exception("Impossible d'ajouter l'agenda « " + id + " ». Soit l'adresse est mal saisie, soit la personne ne vous l'a pas encore partagé. "
                        + "Demandez-lui de le partager avec votre adresse Google, puis réessayez.");
                }
            });
            if (ok)
            {
                await LoadCalendars(id);
                await LoadMonth(SelectedDay, null);
                days.Focus();
                var cal = CurrentCalendar;
                Ui.Info(this, "Agenda ajouté à votre liste" + (cal != null && cal.Id == id ? " : " + cal : "."));
            }
        }

        void CopyCalendarId()
        {
            if (!RequireSingle()) return;
            var cal = CurrentCalendar;
            try
            {
                Clipboard.SetText(cal.Id);
                Ui.Info(this, "Identifiant de l'agenda copié dans le presse-papiers :\r\n" + cal.Id);
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        async void CreateCalendar()
        {
            string name;
            using (var d = new InputDialog("Créer un agenda", "Un nouvel agenda vide, séparé de votre agenda principal. Pratique pour le partager (famille, association…).", "&Nom du nouvel agenda :", ""))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Length == 0) return;
                name = d.Value;
            }
            var primary = calendarList.FirstOrDefault(c => c.Primary);
            string tz = primary != null && !string.IsNullOrEmpty(primary.TimeZone) ? primary.TimeZone : "Europe/Paris";
            string newId = null;
            bool ok = await Guard(async () => { newId = await api.CreateCalendar(name, tz); });
            if (ok)
            {
                await LoadCalendars(newId);
                await LoadMonth(SelectedDay, null);
                days.Focus();
                Ui.Info(this, "Agenda « " + name + " » créé. Vous pouvez maintenant le partager avec le menu Agenda, « Partager cet agenda ».");
            }
        }

        async void RemoveCalendar()
        {
            if (!RequireSingle()) return;
            var cal = CurrentCalendar;
            if (cal.Primary) { Ui.Error(this, "Votre agenda principal ne peut pas être retiré."); return; }
            bool delete = false;
            if (cal.IsOwner)
            {
                var r = MessageBox.Show(this,
                    "Vous êtes propriétaire de l'agenda « " + cal.Name + " ».\r\n\r\n"
                    + "Oui : le SUPPRIMER DÉFINITIVEMENT, avec tous ses événements, pour vous et pour les personnes avec qui il est partagé.\r\n"
                    + "Non : seulement le retirer de votre liste (il continue d'exister).\r\n"
                    + "Annuler : ne rien faire.",
                    "Retirer ou supprimer l'agenda", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                if (r == DialogResult.Cancel) return;
                delete = r == DialogResult.Yes;
                if (delete && !Ui.Confirm(this, "Dernière confirmation : supprimer définitivement « " + cal.Name + " » et tous ses événements ?")) return;
            }
            else if (!Ui.Confirm(this, "Retirer l'agenda « " + cal.Name + " » de votre liste ? Vous pourrez le rajouter plus tard s'il est toujours partagé avec vous."))
                return;

            bool ok = await Guard(async () =>
            {
                if (delete) await api.DeleteCalendar(cal.Id);
                else await api.RemoveCalendarFromList(cal.Id);
            });
            if (ok)
            {
                await LoadCalendars("*");
                await LoadMonth(SelectedDay, null);
                days.Focus();
                SetStatus(delete ? "Agenda supprimé." : "Agenda retiré de la liste.");
            }
        }

    }
}
