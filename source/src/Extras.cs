using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Réglages enregistrés entre deux lancements (fichier texte clé=valeur).</summary>
    static class Settings
    {
        static readonly string PathFile = System.IO.Path.Combine(GoogleAuth.DataDir, "reglages.txt");
        static Dictionary<string, string> values;

        static Dictionary<string, string> Values
        {
            get
            {
                if (values != null) return values;
                values = new Dictionary<string, string>();
                try
                {
                    if (File.Exists(PathFile))
                        foreach (var line in File.ReadAllLines(PathFile, Encoding.UTF8))
                        {
                            int i = line.IndexOf('=');
                            if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1);
                        }
                }
                catch { }
                return values;
            }
        }

        public static string Get(string key, string def = null)
        {
            string v;
            return Values.TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            Values[key] = value ?? "";
            try
            {
                Directory.CreateDirectory(GoogleAuth.DataDir);
                File.WriteAllLines(PathFile, Values.Select(kv => kv.Key + "=" + kv.Value), Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>Petit signal sonore fabriqué en mémoire (pas de fichier son à fournir).</summary>
    static class Beep
    {
        static SoundPlayer appointment;

        static SoundPlayer Make(double[] freqs, int msEach)
        {
            const int rate = 22050;
            int perTone = rate * msEach / 1000;
            int samples = perTone * freqs.Length;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
            for (int t = 0; t < freqs.Length; t++)
                for (int i = 0; i < perTone; i++)
                {
                    // Enveloppe douce pour éviter les « clics ».
                    double env = Math.Min(1.0, Math.Min(i, perTone - i) / (rate * 0.005));
                    double v = Math.Sin(2 * Math.PI * freqs[t] * i / rate) * env * 0.35;
                    w.Write((short)(v * short.MaxValue));
                }
            w.Flush();
            ms.Position = 0;
            var p = new SoundPlayer(ms);
            p.Load();
            return p;
        }

        /// <summary>Signal joué quand on arrive sur un jour qui a au moins un rendez-vous.</summary>
        public static void Appointment()
        {
            try
            {
                if (appointment == null) appointment = Make(new[] { 880.0, 1175.0 }, 45);
                appointment.Play();
            }
            catch { }
        }
    }

    /// <summary>Fenêtre de texte en lecture seule (détail d'un rendez-vous, aide…), lisible aux flèches.</summary>
    class TextViewForm : Form
    {
        public const DialogResult Modify = DialogResult.Yes;
        public const DialogResult Delete = DialogResult.No;
        public const DialogResult GoTo = DialogResult.Retry;

        readonly TextBox box;

        public TextViewForm(string title, string text, bool canEdit = false, bool canGoTo = false)
        {
            Text = title;
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(760, 520);
            KeyPreview = true;

            box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), AccessibleName = title, BackColor = SystemColors.Window, TabIndex = 0
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), TabIndex = 1 };
            int tab = 0;
            if (canEdit)
            {
                AddButton(buttons, "&Modifier (Ctrl+M)", Modify, ref tab);
                AddButton(buttons, "&Supprimer (Ctrl+S)", Delete, ref tab);
            }
            if (canGoTo) AddButton(buttons, "&Aller à ce jour", GoTo, ref tab);
            var close = AddButton(buttons, "&Fermer (Échap)", DialogResult.Cancel, ref tab);
            CancelButton = close;

            Controls.Add(box);
            Controls.Add(buttons);
            KeyDown += (s, e) =>
            {
                if (canEdit && e.Control && e.KeyCode == Keys.M) { e.SuppressKeyPress = true; DialogResult = Modify; }
                else if (canEdit && e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; DialogResult = Delete; }
            };
            Shown += (s, e) => { box.Focus(); box.Select(0, 0); };
        }

        static Button AddButton(FlowLayoutPanel panel, string text, DialogResult result, ref int tab)
        {
            var b = Ui.MakeButton(text, null);
            b.DialogResult = result;
            b.TabIndex = tab++;
            panel.Controls.Add(b);
            return b;
        }
    }

    /// <summary>Choix d'un élément dans une liste (agenda, rendez-vous…). Entrée valide, Échap annule.</summary>
    class ChooseDialog : Form
    {
        readonly ListBox list;
        public object Selected { get { return list.SelectedItem; } }

        public ChooseDialog(string title, string label, IEnumerable<object> items, int selectedIndex)
        {
            Text = title;
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(700, 380);

            var lbl = new Label { Text = label, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4), TabIndex = 0 };
            list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = label.Replace("&", "").TrimEnd(' ', ':'), TabIndex = 1 };
            foreach (var i in items) list.Items.Add(i);
            if (list.Items.Count > 0) list.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, list.Items.Count - 1));
            list.DoubleClick += (s, e) => { if (list.SelectedItem != null) DialogResult = DialogResult.OK; };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8), TabIndex = 2 };
            var cancel = Ui.MakeButton("Annuler", null); cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.MakeButton("OK", null); ok.DialogResult = DialogResult.OK;
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            AcceptButton = ok; CancelButton = cancel;

            Controls.Add(list);
            Controls.Add(lbl);
            Controls.Add(buttons);
            Shown += (s, e) => list.Focus();
        }
    }

    /// <summary>Un résultat de recherche : un rendez-vous et l'agenda où il se trouve.</summary>
    class SearchHit
    {
        public EventInfo Event;
        public CalendarInfo Calendar;
        public bool ShowCalendar;

        public override string ToString()
        {
            if (Event.Placeholder) return Event.PlaceholderText;
            return Event + (ShowCalendar ? " (agenda : " + Calendar.Name + ")" : "");
        }
    }

    /// <summary>Résultats d'une recherche. Entrée : détail ; Alt+A : aller au jour du rendez-vous.</summary>
    class SearchResultsForm : Form
    {
        readonly ListBox list;
        public SearchHit GoToHit { get; private set; }

        public SearchResultsForm(string title, List<SearchHit> hits)
        {
            Text = title;
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(900, 480);

            list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, AccessibleName = title, TabIndex = 0 };
            foreach (var h in hits) list.Items.Add(h);
            if (hits.Count == 0)
                list.Items.Add(new SearchHit { Event = new EventInfo { Placeholder = true, PlaceholderText = "Aucun rendez-vous trouvé." } });
            list.SelectedIndex = 0;
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Alt && !e.Control) { e.Handled = true; e.SuppressKeyPress = true; ShowDetail(); }
            };
            list.DoubleClick += (s, e) => ShowDetail();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), TabIndex = 1 };
            var detail = Ui.MakeButton("&Voir le détail (Entrée)", (s, e) => ShowDetail()); detail.TabIndex = 0;
            var go = Ui.MakeButton("&Aller à ce jour", (s, e) => GoTo()); go.TabIndex = 1;
            var close = Ui.MakeButton("&Fermer (Échap)", null); close.DialogResult = DialogResult.Cancel; close.TabIndex = 2;
            buttons.Controls.Add(detail); buttons.Controls.Add(go); buttons.Controls.Add(close);
            CancelButton = close;

            Controls.Add(list);
            Controls.Add(buttons);
            Shown += (s, e) => list.Focus();
        }

        SearchHit Current
        {
            get { var h = list.SelectedItem as SearchHit; return h == null || h.Event.Placeholder ? null : h; }
        }

        void ShowDetail()
        {
            var h = Current;
            if (h == null) return;
            using (var f = new TextViewForm("Détail du rendez-vous", EventText.Detail(h.Event, h.Calendar), false, true))
            {
                if (f.ShowDialog(this) == TextViewForm.GoTo) { GoTo(); return; }
            }
            list.Focus();
        }

        void GoTo()
        {
            var h = Current;
            if (h == null) return;
            GoToHit = h;
            DialogResult = DialogResult.OK;
        }
    }

    /// <summary>Textes lus par le lecteur d'écran pour les jours et les rendez-vous.</summary>
    static class EventText
    {
        static string Lower(string s) { return s.ToLower(EventInfo.Fr); }

        public static bool OccursOn(EventInfo ev, DateTime day)
        {
            day = day.Date;
            if (ev.AllDay) return ev.Start.Date <= day && ev.End.Date >= day;
            if (ev.End <= ev.Start) return ev.Start.Date == day;
            return ev.Start < day.AddDays(1) && ev.End > day;
        }

        public static string Count(int n)
        {
            return n == 0 ? "aucun rendez-vous" : n + " rendez-vous";
        }

        /// <summary>« Aujourd'hui, mardi 3 novembre, 2 rendez-vous »</summary>
        public static string DayLine(DateTime day, int count)
        {
            bool year = day.Year != DateTime.Today.Year;
            string d = EventInfo.DayText(day, year);
            if (day == DateTime.Today) d = "Aujourd'hui, " + Lower(d);
            else if (day == DateTime.Today.AddDays(1)) d = "Demain, " + Lower(d);
            return d + ", " + Count(count);
        }

        /// <summary>Moment du rendez-vous vu depuis un jour donné : « De 10h00 à 11h30 », « Toute la journée »…</summary>
        public static string WhenOnDay(EventInfo ev, DateTime day)
        {
            day = day.Date;
            if (ev.AllDay)
            {
                if (ev.End.Date <= ev.Start.Date) return "Toute la journée";
                return "Toute la journée, du " + Lower(EventInfo.DayText(ev.Start, ev.Start.Year != DateTime.Today.Year))
                    + " au " + Lower(EventInfo.DayText(ev.End, ev.End.Year != ev.Start.Year));
            }
            bool endsSameDay = ev.End.Date == ev.Start.Date || (ev.End.Date == ev.Start.Date.AddDays(1) && ev.End.TimeOfDay == TimeSpan.Zero);
            if (endsSameDay)
                return ev.End > ev.Start ? "De " + EventInfo.TimeText(ev.Start) + " à " + EventInfo.TimeText(ev.End) : "À " + EventInfo.TimeText(ev.Start);
            if (day == ev.Start.Date)
                return "À partir de " + EventInfo.TimeText(ev.Start) + ", jusqu'au " + Lower(EventInfo.DayText(ev.End, ev.End.Year != ev.Start.Year)) + " " + EventInfo.TimeText(ev.End);
            if (day == ev.End.Date)
                return "Jusqu'à " + EventInfo.TimeText(ev.End) + ", depuis le " + Lower(EventInfo.DayText(ev.Start, ev.Start.Year != ev.End.Year)) + " " + EventInfo.TimeText(ev.Start);
            return "Toute la journée, du " + Lower(EventInfo.DayText(ev.Start, false)) + " " + EventInfo.TimeText(ev.Start)
                + " au " + Lower(EventInfo.DayText(ev.End, ev.End.Year != ev.Start.Year)) + " " + EventInfo.TimeText(ev.End);
        }

        public static string Title(EventInfo ev)
        {
            return string.IsNullOrWhiteSpace(ev.Summary) ? "(sans titre)" : ev.Summary.Trim();
        }

        public static string LineOnDay(EventInfo ev, DateTime day)
        {
            string s = WhenOnDay(ev, day) + " : " + Title(ev);
            if (!string.IsNullOrWhiteSpace(ev.Location)) s += ", lieu : " + ev.Location.Trim();
            if (ev.IsInvitation && (ev.SelfResponse == "needsAction" || ev.SelfResponse == "tentative"))
                s += ev.SelfResponse == "tentative" ? ", invitation : peut-être" : ", invitation sans réponse";
            if (ev.MeetLink != null) s += ", visio";
            return s;
        }

        /// <summary>Fiche complète d'un rendez-vous, en texte.</summary>
        public static string Detail(EventInfo ev, CalendarInfo cal)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Titre : " + Title(ev));
            bool year = ev.Start.Year != DateTime.Today.Year;
            if (ev.AllDay)
            {
                if (ev.End.Date <= ev.Start.Date) sb.AppendLine("Date : " + EventInfo.DayText(ev.Start, year) + ", toute la journée");
                else sb.AppendLine("Date : du " + Lower(EventInfo.DayText(ev.Start, year)) + " au " + Lower(EventInfo.DayText(ev.End, true)) + ", toute la journée");
            }
            else
            {
                sb.AppendLine("Date : " + EventInfo.DayText(ev.Start, year));
                bool endsSameDay = ev.End.Date == ev.Start.Date || (ev.End.Date == ev.Start.Date.AddDays(1) && ev.End.TimeOfDay == TimeSpan.Zero);
                if (endsSameDay)
                {
                    sb.AppendLine("Heure : de " + EventInfo.TimeText(ev.Start) + " à " + EventInfo.TimeText(ev.End));
                    var dur = ev.End - ev.Start;
                    if (dur > TimeSpan.Zero)
                        sb.AppendLine("Durée : " + (dur.TotalHours >= 1 ? (int)dur.TotalHours + " h " + (dur.Minutes > 0 ? dur.Minutes.ToString("00") : "") : dur.Minutes + " minutes").Trim());
                }
                else
                    sb.AppendLine("Heure : de " + EventInfo.TimeText(ev.Start) + " jusqu'au " + Lower(EventInfo.DayText(ev.End, ev.End.Year != ev.Start.Year)) + " " + EventInfo.TimeText(ev.End));
            }
            if (!string.IsNullOrWhiteSpace(ev.Location)) sb.AppendLine("Lieu : " + ev.Location.Trim());
            if (cal != null) sb.AppendLine("Agenda : " + cal.Name + " (" + CalendarInfo.RoleText(cal.AccessRole) + ")");
            if (ev.Calendar == null) ev.Calendar = cal;
            sb.AppendLine("Alarmes : " + AlarmText.ForEvent(ev));
            if (!string.IsNullOrEmpty(ev.RecurringEventId)) sb.AppendLine("Répétition : ce rendez-vous fait partie d'une série qui se répète.");
            if (!string.IsNullOrWhiteSpace(ev.Organizer)) sb.AppendLine("Organisateur : " + ev.Organizer);
            if (ev.Attendees.Count > 0) sb.AppendLine("Invités : " + string.Join(", ", ev.Attendees));
            if (ev.IsInvitation) sb.AppendLine("Votre réponse à l'invitation : " + EventInfo.ResponseText(ev.SelfResponse) + " (menu Rendez-vous, « Répondre à l'invitation »).");
            if (ev.MeetLink != null) sb.AppendLine("Visioconférence : " + ev.MeetLink + " (Ctrl+Maj+V pour la rejoindre).");
            sb.AppendLine();
            if (string.IsNullOrWhiteSpace(ev.Description)) sb.AppendLine("Pas de description.");
            else
            {
                sb.AppendLine("Description :");
                sb.AppendLine(StripHtml(ev.Description));
            }
            return sb.ToString();
        }

        /// <summary>Google renvoie parfois la description en HTML : on la rend lisible.</summary>
        static string StripHtml(string s)
        {
            if (s.IndexOf('<') < 0) return s.Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"<\s*br\s*/?\s*>|</\s*(p|div|li)\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
            return System.Net.WebUtility.HtmlDecode(s).Trim();
        }
    }

    /// <summary>Textes d'aide.</summary>
    static class HelpText
    {
        public const string General =
@"AGENDA GOOGLE ACCESSIBLE - AIDE

La fenêtre principale a deux listes. Passez de l'une à l'autre avec F6.
Par défaut, elles regroupent tous vos agendas (Ctrl+G pour n'en afficher qu'un).

1. La liste des jours (au démarrage)
Un jour par ligne, par exemple : « Aujourd'hui, mardi 3 novembre, 2 rendez-vous ».
Un petit signal sonore indique les jours qui ont au moins un rendez-vous
(vous pouvez le couper dans le menu Affichage).
Flèches haut et bas : jour précédent ou suivant. En arrivant au bout du mois,
la liste passe toute seule au mois suivant ou précédent.
Ctrl+Page suivante et Ctrl+Page précédente : mois suivant ou précédent.
Ctrl+Origine : revenir à aujourd'hui. Ctrl+D : aller à une date précise.
Entrée : aller aux rendez-vous du jour.

2. La liste des rendez-vous du jour choisi
Un rendez-vous par ligne, par exemple : « De 10h00 à 11h30 : Dentiste, lieu : Rennes ».
Entrée : lire le détail complet du rendez-vous dans une fenêtre de texte
(flèches pour lire, Échap pour fermer).
Échap ou F6 : revenir à la liste des jours.

3. Gérer les rendez-vous (menu Rendez-vous)
Ctrl+N : nouveau rendez-vous, à la date du jour choisi.
Ctrl+M : modifier le rendez-vous.
Suppr ou Ctrl+S : supprimer le rendez-vous (avec confirmation).
Depuis la liste des jours, si le jour a plusieurs rendez-vous,
l'application vous demande lequel modifier ou supprimer.
Pour un rendez-vous qui se répète, seule cette occurrence est modifiée ou supprimée.

Dans la fiche d'un rendez-vous, cases à cocher pour les alarmes :
« Alarmes par défaut de l'agenda », ou bien 1 heure avant, 30 minutes avant,
15 minutes avant, à l'heure exacte (plusieurs choix possibles).
Répétition (Alt+P), pour un nouveau rendez-vous : tous les jours, du lundi au vendredi,
toutes les semaines, toutes les 2 semaines, tous les mois, tous les ans,
ou « Anniversaire » (tous les ans, toute la journée). Fin de la répétition (Alt+O) : facultative.
Pour un rendez-vous répété, Modifier ne change que cette date ;
Supprimer demande s'il faut supprimer cette date ou toute la série.
En vue « Tous mes agendas », le champ « Ranger dans l'agenda » (Alt+R) choisit l'agenda du nouveau rendez-vous.

Saisie rapide (Ctrl+Maj+N) : écrivez le rendez-vous en une phrase, par exemple
« Dentiste mardi 14h30 Rennes » ou « Réunion demain de 18h à 20h salle des fêtes ».
La fiche s'ouvre déjà remplie : vérifiez-la, puis enregistrez.
Dupliquer (Ctrl+Maj+D) : ouvre une copie du rendez-vous, à changer de date.
Dans la liste des rendez-vous, Ctrl+Flèche droite ou gauche décale d'un jour,
Ctrl+Flèche bas ou haut d'une heure ; Ctrl+Z annule le dernier déplacement.
Invitations reçues : Ctrl+Maj+A accepter, Ctrl+Maj+P peut-être, Ctrl+Maj+R refuser.
Ctrl+Maj+V : rejoindre la visioconférence (Google Meet, Zoom, Teams).

4. Rechercher (menu Édition)
Prochains rendez-vous : demain (Ctrl+Maj+J), les 7 prochains jours (Ctrl+Maj+S),
les 30 prochains jours (Ctrl+Maj+M), ou un mot dans les prochains rendez-vous (Ctrl+Maj+T).
Ctrl+F : rechercher un mot dans l'agenda affiché.
Ctrl+Maj+F : rechercher dans tous vos agendas.
La recherche couvre l'année passée et les deux années à venir.
Dans les résultats : Entrée pour le détail, Alt+A pour aller au jour du rendez-vous.

5. Les agendas et le partage (menu Agenda)
Ctrl+G : choisir l'agenda affiché. Le nom de l'agenda est dans le titre de la fenêtre.
Partager cet agenda : donner l'accès à une personne (voir ses disponibilités,
voir les détails, modifier, ou gérer le partage). Vous devez être propriétaire de l'agenda.
S'abonner à un agenda partagé avec moi : ajouter à votre liste l'agenda
qu'une personne vous a partagé (son adresse Gmail, ou l'identifiant de l'agenda).
Créer un agenda : un agenda séparé, pratique pour la famille ou une association.
Copier l'identifiant de l'agenda : à donner à la personne avec qui vous le partagez.
Retirer cet agenda de ma liste.

