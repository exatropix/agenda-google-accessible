using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Fiche d'un événement : création, modification ou simple consultation.</summary>
    class EventForm : Form
    {
        readonly TextBox title, startDate, startTime, endDate, endTime, location, description, guests;
        readonly CheckBox allDay, alarmDefault;
        readonly CheckBox[] alarmBoxes;
        readonly ComboBox calendarBox, repeatBox;
        readonly TextBox repeatUntil;
        readonly bool isSeriesInstance;

        // Choix de répétition, dans l'ordre de la liste.
        static readonly string[] RepeatLabels =
        {
            "Ne se répète pas",
            "Tous les jours",
            "Du lundi au vendredi",
            "Toutes les semaines, le même jour",
            "Toutes les 2 semaines, le même jour",
            "Tous les mois, à la même date",
            "Tous les ans, à la même date",
            "Anniversaire (tous les ans, toute la journée)"
        };
        const int RepeatAnniversary = 7;
        readonly bool readOnly;
        readonly string originalGuests;
        CalendarInfo calendar;
        readonly List<Reminder> otherReminders; // rappels existants hors des 4 cases (conservés)
        readonly string originalAlarms;

        public static readonly int[] AlarmMinutes = { 60, 30, 15, 0 };
        public static readonly string[] AlarmLabels = { "1 &heure avant", "30 &minutes avant", "15 mi&nutes avant", "À l'heure e&xacte" };

        public EventInfo Result { get; private set; }
        public bool GuestsChanged { get; private set; }

        /// <param name="choices">Agendas proposés pour un nouveau rendez-vous (null : agenda imposé).</param>
        public EventForm(CalendarInfo cal, EventInfo existing, DateTime defaultDay, bool readOnly, List<CalendarInfo> choices = null)
        {
            this.readOnly = readOnly;
            calendar = cal;
            bool isNew = existing == null;
            Text = readOnly ? "Détails de l'événement" : isNew ? "Nouvel événement" : "Modifier l'événement";
            if (choices == null) Text += " (" + cal.Name + ")";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(800, 860);
            int tab = 0;

            var table = Ui.FieldTable();
            if (choices != null && choices.Count > 0)
            {
                calendarBox = Ui.AddField(table, "&Ranger dans l'agenda :", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, ref tab);
                foreach (var c in choices) calendarBox.Items.Add(c);
                int i = choices.FindIndex(c => c.Id == cal.Id);
                calendarBox.SelectedIndex = i >= 0 ? i : 0;
                calendar = (CalendarInfo)calendarBox.SelectedItem;
            }
            title = Ui.AddField(table, "&Titre :", new TextBox(), ref tab);
            allDay = Ui.AddField(table, "Toute la &journée :", new CheckBox { Text = "Toute la journée" }, ref tab);
            allDay.AccessibleName = "Toute la journée";
            startDate = Ui.AddField(table, "&Date de début (jj/mm/aaaa) :", new TextBox(), ref tab);
            startTime = Ui.AddField(table, "Heure de dé&but (hh:mm) :", new TextBox(), ref tab);
            endDate = Ui.AddField(table, "Date de &fin (jj/mm/aaaa) :", new TextBox(), ref tab);
            endTime = Ui.AddField(table, "Heure de f&in (hh:mm) :", new TextBox(), ref tab);
            location = Ui.AddField(table, "&Lieu :", new TextBox(), ref tab);
            description = Ui.AddField(table, "D&escription :", new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = 120 }, ref tab);
            guests = Ui.AddField(table, "In&vités (adresses e-mail séparées par des virgules) :", new TextBox(), ref tab);
            guests.AccessibleDescription = "Les invités reçoivent un e-mail d'invitation de Google.";
            isSeriesInstance = existing != null && !string.IsNullOrEmpty(existing.RecurringEventId);
            repeatBox = Ui.AddField(table, "Ré&pétition :", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, ref tab);
            if (isSeriesInstance)
            {
                repeatBox.Items.Add("Fait partie d'une série : seule cette occurrence sera modifiée");
                repeatBox.Enabled = false;
            }
            else if (existing != null)
            {
                repeatBox.Items.Add("Ne se répète pas (la répétition se choisit à la création)");
                repeatBox.Enabled = false;
            }
            else foreach (var l in RepeatLabels) repeatBox.Items.Add(l);
            repeatBox.SelectedIndex = 0;
            repeatUntil = Ui.AddField(table, "Fin de la répétiti&on (jj/mm/aaaa, vide : sans fin) :", new TextBox(), ref tab);
            repeatUntil.Enabled = false;
            alarmDefault = AddCheck(table, "&Alarmes par défaut de l'agenda", ref tab);
            alarmDefault.AccessibleDescription = "Décochez pour choisir vous-même les alarmes de ce rendez-vous.";
            alarmBoxes = new CheckBox[AlarmMinutes.Length];
            for (int i = 0; i < AlarmMinutes.Length; i++) alarmBoxes[i] = AddCheck(table, "Alarme " + AlarmLabels[i], ref tab);

            if (isNew)
            {
                DateTime start = defaultDay.Date == DateTime.Today
                    ? DateTime.Today.AddHours(Math.Min(DateTime.Now.Hour + 1, 23))
                    : defaultDay.Date.AddHours(9);
                existing = new EventInfo { Start = start, End = start.AddHours(1) };
            }
            title.Text = existing.Summary ?? "";
            allDay.Checked = existing.AllDay;
            startDate.Text = Ui.FormatDate(existing.Start);
            endDate.Text = Ui.FormatDate(existing.End);
            startTime.Text = existing.AllDay ? "09:00" : Ui.FormatTime(existing.Start);
            endTime.Text = existing.AllDay ? "10:00" : Ui.FormatTime(existing.End);
            location.Text = existing.Location ?? "";
            description.Text = (existing.Description ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            originalGuests = string.Join(", ", existing.Attendees);
            guests.Text = originalGuests;
            Result = existing;

            otherReminders = existing.ReminderOverrides.Where(r => r.Method != "popup" || Array.IndexOf(AlarmMinutes, r.Minutes) < 0).ToList();
            alarmDefault.Checked = isNew || existing.RemindersUseDefault;
            if (!alarmDefault.Checked)
            {
                var mins = existing.PopupMinutes(calendar);
                for (int i = 0; i < AlarmMinutes.Length; i++) alarmBoxes[i].Checked = mins.Contains(AlarmMinutes[i]);
            }
            originalAlarms = AlarmState();

            allDay.CheckedChanged += (s, e) => UpdateTimeFields();
            repeatBox.SelectedIndexChanged += (s, e) =>
            {
                if (!repeatBox.Enabled) return;
                repeatUntil.Enabled = repeatBox.SelectedIndex > 0;
                if (repeatBox.SelectedIndex == RepeatAnniversary) allDay.Checked = true;
            };
            alarmDefault.CheckedChanged += (s, e) =>
            {
                if (!alarmDefault.Checked)
                {
                    // On part des alarmes par défaut, que l'on peut ensuite modifier.
                    var mins = calendar != null ? calendar.DefaultPopupMinutes : new List<int>();
                    for (int i = 0; i < AlarmMinutes.Length; i++) alarmBoxes[i].Checked = mins.Contains(AlarmMinutes[i]);
                }
                UpdateTimeFields();
            };
            if (calendarBox != null)
                calendarBox.SelectedIndexChanged += (s, e) => { calendar = (CalendarInfo)calendarBox.SelectedItem; UpdateTimeFields(); };
            startDate.Leave += (s, e) =>
            {
                DateTime sd, ed;
                if (Ui.TryParseDate(startDate.Text, out sd))
                {
                    startDate.Text = Ui.FormatDate(sd);
                    if (!Ui.TryParseDate(endDate.Text, out ed) || ed < sd) endDate.Text = Ui.FormatDate(sd);
                }
            };
            startTime.Leave += (s, e) =>
            {
                // Si on décale l'heure de début, on garde la même durée.
                TimeSpan st, et;
                if (Ui.TryParseTime(startTime.Text, out st) && Ui.TryParseTime(endTime.Text, out et) && et <= st && startDate.Text == endDate.Text)
                    endTime.Text = Ui.FormatTime(DateTime.Today.Add(st).AddHours(1));
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            if (readOnly)
            {
                foreach (var tb in new[] { title, startDate, startTime, endDate, endTime, location, description, guests }) tb.ReadOnly = true;
                allDay.AutoCheck = false;
                var close = Ui.MakeButton("Fermer", null); close.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(close);
                CancelButton = close;
                close.TabIndex = 0;
                if (!string.IsNullOrEmpty(existing.Organizer)) description.AccessibleDescription = "Organisateur : " + existing.Organizer;
            }
            else
            {
                var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
                var save = Ui.MakeButton("Enre&gistrer", (s, e) => Save());
                buttons.Controls.Add(cancel); buttons.Controls.Add(save);
                AcceptButton = save; CancelButton = cancel;
                save.TabIndex = 0; cancel.TabIndex = 1;
            }
            table.TabIndex = 0; buttons.TabIndex = 1;
            Controls.Add(table);
            Controls.Add(buttons);
            UpdateTimeFields();
            Shown += (s, e) => title.Focus();
        }

        CheckBox AddCheck(TableLayoutPanel table, string text, ref int tab)
        {
            var cb = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 4), TabIndex = tab++ };
            cb.AccessibleName = text.Replace("&", "");
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(cb, 1, row);
            return cb;
        }

        string AlarmState()
        {
            return alarmDefault.Checked ? "d" : string.Join(",", alarmBoxes.Select(b => b.Checked ? "1" : "0"));
        }

        void UpdateTimeFields()
        {
            startTime.Enabled = !allDay.Checked;
            endTime.Enabled = !allDay.Checked;
            // Les alarmes ne concernent que les rendez-vous avec une heure.
            alarmDefault.Enabled = !allDay.Checked && !readOnly;
            bool custom = !allDay.Checked && !alarmDefault.Checked && !readOnly;
            if (alarmDefault.Checked)
            {
                var mins = calendar != null ? calendar.DefaultPopupMinutes : new List<int>();
                for (int i = 0; i < AlarmMinutes.Length; i++) alarmBoxes[i].Checked = mins.Contains(AlarmMinutes[i]);
            }
            foreach (var b in alarmBoxes) b.Enabled = custom;
        }

        void Fail(Control c, string message)
        {
            Ui.Error(this, message);
            c.Focus();
            var tb = c as TextBox;
            if (tb != null) tb.SelectAll();
        }

        void Save()
        {
            DateTime sd, ed;
            TimeSpan st = TimeSpan.Zero, et = TimeSpan.Zero;
            if (!Ui.TryParseDate(startDate.Text, out sd)) { Fail(startDate, "La date de début n'est pas valide. Exemple : 25/12/2026."); return; }
            if (string.IsNullOrWhiteSpace(endDate.Text)) ed = sd;
            else if (!Ui.TryParseDate(endDate.Text, out ed)) { Fail(endDate, "La date de fin n'est pas valide. Exemple : 25/12/2026."); return; }
            if (!allDay.Checked)
            {
                if (!Ui.TryParseTime(startTime.Text, out st)) { Fail(startTime, "L'heure de début n'est pas valide. Exemple : 14:30 ou 14h30."); return; }
                if (!Ui.TryParseTime(endTime.Text, out et)) { Fail(endTime, "L'heure de fin n'est pas valide. Exemple : 15:30 ou 15h30."); return; }
            }
            DateTime start = allDay.Checked ? sd : sd.Add(st);
            DateTime end = allDay.Checked ? ed : ed.Add(et);
            if (allDay.Checked ? end < start : end <= start) { Fail(endDate, "La fin doit être après le début."); return; }

            var mails = guests.Text.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var m in mails)
            {
                if (!m.Contains("@") || m.StartsWith("@") || m.EndsWith("@")) { Fail(guests, "L'adresse « " + m + " » ne semble pas être une adresse e-mail."); return; }
            }

            List<string> rule = null;
            if (repeatBox.Enabled && repeatBox.SelectedIndex > 0)
            {
                string freq;
                switch (repeatBox.SelectedIndex)
                {
                    case 1: freq = "FREQ=DAILY"; break;
                    case 2: freq = "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"; break;
                    case 3: freq = "FREQ=WEEKLY"; break;
                    case 4: freq = "FREQ=WEEKLY;INTERVAL=2"; break;
                    case 5: freq = "FREQ=MONTHLY"; break;
                    default: freq = "FREQ=YEARLY"; break;
                }
                if (!string.IsNullOrWhiteSpace(repeatUntil.Text))
                {
                    DateTime until;
                    if (!Ui.TryParseDate(repeatUntil.Text, out until)) { Fail(repeatUntil, "La date de fin de la répétition n'est pas valide. Exemple : 31/12/2027."); return; }
                    if (until < sd) { Fail(repeatUntil, "La fin de la répétition doit être après le premier rendez-vous."); return; }
                    freq += ";UNTIL=" + (allDay.Checked
                        ? until.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
                        : until.Date.AddDays(1).AddSeconds(-1).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                }
                rule = new List<string> { "RRULE:" + freq };
            }

            var r = new EventInfo
            {
                Id = Result.Id,
                Summary = title.Text.Trim(),
                Location = location.Text.Trim(),
                Description = description.Text.Replace("\r\n", "\n"),
                AllDay = allDay.Checked,
                Start = start,
                End = end,
                Attendees = mails,
                Calendar = calendar,
                RecurringEventId = Result.RecurringEventId,
                Recurrence = rule,
                TimeZone = calendar != null ? calendar.TimeZone : null
            };
            if (!r.AllDay && (Result.Id == null || AlarmState() != originalAlarms))
            {
                r.SendReminders = true;
                r.RemindersUseDefault = alarmDefault.Checked;
                if (!alarmDefault.Checked)
                {
                    var list = new List<Reminder>(otherReminders);
                    for (int i = 0; i < AlarmMinutes.Length; i++)
                        if (alarmBoxes[i].Checked) list.Add(new Reminder { Method = "popup", Minutes = AlarmMinutes[i] });
                    if (list.Count > 5) { Fail(alarmBoxes[0], "Google accepte au plus 5 alarmes par rendez-vous. Décochez-en une."); return; }
                    r.ReminderOverrides = list;
                }
            }
            else
            {
                r.RemindersUseDefault = Result.RemindersUseDefault;
                r.ReminderOverrides = Result.ReminderOverrides;
            }
            GuestsChanged = !string.Equals(string.Join(", ", mails), originalGuests, StringComparison.OrdinalIgnoreCase);
            Result = r;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
