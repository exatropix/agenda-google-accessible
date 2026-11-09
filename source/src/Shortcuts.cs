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

        public static readonly GlobalShortcut ZoomMic = new GlobalShortcut { SettingKey = "rac_zoom_micro", Label = "Zoom : couper ou rétablir le micro", Explanation = "Pendant une réunion Zoom, coupe ou rétablit le micro depuis n'importe où et annonce l'état." };
        public static readonly GlobalShortcut ZoomVideo = new GlobalShortcut { SettingKey = "rac_zoom_camera", Label = "Zoom : couper ou rétablir la caméra", Explanation = "Pendant une réunion Zoom, coupe ou rétablit la caméra depuis n'importe où et annonce l'état." };

        public static readonly GlobalShortcut[] All = { Open, Next, Today, Launch, ZoomMic, ZoomVideo };

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

    /// <summary>Préférences, Raccourcis globaux : pour chaque raccourci, l'utilisateur presse la combinaison voulue
    /// dans une zone de saisie ; l'agenda teste aussitôt si Windows l'accepte et l'annonce.</summary>
    class ShortcutsForm : Form
    {
        readonly Dictionary<GlobalShortcut, CaptureBox> rows = new Dictionary<GlobalShortcut, CaptureBox>();

        public ShortcutsForm()
        {
            Text = "Raccourcis globaux";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(760, 600);

            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };
            panel.Controls.Add(new Label
            {
                Text = "Raccourcis globaux : ils fonctionnent depuis n'importe quelle application. Ils sont désactivés par défaut. "
                     + "Pour en choisir un, placez-vous dans sa zone et pressez la combinaison voulue, par exemple Ctrl+Alt+N : "
                     + "l'agenda vérifie aussitôt si elle est libre et vous le dit. Si rien n'est annoncé, c'est que NVDA ou un autre "
                     + "programme intercepte cette combinaison : essayez-en une autre. Retour arrière ou Suppr désactive le raccourci. "
                     + "Tab passe à la zone suivante.",
                AutoSize = true, MaximumSize = new Size(720, 0)
            });
            foreach (var g in GlobalShortcut.All)
            {
                var label = new Label { Text = g.Label + " :", AutoSize = true, Margin = new Padding(3, 12, 3, 0) };
                var box = new CaptureBox(this, g) { Width = 400 };
                box.AccessibleName = g.Label + ", pressez la combinaison";
                box.AccessibleDescription = g.Explanation;
                string m0, k0;
                box.Value = GlobalShortcut.Split(g.Value, out m0, out k0) ? g.Value : "";
                panel.Controls.Add(label);
                panel.Controls.Add(box);
                rows[g] = box;
            }
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.MakeButton("Enre&gistrer", null);
            ok.Click += (s, e) => { if (Validate()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            CancelButton = cancel;
            Controls.Add(panel);
            Controls.Add(buttons);
            Shown += (s, e) => rows[GlobalShortcut.Open].Focus();
        }

        public string ValueOf(GlobalShortcut g) { return rows[g].Value; }

        /// <summary>Appelé quand l'utilisateur a pressé une combinaison dans la zone d'un raccourci.</summary>
        internal void Captured(CaptureBox box, string combo)
        {
            var g = box.Shortcut;
            string mods, key;
            if (!GlobalShortcut.Split(combo, out mods, out key))
            {
                Speech.Say(combo + " : combinaison non acceptée. Utilisez Ctrl+Alt, Ctrl+Maj, Ctrl+Alt+Maj, Alt+Maj ou Windows+Maj, puis une lettre, un chiffre ou une touche F1 à F12.");
                return;
            }
            if (g.ForLauncher && !GlobalShortcut.LauncherModifiers.Contains(mods))
            {
                Speech.Say(combo + " : non acceptée pour lancer l'agenda. Windows n'accepte ici que Ctrl+Alt, Ctrl+Maj ou Ctrl+Alt+Maj.");
                return;
            }
            foreach (var other in GlobalShortcut.All)
                if (other != g && rows[other].Value == combo)
                {
                    Ui.Error(this, combo + " est déjà choisi pour « " + other.Label + " ». Pressez une autre combinaison.");
                    box.Focus();
                    return;
                }
            // Une combinaison déjà enregistrée par l'agenda lui-même est forcément libre pour lui.
            bool ours = GlobalShortcut.All.Any(o => !o.ForLauncher && o.Value == combo);
            if (!ours && !IsFree(combo))
            {
                Ui.Error(this, combo + " n'est pas disponible : un autre programme l'utilise déjà. Pressez une autre combinaison.");
                box.Focus();
                return;
            }
            box.Value = combo;
            Speech.Say(combo + " : disponible, retenu pour « " + g.Label + " ». Activez Enregistrer pour valider.");
        }

        internal void Cleared(CaptureBox box)
        {
            box.Value = "";
            Speech.Say("« " + box.Shortcut.Label + " » : désactivé.");
        }

        /// <summary>Essaie d'enregistrer la combinaison auprès de Windows, puis la libère aussitôt.</summary>
        bool IsFree(string combo)
        {
            uint mods, vk;
            if (!GlobalShortcut.ToNative(combo, out mods, out vk)) return false;
            const int testId = 0x7F00;
            if (!Native.RegisterHotKey(Handle, testId, mods, vk)) return false;
            Native.UnregisterHotKey(Handle, testId);
            return true;
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
                    rows[g].Focus();
                    return false;
                }
                used[v] = g;
            }
            return true;
        }
    }

    /// <summary>Zone qui capte une combinaison de touches au lieu de la taper comme du texte.</summary>
    class CaptureBox : TextBox
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetKeyState(int vk);

        readonly ShortcutsForm owner;
        public readonly GlobalShortcut Shortcut;
        string value = "";

        public CaptureBox(ShortcutsForm owner, GlobalShortcut g)
        {
            this.owner = owner; Shortcut = g;
            ReadOnly = true; ShortcutsEnabled = false; BackColor = SystemColors.Window;
        }

        public string Value
        {
            get { return value; }
            set { this.value = value ?? ""; Text = this.value.Length == 0 ? "Désactivé" : this.value; }
        }

        static bool WinDown { get { return (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0; } }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            bool win = WinDown;
            // Tab, Maj+Tab et Échap gardent leur rôle habituel.
            if ((code == Keys.Tab && (mods == Keys.None || mods == Keys.Shift) && !win) || (code == Keys.Escape && mods == Keys.None))
                return base.ProcessCmdKey(ref msg, keyData);
            if ((code == Keys.Back || code == Keys.Delete) && mods == Keys.None && !win) { owner.Cleared(this); return true; }
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin)
                return true; // on attend la touche principale
            if (mods == Keys.None && !win) return base.ProcessCmdKey(ref msg, keyData); // flèches, etc. : lecture normale

            string key;
            if (code >= Keys.A && code <= Keys.Z) key = ((char)code).ToString();
            else if (code >= Keys.D0 && code <= Keys.D9) key = ((int)(code - Keys.D0)).ToString();
            else if (code >= Keys.NumPad0 && code <= Keys.NumPad9) key = ((int)(code - Keys.NumPad0)).ToString();
            else if (code >= Keys.F1 && code <= Keys.F12) key = "F" + (code - Keys.F1 + 1);
            else key = code.ToString();
            var parts = new List<string>();
            if (win) parts.Add("Windows");
            if ((mods & Keys.Control) != 0) parts.Add("Ctrl");
            if ((mods & Keys.Alt) != 0) parts.Add("Alt");
            if ((mods & Keys.Shift) != 0) parts.Add("Maj");
            owner.Captured(this, string.Join("+", parts) + "+" + key);
            return true;
        }

        // Alt+lettre arrive comme touche système : on la capte aussi, au lieu d'ouvrir un menu.
        protected override bool ProcessDialogChar(char charCode) { return Focused || base.ProcessDialogChar(charCode); }
        protected override void OnKeyPress(KeyPressEventArgs e) { e.Handled = true; }
    }
}