6. Compte (menu Fichier)
Se connecter ou se déconnecter du compte Google. F5 : actualiser.
« Utiliser mes propres identifiants Google » : se connecter avec votre propre projet Google Cloud.
« Préparer une version à distribuer » : crée un zip prêt à donner, avec vos identifiants,
pour que d'autres se connectent sans passer par Google Cloud.
Fermer la fenêtre (Alt+F4) : l'agenda reste actif près de l'horloge pour les alarmes.
Quitter complètement (Ctrl+Q) : plus aucune alarme.

7. Alarmes et résumé du matin (menu Alarmes, Alt+L)
À l'heure d'une alarme, une sonnerie retentit et une fenêtre s'ouvre au premier plan :
« Dans 30 minutes, à 14h30 : Dentiste ». Échap arrête la sonnerie,
Alt+5 rappelle dans 5 minutes, Alt+O ouvre l'agenda sur ce rendez-vous.
Si l'ordinateur était éteint à l'heure d'une alarme, elle sonne au rallumage,
tant que le rendez-vous n'a pas commencé.
Chaque matin, au premier démarrage, un résumé annonce les rendez-vous du jour
et les alarmes prévues (Ctrl+R pour le relire).
Ctrl+L : liste des prochaines alarmes.
« Alarmes par défaut de mes agendas » : les alarmes des rendez-vous qui gardent
« Alarmes par défaut ». Elles sont enregistrées dans Google, donc aussi sur votre téléphone.
Vous pouvez aussi couper les alarmes, le résumé du matin ou le démarrage avec Windows.
Raccourcis globaux (menu Préférences, Raccourcis clavier) : désactivés par défaut. Vous pouvez en activer
pour afficher l'agenda, faire dire par NVDA (ou JAWS) le prochain rendez-vous ou ceux du jour,
depuis n'importe quelle application, et choisir leurs touches.
Les alarmes sont aussi annoncées à la voix (menu Alarmes pour le couper).

