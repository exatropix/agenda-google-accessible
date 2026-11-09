using System;
using System.Net;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Agenda Google accessible")]
[assembly: AssemblyDescription("Gestion accessible (NVDA, JAWS) d'un agenda Google : événements, partage, agendas partagés.")]
[assembly: AssemblyCompany("Exatropic")]
[assembly: AssemblyProduct("Agenda Google accessible")]
[assembly: AssemblyCopyright("Luc Rasschaert")]
[assembly: AssemblyVersion("1.7.5.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.6.2", FrameworkDisplayName = ".NET Framework 4.6.2")]
[assembly: AssemblyFileVersion("1.7.5.0")]

namespace AgendaAccessible
{
    static class Program
    {
        /// <summary>Cherche une autre copie de l'agenda, plus ancienne que celle-ci, en cours d'exécution.</summary>
        static bool OlderCopyRunning(out Tuple<System.Diagnostics.Process, string> found)
        {
            found = null;
            try
            {
                int me = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("AgendaGoogleAccessible"))
                {
                    if (p.Id == me) continue;
                    try
                    {
                        var fv = p.MainModule.FileVersionInfo;
                        var v = new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, 0);
                        var mine = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(0, AppInfo.Version.Build), 0);
                        if (v < mine)
                        {
                            found = Tuple.Create(p, v.Build > 0 ? v.ToString(3) : v.ToString(2));
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        [STAThread]
        static void Main(string[] args)
        {
            Func<string, bool> has = name => Array.Exists(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Une seule copie à la fois : si l'agenda tourne déjà (par exemple lancé avec Windows), on le fait apparaître.
            bool created;
            var mutex = new System.Threading.Mutex(true, "AgendaGoogleAccessible_Exatropic", out created);
            bool uninstall = has("/desinstaller");
            bool removeData = false;
            if (uninstall)
            {
                // Les questions d'abord : si l'on répond Non, l'agenda en cours n'est pas fermé.
                if (MessageBox.Show("Désinstaller l'Agenda Google accessible de cet ordinateur ?\r\n\r\nVos rendez-vous restent dans votre agenda Google : rien n'y est effacé.",
                        "Désinstaller", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    if (created) mutex.ReleaseMutex();
                    return;
                }
                removeData = MessageBox.Show("Effacer aussi vos réglages et votre connexion à Google enregistrés sur cet ordinateur ?",
                    "Désinstaller", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                // On demande à la copie en cours de se fermer.
                if (!created) try { System.Threading.EventWaitHandle.OpenExisting("AgendaGoogleAccessible_Quitter").Set(); } catch { }
            }
            bool waitForOld = has("/apresmiseajour") || has("/apresinstallation") || uninstall;
            if (!created && waitForOld)
            {
                try { created = mutex.WaitOne(20000); }
                catch (System.Threading.AbandonedMutexException) { created = true; }
                if (!created && uninstall)
                {
                    MessageBox.Show("L'agenda est encore ouvert. Quittez-le (menu Fichier, « Quitter complètement », ou Ctrl+Q), puis recommencez la désinstallation.",
                        "Désinstaller", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            if (!created && OlderCopyRunning(out var older))
            {
                // Une ancienne version tourne (souvent cachée près de l'horloge) : on propose de la remplacer.
                if (MessageBox.Show("Une ancienne version de l'agenda (" + older.Item2 + ") est déjà ouverte.\r\n\r\nLa fermer pour lancer la version " + AppInfo.VersionText + " ?",
                        "Agenda Google accessible", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try { older.Item1.Kill(); older.Item1.WaitForExit(5000); } catch { }
                    try { created = mutex.WaitOne(10000); }
                    catch (System.Threading.AbandonedMutexException) { created = true; }
                }
            }
            if (!created)
            {
                try { System.Threading.EventWaitHandle.OpenExisting("AgendaGoogleAccessible_Afficher").Set(); } catch { }
                return;
            }

            if (uninstall)
            {
                Installer.Uninstall(removeData);
                MessageBox.Show("L'Agenda Google accessible est désinstallé.", "Désinstaller", MessageBoxButtons.OK, MessageBoxIcon.Information);
                mutex.ReleaseMutex();
                GC.KeepAlive(mutex);
                return;
            }

            var show = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "AgendaGoogleAccessible_Afficher");
            var quit = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "AgendaGoogleAccessible_Quitter");
            bool hidden = has("/demarrage");
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Erreur inattendue : " + e.Exception.Message, "Agenda Google accessible", MessageBoxButtons.OK, MessageBoxIcon.Error);
            var form = new MainForm(hidden, has("/apresmiseajour"));
            var waiter = new System.Threading.Thread(() =>
            {
                var handles = new System.Threading.WaitHandle[] { show, quit };
                while (true)
                {
                    int i = System.Threading.WaitHandle.WaitAny(handles);
                    try
                    {
                        // La fenêtre n'existe peut-être pas encore (démarrage) : on attend au lieu d'ignorer la demande.
                        for (int k = 0; k < 150 && !form.IsHandleCreated; k++) System.Threading.Thread.Sleep(200);
                        if (!form.IsHandleCreated) continue;
                        if (i == 0) form.BeginInvoke(new Action(form.ShowMain));
                        else form.BeginInvoke(new Action(form.QuitApp));
                    }
                    catch { }
                }
            }) { IsBackground = true };
            waiter.Start();
            Application.Run(form);
            mutex.ReleaseMutex();
            GC.KeepAlive(mutex);
        }
    }
}
