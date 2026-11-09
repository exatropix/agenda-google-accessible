using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Un raccourci clavier global, enregistré dans les réglages sous forme de texte (« Ctrl+Alt+P »), vide s'il est désactivé.</summary>
    class GlobalShortcut
    {
        public string SettingKey, Label, Explanation;
        public bool ForLauncher; // raccourci du menu Démarrer (lance l'agenda quand il est fermé)

        public string Value
        {
            get { return Settings.Get(SettingKey, ""); }
            set { Settings.Set(SettingKey, value ?? ""); }
        }

        public static readonly GlobalShortcut Open = new GlobalShortcut { SettingKey = "rac_ouvrir", Label = "Afficher l'agenda", Explanation = "Fait apparaître l'agenda quand il tourne près de l'horloge." };
        public static readonly GlobalShortcut Next = new GlobalShortcut { SettingKey = "rac_prochain", Label = "Dire le prochain rendez-vous", Explanation = "NVDA dit le prochain rendez-vous, sans ouvrir l'agenda." };
        public static readonly GlobalShortcut Today = new GlobalShortcut { SettingKey = "rac_jour", Label = "Dire les rendez-vous du jour", Explanation = "NVDA dit les rendez-vous d'aujourd'hui, sans ouvrir l'agenda." };
        public static readonly GlobalShortcut Launch = new GlobalShortcut { SettingKey = "rac_lancer", Label = "Lancer l'agenda quand il est fermé", Explanation = "Raccourci du menu Démarrer. Windows n'accepte ici que des combinaisons avec Ctrl+Alt ou Ctrl+Maj.", ForLauncher = true };

        public static readonly GlobalShortcut[] All = { Open, Next, Today, Launch };

        public static readonly string[] Modifiers = { "Ctrl+Alt", "Ctrl+Maj", "Ctrl+Alt+Maj", "Alt+Maj", "Windows+Maj" };
        public static readonly string[] LauncherModifiers = { "Ctrl+Alt", "Ctrl+Maj", "Ctrl+Alt+Maj" };
        public static readonly string[] KeyNames = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range(0, 10).Select(i => i.ToString()))
            .Concat(Enumerable.Range(1, 12).Select(i => "F" + i)).ToArray();

        /// <summary>Découpe « Ctrl+Alt+P » en modificateurs et touche. Faux si le texte est vide ou invalide.</summary>
        public static bool Split(string value, out string mods, out string key)
        {
            mods = key = null;
            if (string.IsNullOrEmpty(value)) return false;
            int i = value.LastIndexOf('+');
            if (i <= 0) return false;
            mods = value.Substring(0, i);
            key = value.Substring(i + 1);
            return Modifiers.Contains(mods) && KeyNames.Contains(key);
        }

        /// <summary>Valeurs pour RegisterHotKey (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8, MOD_NOREPEAT=0x4000).</summary>
        public static bool ToNative(string value, out uint mods, out uint vk)
        {
            mods = 0x4000; vk = 0;
            string m, k;
            if (!Split(value, out m, out k)) return false;
            foreach (var part in m.Split('+'))
            {
                if (part == "Ctrl") mods |= 2;
                else if (part == "Alt") mods |= 1;
                else if (part == "Maj") mods |= 4;
                else if (part == "Windows") mods |= 8;
            }
            vk = k.Length == 1 ? (uint)k[0] : (uint)(Keys.F1 + int.Parse(k.Substring(1)) - 1);
            return true;
        }

        /// <summary>Format attendu par les raccourcis Windows (.lnk) : « Ctrl+Alt+G », « Ctrl+Shift+G ».</summary>
        public static string ToLnk(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Replace("Maj", "Shift");
        }

        /// <summary>Texte affiché dans les menus : la combinaison choisie, ou rien si le raccourci est désactivé.</summary>
        public string MenuText { get { return string.IsNullOrEmpty(Value) ? null : Value + ", partout"; } }
    }

    /// <summary>Préférences, Raccourcis clavier globaux : pour chaque raccourci, l'activer ou non et choisir sa combinaison.</summary>
    class ShortcutsForm : Form
    {
        readonly Dictionary<GlobalShortcut, Tuple<CheckBox, ComboBox, ComboBox>> rows = new Dictionary<GlobalShortcut, Tuple<CheckBox, ComboBox, ComboBox>>();
        readonly CheckBox ctrlArrows;

        public bool CtrlArrows { get { return ctrlArrows.Checked; } }

        public ShortcutsForm()
        {
            Text = "Raccourcis clavier";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(760, 640);

            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };
            panel.Controls.Add(new Label
            {
                Text = "Raccourcis globaux : ils fonctionnent depuis n'importe quelle application. Ils sont désactivés par défaut, "
                     + "car ils peuvent entrer en conflit avec d'autres programmes. Cochez ceux que vous voulez et choisissez leurs touches.",
                AutoSize = true, MaximumSize = new Size(720, 0)
            });
            char[] mnemonics = { 'f', 'p', 'j', 'l' };
            int n = 0;
            foreach (var g in GlobalShortcut.All)
            {
                string mods, key;
                bool on = GlobalShortcut.Split(g.Value, out mods, out key);
                var box = new GroupBox { Text = g.Label, Width = 720, Height = 120 };
                var cb = new CheckBox { Text = "Activer : " + Mnemonic(g.Label, mnemonics[n]), Location = new Point(12, 26), AutoSize = true, Checked = on };
                cb.AccessibleName = "Activer : " + g.Label;
                cb.AccessibleDescription = g.Explanation;
                var modList = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(12, 70), Width = 220 };
                modList.Items.AddRange(g.ForLauncher ? GlobalShortcut.LauncherModifiers : GlobalShortcut.Modifiers);
                modList.AccessibleName = g.Label + " : touches de modification";
                var keyList = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(250, 70), Width = 120 };
                keyList.Items.AddRange(GlobalShortcut.KeyNames);
                keyList.AccessibleName = g.Label + " : touche";
                modList.SelectedItem = on && modList.Items.Contains(mods) ? mods : "Ctrl+Alt";
                keyList.SelectedItem = on ? key : DefaultKey(g);
                modList.Enabled = keyList.Enabled = on;
                cb.CheckedChanged += (s, e) => modList.Enabled = keyList.Enabled = cb.Checked;
                box.Controls.Add(cb); box.Controls.Add(modList); box.Controls.Add(keyList);
                box.Controls.Add(new Label { Text = "puis", Location = new Point(236, 74), AutoSize = true, AccessibleRole = AccessibleRole.StaticText });
                panel.Controls.Add(box);
                rows[g] = Tuple.Create(cb, modList, keyList);
                n++;
            }
            ctrlArrows = new CheckBox
            {
                Text = "Dans la liste des rendez-vous, Ctrl+&Flèches déplace le rendez-vous d'un jour ou d'une heure",
                AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(3, 14, 3, 3),
                Checked = Settings.Get("ctrl_fleches", "1") == "1"
            };
            ctrlArrows.AccessibleName = ctrlArrows.Text.Replace("&", "");
            panel.Controls.Add(ctrlArrows);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.MakeButton("Enre&gistrer", null);
            ok.Click += (s, e) => { if (Validate()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(panel);
            Controls.Add(buttons);
            Shown += (s, e) => rows[GlobalShortcut.Open].Item1.Focus();
        }

        static string Mnemonic(string label, char c)
        {
            int i = label.IndexOf(c);
            if (i < 0) i = label.ToLower().IndexOf(c);
            return i < 0 ? label : label.Substring(0, i) + "&" + label.Substring(i);
        }

        static string DefaultKey(GlobalShortcut g)
        {
            return g == GlobalShortcut.Open ? "A" : g == GlobalShortcut.Next ? "P" : g == GlobalShortcut.Today ? "J" : "G";
        }

        public string ValueOf(GlobalShortcut g)
        {
            var r = rows[g];
            return r.Item1.Checked ? r.Item2.SelectedItem + "+" + r.Item3.SelectedItem : "";
        }

        new bool Validate()
        {
            var used = new Dictionary<string, GlobalShortcut>();
            foreach (var g in GlobalShortcut.All)
            {
                string v = ValueOf(g);
                if (v.Length == 0) continue;
                GlobalShortcut other;
                if (used.TryGetValue(v, out other))
                {
                    Ui.Error(this, "La combinaison " + v + " est choisie deux fois : pour « " + other.Label + " » et pour « " + g.Label + " ». Choisissez-en une autre.");
                    rows[g].Item3.Focus();
                    return false;
                }
                used[v] = g;
            }
            return true;
        }
    }
}