8. Installation et mises à jour (menu Aide)
Au premier lancement, l'agenda propose de s'installer : menu Démarrer,
Bureau, démarrage avec Windows, désinstallation depuis « Applications installées ».
Il vérifie chaque jour s'il existe une nouvelle version et propose de l'installer ;
il redémarre alors tout seul. « Rechercher les mises à jour » vérifie à la demande.

La ligne d'état en bas de la fenêtre se lit avec NVDA+Fin (JAWS : Insert+Page suivante).
Le guide complet, avec la préparation de l'accès Google, est dans le fichier LISEZMOI.txt.";

        /// <summary>Guide court fourni avec une version distribuée (sans les étapes Google Cloud).</summary>
        public const string UserGuide =
@"AGENDA GOOGLE ACCESSIBLE - Version 1.7.1
Par Luc Rasschaert (Exatropic)

Gérez votre agenda Google au clavier, avec NVDA ou JAWS : rendez-vous, alarmes,
résumé du matin, agendas partagés.


1. INSTALLATION

Lancez AgendaGoogleAccessible.exe et répondez Oui quand il propose de s'installer.
Il se range dans vos programmes, avec un raccourci dans le menu Démarrer et sur le Bureau,
et une touche de lancement peut être choisie dans Préférences. Rien d'autre à installer : Windows 10 et 11 ont déjà tout ce qu'il faut.
Il se met ensuite à jour tout seul. Pour le désinstaller : Paramètres de Windows,
« Applications installées », « Agenda Google accessible ».


