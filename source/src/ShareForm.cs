using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AgendaAccessible
{
    /// <summary>Gestion du partage d'un agenda : voir qui y a accès, partager avec une personne, retirer un accès.</summary>
    class ShareForm : Form
    {
        class RoleChoice
        {
            public string Code, Text;
            public override string ToString() { return Text; }
        }

        readonly CalendarApi api;
        readonly CalendarInfo calendar;
        readonly ListBox rules;
        readonly TextBox email;
        readonly ComboBox role;
        readonly Button removeButton;
        readonly Label status;

        public ShareForm(CalendarApi api, CalendarInfo calendar)
        {
            this.api = api;
            this.calendar = calendar;
            Text = "Partager l'agenda « " + calendar.Name + " »";
            Font = Ui.BaseFont;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(820, 560);
            int tab = 0;

            var table = Ui.FieldTable();
            rules = Ui.AddField(table, "&Personnes ayant accès :", new ListBox { Height = 200, IntegralHeight = false }, ref tab);
            rules.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { e.Handled = true; RemoveSelected(); } };
            removeButton = Ui.MakeButton("&Retirer l'accès sélectionné (Suppr)", (s, e) => RemoveSelected());
            AddSingle(table, removeButton, ref tab);

            email = Ui.AddField(table, "&Adresse e-mail de la personne :", new TextBox(), ref tab);
            email.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; Share(); } };
            role = Ui.AddField(table, "&Droit accordé :", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, ref tab);
            role.Items.Add(new RoleChoice { Code = "reader", Text = "Voir tous les détails des événements" });
            role.Items.Add(new RoleChoice { Code = "freeBusyReader", Text = "Voir seulement si je suis libre ou occupé" });
            role.Items.Add(new RoleChoice { Code = "writer", Text = "Modifier les événements" });
            role.Items.Add(new RoleChoice { Code = "owner", Text = "Modifier les événements et gérer le partage" });
            role.SelectedIndex = 0;
            AddSingle(table, Ui.MakeButton("Partager a&vec cette personne", (s, e) => Share()), ref tab);
            AddSingle(table, Ui.MakeButton("&Copier l'identifiant de cet agenda", (s, e) =>
            {
                try { Clipboard.SetText(calendar.Id); Ui.Info(this, "Identifiant copié dans le presse-papiers :\r\n" + calendar.Id + "\r\n\r\nLa personne avec qui vous partagez peut l'utiliser pour ajouter cet agenda."); }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }), ref tab);

            status = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 30, Padding = new Padding(10, 4, 4, 4) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var close = Ui.MakeButton("&Fermer", null); close.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(close);
            CancelButton = close;
            table.TabIndex = 0; buttons.TabIndex = 1;
            Controls.Add(table);
            Controls.Add(buttons);
            Controls.Add(status);

            Shown += async (s, e) => { await Reload(); email.Focus(); };
        }

        static void AddSingle(TableLayoutPanel table, Control c, ref int tab)
        {
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.TabIndex = tab++;
            c.Anchor = AnchorStyles.Left;
            table.Controls.Add(c, 1, row);
        }

        async System.Threading.Tasks.Task Reload()
        {
            status.Text = "Chargement des partages…";
            UseWaitCursor = true;
            try
            {
                var list = await api.ListAcl(calendar.Id);
                rules.Items.Clear();
                // On n'affiche pas la ligne du propriétaire lui-même si c'est l'identifiant de l'agenda.
                foreach (var r in list.OrderBy(r => r.ScopeType == "user" ? 0 : 1).ThenBy(r => r.ScopeValue))
                    rules.Items.Add(r);
                if (rules.Items.Count > 0) rules.SelectedIndex = 0;
                rules.AccessibleName = "Personnes ayant accès : " + rules.Items.Count;
                status.Text = rules.Items.Count + " accès trouvé(s).";
            }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden || ex.Status == System.Net.HttpStatusCode.NotFound)
            {
                status.Text = "Vous ne pouvez pas gérer le partage de cet agenda.";
                Ui.Error(this, "Seul le propriétaire de cet agenda (ou une personne autorisée à gérer le partage) peut le partager.");
            }
            catch (Exception ex)
            {
                status.Text = "Erreur.";
                Ui.Error(this, ex.Message);
            }
            finally { UseWaitCursor = false; }
        }

        async void Share()
        {
            string mail = email.Text.Trim();
            if (mail.Length == 0 || !mail.Contains("@"))
            {
                Ui.Error(this, "Saisissez l'adresse e-mail de la personne (de préférence son adresse Gmail ou Google).");
                email.Focus();
                return;
            }
            var choice = (RoleChoice)role.SelectedItem;
            if (choice.Code == "owner" && !Ui.Confirm(this, "Avec ce droit, " + mail + " pourra modifier vos événements, partager l'agenda avec d'autres et même retirer votre accès. Continuer ?"))
                return;
            UseWaitCursor = true;
            try
            {
                await api.ShareWith(calendar.Id, mail, choice.Code);
                email.Clear();
                await Reload();
                Ui.Info(this, "L'agenda est partagé avec " + mail + " (" + choice.Text.ToLower() + "). Google lui envoie un e-mail pour l'ajouter à son agenda.");
                email.Focus();
            }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
            {
                Ui.Error(this, "Partage refusé par Google : " + ex.Message);
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
            finally { UseWaitCursor = false; }
        }

        async void RemoveSelected()
        {
            var r = rules.SelectedItem as AclRule;
            if (r == null) { Ui.Error(this, "Sélectionnez d'abord un accès dans la liste."); rules.Focus(); return; }
            if (r.Role == "owner" && r.ScopeType == "user" && string.Equals(r.ScopeValue, calendar.Id, StringComparison.OrdinalIgnoreCase))
            {
                Ui.Error(this, "C'est votre propre accès de propriétaire : il ne peut pas être retiré.");
                return;
            }
            if (!Ui.Confirm(this, "Retirer l'accès : " + r + " ?")) return;
            UseWaitCursor = true;
            try
            {
                await api.RemoveAcl(calendar.Id, r.Id);
                await Reload();
                status.Text = "Accès retiré.";
                rules.Focus();
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
            finally { UseWaitCursor = false; }
        }
    }
}