namespace AgendaAccessible
{
    /// <summary>Touches des commandes de l'agenda (menus), modifiables par l'utilisateur :
    /// beaucoup de combinaisons sont déjà prises par NVDA ou ses modules complémentaires.</summary>
    static class AppKeys
    {
        public class Command
        {
            public string Id, Name;
            public ToolStripMenuItem Item;
            public Keys Default;
            public string DefaultDisplay;
        }

        public static readonly List<Command> Commands = new List<Command>();

        public static void Register(ToolStripMenuItem item, Keys keys, string display)
        {
            string name = item.Text.Replace("&", "").Replace("…", "").Trim();
            if (Commands.Any(c => c.Id == name)) return;
            Commands.Add(new Command { Id = name, Name = name, Item = item, Default = keys, DefaultDisplay = display });
        }

        static string SettingKey(Command c) { return "touche:" + c.Id; }

        /// <summary>Touche effective d'une commande (réglage de l'utilisateur, sinon celle par défaut).</summary>
        public static Keys Current(Command c)
        {
            string v = Settings.Get(SettingKey(c), "");
            if (v == "") return c.Default;
            if (v == "aucune") return Keys.None;
            Keys k;
            return Enum.TryParse(v, out k) && IsValid(k) ? k : c.Default;
        }

        public static void Set(Command c, Keys k)
        {
            Settings.Set(SettingKey(c), k == c.Default ? "" : k == Keys.None ? "aucune" : k.ToString());
        }