2. PREMIÈRE CONNEXION (une seule fois)

1. Lancez AgendaGoogleAccessible.exe.
2. Un message annonce l'ouverture du navigateur. Validez.
3. Choisissez votre compte Google.
4. Si Google affiche « Google n'a pas validé cette application » : activez le lien
   « Paramètres avancés », puis « Accéder à Agenda accessible ».
5. Autorisez l'accès à l'agenda (bouton « Continuer »).
6. La page affiche « Connexion réussie ». Fermez l'onglet (Ctrl+W)
   et revenez à l'agenda (Alt+Tab).

Les fois suivantes, l'agenda se connecte tout seul.


3. L'ESSENTIEL

- Deux listes : les jours, puis les rendez-vous du jour choisi. F6 passe de l'une à l'autre.
- Entrée sur un rendez-vous : son détail. Ctrl+N : nouveau rendez-vous.
  Ctrl+M : modifier. Suppr : supprimer.
- Ctrl+G : afficher tous vos agendas ou un seul.
- Ctrl+F : rechercher. Ctrl+D : aller à une date. Ctrl+Origine : aujourd'hui.
- Dans la fiche d'un nouveau rendez-vous, « Répétition » (Alt+P) : tous les jours, toutes les semaines,
  tous les mois, tous les ans, ou « Anniversaire » (tous les ans, toute la journée).
