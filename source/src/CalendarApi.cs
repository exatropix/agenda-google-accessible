using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace AgendaAccessible
{
    class ApiException : Exception
    {
        public HttpStatusCode Status;
        public ApiException(HttpStatusCode status, string message) : base(message) { Status = status; }
    }

    /// <summary>Un rappel Google : méthode (popup = notification, email) et délai en minutes avant le début.</summary>
    class Reminder
    {
        public string Method;
        public int Minutes;
        public static List<Reminder> Parse(IEnumerable<Dictionary<string, object>> arr)
        {
            var r = new List<Reminder>();
            foreach (var o in arr)
            {
                object m; int min = 0;
                if (o != null && o.TryGetValue("minutes", out m) && m != null) min = Convert.ToInt32(m, CultureInfo.InvariantCulture);
                r.Add(new Reminder { Method = Json.Str(o, "method") ?? "popup", Minutes = min });
            }
            return r;
        }
        public static object[] ToJson(IEnumerable<Reminder> list)
        {
            return list.Select(x => (object)new Dictionary<string, object> { { "method", x.Method }, { "minutes", x.Minutes } }).ToArray();
        }
    }

    /// <summary>Un agenda de la liste de l'utilisateur.</summary>
    class CalendarInfo
    {
        public string Id, Name, AccessRole, TimeZone;
        public bool Primary;
        public bool IsAll; // pseudo-agenda « Tous mes agendas »
        public List<Reminder> DefaultReminders = new List<Reminder>();

        public static readonly CalendarInfo All = new CalendarInfo { Id = "*", Name = "Tous mes agendas", AccessRole = "all", IsAll = true };

        /// <summary>Délais (minutes) des notifications par défaut de cet agenda.</summary>
        public List<int> DefaultPopupMinutes { get { return DefaultReminders.Where(r => r.Method == "popup").Select(r => r.Minutes).Distinct().OrderByDescending(m => m).ToList(); } }

        public bool CanEdit { get { return AccessRole == "owner" || AccessRole == "writer"; } }
        public bool IsOwner { get { return AccessRole == "owner"; } }

        public static string RoleText(string role)
        {
            switch (role)
            {
                case "owner": return "propriétaire";
                case "writer": return "modification autorisée";
                case "reader": return "lecture seule";
                case "freeBusyReader": return "disponibilités seulement";
                case "none": return "aucun accès";
                default: return role ?? "";
            }
        }

        public override string ToString()
        {
            if (IsAll) return "Tous mes agendas";
            string n = Primary ? "Mon agenda principal (" + Name + ")" : Name;
            return n + ", " + RoleText(AccessRole);
        }
    }

    /// <summary>Un événement (rendez-vous).</summary>
    class EventInfo
    {
        public string Id, Summary, Location, Description, HtmlLink;
        public bool AllDay;
        public DateTime Start, End; // heure locale ; pour un événement « toute la journée », End est le dernier jour inclus
        public List<string> Attendees = new List<string>();
        public string Organizer;
        public bool Placeholder; // ligne « Aucun événement »
        public CalendarInfo Calendar; // agenda d'où vient le rendez-vous
        public bool RemindersUseDefault = true;
        public List<Reminder> ReminderOverrides = new List<Reminder>();
        public bool SendReminders; // à envoyer à Google lors de l'enregistrement
        public string RecurringEventId; // non nul : cette occurrence fait partie d'une série
        public List<string> Recurrence; // règles de répétition à envoyer (nouveau rendez-vous)
        public string TimeZone; // fuseau horaire, obligatoire pour une série avec heure

        /// <summary>Délais (minutes) des notifications réellement prévues pour ce rendez-vous.</summary>
        public List<int> PopupMinutes(CalendarInfo cal)
        {
            if (RemindersUseDefault) return cal != null ? cal.DefaultPopupMinutes : new List<int>();
            return ReminderOverrides.Where(r => r.Method == "popup").Select(r => r.Minutes).Distinct().OrderByDescending(m => m).ToList();
        }
        public string PlaceholderText;

        public static readonly CultureInfo Fr = new CultureInfo("fr-FR");

        public static string DayText(DateTime d, bool withYear)
        {
            string day = d.Day == 1 ? "1er" : d.Day.ToString(Fr);
            string s = d.ToString("dddd", Fr) + " " + day + " " + d.ToString(withYear ? "MMMM yyyy" : "MMMM", Fr);
            return char.ToUpper(s[0], Fr) + s.Substring(1);
        }

        public static string TimeText(DateTime d)
        {
            return d.ToString("H'h'mm", Fr);
        }

        public override string ToString()
        {
            if (Placeholder) return PlaceholderText;
            bool year = Start.Year != DateTime.Today.Year;
            string title = string.IsNullOrWhiteSpace(Summary) ? "(sans titre)" : Summary.Trim();
            string when;
            if (AllDay)
            {
                when = DayText(Start, year) + ", toute la journée";
                if (End.Date > Start.Date) when += " jusqu'au " + DayText(End, End.Year != Start.Year).ToLower(Fr);
            }
            else if (End.Date == Start.Date || (End.Date == Start.Date.AddDays(1) && End.TimeOfDay == TimeSpan.Zero))
            {
                when = DayText(Start, year) + ", de " + TimeText(Start) + " à " + TimeText(End);
            }
            else
            {
                when = DayText(Start, year) + " " + TimeText(Start) + ", jusqu'au " + DayText(End, End.Year != Start.Year).ToLower(Fr) + " " + TimeText(End);
            }
            string s = when + " : " + title;
            if (!string.IsNullOrWhiteSpace(Location)) s += ", lieu : " + Location.Trim();
            return s;
        }

        public static EventInfo FromJson(Dictionary<string, object> e)
        {
            var ev = new EventInfo
            {
                Id = Json.Str(e, "id"),
                Summary = Json.Str(e, "summary"),
                Location = Json.Str(e, "location"),
                Description = Json.Str(e, "description"),
                HtmlLink = Json.Str(e, "htmlLink")
            };
            var start = Json.Obj(e, "start");
            var end = Json.Obj(e, "end");
            string sd = Json.Str(start, "date");
            if (sd != null)
            {
                ev.AllDay = true;
                ev.Start = DateTime.ParseExact(sd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                string ed = Json.Str(end, "date");
                ev.End = ed != null ? DateTime.ParseExact(ed, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1) : ev.Start;
                if (ev.End < ev.Start) ev.End = ev.Start;
            }
            else
            {
                ev.Start = ParseDateTime(Json.Str(start, "dateTime"));
                string edt = Json.Str(end, "dateTime");
                ev.End = edt != null ? ParseDateTime(edt) : ev.Start;
            }
            foreach (var a in Json.Arr(e, "attendees"))
            {
                string mail = Json.Str(a, "email");
                if (!string.IsNullOrEmpty(mail) && !Json.Bool(a, "resource")) ev.Attendees.Add(mail);
            }
            ev.RecurringEventId = Json.Str(e, "recurringEventId");
            var rem = Json.Obj(e, "reminders");
            if (rem != null)
            {
                ev.RemindersUseDefault = Json.Bool(rem, "useDefault");
                ev.ReminderOverrides = Reminder.Parse(Json.Arr(rem, "overrides"));
            }
            var org = Json.Obj(e, "organizer");
            ev.Organizer = Json.Str(org, "displayName") ?? Json.Str(org, "email");
            return ev;
        }

        static DateTime ParseDateTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.Today;
            return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).ToLocalTime().DateTime;
        }
    }

    /// <summary>Une règle de partage (qui a accès à un agenda, et avec quel droit).</summary>
    class AclRule
    {
        public string Id, Role, ScopeType, ScopeValue;

        public override string ToString()
        {
            string who;
            switch (ScopeType)
            {
                case "default": who = "Tout le monde (agenda public)"; break;
                case "domain": who = "Tout le domaine " + ScopeValue; break;
                case "group": who = "Groupe " + ScopeValue; break;
                default: who = ScopeValue; break;
            }
            return who + " : " + CalendarInfo.RoleText(Role);
        }
    }

    /// <summary>Accès à l'API Google Agenda (version 3).</summary>
    class CalendarApi
    {
        const string Base = "https://www.googleapis.com/calendar/v3/";
        readonly HttpClient http;
        readonly GoogleAuth auth;

        public CalendarApi(HttpClient http, GoogleAuth auth) { this.http = http; this.auth = auth; }

        static string E(string s) { return Uri.EscapeDataString(s); }

        public static string Rfc3339(DateTime local)
        {
            return new DateTimeOffset(local).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        async Task<Dictionary<string, object>> Send(HttpMethod method, string path, object body = null)
        {
            for (int attempt = 0; ; attempt++)
            {
                string token = await auth.GetAccessToken(attempt > 0);
                var req = new HttpRequestMessage(method, Base + path);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                if (body != null) req.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                var resp = await http.SendAsync(req);
                string text = await resp.Content.ReadAsStringAsync();
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (!resp.IsSuccessStatusCode)
                {
                    Dictionary<string, object> json = null;
                    try { json = Json.Parse(text); } catch { }
                    string msg = Json.Str(Json.Obj(json, "error"), "message") ?? resp.ReasonPhrase;
                    throw new ApiException(resp.StatusCode, msg);
                }
                try { return Json.Parse(text); } catch { return null; }
            }
        }

        // ---------- Agendas ----------

        public async Task<List<CalendarInfo>> ListCalendars()
        {
            var result = new List<CalendarInfo>();
            string pageToken = null;
            do
            {
                string path = "users/me/calendarList?maxResults=250&showHidden=true" + (pageToken != null ? "&pageToken=" + E(pageToken) : "");
                var json = await Send(HttpMethod.Get, path);
                foreach (var c in Json.Arr(json, "items"))
                {
                    result.Add(new CalendarInfo
                    {
                        Id = Json.Str(c, "id"),
                        Name = Json.Str(c, "summaryOverride") ?? Json.Str(c, "summary") ?? Json.Str(c, "id"),
                        AccessRole = Json.Str(c, "accessRole"),
                        TimeZone = Json.Str(c, "timeZone"),
                        Primary = Json.Bool(c, "primary"),
                        DefaultReminders = Reminder.Parse(Json.Arr(c, "defaultReminders"))
                    });
                }
                pageToken = Json.Str(json, "nextPageToken");
            } while (pageToken != null);
            // Agenda principal d'abord, puis ceux qu'on possède, puis les autres, par ordre alphabétique.
            return result
                .OrderByDescending(c => c.Primary)
                .ThenBy(c => c.IsOwner ? 0 : c.CanEdit ? 1 : 2)
                .ThenBy(c => c.Name, StringComparer.Create(EventInfo.Fr, true))
                .ToList();
        }

        /// <summary>Ajoute à sa liste un agenda partagé par quelqu'un d'autre (ou un agenda public).</summary>
        public Task AddCalendarToList(string calendarId)
        {
            return Send(HttpMethod.Post, "users/me/calendarList", new Dictionary<string, object> { { "id", calendarId } });
        }

        /// <summary>Remplace les rappels par défaut d'un agenda (réglage personnel, synchronisé avec le téléphone).</summary>
        public Task SetDefaultReminders(string calendarId, List<Reminder> reminders)
        {
            return Send(new HttpMethod("PATCH"), "users/me/calendarList/" + E(calendarId),
                new Dictionary<string, object> { { "defaultReminders", Reminder.ToJson(reminders) } });
        }

        public Task RemoveCalendarFromList(string calendarId)
        {
            return Send(HttpMethod.Delete, "users/me/calendarList/" + E(calendarId));
        }

        public async Task<string> CreateCalendar(string name, string timeZone)
        {
            var body = new Dictionary<string, object> { { "summary", name } };
            if (!string.IsNullOrEmpty(timeZone)) body["timeZone"] = timeZone;
            var json = await Send(HttpMethod.Post, "calendars", body);
            return Json.Str(json, "id");
        }

        public Task DeleteCalendar(string calendarId)
        {
            return Send(HttpMethod.Delete, "calendars/" + E(calendarId));
        }

        // ---------- Événements ----------

        public async Task<List<EventInfo>> ListEvents(string calendarId, DateTime from, DateTime to, string search)
        {
            var result = new List<EventInfo>();
            string pageToken = null;
            int guard = 0;
            do
            {
                string path = "calendars/" + E(calendarId) + "/events?singleEvents=true&orderBy=startTime&maxResults=250"
                    + "&timeMin=" + E(Rfc3339(from)) + "&timeMax=" + E(Rfc3339(to))
                    + (string.IsNullOrWhiteSpace(search) ? "" : "&q=" + E(search.Trim()))
                    + (pageToken != null ? "&pageToken=" + E(pageToken) : "");
                var json = await Send(HttpMethod.Get, path);
                foreach (var e in Json.Arr(json, "items"))
                {
                    if (Json.Str(e, "status") == "cancelled") continue;
                    result.Add(EventInfo.FromJson(e));
                }
                pageToken = Json.Str(json, "nextPageToken");
            } while (pageToken != null && ++guard < 20);
            return result;
        }

        static Dictionary<string, object> EventBody(EventInfo ev, bool includeAttendees)
        {
            var body = new Dictionary<string, object>
            {
                { "summary", ev.Summary ?? "" },
                { "location", ev.Location ?? "" },
                { "description", ev.Description ?? "" }
            };
            if (ev.AllDay)
            {
                body["start"] = new Dictionary<string, object> { { "date", ev.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "dateTime", null } };
                body["end"] = new Dictionary<string, object> { { "date", ev.End.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "dateTime", null } };
            }
            else
            {
                body["start"] = new Dictionary<string, object> { { "dateTime", Rfc3339(ev.Start) }, { "date", null } };
                body["end"] = new Dictionary<string, object> { { "dateTime", Rfc3339(ev.End) }, { "date", null } };
                if (ev.Recurrence != null && ev.Recurrence.Count > 0)
                {
                    // Google exige le fuseau horaire pour une série de rendez-vous avec heure.
                    string tz = string.IsNullOrEmpty(ev.TimeZone) ? "Europe/Paris" : ev.TimeZone;
                    ((Dictionary<string, object>)body["start"])["timeZone"] = tz;
                    ((Dictionary<string, object>)body["end"])["timeZone"] = tz;
                }
            }
            if (ev.Recurrence != null && ev.Recurrence.Count > 0)
                body["recurrence"] = ev.Recurrence.Cast<object>().ToArray();
            if (ev.SendReminders)
            {
                var r = new Dictionary<string, object> { { "useDefault", ev.RemindersUseDefault } };
                r["overrides"] = ev.RemindersUseDefault ? new object[0] : Reminder.ToJson(ev.ReminderOverrides);
                body["reminders"] = r;
            }
            if (includeAttendees)
                body["attendees"] = ev.Attendees.Select(a => (object)new Dictionary<string, object> { { "email", a } }).ToArray();
            return body;
        }

        public Task CreateEvent(string calendarId, EventInfo ev)
        {
            string notify = ev.Attendees.Count > 0 ? "all" : "none";
            return Send(HttpMethod.Post, "calendars/" + E(calendarId) + "/events?sendUpdates=" + notify, EventBody(ev, ev.Attendees.Count > 0));
        }

        public Task UpdateEvent(string calendarId, EventInfo ev, bool attendeesChanged)
        {
            string notify = (attendeesChanged || ev.Attendees.Count > 0) ? "all" : "none";
            return Send(new HttpMethod("PATCH"), "calendars/" + E(calendarId) + "/events/" + E(ev.Id) + "?sendUpdates=" + notify, EventBody(ev, attendeesChanged));
        }

        public Task DeleteEvent(string calendarId, string eventId, bool notify)
        {
            return Send(HttpMethod.Delete, "calendars/" + E(calendarId) + "/events/" + E(eventId) + "?sendUpdates=" + (notify ? "all" : "none"));
        }

        // ---------- Partage ----------

        public async Task<List<AclRule>> ListAcl(string calendarId)
        {
            var result = new List<AclRule>();
            string pageToken = null;
            do
            {
                var json = await Send(HttpMethod.Get, "calendars/" + E(calendarId) + "/acl?maxResults=250" + (pageToken != null ? "&pageToken=" + E(pageToken) : ""));
                foreach (var r in Json.Arr(json, "items"))
                {
                    var scope = Json.Obj(r, "scope");
                    result.Add(new AclRule
                    {
                        Id = Json.Str(r, "id"),
                        Role = Json.Str(r, "role"),
                        ScopeType = Json.Str(scope, "type"),
                        ScopeValue = Json.Str(scope, "value")
                    });
                }
                pageToken = Json.Str(json, "nextPageToken");
            } while (pageToken != null);
            return result;
        }

        public Task ShareWith(string calendarId, string email, string role)
        {
            var body = new Dictionary<string, object>
            {
                { "role", role },
                { "scope", new Dictionary<string, object> { { "type", "user" }, { "value", email } } }
            };
            return Send(HttpMethod.Post, "calendars/" + E(calendarId) + "/acl?sendNotifications=true", body);
        }

        public Task RemoveAcl(string calendarId, string ruleId)
        {
            return Send(HttpMethod.Delete, "calendars/" + E(calendarId) + "/acl/" + E(ruleId));
        }
    }
}