        /// <summary>Applique les touches choisies aux menus.</summary>
        public static void ApplyAll()
        {
            // Ancienne option « Ctrl+Flèches » décochée : on retire ces quatre touches.
            if (Settings.Get("ctrl_fleches", "1") == "0" && Settings.Get("ctrl_fleches_migre") != "1")
            {
                foreach (var c in Commands.Where(c => (c.Default & Keys.KeyCode) == Keys.Left || (c.Default & Keys.KeyCode) == Keys.Right
                                                   || (c.Default & Keys.KeyCode) == Keys.Up || (c.Default & Keys.KeyCode) == Keys.Down))
                    Set(c, Keys.None);
                Settings.Set("ctrl_fleches_migre", "1");
            }
            foreach (var c in Commands) Apply(c, Current(c));
        }

        static void Apply(Command c, Keys k)
        {
            try { c.Item.ShortcutKeys = k; } catch { c.Item.ShortcutKeys = Keys.None; k = Keys.None; }
            c.Item.ShortcutKeyDisplayString = k == c.Default && c.DefaultDisplay != null ? c.DefaultDisplay : (k == Keys.None ? null : Describe(k));
        }

        /// <summary>Une touche seule n'est permise que pour F1 à F12 ; Alt seul + lettre est réservé aux menus.</summary>
        public static bool IsValid(Keys k)
        {
            if (k == Keys.None) return true;
            Keys code = k & Keys.KeyCode, mods = k & Keys.Modifiers;
            bool fkey = code >= Keys.F1 && code <= Keys.F12;
            if (mods == Keys.None || mods == Keys.Shift) return fkey;
            if (mods == Keys.Alt && !fkey) return false;
            return true;
        }

        public static readonly Tuple<string, Keys>[] ModifierChoices =
        {
            Tuple.Create("Aucune touche", Keys.None),
            Tuple.Create("Ctrl", Keys.Control),
            Tuple.Create("Ctrl+Maj", Keys.Control | Keys.Shift),
            Tuple.Create("Ctrl+Alt", Keys.Control | Keys.Alt),
            Tuple.Create("Ctrl+Alt+Maj", Keys.Control | Keys.Alt | Keys.Shift),
            Tuple.Create("Alt+Maj", Keys.Alt | Keys.Shift),
            Tuple.Create("Maj (avec F1 à F12)", Keys.Shift),
            Tuple.Create("Seule (F1 à F12)", (Keys)(-1)),
        };