- Dans la fiche d'un rendez-vous, cases d'alarme : 1 heure, 30 minutes, 15 minutes avant,
  ou à l'heure exacte.
- Chaque matin, un résumé annonce les rendez-vous du jour.
- L'agenda démarre avec Windows et reste actif près de l'horloge pour sonner.
  Alt+F4 ferme la fenêtre, Ctrl+Q quitte vraiment.
- Menu Préférences, Raccourcis clavier : des touches utilisables partout pour rouvrir l'agenda
  ou faire dire le prochain rendez-vous par NVDA. Elles sont désactivées au départ.
- Ctrl+Maj+N : saisie rapide en une phrase, par exemple « Dentiste mardi 14h30 Rennes ».
- Ctrl+Maj+S : les 7 prochains jours. Ctrl+Flèches : décaler un rendez-vous.
- Le résumé du matin et les alarmes se ferment avec Échap ou Entrée.
- F1 : aide complète. Ctrl+F1 : toutes les touches.


4. CONFIDENTIALITÉ

L'application parle directement à Google, sans aucun serveur intermédiaire :
personne d'autre que vous ne voit votre agenda.
Le jeton de connexion est enregistré chiffré sur votre ordinateur.
Menu Fichier, « Se déconnecter » l'efface. Vous pouvez aussi retirer l'accès à tout moment
depuis https://myaccount.google.com/permissions
";

        public const string Shortcuts =
