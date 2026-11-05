using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Une alarme à venir : un rendez-vous et le moment où il faut sonner.</summary>
    class AlarmItem
    {
        public EventInfo Event;
        public DateTime When;
        public int Minutes;
        public override string ToString()
        {
            return "À " + EventInfo.TimeText(When) + (When.Date != DateTime.Today ? " " + EventInfo.DayText(When, false).ToLower(EventInfo.Fr) : "")
                + " (" + AlarmText.Delay(Minutes) + ") : " + EventText.LineOnDay(Event, Event.Start.Date) + AlarmText.CalendarSuffix(Event);
        }
    }

    static class AlarmText
    {
        public static string Delay(int minutes)
        {
            if (minutes <= 0) return "à l'heure exacte";
            return Duration(minutes) + " avant";
        }

        public static string Duration(int minutes)
        {
            if (minutes < 60) return minutes + " minute" + (minutes > 1 ? "s" : "");
            int h = minutes / 60, m = minutes % 60;
            if (minutes % 1440 == 0) { int d = minutes / 1440; return d + " jour" + (d > 1 ? "s" : ""); }
            if (m == 0) return h + " heure" + (h > 1 ? "s" : "");
            return h + " h " + m.ToString("00");
        }

        /// <summary>« Dans 30 minutes », « Maintenant », « Commencé depuis 2 minutes ».</summary>
        public static string Until(DateTime start)
        {
            int m = (int)Math.Round((start - DateTime.Now).TotalMinutes);
            if (m > 0) return "Dans " + Duration(m);
            if (m == 0) return "Maintenant";
            return "Commencé depuis " + Duration(-m);
        }

        public static string CalendarSuffix(EventInfo ev)
        {
            return ev.Calendar != null && !ev.Calendar.Primary ? " (agenda : " + ev.Calendar.Name + ")" : "";
        }

        public static string ForEvent(EventInfo ev)
        {
            if (ev.AllDay) return "aucune (rendez-vous sur toute la journée)";
            var mins = ev.PopupMinutes(ev.Calendar);
            string s = mins.Count == 0 ? "aucune" : string.Join(", ", mins.Select(Delay));
            return s + (ev.RemindersUseDefault ? " (alarmes par défaut de l'agenda)" : "");
        }
    }

    /// <summary>Sons fabriqués en mémoire.</summary>
    static class Ring
    {
        static SoundPlayer ring, chime;
        public static SoundPlayer Make(params double[] segments) // paires (fréquence Hz, durée ms) ; fréquence 0 = silence
        {
            const int rate = 22050;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int samples = 0;
            for (int s = 0; s < segments.Length; s += 2) samples += (int)(rate * segments[s + 1] / 1000);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
            for (int s = 0; s < segments.Length; s += 2)
            {
                double f = segments[s]; int n = (int)(rate * segments[s + 1] / 1000);
                for (int i = 0; i < n; i++)
                {
                    double env = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.006));
                    double v = f <= 0 ? 0 : (Math.Sin(2 * Math.PI * f * i / rate) * 0.7 + Math.Sin(4 * Math.PI * f * i / rate) * 0.3) * env * 0.45;
                    w.Write((short)(v * short.MaxValue));
                }
            }
            w.Flush();
            ms.Position = 0;
            var p = new SoundPlayer(ms);
            p.Load();
            return p;
        }

        /// <summary>Sonnerie d'alarme, jouée en boucle.</summary>
        public static void StartAlarm()
        {
            try
            {
                if (ring == null) ring = Make(988, 140, 0, 70, 988, 140, 0, 70, 1319, 260, 0, 1100);
                ring.PlayLooping();
            }
            catch { }
        }

        public static void Stop() { try { if (ring != null) ring.Stop(); } catch { } }

        /// <summary>Petit carillon pour le résumé du matin.</summary>
        public static void Chime()
        {
            try
            {
                if (chime == null) chime = Make(659, 160, 784, 160, 1047, 320);
                chime.Play();
            }
            catch { }
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>Met la fenêtre au premier plan, même si une autre application a le clavier,
        /// pour que NVDA ou JAWS la lisent tout de suite.</summary>
        public static void ForceForeground(Form f)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
                bool attached = fgThread != me && AttachThreadInput(me, fgThread, true);
                BringWindowToTop(f.Handle);
                SetForegroundWindow(f.Handle);
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            catch { }
            f.Activate();
        }
    }

    /// <summary>Démarrage automatique avec Windows (clé Run de l'utilisateur).</summary>
    static class AutoStart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "AgendaGoogleAccessible";
        public static bool Enabled
        {
            get
            {
                try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key)) return k != null && k.GetValue(Name) != null; }
                catch { return false; }
            }
            set
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value) k.SetValue(Name, "\"" + Application.ExecutablePath + "\" /demarrage");
                    else if (k.GetValue(Name) != null) k.DeleteValue(Name);
                }
            }
        }
    }

    /// <summary>Fenêtre d'alerte (alarme ou résumé du jour), au premier plan, lue par le lecteur d'écran.</summary>
    class AlertForm : Form
    {
        public const DialogResult Snooze = DialogResult.Retry, Open = DialogResult.Yes;
        readonly bool ringing;
        readonly Timer stopRing;

        public AlertForm(string title, string text, bool isAlarm)
        {
            ringing = isAlarm;
            Text = title;
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(720, 380);
            KeyPreview = true;

            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), AccessibleName = title, BackColor = SystemColors.Window, TabIndex = 0
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), TabIndex = 1 };
            // Fenêtre non modale : chaque bouton la ferme lui-même (DialogResult seul ne suffit pas).
            var ok = Ui.MakeButton(isAlarm ? "&Arrêter (Échap)" : "&Fermer (Échap)", (s, e) => Finish(DialogResult.OK));
            buttons.Controls.Add(ok);
            if (isAlarm)
            {
                var snooze = Ui.MakeButton("Rappeler dans &5 minutes", (s, e) => Finish(Snooze));
                buttons.Controls.Add(snooze);
            }
            var open = Ui.MakeButton("&Ouvrir l'agenda", (s, e) => Finish(Open));
            buttons.Controls.Add(open);
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape || (e.KeyCode == Keys.Enter && !e.Alt && !e.Control && !(ActiveControl is Button)))
                { e.Handled = true; e.SuppressKeyPress = true; Finish(DialogResult.OK); }
            };
            Controls.Add(box);
            Controls.Add(buttons);

            // La sonnerie s'arrête d'elle-même au bout de 2 minutes.
            stopRing = new Timer { Interval = 120000 };
            stopRing.Tick += (s, e) => { stopRing.Stop(); Ring.Stop(); };
            Shown += (s, e) =>
            {
                Native.ForceForeground(this);
                box.Focus(); box.Select(0, 0);
                if (ringing) { Ring.StartAlarm(); stopRing.Start(); } else Ring.Chime();
            };
            FormClosed += (s, e) => { stopRing.Stop(); if (ringing) Ring.Stop(); };
        }

        void Finish(DialogResult r)
        {
            DialogResult = r;
            Close();
        }
    }

    /// <summary>Réglage des alarmes par défaut de ses agendas Google.</summary>
    class DefaultAlarmsForm : Form
    {
        readonly CheckBox[] boxes;
        readonly CheckedListBox cals;
        readonly List<CalendarInfo> calendars;

        public List<int> Minutes { get { return Enumerable.Range(0, boxes.Length).Where(i => boxes[i].Checked).Select(i => EventForm.AlarmMinutes[i]).ToList(); } }
        public List<CalendarInfo> Targets { get { return cals.CheckedIndices.Cast<int>().Select(i => calendars[i]).ToList(); } }

        public DefaultAlarmsForm(List<CalendarInfo> list)
        {
            calendars = list;
            Text = "Alarmes par défaut";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(720, 520);

            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10), AutoScroll = true };
            panel.Controls.Add(new Label { Text = "Alarmes appliquées aux rendez-vous qui gardent « Alarmes par défaut de l'agenda ». Elles sont enregistrées dans Google : votre téléphone les utilise aussi.", AutoSize = true, MaximumSize = new Size(680, 0) });
            var primary = list.FirstOrDefault(c => c.Primary) ?? list.FirstOrDefault();
            var current = primary != null ? primary.DefaultPopupMinutes : new List<int>();
            boxes = new CheckBox[EventForm.AlarmMinutes.Length];
            for (int i = 0; i < boxes.Length; i++)
            {
                boxes[i] = new CheckBox { Text = EventForm.AlarmLabels[i], AutoSize = true, Checked = current.Contains(EventForm.AlarmMinutes[i]) };
                boxes[i].AccessibleName = EventForm.AlarmLabels[i].Replace("&", "");
                panel.Controls.Add(boxes[i]);
            }
            panel.Controls.Add(new Label { Text = "Appliquer à ces &agendas (Espace pour cocher ou décocher) :", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
            cals = new CheckedListBox { Width = 680, Height = 200, CheckOnClick = true, AccessibleName = "Appliquer à ces agendas" };
            foreach (var c in list) cals.Items.Add(c.ToString() + (c.DefaultPopupMinutes.Count > 0 ? ", actuellement : " + string.Join(", ", c.DefaultPopupMinutes.Select(AlarmText.Delay)) : ", actuellement : aucune alarme"), c.CanEdit);
            panel.Controls.Add(cals);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.MakeButton("Enre&gistrer", null); ok.DialogResult = DialogResult.OK;
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(panel);
            Controls.Add(buttons);
            Shown += (s, e) => boxes[0].Focus();
        }
    }

    /// <summary>Surveille tous les agendas : alarmes des rendez-vous et résumé du matin.</summary>
    class AlarmMonitor
    {
        readonly CalendarApi api;
        readonly Func<List<CalendarInfo>> calendars;
        readonly Func<bool> isConnected;
        readonly Action<EventInfo> openEvent;
        readonly Timer timer;
        List<EventInfo> events = new List<EventInfo>();
        DateTime lastRefresh = DateTime.MinValue;
        bool refreshing, loadedOnce;
        readonly HashSet<string> fired = new HashSet<string>();
        readonly List<AlarmItem> snoozed = new List<AlarmItem>();
        string firedDay;
        AlertForm openAlert;

        public bool Enabled = true, SummaryEnabled = true;
        public event Action Changed; // la liste des alarmes a changé (infobulle de la zone de notification)

        public AlarmMonitor(CalendarApi api, Func<List<CalendarInfo>> calendars, Func<bool> isConnected, Action<EventInfo> openEvent)
        {
            this.api = api; this.calendars = calendars; this.isConnected = isConnected; this.openEvent = openEvent;
            LoadFired();
            timer = new Timer { Interval = 20000 };
            timer.Tick += async (s, e) => await Tick();
        }

        public void Start() { timer.Start(); }

        /// <summary>Demande un rechargement au prochain passage (après un ajout ou une modification).</summary>
        public async Task RefreshNow() { lastRefresh = DateTime.MinValue; await Tick(); }

        void LoadFired()
        {
            firedDay = DateTime.Today.ToString("yyyyMMdd");
            string saved = Settings.Get("alarmes_faites", "");
            int bar = saved.IndexOf('|');
            if (bar > 0 && saved.Substring(0, bar) == firedDay)
                foreach (var k in saved.Substring(bar + 1).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) fired.Add(k);
        }

        void SaveFired()
        {
            Settings.Set("alarmes_faites", firedDay + "|" + string.Join(";", fired));
        }

        static string Key(EventInfo ev, int minutes)
        {
            return (ev.Calendar != null ? ev.Calendar.Id : "") + "/" + ev.Id + "/" + ev.Start.ToString("yyyyMMddHHmm") + "/" + minutes;
        }

        async Task Tick()
        {
            if (!isConnected() || calendars().Count == 0) return;
            string today = DateTime.Today.ToString("yyyyMMdd");
            if (today != firedDay) { firedDay = today; fired.Clear(); SaveFired(); lastRefresh = DateTime.MinValue; }
            if (!refreshing && (DateTime.Now - lastRefresh).TotalMinutes >= 10)
            {
                refreshing = true;
                try
                {
                    var list = new List<EventInfo>();
                    DateTime from = DateTime.Today.AddDays(-1), to = DateTime.Today.AddDays(3);
                    foreach (var cal in calendars().Where(c => c.AccessRole != "freeBusyReader"))
                    {
                        try
                        {
                            foreach (var ev in await api.ListEvents(cal.Id, from, to, null)) { ev.Calendar = cal; list.Add(ev); }
                        }
                        catch (ApiException) { }
                    }
                    events = list;
                    lastRefresh = DateTime.Now;
                    loadedOnce = true;
                    if (Changed != null) Changed();
                }
                catch { /* réseau indisponible : on réessaiera au prochain passage */ lastRefresh = DateTime.Now.AddMinutes(-8); }
                finally { refreshing = false; }
            }
            if (!loadedOnce) return;
            MaybeSummary();
            CheckAlarms();
        }

        /// <summary>Toutes les alarmes prévues entre deux moments, dans l'ordre.</summary>
        public List<AlarmItem> Upcoming(DateTime from, DateTime to)
        {
            var r = new List<AlarmItem>();
            foreach (var ev in events.Where(e => !e.AllDay))
                foreach (int m in ev.PopupMinutes(ev.Calendar))
                {
                    var when = ev.Start.AddMinutes(-m);
                    if (when >= from && when < to) r.Add(new AlarmItem { Event = ev, When = when, Minutes = m });
                }
            r.AddRange(snoozed.Where(a => a.When >= from && a.When < to));
            return r.OrderBy(a => a.When).ToList();
        }

        public AlarmItem Next()
        {
            return Upcoming(DateTime.Now, DateTime.Now.AddDays(2)).FirstOrDefault();
        }

        public List<EventInfo> EventsOn(DateTime day)
        {
            return events.Where(e => EventText.OccursOn(e, day))
                .GroupBy(e => (e.Calendar != null ? e.Calendar.Id : "") + e.Id).Select(g => g.First())
                .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start).ToList();
        }

        void CheckAlarms()
        {
            if (!Enabled) return;
            var now = DateTime.Now;
            var due = new List<AlarmItem>();
            // Pour chaque rendez-vous, on ne sonne qu'une fois : pour la dernière alarme échue
            // (si l'ordinateur était éteint à l'heure d'une alarme, elle sonne au rallumage tant que le rendez-vous n'a pas commencé).
            foreach (var ev in events.Where(e => !e.AllDay && e.Start.AddMinutes(3) > now))
            {
                var mins = ev.PopupMinutes(ev.Calendar).Where(m => ev.Start.AddMinutes(-m) <= now).ToList();
                if (mins.Count == 0) continue;
                int last = mins.Min();
                if (last > 0 && ev.Start <= now) continue; // déjà commencé et pas d'alarme « à l'heure exacte »
                var keys = mins.Select(m => Key(ev, m)).ToList();
                if (keys.All(fired.Contains)) continue;
                foreach (var k in keys) fired.Add(k);
                due.Add(new AlarmItem { Event = ev, When = ev.Start.AddMinutes(-last), Minutes = last });
            }
            foreach (var s in snoozed.Where(a => a.When <= now).ToList()) { snoozed.Remove(s); due.Add(s); }
            if (due.Count == 0) return;
            SaveFired();
            // Une nouvelle alarme remplace la fenêtre précédente (résumé ou alarme restée ouverte).
            if (openAlert != null) { var old = openAlert; openAlert = null; old.Close(); }
            ShowAlarm(due);
        }

        void ShowAlarm(List<AlarmItem> due)
        {
            var sb = new StringBuilder();
            foreach (var a in due.OrderBy(a => a.Event.Start))
            {
                var ev = a.Event;
                sb.AppendLine(AlarmText.Until(ev.Start) + ", à " + EventInfo.TimeText(ev.Start) + " : " + EventText.Title(ev) + ".");
                if (!string.IsNullOrWhiteSpace(ev.Location)) sb.AppendLine("Lieu : " + ev.Location.Trim());
                if (ev.Calendar != null) sb.AppendLine("Agenda : " + ev.Calendar.Name);
                sb.AppendLine();
            }
            var first = due[0].Event;
            string title = "Alarme : " + EventText.Title(first) + " à " + EventInfo.TimeText(first.Start) + (due.Count > 1 ? " (et " + (due.Count - 1) + " autre" + (due.Count > 2 ? "s" : "") + ")" : "");
            var form = new AlertForm(title, sb.ToString().TrimEnd(), true);
            openAlert = form;
            form.FormClosed += (s, e) =>
            {
                var r = form.DialogResult;
                if (openAlert == form) openAlert = null;
                if (r == AlertForm.Snooze)
                    foreach (var a in due) snoozed.Add(new AlarmItem { Event = a.Event, When = DateTime.Now.AddMinutes(5), Minutes = a.Minutes });
                else if (r == AlertForm.Open) openEvent(first);
                if (Changed != null) Changed();
            };
            openAlert.Show();
        }

        void MaybeSummary()
        {
            string today = DateTime.Today.ToString("yyyyMMdd");
            if (!SummaryEnabled || Settings.Get("resume_fait") == today || DateTime.Now.Hour < 5 || openAlert != null) return;
            Settings.Set("resume_fait", today);
            ShowSummary();
        }

        public string SummaryText()
        {
            var list = EventsOn(DateTime.Today);
            var sb = new StringBuilder();
            sb.AppendLine(EventText.DayLine(DateTime.Today, list.Count) + ".");
            sb.AppendLine();
            foreach (var ev in list) sb.AppendLine(EventText.LineOnDay(ev, DateTime.Today) + AlarmText.CalendarSuffix(ev) + ".");
            var alarms = Upcoming(DateTime.Now, DateTime.Today.AddDays(1));
            if (list.Count > 0) sb.AppendLine();
            if (alarms.Count == 0) sb.AppendLine("Aucune alarme prévue d'ici ce soir.");
            else
            {
                sb.AppendLine("Alarmes prévues aujourd'hui :");
                foreach (var a in alarms) sb.AppendLine("À " + EventInfo.TimeText(a.When) + " pour " + EventText.Title(a.Event) + " (" + AlarmText.Delay(a.Minutes) + ").");
            }
            var tomorrow = EventsOn(DateTime.Today.AddDays(1));
            sb.AppendLine();
            sb.AppendLine("Demain : " + EventText.Count(tomorrow.Count) + ".");
            return sb.ToString().TrimEnd();
        }

        public void ShowSummary()
        {
            if (openAlert != null) return;
            var list = EventsOn(DateTime.Today);
            string greet = DateTime.Now.Hour < 18 ? "Bonjour" : "Bonsoir";
            var form = new AlertForm(greet + ", aujourd'hui " + EventText.Count(list.Count), SummaryText(), false);
            openAlert = form;
            form.FormClosed += (s, e) =>
            {
                var r = form.DialogResult;
                if (openAlert == form) openAlert = null;
                if (r == AlertForm.Open) openEvent(null);
            };
            openAlert.Show();
        }
    }
}
