using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgendaAccessible
{
    // =====================================================================
    //  Parole : NVDA, puis JAWS, puis la voix de Windows (SAPI).
    // =====================================================================

    /// <summary>Fait dire un texte par le lecteur d'écran, sans ouvrir de fenêtre.</summary>
    static class Speech
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NvdaTest();
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate int NvdaSpeak([MarshalAs(UnmanagedType.LPWStr)] string text);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NvdaCancel();

        static bool nvdaTried;
        static NvdaTest nvdaTest;
        static NvdaSpeak nvdaSpeak;
        static NvdaCancel nvdaCancel;
        static object sapi;

        /// <summary>Bibliothèque de NVDA, intégrée au programme lors de la publication, extraite au premier usage.</summary>
        static void LoadNvda()
        {
            if (nvdaTried) return;
            nvdaTried = true;
            try
            {
                string name = Environment.Is64BitProcess ? "nvdaControllerClient64.dll" : "nvdaControllerClient32.dll";
                string path = Path.Combine(GoogleAuth.DataDir, name);
                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (res == null) return;
                    if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
                    {
                        Directory.CreateDirectory(GoogleAuth.DataDir);
                        using (var f = File.Create(path)) res.CopyTo(f);
                    }
                }
                IntPtr lib = LoadLibrary(path);
                if (lib == IntPtr.Zero) return;
                IntPtr t = GetProcAddress(lib, "nvdaController_testIfRunning");
                IntPtr s = GetProcAddress(lib, "nvdaController_speakText");
                IntPtr c = GetProcAddress(lib, "nvdaController_cancelSpeech");
                if (t == IntPtr.Zero || s == IntPtr.Zero) return;
                nvdaTest = (NvdaTest)Marshal.GetDelegateForFunctionPointer(t, typeof(NvdaTest));
                nvdaSpeak = (NvdaSpeak)Marshal.GetDelegateForFunctionPointer(s, typeof(NvdaSpeak));
                if (c != IntPtr.Zero) nvdaCancel = (NvdaCancel)Marshal.GetDelegateForFunctionPointer(c, typeof(NvdaCancel));
            }
            catch { nvdaTest = null; nvdaSpeak = null; }
        }

        static bool NvdaRunning()
        {
            LoadNvda();
            try { return nvdaTest != null && nvdaTest() == 0; } catch { return false; }
        }

        static bool JawsRunning()
        {
            try { return Process.GetProcessesByName("jfw").Length > 0; } catch { return false; }
        }

        /// <summary>Dit le texte. interrupt : coupe d'abord ce qui est en train d'être dit.</summary>
        public static void Say(string text, bool interrupt = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                if (NvdaRunning())
                {
                    if (interrupt && nvdaCancel != null) nvdaCancel();
                    nvdaSpeak(text);
                    return;
                }
                if (JawsRunning())
                {
                    var t = Type.GetTypeFromProgID("FreedomSci.JawsApi");
                    if (t != null)
                    {
                        object jaws = Activator.CreateInstance(t);
                        t.InvokeMember("SayString", BindingFlags.InvokeMethod, null, jaws, new object[] { text, interrupt });
                        return;
                    }
                }
                // Sans lecteur d'écran : la voix de Windows.
                if (sapi == null)
                {
                    var st = Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (st == null) return;
                    sapi = Activator.CreateInstance(st);
                }
                // 1 = asynchrone, 2 = interrompre ce qui précède.
                sapi.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, sapi, new object[] { text, interrupt ? 3 : 1 });
            }
            catch { }
        }
    }

    // =====================================================================
    //  Version, installation et désinstallation
    // =====================================================================

    static class AppInfo
    {
        public const string Repo = "exatropix/agenda-google-accessible";
        public static Version Version { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public static string VersionText
        {
            get { var v = Version; return v.Build > 0 ? v.Major + "." + v.Minor + "." + v.Build : v.Major + "." + v.Minor; }
        }
        public static string ExePath { get { return Application.ExecutablePath; } }
    }

    static class Installer
    {
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AgendaGoogleAccessible"); }
        }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "AgendaGoogleAccessible.exe"); } }
        static string StartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Agenda Google accessible.lnk"); }
        }
        static string DesktopShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Agenda Google accessible.lnk"); }
        }
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgendaGoogleAccessible";

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(AppInfo.ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        public static bool IsInstalled { get { return File.Exists(InstalledExe); } }

        /// <summary>Copie le programme dans son dossier, crée les raccourcis et l'entrée de désinstallation.
        /// Renvoie null si tout va bien, sinon le message d'erreur.</summary>
        public static string Install(bool desktopShortcut)
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                string srcDir = Path.GetDirectoryName(AppInfo.ExePath);
                if (!IsInstalledCopy)
                {
                    File.Copy(AppInfo.ExePath, InstalledExe, true);
                    foreach (var name in new[] { "AgendaGoogleAccessible.exe.config", GoogleAuth.AppCredentialsName, "LISEZMOI.txt" })
                    {
                        string src = Path.Combine(srcDir, name);
                        if (File.Exists(src)) File.Copy(src, Path.Combine(InstallDir, name), true);
                    }
                }
                // Un raccourci qui échoue (stratégie de l'entreprise, par exemple) n'empêche pas le reste de l'installation.
                try { CreateShortcut(StartMenuShortcut, "Ctrl+Alt+G"); } catch { }
                if (desktopShortcut) try { CreateShortcut(DesktopShortcut, null); } catch { }
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "Agenda Google accessible");
                    k.SetValue("DisplayVersion", AppInfo.VersionText);
                    k.SetValue("Publisher", "Exatropic (Luc Rasschaert)");
                    k.SetValue("DisplayIcon", InstalledExe);
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" /desinstaller");
                    k.SetValue("URLInfoAbout", "https://github.com/" + AppInfo.Repo);
                    k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
                AutoStart.SetPath(InstalledExe);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Met à jour le numéro de version affiché dans « Applications installées ».</summary>
        public static void RefreshVersionInfo()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey, true))
                    if (k != null) k.SetValue("DisplayVersion", AppInfo.VersionText);
            }
            catch { }
        }

        static void CreateShortcut(string path, string hotkey)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { InstallDir });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Agenda Google accessible avec NVDA et JAWS" });
            if (hotkey != null) try { lt.InvokeMember("Hotkey", BindingFlags.SetProperty, null, lnk, new object[] { hotkey }); } catch { }
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }

        /// <summary>Retire raccourcis, démarrage automatique et entrée de désinstallation ;
        /// le dossier du programme est effacé juste après la fermeture.</summary>
        public static void Uninstall(bool removeData)
        {
            try { AutoStart.Enabled = false; } catch { }
            foreach (var p in new[] { StartMenuShortcut, DesktopShortcut }) try { if (File.Exists(p)) File.Delete(p); } catch { }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            // Le script attend la fin de ce programme (son fichier est verrouillé tant qu'il tourne), puis efface le dossier.
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("cd /d \"%TEMP%\"");
            sb.AppendLine("set n=0");
            sb.AppendLine(":attente");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("tasklist /fi \"PID eq %AGENDA_PID%\" 2>nul | find \"%AGENDA_PID%\" >nul && if %n% lss 600 goto attente");
            sb.AppendLine("set n=0");
            sb.AppendLine(":effacer");
            sb.AppendLine("rmdir /s /q \"%AGENDA_DOSSIER%\" >nul 2>&1");
            sb.AppendLine("if exist \"%AGENDA_DOSSIER%\" (set /a n+=1 & timeout /t 1 /nobreak >nul & if %n% lss 30 goto effacer)");
            if (removeData) sb.AppendLine("rmdir /s /q \"%AGENDA_DONNEES%\" >nul 2>&1");
            sb.AppendLine("del \"%~f0\"");
            RunHiddenScript(sb.ToString(), new Dictionary<string, string>
            {
                { "AGENDA_PID", Process.GetCurrentProcess().Id.ToString() },
                { "AGENDA_DOSSIER", InstallDir },
                { "AGENDA_DONNEES", GoogleAuth.DataDir }
            });
        }

        /// <summary>Lance un script caché. Les chemins passent par des variables d'environnement :
        /// le script reste en caractères simples, quels que soient les accents ou l'alphabet du nom d'utilisateur.</summary>
        public static void RunHiddenScript(string content, Dictionary<string, string> vars)
        {
            string script = Path.Combine(Path.GetTempPath(), "agenda-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(script, content, Encoding.ASCII);
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            };
            foreach (var kv in vars) psi.EnvironmentVariables[kv.Key] = kv.Value;
            Process.Start(psi);
        }
    }

    // =====================================================================
    //  Mise à jour automatique depuis GitHub
    // =====================================================================

    class UpdateInfo
    {
        public Version Version;
        public string Tag, Notes, ExeUrl;
    }

    static class Updater
    {
        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("AgendaGoogleAccessible/" + AppInfo.VersionText);
            return h;
        }

        public static bool AutoCheck
        {
            get { return Settings.Get("maj_auto", "1") == "1"; }
            set { Settings.Set("maj_auto", value ? "1" : "0"); }
        }

        /// <summary>Cherche la dernière version publiée. Renvoie null s'il n'y a rien de plus récent.</summary>
        public static async Task<UpdateInfo> Check()
        {
            string json = await http.GetStringAsync("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
            var root = Json.Parse(json);
            string tag = Json.Str(root, "tag_name") ?? "";
            Version v;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            v = new Version(v.Major, v.Minor, Math.Max(0, v.Build), 0);
            var cur = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(0, AppInfo.Version.Build), 0);
            if (v <= cur) return null;
            string exeUrl = null;
            foreach (var a in Json.Arr(root, "assets"))
                if (string.Equals(Json.Str(a, "name"), "AgendaGoogleAccessible.exe", StringComparison.OrdinalIgnoreCase))
                    exeUrl = Json.Str(a, "browser_download_url");
            if (exeUrl == null) return null;
            return new UpdateInfo { Version = v, Tag = tag, Notes = Json.Str(root, "body") ?? "", ExeUrl = exeUrl };
        }

        /// <summary>Télécharge la nouvelle version, la met à la place de l'actuelle après la fermeture, puis la relance.</summary>
        public static async Task Install(UpdateInfo info)
        {
            byte[] data = await http.GetByteArrayAsync(info.ExeUrl);
            if (data.Length < 50000 || data[0] != 'M' || data[1] != 'Z') throw new Exception("le fichier téléchargé n'est pas un programme valide.");
            string tmp = Path.Combine(Path.GetTempPath(), "AgendaGoogleAccessible-" + info.Tag + ".exe");
            File.WriteAllBytes(tmp, data);
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("cd /d \"%TEMP%\"");
            sb.AppendLine("set n=0");
            sb.AppendLine(":attente");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("copy /y \"%AGENDA_NOUVEAU%\" \"%AGENDA_CIBLE%\" >nul 2>&1 && goto ok");
            sb.AppendLine("if %n% lss 60 goto attente");
            // Échec : on relance l'ancienne version, qui proposera de nouveau la mise à jour.
            sb.AppendLine("start \"\" \"%AGENDA_CIBLE%\"");
            sb.AppendLine("goto fin");
            sb.AppendLine(":ok");
            sb.AppendLine("start \"\" \"%AGENDA_CIBLE%\" /apresmiseajour");
            sb.AppendLine(":fin");
            sb.AppendLine("del \"%AGENDA_NOUVEAU%\" >nul 2>&1");
            sb.AppendLine("del \"%~f0\"");
            Installer.RunHiddenScript(sb.ToString(), new Dictionary<string, string>
            {
                { "AGENDA_NOUVEAU", tmp },
                { "AGENDA_CIBLE", AppInfo.ExePath }
            });
        }
    }

    // =====================================================================
    //  Saisie rapide : « Dentiste mardi 14h30 Rennes »
    // =====================================================================

    /// <summary>Découpe une phrase en rendez-vous : titre, date, heures, lieu. Sans intelligence artificielle :
    /// des règles simples reconnaissent les jours, les dates et les heures ; ce qui précède est le titre, ce qui suit est le lieu.</summary>
    static class QuickEntry
    {
        static readonly string[] Days = { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };
        static readonly string[] Months = { "janvier", "fevrier", "mars", "avril", "mai", "juin", "juillet", "aout", "septembre", "octobre", "novembre", "decembre" };
        static readonly HashSet<string> Fillers = new HashSet<string> { "le", "a", "au", "de", "du", "des", "pour", "et", "jusqu'a", "jusqu", "vers", "ce", "cette", "prochain", "prochaine", "matin", "soir", "apres-midi", "aprem" };

        static string Norm(string s)
        {
            var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Trim(',', '.', ';', '!', '?');
        }

        static bool TryTime(string t, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (t == "midi") { time = new TimeSpan(12, 0, 0); return true; }
            if (t == "minuit") { time = TimeSpan.Zero; return true; }
            var m = Regex.Match(t, @"^(\d{1,2})(?:h|:)(\d{2})?$");
            if (!m.Success) m = Regex.Match(t, @"^(\d{1,2})h(\d{2})?(?:min)?$");
            if (!m.Success) return false;
            int h = int.Parse(m.Groups[1].Value), mi = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : 0;
            if (h > 23 || mi > 59) return false;
            time = new TimeSpan(h, mi, 0);
            return true;
        }

        public class Result
        {
            public EventInfo Event;
            public string Summary; // ce qui a été compris, à lire à l'utilisateur
        }

        public static Result Parse(string text, DateTime now)
        {
            var raw = Regex.Split((text ?? "").Trim(), @"\s+").Where(w => w.Length > 0).ToList();
            var n = raw.Select(Norm).ToList();
            var used = new bool[raw.Count];
            DateTime? date = null;
            TimeSpan? start = null, end = null;
            TimeSpan? duration = null;

            for (int i = 0; i < n.Count; i++)
            {
                string w = n[i];
                TimeSpan t;
                int dIdx = Array.IndexOf(Days, w);
                int num;
                if (w == "aujourd'hui" || w == "aujourdhui") { date = now.Date; used[i] = true; }
                else if (w == "demain") { date = now.Date.AddDays(1); used[i] = true; }
                else if (w == "apres-demain" || (w == "apres" && i + 1 < n.Count && n[i + 1] == "demain"))
                {
                    date = now.Date.AddDays(2); used[i] = true; if (w == "apres") used[i + 1] = true;
                }
                else if (dIdx >= 0)
                {
                    int diff = ((dIdx - (int)now.DayOfWeek) + 7) % 7;
                    if (diff == 0) diff = 7;
                    date = now.Date.AddDays(diff); used[i] = true;
                    if (i + 1 < n.Count && (n[i + 1] == "prochain" || n[i + 1] == "prochaine")) used[i + 1] = true;
                }
                else if (Regex.IsMatch(w, @"^\d{1,2}/\d{1,2}(/\d{2,4})?$"))
                {
                    DateTime d;
                    if (Ui.TryParseDate(w, out d)) { date = d; used[i] = true; }
                }
                else if ((int.TryParse(w, out num) || (w.EndsWith("er") && int.TryParse(w.Substring(0, w.Length - 2), out num)))
                         && num >= 1 && num <= 31 && i + 1 < n.Count && Array.IndexOf(Months, n[i + 1]) >= 0)
                {
                    int month = Array.IndexOf(Months, n[i + 1]) + 1;
                    int year = now.Year;
                    int skip = 1;
                    int y;
                    if (i + 2 < n.Count && int.TryParse(n[i + 2], out y) && y >= 2000 && y <= 2100) { year = y; skip = 2; }
                    try
                    {
                        var d = new DateTime(year, month, Math.Min(num, DateTime.DaysInMonth(year, month)));
                        if (skip == 1 && d < now.Date) d = d.AddYears(1);
                        date = d;
                        for (int k = 0; k <= skip; k++) used[i + k] = true;
                        if (i > 0 && n[i - 1] == "le") used[i - 1] = true;
                    }
                    catch { }
                }
                else if (w == "pendant" && i + 1 < n.Count)
                {
                    var m = Regex.Match(n[i + 1], @"^(\d{1,2})h(\d{2})?$|^(\d{1,3})\s*min$");
                    if (m.Success)
                    {
                        duration = m.Groups[3].Success && m.Groups[3].Value.Length > 0
                            ? TimeSpan.FromMinutes(int.Parse(m.Groups[3].Value))
                            : new TimeSpan(int.Parse(m.Groups[1].Value), m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : 0, 0);
                        used[i] = used[i + 1] = true;
                    }
                }
                else if (Regex.IsMatch(w, @"^\d{1,2}h(\d{2})?-\d{1,2}h(\d{2})?$"))
                {
                    var parts = w.Split('-');
                    TimeSpan a, b;
                    if (TryTime(parts[0], out a) && TryTime(parts[1], out b)) { start = a; end = b; used[i] = true; }
                }
                else if (TryTime(w, out t))
                {
                    if (start == null) start = t; else if (end == null) end = t;
                    used[i] = true;
                    if (i > 0 && (n[i - 1] == "a" || n[i - 1] == "de" || n[i - 1] == "vers" || n[i - 1] == "jusqu'a")) used[i - 1] = true;
                }
            }

            int first = Array.IndexOf(used, true);
            int last = Array.LastIndexOf(used, true);
            string title, location = null;
            if (first < 0) title = string.Join(" ", raw);
            else
            {
                title = string.Join(" ", raw.Take(first));
                var after = new List<string>();
                for (int i = last + 1; i < raw.Count; i++) after.Add(raw[i]);
                // « à Rennes », « chez Paul » : le petit mot qui introduit le lieu ne fait pas partie du lieu.
                while (after.Count > 0 && (Norm(after[0]) == "a" || Norm(after[0]) == "au" || Norm(after[0]) == "lieu" || Norm(after[0]) == "lieu:")) after.RemoveAt(0);
                // Mots restés entre deux éléments reconnus (hors petits mots) : on les garde dans le titre.
                var middle = new List<string>();
                for (int i = first; i <= last; i++) if (!used[i] && !Fillers.Contains(n[i])) middle.Add(raw[i]);
                if (middle.Count > 0) title = (title + " " + string.Join(" ", middle)).Trim();
                if (after.Count > 0) location = string.Join(" ", after);
                if (title.Length == 0 && location != null) { title = location; location = null; }
            }
            title = title.Trim(' ', ',', '-', ':');
            if (title.Length > 0) title = char.ToUpper(title[0]) + title.Substring(1);

            var ev = new EventInfo { Summary = title, Location = location };
            if (start == null)
            {
                ev.AllDay = true;
                ev.Start = ev.End = (date ?? now.Date);
            }
            else
            {
                var day = date ?? (now.Date + start.Value < now ? now.Date.AddDays(1) : now.Date);
                ev.Start = day + start.Value;
                ev.End = end != null && end.Value > start.Value ? day + end.Value : ev.Start + (duration ?? TimeSpan.FromHours(1));
            }

            var said = new StringBuilder();
            said.Append(ev.ToString());
            return new Result { Event = ev, Summary = said.ToString() };
        }
    }
}