        public static readonly Tuple<string, Keys>[] KeyChoices = BuildKeyChoices();

        static Tuple<string, Keys>[] BuildKeyChoices()
        {
            var l = new List<Tuple<string, Keys>>();
            for (char c = 'A'; c <= 'Z'; c++) l.Add(Tuple.Create(c.ToString(), (Keys)c));
            for (int i = 0; i <= 9; i++) l.Add(Tuple.Create(i.ToString(), Keys.D0 + i));
            for (int i = 1; i <= 12; i++) l.Add(Tuple.Create("F" + i, Keys.F1 + i - 1));
            l.Add(Tuple.Create("Flèche droite", Keys.Right));
            l.Add(Tuple.Create("Flèche gauche", Keys.Left));
            l.Add(Tuple.Create("Flèche haut", Keys.Up));
            l.Add(Tuple.Create("Flèche bas", Keys.Down));
            l.Add(Tuple.Create("Origine", Keys.Home));
            l.Add(Tuple.Create("Fin", Keys.End));
            l.Add(Tuple.Create("Page précédente", Keys.PageUp));
            l.Add(Tuple.Create("Page suivante", Keys.PageDown));
            l.Add(Tuple.Create("Suppr", Keys.Delete));
            l.Add(Tuple.Create("Inser", Keys.Insert));
            return l.ToArray();
        }

        /// <summary>« Ctrl+Maj+Flèche droite ».</summary>
        public static string Describe(Keys k)
        {
            if (k == Keys.None) return "aucune touche";
            var parts = new List<string>();
            if ((k & Keys.Control) != 0) parts.Add("Ctrl");
            if ((k & Keys.Alt) != 0) parts.Add("Alt");
            if ((k & Keys.Shift) != 0) parts.Add("Maj");
            var code = k & Keys.KeyCode;
            var kc = KeyChoices.FirstOrDefault(t => t.Item2 == code);
            parts.Add(kc != null ? kc.Item1 : code.ToString());
            return string.Join("+", parts);
        }
    }

    /// <summary>Préférences, Touches de l'agenda : la liste des commandes, et pour chacune le choix de sa touche.</summary>
    class AppKeysForm : Form
    {
        readonly ListBox list;
        readonly ComboBox mods, keys;
        readonly Dictionary<AppKeys.Command, Keys> work = new Dictionary<AppKeys.Command, Keys>();
        bool updating;

        public Dictionary<AppKeys.Command, Keys> Result { get { return work; } }

        class Row
        {
            public AppKeys.Command C; public AppKeysForm F;
            public override string ToString() { return C.Name + " : " + AppKeys.Describe(F.work[C]); }
        }

