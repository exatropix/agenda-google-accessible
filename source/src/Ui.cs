using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Outils d'interface : champs étiquetés lisibles par NVDA/JAWS, analyse des dates et heures.</summary>
    static class Ui
    {
        public static readonly Font BaseFont = new Font("Segoe UI", 11F);

        public static TableLayoutPanel FieldTable()
        {
            var t = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(10),
                AutoScroll = true
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        /// <summary>Ajoute « étiquette + champ » ; l'étiquette précède le champ dans l'ordre de tabulation,
        /// et le nom accessible du champ est fixé explicitement.</summary>
        public static T AddField<T>(TableLayoutPanel table, string labelText, T control, ref int tab) where T : Control
        {
            var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3), TabIndex = tab++ };
            control.AccessibleName = labelText.Replace("&", "").TrimEnd(' ', ':');
            control.TabIndex = tab++;
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(3, 4, 3, 4);
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(label, 0, row);
            table.Controls.Add(control, 1, row);
            return control;
        }

        public static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 3, 6, 3), Margin = new Padding(4) };
            b.AccessibleName = text.Replace("&", "");
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static void Error(IWin32Window owner, string message)
        {
            MessageBox.Show(owner, message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static void Info(IWin32Window owner, string message)
        {
            MessageBox.Show(owner, message, "Agenda Google accessible", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static bool Confirm(IWin32Window owner, string message)
        {
            return MessageBox.Show(owner, message, "Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        public static string FormatDate(DateTime d) { return d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture); }
        public static string FormatTime(DateTime d) { return d.ToString("HH:mm", CultureInfo.InvariantCulture); }

        /// <summary>Accepte : 03/11/2026, 3/11/26, 3-11-2026, 3.11.2026, 3/11 (année en cours),
        /// et les mots « aujourd'hui », « demain », « après-demain ».</summary>
        public static bool TryParseDate(string text, out DateTime date)
        {
            date = DateTime.Today;
            if (text == null) return false;
            string s = text.Trim().ToLowerInvariant();
            if (s == "aujourd'hui" || s == "aujourdhui" || s == "auj") { date = DateTime.Today; return true; }
            if (s == "demain") { date = DateTime.Today.AddDays(1); return true; }
            if (s == "après-demain" || s == "apres-demain" || s == "après demain" || s == "apres demain") { date = DateTime.Today.AddDays(2); return true; }
            s = s.Replace('-', '/').Replace('.', '/').Replace(' ', '/');
            var parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int d, m, y = DateTime.Today.Year;
            if (parts.Length < 2 || parts.Length > 3) return false;
            if (!int.TryParse(parts[0], out d) || !int.TryParse(parts[1], out m)) return false;
            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[2], out y)) return false;
                if (y < 100) y += 2000;
            }
            if (m < 1 || m > 12 || y < 1900 || y > 2200 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
            date = new DateTime(y, m, d);
            return true;
        }

        /// <summary>Accepte : 14:30, 14h30, 14 h 30, 14h, 14, 9.15</summary>
        public static bool TryParseTime(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (text == null) return false;
            string s = text.Trim().ToLowerInvariant().Replace(" ", "").Replace('h', ':').Replace('.', ':');
            if (s.Length == 0) return false;
            var parts = s.Split(new[] { ':' }, StringSplitOptions.None);
            int h, m = 0;
            if (!int.TryParse(parts[0], out h)) return false;
            if (parts.Length > 1 && parts[1].Length > 0 && !int.TryParse(parts[1], out m)) return false;
            if (parts.Length > 2) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            time = new TimeSpan(h, m, 0);
            return true;
        }
    }

    /// <summary>Petite boîte de saisie d'un texte.</summary>
    class InputDialog : Form
    {
        readonly TextBox box;

        public string Value { get { return box.Text.Trim(); } }

        public InputDialog(string title, string explanation, string label, string initial)
        {
            Text = title;
            Font = Ui.BaseFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 230);
            int tab = 0;

            var table = Ui.FieldTable();
            if (!string.IsNullOrEmpty(explanation))
            {
                var info = new TextBox { Text = explanation, ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, TabStop = true, Height = 70, BackColor = SystemColors.Control };
                info.AccessibleName = "Explication";
                table.RowCount = 1;
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(info, 0, 0);
                table.SetColumnSpan(info, 2);
                info.Dock = DockStyle.Fill;
                info.TabIndex = 99;
            }
            box = Ui.AddField(table, label, new TextBox { Text = initial ?? "" }, ref tab);
            if (!string.IsNullOrEmpty(explanation)) box.AccessibleDescription = explanation;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.MakeButton("OK", null); ok.DialogResult = DialogResult.OK;
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            AcceptButton = ok; CancelButton = cancel;
            ok.TabIndex = tab++; cancel.TabIndex = tab++;

            table.TabIndex = 0; buttons.TabIndex = 1;
            Controls.Add(table);
            Controls.Add(buttons);
            Shown += (s, e) => { box.Focus(); box.SelectAll(); };
        }
    }
}
