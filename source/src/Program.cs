using System;
using System.Net;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Agenda Google accessible")]
[assembly: AssemblyDescription("Gestion accessible (NVDA, JAWS) d'un agenda Google : événements, partage, agendas partagés.")]
[assembly: AssemblyCompany("Exatropic")]
[assembly: AssemblyProduct("Agenda Google accessible")]
[assembly: AssemblyCopyright("François Burlet")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.6.2", FrameworkDisplayName = ".NET Framework 4.6.2")]
[assembly: AssemblyFileVersion("1.6.0.0")]

namespace AgendaAccessible
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Une seule copie à la fois : si l'agenda tourne déjà (par exemple lancé avec Windows), on le fait apparaître.
            bool created;
            var mutex = new System.Threading.Mutex(true, "AgendaGoogleAccessible_Exatropic", out created);
            if (!created)
            {
                try { System.Threading.EventWaitHandle.OpenExisting("AgendaGoogleAccessible_Afficher").Set(); } catch { }
                return;
            }
            var show = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "AgendaGoogleAccessible_Afficher");
            bool hidden = Array.Exists(args, a => a.Equals("/demarrage", StringComparison.OrdinalIgnoreCase));
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Erreur inattendue : " + e.Exception.Message, "Agenda Google accessible", MessageBoxButtons.OK, MessageBoxIcon.Error);
            var form = new MainForm(hidden);
            var waiter = new System.Threading.Thread(() =>
            {
                while (show.WaitOne())
                {
                    try { if (form.IsHandleCreated) form.BeginInvoke(new Action(form.ShowMain)); } catch { }
                }
            }) { IsBackground = true };
            waiter.Start();
            Application.Run(form);
            GC.KeepAlive(mutex);
        }
    }
}