        public AppKeysForm()
        {
            Text = "Touches de l'agenda";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(820, 620);
            foreach (var c in AppKeys.Commands) work[c] = AppKeys.Current(c);

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            table.Controls.Add(new Label
            {
                Text = "Choisissez une commande dans la liste, puis sa nouvelle touche, et activez « Appliquer ». "
                     + "Choisissez « Aucune touche » pour libérer une combinaison utilisée par NVDA ou l'un de ses modules.",
                AutoSize = true, MaximumSize = new Size(780, 0)
            });
            table.Controls.Add(new Label { Text = "&Commandes :", AutoSize = true, Margin = new Padding(3, 10, 3, 3) });
            list = new ListBox { Width = 780, Height = 330, AccessibleName = "Commandes et leurs touches" };
            foreach (var c in AppKeys.Commands) list.Items.Add(new Row { C = c, F = this });
            table.Controls.Add(list);

            var line = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            line.Controls.Add(new Label { Text = "&Modificateurs :", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            mods = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, AccessibleName = "Modificateurs" };
            foreach (var m in AppKeys.ModifierChoices) mods.Items.Add(m.Item1);
            line.Controls.Add(mods);
            line.Controls.Add(new Label { Text = "&Touche :", AutoSize = true, Margin = new Padding(12, 8, 3, 3) });
            keys = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, AccessibleName = "Touche" };
            foreach (var k in AppKeys.KeyChoices) keys.Items.Add(k.Item1);
            line.Controls.Add(keys);
            var apply = Ui.MakeButton("A&ppliquer", (s, e) => ApplySelection());
            line.Controls.Add(apply);
            table.Controls.Add(line);
            var reset = Ui.MakeButton("&Rétablir toutes les touches par défaut", (s, e) =>
            {
                foreach (var c in AppKeys.Commands) work[c] = c.Default;
                RefreshList();
                Speech.Say("Touches par défaut rétablies.");
            });
            table.Controls.Add(reset);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.MakeButton("Enre&gistrer", null); ok.DialogResult = DialogResult.OK;
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            CancelButton = cancel;
            Controls.Add(table);
            Controls.Add(buttons);

            mods.SelectedIndexChanged += (s, e) => { if (!updating) keys.Enabled = mods.SelectedIndex != 0; };
            list.SelectedIndexChanged += (s, e) => ShowSelection();
            list.SelectedIndex = 0;
            Shown += (s, e) => list.Focus();
        }

        void ShowSelection()
        {
            var r = list.SelectedItem as Row;
            if (r == null) return;
            updating = true;
            Keys k = work[r.C];
            Keys m = k & Keys.Modifiers, code = k & Keys.KeyCode;
            int mi = 0;
            if (k != Keys.None)
            {
                mi = Array.FindIndex(AppKeys.ModifierChoices, t => t.Item2 == m);
                if (m == Keys.None) mi = AppKeys.ModifierChoices.Length - 1;
                if (mi < 0) mi = 1;
            }
            mods.SelectedIndex = mi;
            int ki = Array.FindIndex(AppKeys.KeyChoices, t => t.Item2 == code);
            keys.SelectedIndex = ki >= 0 ? ki : 0;
            keys.Enabled = mi != 0;
            updating = false;
        }

        void ApplySelection()
        {
            var r = list.SelectedItem as Row;
            if (r == null) return;
            Keys k = Keys.None;
            if (mods.SelectedIndex > 0)
            {
                var m = AppKeys.ModifierChoices[mods.SelectedIndex].Item2;
                if (m == (Keys)(-1)) m = Keys.None;
                k = m | AppKeys.KeyChoices[Math.Max(0, keys.SelectedIndex)].Item2;
            }
            if (!AppKeys.IsValid(k))
            {
                Ui.Error(this, "Cette combinaison n'est pas possible : une touche seule (ou avec Maj) n'est permise que pour F1 à F12, "
                    + "et Alt avec une lettre est réservé à l'ouverture des menus. Ajoutez Ctrl, ou Alt+Maj.");
                mods.Focus();
                return;
            }
            if (k != Keys.None)
            {
                var other = work.FirstOrDefault(p => p.Key != r.C && p.Value == k).Key;
                if (other != null)
                {
                    Ui.Error(this, AppKeys.Describe(k) + " est déjà la touche de « " + other.Name + " ». Changez d'abord celle-ci, ou choisissez une autre combinaison.");
                    keys.Focus();
                    return;
                }
            }
            work[r.C] = k;
            RefreshList();
            list.Focus();
            Speech.Say(r.C.Name + " : " + AppKeys.Describe(k) + ".");
        }

        void RefreshList()
        {
            int i = list.SelectedIndex;
            for (int n = 0; n < list.Items.Count; n++) list.Items[n] = list.Items[n];
            list.SelectedIndex = Math.Max(0, i);
        }
    }
}
