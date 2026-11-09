using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgendaAccessible
{
    /// <summary>
    /// Connexion au compte Google (OAuth 2.0 pour application de bureau, avec PKCE).
    /// Le jeton est chiffré avec la protection de données Windows (DPAPI) : seul l'utilisateur
    /// Windows courant peut le relire.
    /// </summary>
    class GoogleAuth
    {
        public const string Scope = "https://www.googleapis.com/auth/calendar";

        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgendaGoogleAccessible");

        public static string CredentialsCopyPath { get { return Path.Combine(DataDir, "credentials.json"); } }

        /// <summary>Identifiants fournis avec une version distribuée : l'utilisateur n'a rien à faire dans Google Cloud.</summary>
        public const string AppCredentialsName = "agenda-identifiants.json";
        public static string AppCredentialsPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppCredentialsName); } }

        /// <summary>Vrai si l'application utilise les identifiants fournis avec elle (et non ceux de l'utilisateur).</summary>
        public bool UsingAppCredentials { get; private set; }

        /// <summary>Choisit le fichier d'identifiants à utiliser, dans l'ordre :
        /// les identifiants personnels si l'utilisateur les a choisis, sinon ceux fournis avec l'application,
        /// sinon un fichier personnel trouvé à côté du programme ou dans le dossier de données.</summary>
        /// <summary>Identifiants intégrés au programme lors de la publication (un seul fichier à distribuer).</summary>
        public static string EmbeddedCredentialsPath { get { return Path.Combine(DataDir, "identifiants-application.json"); } }

        public static bool HasEmbeddedCredentials
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceInfo(AppCredentialsName) != null; }
        }

        static string ExtractEmbeddedCredentials()
        {
            try
            {
                using (var res = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(AppCredentialsName))
                {
                    if (res == null) return null;
                    Directory.CreateDirectory(DataDir);
                    using (var f = File.Create(EmbeddedCredentialsPath)) res.CopyTo(f);
                }
                return EmbeddedCredentialsPath;
            }
            catch { return null; }
        }

        public static string ResolveCredentials(out bool appProvided)
        {
            appProvided = false;
            if (Settings.Get("identifiants") == "perso" && File.Exists(CredentialsCopyPath)) return CredentialsCopyPath;
            if (File.Exists(AppCredentialsPath)) { appProvided = true; return AppCredentialsPath; }
            string embedded = ExtractEmbeddedCredentials();
            if (embedded != null) { appProvided = true; return embedded; }
            return FindCredentialsFile();
        }
        static string TokenPath { get { return Path.Combine(DataDir, "jeton.dat"); } }

        string clientId, clientSecret;
        string accessToken, refreshToken;
        DateTime accessExpiry = DateTime.MinValue;
        readonly HttpClient http;

        public GoogleAuth(HttpClient http) { this.http = http; }

        /// <summary>Cherche le fichier d'identifiants : à côté du programme, puis dans le dossier de données.</summary>
        public static string FindCredentialsFile()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string p = Path.Combine(exeDir, "credentials.json");
            if (File.Exists(p)) return p;
            try
            {
                foreach (var f in Directory.GetFiles(exeDir, "client_secret*.json")) return f;
            }
            catch { }
            if (File.Exists(CredentialsCopyPath)) return CredentialsCopyPath;
            return null;
        }

        /// <summary>Charge le fichier d'identifiants téléchargé depuis Google Cloud. Renvoie un message d'erreur ou null.</summary>
        public string LoadCredentials(string path, bool keepCopy)
        {
            Dictionary<string, object> root;
            try { root = Json.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { return "Le fichier n'a pas pu être lu : " + ex.Message; }
            var section = Json.Obj(root, "installed") ?? Json.Obj(root, "web");
            if (section == null) return "Ce fichier ne ressemble pas à un fichier d'identifiants Google (section « installed » absente).";
            clientId = Json.Str(section, "client_id");
            clientSecret = Json.Str(section, "client_secret");
            if (string.IsNullOrEmpty(clientId)) return "Le fichier ne contient pas d'identifiant client (client_id).";
            UsingAppCredentials = string.Equals(Path.GetFullPath(path), Path.GetFullPath(AppCredentialsPath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFullPath(path), Path.GetFullPath(EmbeddedCredentialsPath), StringComparison.OrdinalIgnoreCase);
            if (UsingAppCredentials) keepCopy = false;
            if (keepCopy)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(CredentialsCopyPath), StringComparison.OrdinalIgnoreCase))
                        File.Copy(path, CredentialsCopyPath, true);
                }
                catch { }
            }
            return null;
        }

        /// <summary>Vérifie qu'un fichier est bien un identifiant Google « application de bureau ». Renvoie un message d'erreur ou null.</summary>
        public static string CheckDesktopCredentials(string path)
        {
            Dictionary<string, object> root;
            try { root = Json.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { return "Le fichier n'a pas pu être lu : " + ex.Message; }
            if (Json.Obj(root, "web") != null) return "Ce fichier est un identifiant de type « Application Web ». Il faut un identifiant de type « Application de bureau ».";
            var section = Json.Obj(root, "installed");
            if (section == null || string.IsNullOrEmpty(Json.Str(section, "client_id"))) return "Ce fichier ne ressemble pas à un fichier d'identifiants Google (section « installed » absente).";
            return null;
        }

        public bool HasSavedToken
        {
            get { LoadToken(); return !string.IsNullOrEmpty(refreshToken); }
        }

        void LoadToken()
        {
            if (!string.IsNullOrEmpty(refreshToken) || !File.Exists(TokenPath)) return;
            try
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
                refreshToken = Encoding.UTF8.GetString(bytes);
            }
            catch { refreshToken = null; }
        }

        void SaveToken()
        {
            Directory.CreateDirectory(DataDir);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath, bytes);
        }

        public void SignOut()
        {
            accessToken = null; refreshToken = null; accessExpiry = DateTime.MinValue;
            try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch { }
        }

        static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static string RandomString(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Base64Url(b);
        }

        /// <summary>Ouvre le navigateur sur la page de connexion Google et attend la réponse.</summary>
        public async Task SignInInteractive(CancellationToken cancel)
        {
            string verifier = RandomString(48);
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = RandomString(16);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirect = "http://127.0.0.1:" + port + "/";

            string url = "https://accounts.google.com/o/oauth2/v2/auth"
                + "?client_id=" + Uri.EscapeDataString(clientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                + "&response_type=code"
                + "&scope=" + Uri.EscapeDataString(Scope)
                + "&code_challenge=" + challenge
                + "&code_challenge_method=S256"
                + "&access_type=offline&prompt=consent"
                + "&state=" + state;

            Dictionary<string, string> answer;
            try
            {
                Process.Start(url);
                answer = await Task.Run(() => WaitForRedirect(listener, state, cancel));
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }

            string error;
            if (answer.TryGetValue("error", out error))
            {
                if (error == "access_denied") throw new Exception("La connexion a été refusée dans le navigateur.");
                throw new Exception("Google a renvoyé l'erreur : " + error);
            }

            var form = new Dictionary<string, string>
            {
                { "code", answer["code"] },
                { "client_id", clientId },
                { "redirect_uri", redirect },
                { "grant_type", "authorization_code" },
                { "code_verifier", verifier }
            };
            if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
            await RequestToken(form);
            if (string.IsNullOrEmpty(refreshToken))
                throw new Exception("Google n'a pas fourni de jeton durable. Réessayez la connexion.");
            SaveToken();
        }

        static Dictionary<string, string> WaitForRedirect(TcpListener listener, string expectedState, CancellationToken cancel)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                cancel.ThrowIfCancellationRequested();
                if (!listener.Pending()) { Thread.Sleep(100); continue; }
                using (var client = listener.AcceptTcpClient())
                {
                    string line = null;
                    try
                    {
                        client.ReceiveTimeout = 5000;
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII);
                        line = reader.ReadLine();
                        // On lit les en-têtes jusqu'à la ligne vide.
                        string h;
                        while ((h = reader.ReadLine()) != null && h.Length > 0) { }

                        var query = ParseRequestLine(line);
                        bool ok = query != null && (query.ContainsKey("code") || query.ContainsKey("error"));
                        string stateValue;
                        if (ok && (!query.TryGetValue("state", out stateValue) || stateValue != expectedState)) ok = false;

                        string html = ok
                            ? (query.ContainsKey("code")
                                ? "<!DOCTYPE html><html lang=\"fr\"><head><meta charset=\"utf-8\"><title>Connexion réussie</title></head><body><h1>Connexion réussie</h1><p>Vous pouvez fermer cette page et revenir à l'application Agenda Google accessible.</p></body></html>"
                                : "<!DOCTYPE html><html lang=\"fr\"><head><meta charset=\"utf-8\"><title>Connexion annulée</title></head><body><h1>Connexion annulée</h1><p>Vous pouvez fermer cette page et revenir à l'application.</p></body></html>")
                            : "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Agenda</title></head><body></body></html>";
                        var body = Encoding.UTF8.GetBytes(html);
                        var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + (ok ? "200 OK" : "404 Not Found")
                            + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length);
                        stream.Write(body, 0, body.Length);
                        stream.Flush();
                        if (ok) return query;
                    }
                    catch (IOException) { /* connexion préventive du navigateur sans requête : on l'ignore */ }
                }
            }
            throw new TimeoutException("Aucune réponse de Google après 10 minutes. Relancez la connexion.");
        }

        static Dictionary<string, string> ParseRequestLine(string line)
        {
            // Exemple : GET /?state=...&code=...&scope=... HTTP/1.1
            if (string.IsNullOrEmpty(line)) return null;
            var parts = line.Split(' ');
            if (parts.Length < 2) return null;
            int q = parts[1].IndexOf('?');
            if (q < 0) return null;
            var result = new Dictionary<string, string>();
            foreach (var pair in parts[1].Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string k = Uri.UnescapeDataString(pair.Substring(0, eq).Replace('+', ' '));
                string v = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[k] = v;
            }
            return result;
        }

        async Task RequestToken(Dictionary<string, string> form)
        {
            var resp = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
            string text = await resp.Content.ReadAsStringAsync();
            var json = Json.Parse(text);
            if (!resp.IsSuccessStatusCode)
            {
                string err = Json.Str(json, "error");
                if (err == "invalid_grant")
                {
                    SignOut();
                    throw new ReconnectRequiredException("L'autorisation Google a expiré ou a été retirée. Il faut vous reconnecter.");
                }
                throw new Exception("Échec d'obtention du jeton Google : " + (Json.Str(json, "error_description") ?? err ?? resp.StatusCode.ToString()));
            }
            accessToken = Json.Str(json, "access_token");
            int seconds;
            int.TryParse(Json.Str(json, "expires_in"), out seconds);
            if (seconds <= 0) seconds = 3600;
            accessExpiry = DateTime.UtcNow.AddSeconds(seconds - 60);
            string newRefresh = Json.Str(json, "refresh_token");
            if (!string.IsNullOrEmpty(newRefresh)) refreshToken = newRefresh;
        }

        public async Task<string> GetAccessToken(bool forceRefresh = false)
        {
            if (!forceRefresh && !string.IsNullOrEmpty(accessToken) && DateTime.UtcNow < accessExpiry) return accessToken;
            LoadToken();
            if (string.IsNullOrEmpty(refreshToken)) throw new ReconnectRequiredException("Vous n'êtes pas connecté à Google.");
            var form = new Dictionary<string, string>
            {
                { "refresh_token", refreshToken },
                { "client_id", clientId },
                { "grant_type", "refresh_token" }
            };
            if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
            await RequestToken(form);
            return accessToken;
        }
    }

    class ReconnectRequiredException : Exception
    {
        public ReconnectRequiredException(string message) : base(message) { }
    }
}