@"TOUCHES DE RACCOURCI

Partout dans la fenêtre principale
  F6 ...................... passer de la liste des jours à celle des rendez-vous, et inversement
  F5 ...................... actualiser
  Ctrl+N .................. nouveau rendez-vous
  Ctrl+M .................. modifier le rendez-vous
  Suppr ou Ctrl+S ......... supprimer le rendez-vous
  Ctrl+F .................. rechercher dans cet agenda
  Ctrl+Maj+F .............. rechercher dans tous les agendas
  Ctrl+G .................. choisir l'agenda (ou « Tous mes agendas »)
  Ctrl+L .................. prochaines alarmes
  Ctrl+R .................. résumé du jour
  Ctrl+Q .................. quitter complètement (plus d'alarmes)
  Alt+F4 .................. fermer la fenêtre (les alarmes restent actives)
  Raccourcis globaux ....... à choisir dans Préférences, Raccourcis clavier (désactivés au départ)
  Ctrl+Maj+N .............. saisie rapide d'un rendez-vous en une phrase
  Ctrl+Maj+D .............. dupliquer le rendez-vous
  Ctrl+Maj+J .............. rendez-vous de demain
  Ctrl+Maj+S .............. les 7 prochains jours
  Ctrl+Maj+M .............. les 30 prochains jours
  Ctrl+Maj+T .............. chercher un mot dans les prochains rendez-vous
  Ctrl+Maj+A / P / R ...... invitation : accepter, peut-être, refuser
  Ctrl+Maj+V .............. rejoindre la visioconférence
  Ctrl+Z .................. annuler le dernier déplacement
  Ctrl+Origine ............ aujourd'hui
  Ctrl+D .................. aller à une date
  Ctrl+Page suivante ...... mois suivant
  Ctrl+Page précédente .... mois précédent
  F1 ...................... aide générale
  Ctrl+F1 ................. cette liste des raccourcis
  Alt ..................... ouvrir la barre de menus

Liste des jours
  Flèches haut et bas ..... jour précédent ou suivant (change de mois au bout de la liste)
  Origine, Fin ............ premier ou dernier jour du mois
  Entrée .................. aller aux rendez-vous du jour

Liste des rendez-vous
  Ctrl+Flèche droite/gauche  décaler d'un jour (option dans Préférences, Raccourcis clavier)
  Ctrl+Flèche bas/haut .... décaler d'une heure
  Entrée .................. lire le détail du rendez-vous
  Échap ................... revenir à la liste des jours

Fenêtre de détail
  Ctrl+M .................. modifier
  Ctrl+S .................. supprimer
  Échap ................... fermer

Fiche de saisie d'un rendez-vous
  Alt+T titre, Alt+J toute la journée, Alt+D date de début, Alt+B heure de début,
  Alt+F date de fin, Alt+I heure de fin, Alt+L lieu, Alt+E description, Alt+V invités.
  Alt+P répétition, Alt+O fin de la répétition,
  Alt+R agenda (vue « Tous mes agendas »), Alt+A alarmes par défaut,
  Alt+H 1 heure avant, Alt+M 30 minutes, Alt+N 15 minutes, Alt+X à l'heure exacte.
  Entrée ou Alt+G : enregistrer. Échap : annuler.

Menus (touche Alt puis la lettre)
  Alt+F Fichier, Alt+D Édition, Alt+R Rendez-vous, Alt+A Affichage, Alt+L Alarmes, Alt+G Agenda, Alt+E Aide

Fenêtre d'alarme ou du résumé du matin
  Échap ou Entrée ......... fermer (et arrêter la sonnerie)
  Alt+5 ................... rappeler dans 5 minutes
  Alt+O ................... ouvrir l'agenda sur ce rendez-vous";
    }
}
