using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Menu Alarmes, « Choisir la sonnerie » : une liste de sonneries, écoutées dès qu'on s'y déplace,
    /// ou un fichier son personnel (WAV, MP3, WMA).</summary>
    class RingtoneForm : Form
    {
        readonly ListBox list;
        readonly Timer stopTimer = new Timer { Interval = 5000 };
        string fileChoice;
        public string Selected { get; private set; }

        public RingtoneForm()
        {
            Text = "Choisir la sonnerie";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(520, 400);

            var label = new Label { Text = "&Sonneries (chacune est jouée quelques secondes quand vous vous y déplacez) :", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
            list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Sonneries" };
            foreach (var p in Ring.Presets) list.Items.Add(p.Item1);
            string current = Ring.Choice;
            if (current.StartsWith(Ring.FilePrefix)) { fileChoice = current; list.Items.Add(Ring.ChoiceName(current)); }

            int idx = Array.FindIndex(Ring.Presets, p => p.Item2 == current);
            list.SelectedIndex = idx >= 0 ? idx : fileChoice != null ? list.Items.Count - 1 : 0;
            list.SelectedIndexChanged += (s, e) => Preview();
            stopTimer.Tick += (s, e) => { stopTimer.Stop(); Ring.Stop(); };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.MakeButton("Enre&gistrer", (s, e) => { Selected = ChoiceAt(list.SelectedIndex); DialogResult = DialogResult.OK; Close(); });
            var file = Ui.MakeButton("Choisir un &fichier son…", (s, e) => PickFile());
            var listen = Ui.MakeButton("É&couter", (s, e) => Preview());
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok); buttons.Controls.Add(file); buttons.Controls.Add(listen);
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(list); Controls.Add(label); Controls.Add(buttons);
            Shown += (s, e) => list.Focus();
            FormClosed += (s, e) => { stopTimer.Stop(); Ring.Stop(); };
        }

        string ChoiceAt(int i)
        {
            if (i >= 0 && i < Ring.Presets.Length) return Ring.Presets[i].Item2;
            return fileChoice ?? "classique";
        }

        void Preview()
        {
            stopTimer.Stop();
            Ring.StartAlarm(ChoiceAt(list.SelectedIndex));
            stopTimer.Start();
        }

        void PickFile()
        {
            using (var d = new OpenFileDialog { Title = "Choisir un fichier son", Filter = "Fichiers son (*.wav;*.mp3;*.wma)|*.wav;*.mp3;*.wma|Tous les fichiers|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                // On garde une copie : la sonnerie continue de marcher si le fichier d'origine est déplacé.
                string path = d.FileName;
                try
                {
                    string dir = Path.Combine(GoogleAuth.DataDir, "sonneries");
                    Directory.CreateDirectory(dir);
                    string copy = Path.Combine(dir, Path.GetFileName(path));
                    if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase)) File.Copy(path, copy, true);
                    path = copy;
                }
                catch { }
                fileChoice = Ring.FilePrefix + path;
                if (list.Items.Count > Ring.Presets.Length) list.Items.RemoveAt(Ring.Presets.Length);
                list.Items.Add(Ring.ChoiceName(fileChoice));
                list.SelectedIndex = list.Items.Count - 1; // joue le fichier
                list.Focus();
            }
        }
    }

    /// <summary>Agendas exclus de « Tous mes agendas » (par exemple un agenda de numéros de semaine ou de jours fériés).</summary>
    static class CalendarFilter
    {
        static string Key(CalendarInfo c) { return "agenda_masque:" + c.Id; }
        public static bool Hidden(CalendarInfo c) { return Settings.Get(Key(c), "") == "1"; }
        public static void SetHidden(CalendarInfo c, bool hidden) { Settings.Set(Key(c), hidden ? "1" : ""); }
    }

    class IncludedCalendarsForm : Form
    {
        readonly CheckedListBox list;
        readonly List<CalendarInfo> calendars;

        public IncludedCalendarsForm(List<CalendarInfo> calendars)
        {
            this.calendars = calendars;
            Text = "Agendas inclus dans « Tous mes agendas »";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(560, 420);
            var label = new Label
            {
                Text = "&Cochez les agendas à montrer dans « Tous mes agendas », le résumé du matin et les alarmes. "
                     + "Décochez par exemple un agenda de numéros de semaine ou de jours fériés. Espace coche ou décoche.",
                AutoSize = true, MaximumSize = new Size(540, 0), Dock = DockStyle.Top, Padding = new Padding(8)
            };
            list = new CheckedListBox { Dock = DockStyle.Fill, IntegralHeight = false, CheckOnClick = true, AccessibleName = "Agendas" };
            foreach (var c in calendars) list.Items.Add(c.Name, !CalendarFilter.Hidden(c));
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.MakeButton("Enre&gistrer", (s, e) =>
            {
                if (list.CheckedItems.Count == 0) { Ui.Error(this, "Gardez au moins un agenda coché."); list.Focus(); return; }
                for (int i = 0; i < calendars.Count; i++) CalendarFilter.SetHidden(calendars[i], !list.GetItemChecked(i));
                DialogResult = DialogResult.OK; Close();
            });
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            CancelButton = cancel;
            Controls.Add(list); Controls.Add(label); Controls.Add(buttons);
            Shown += (s, e) => list.Focus();
        }
    }
}
