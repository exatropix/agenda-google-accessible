using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Pilotage de l'application Zoom : ouverture directe d'une réunion, envoi de ses touches
    /// (Alt+A micro, Alt+V caméra, Alt+Y main levée, Alt+Q quitter) et lecture de l'état du micro.</summary>
    static class Zoom
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);

        public static bool IsZoomLink(string url)
        {
            Uri u;
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out u)
                && (u.Host == "zoom.us" || u.Host.EndsWith(".zoom.us"));
        }

        /// <summary>« https://us02web.zoom.us/j/850123?pwd=abc » devient « zoommtg://zoom.us/join?action=join&amp;confno=850123&amp;pwd=abc »,
        /// qui ouvre directement l'application Zoom sans passer par le navigateur.</summary>
        public static string ToAppLink(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return null;
            var m = Regex.Match(u.AbsolutePath, @"^/(?:j|w|s|wc/join)/(\d{9,12})");
            if (!m.Success) return null;
            string link = "zoommtg://" + u.Host + "/join?action=join&confno=" + m.Groups[1].Value;
            var pwd = Regex.Match(u.Query, @"[?&]pwd=([A-Za-z0-9._-]+)");
            if (pwd.Success) link += "&pwd=" + pwd.Groups[1].Value;
            return link;
        }

        /// <summary>Ouvre la réunion dans l'application Zoom ; à défaut (Zoom non installé), dans le navigateur.
        /// Renvoie faux si c'est le navigateur qui a été utilisé.</summary>
        public static bool Open(string url)
        {
            string app = ToAppLink(url);
            if (app != null)
            {
                try { System.Diagnostics.Process.Start(app); return true; }
                catch { }
            }
            System.Diagnostics.Process.Start(url);
            return false;
        }

        /// <summary>Fenêtre de la réunion Zoom en cours, ou IntPtr.Zero.</summary>
        public static IntPtr MeetingWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                string cls = sb.ToString();
                if (cls == "ZPContentViewWndClass" || cls == "ConfMultiTabContentWndClass") { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static bool InZoom(IntPtr fg)
        {
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.Equals("Zoom", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static void Activate(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
            keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); // Alt : Windows autorise alors le changement de premier plan
            SetForegroundWindow(h);
        }

        /// <summary>Envoie une touche de Zoom (« %a » pour Alt+A) à la fenêtre de réunion.
        /// Si stay est faux, rend ensuite le clavier à la fenêtre qui l'avait.</summary>
        public static bool Send(string keys, bool stay)
        {
            IntPtr meeting = MeetingWindow();
            if (meeting == IntPtr.Zero) return false;
            IntPtr previous = GetForegroundWindow();
            if (keys.Length == 0) { Activate(meeting); return true; }
            bool already = previous == meeting || InZoom(previous);
            if (!already) { Activate(meeting); Thread.Sleep(250); }
            SendKeys.SendWait(keys);
            if (!already && !stay && IsWindow(previous)) { Thread.Sleep(250); Activate(previous); }
            return true;
        }

        /// <summary>Lit, par UI Automation, le nom du bouton micro ou caméra de Zoom pour en déduire l'état.
        /// Vrai = coupé, faux = actif, null = inconnu.</summary>
        public static bool? IsOff(bool video)
        {
            IntPtr meeting = MeetingWindow();
            if (meeting == IntPtr.Zero) return null;
            try
            {
                const string asm = ", UIAutomationClient, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
                var ae = Type.GetType("System.Windows.Automation.AutomationElement" + asm);
                var cond = Type.GetType("System.Windows.Automation.Condition" + asm);
                var scope = Type.GetType("System.Windows.Automation.TreeScope, UIAutomationTypes, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                if (ae == null || cond == null || scope == null) return null;
                object root = ae.GetMethod("FromHandle").Invoke(null, new object[] { meeting });
                object all = ae.GetMethod("FindAll").Invoke(root, new[] { Enum.ToObject(scope, 4), cond.GetField("TrueCondition").GetValue(null) });
                var current = ae.GetProperty("Current");
                foreach (object el in (IEnumerable)all)
                {
                    object c = current.GetValue(el, null);
                    string name = ((c.GetType().GetProperty("Name").GetValue(c, null) as string) ?? "").ToLowerInvariant();
                    if (name.Length == 0 || name.Length > 60) continue;
                    if (!video)
                    {
                        // Anglais : « Mute my audio » / « Unmute my audio » ; français : « Couper le son » / « Réactiver le son ».
                        if (name.StartsWith("unmute") || name.StartsWith("réactiver le son") || name.StartsWith("activer le son") || name.StartsWith("rétablir le son")) return true;
                        if (name.StartsWith("mute") || name.StartsWith("couper le son") || name.StartsWith("désactiver le son")) return false;
                    }
                    else
                    {
                        if (name.StartsWith("start video") || name.StartsWith("start my video") || name.StartsWith("démarrer la vidéo") || name.StartsWith("lancer la vidéo")) return true;
                        if (name.StartsWith("stop video") || name.StartsWith("stop my video") || name.StartsWith("arrêter la vidéo")) return false;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Bascule le micro ou la caméra et annonce le nouvel état.</summary>
        public static void Toggle(bool video)
        {
            if (!Send(video ? "%v" : "%a", false)) { Speech.Say("Aucune réunion Zoom ouverte."); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(600);
                bool? off = IsOff(video);
                string what = video ? "Caméra" : "Micro";
                SayOnUi(off == null ? what + " basculé." : off.Value ? what + " coupé" + (video ? "e." : ".") : what + (video ? " allumée." : " ouvert."));
            });
        }

        /// <summary>Parle depuis le fil de l'interface (les lecteurs d'écran n'aiment pas les autres fils).</summary>
        public static void SayOnUi(string text)
        {
            var f = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.IsHandleCreated && !x.IsDisposed);
            if (f != null) f.BeginInvoke((Action)(() => Speech.Say(text)));
            else Speech.Say(text);
        }

        /// <summary>Demande comment ouvrir une réunion Zoom : 1 = avec la télécommande, 2 = dans Zoom seulement, 0 = annuler.</summary>
        public static int AskMode(IWin32Window owner, string title)
        {
            using (var f = new Form { Text = "Rejoindre la réunion Zoom", Font = Ui.BaseFont, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink })
            {
                int choice = 0;
                var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
                panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(440, 0), Text = "Rejoindre « " + title + " » :" });
                var remote = Ui.MakeButton("Avec la &télécommande de l'agenda (micro, caméra, main levée…)", (s, e) => { choice = 1; f.Close(); });
                var only = Ui.MakeButton("Dans &Zoom seulement", (s, e) => { choice = 2; f.Close(); });
                var cancel = Ui.MakeButton("Annuler", (s, e) => f.Close());
                panel.Controls.Add(remote); panel.Controls.Add(only); panel.Controls.Add(cancel);
                f.Controls.Add(panel);
                f.CancelButton = cancel; f.AcceptButton = remote;
                f.Shown += (s, e) => remote.Focus();
                f.ShowDialog(owner);
                return choice;
            }
        }

        public const string Help =
@"Touches de Zoom (dans la fenêtre de la réunion) :
Alt+A : couper ou rétablir le micro
Alt+V : couper ou rétablir la caméra
Alt+Y : lever ou baisser la main
Alt+H : afficher ou masquer la discussion
Alt+U : afficher ou masquer les participants
Alt+S : partager l'écran
Alt+Q : quitter la réunion
Ctrl+Alt+Maj : atteindre la barre de commandes de la réunion
F6 : passer d'une zone de Zoom à l'autre

Dans Zoom, Paramètres, Raccourcis clavier, on peut aussi rendre Alt+A et Alt+V utilisables partout.";
    }

    /// <summary>Télécommande Zoom : petite fenêtre à boutons, toujours au premier plan.</summary>
    class ZoomRemoteForm : Form
    {
        static ZoomRemoteForm instance;
        readonly Label status;

        public static void ShowFor(string title)
        {
            if (instance == null || instance.IsDisposed) instance = new ZoomRemoteForm();
            instance.Text = "Télécommande Zoom" + (string.IsNullOrEmpty(title) ? "" : " : " + title);
            instance.Show();
            instance.Activate();
        }

        ZoomRemoteForm()
        {
            Font = Ui.BaseFont;
            TopMost = true;
            MinimizeBox = true; MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            KeyPreview = true;

            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
            status = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Text = "Zoom s'ouvre. Revenez ici avec Alt+Tab quand la réunion a commencé." };
            panel.Controls.Add(status);
            panel.Controls.Add(Ui.MakeButton("&Micro : couper ou rétablir", (s, e) => Zoom.Toggle(false)));
            panel.Controls.Add(Ui.MakeButton("&Caméra : couper ou rétablir", (s, e) => Zoom.Toggle(true)));
            panel.Controls.Add(Ui.MakeButton("Lever ou baisser la &main", (s, e) => Do("%y", "Main levée ou baissée.")));
            panel.Controls.Add(Ui.MakeButton("É&tat du micro et de la caméra", (s, e) => SayState()));
            panel.Controls.Add(Ui.MakeButton("&Aller dans la fenêtre Zoom", (s, e) => { if (!Zoom.Send("", true)) Speech.Say("Aucune réunion Zoom ouverte."); }));
            panel.Controls.Add(Ui.MakeButton("&Quitter la réunion…", (s, e) => LeaveMeeting()));
            panel.Controls.Add(Ui.MakeButton("Aide-mémoire des touches &Zoom", (s, e) => Ui.Info(this, Zoom.Help)));
            var close = Ui.MakeButton("&Fermer la télécommande", (s, e) => Close());
            panel.Controls.Add(close);
            CancelButton = close;
            Controls.Add(panel);
        }

        void Do(string keys, string done)
        {
            if (Zoom.Send(keys, false)) Speech.Say(done); else Speech.Say("Aucune réunion Zoom ouverte.");
        }

        void SayState()
        {
            if (Zoom.MeetingWindow() == IntPtr.Zero) { Speech.Say("Aucune réunion Zoom ouverte."); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool? mic = Zoom.IsOff(false), cam = Zoom.IsOff(true);
                Zoom.SayOnUi((mic == null ? "Micro : état inconnu" : mic.Value ? "Micro coupé" : "Micro ouvert")
                    + ", " + (cam == null ? "caméra : état inconnue." : cam.Value ? "caméra coupée." : "caméra allumée."));
            });
        }

        void LeaveMeeting()
        {
            if (Zoom.MeetingWindow() == IntPtr.Zero) { Speech.Say("Aucune réunion Zoom ouverte."); return; }
            if (!Ui.Confirm(this, "Quitter la réunion Zoom ?\r\n\r\nZoom demandera encore de confirmer : choisissez « Quitter la réunion »."))
                return;
            Zoom.Send("%q", true);
        }
    }
}
