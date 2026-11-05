# Agenda Google accessible

**Votre agenda Google entièrement au clavier, accessible avec NVDA et JAWS. Pour Windows 10 et 11. En français.**

Par Exatropic.

## Ce qu'il fait

- **Les jours et les rendez-vous en deux listes** : « Aujourd'hui, jeudi 5 novembre, 2 rendez-vous », puis « De 10h00 à 11h30 : Dentiste, lieu : Rennes ». F6 passe de l'une à l'autre, Entrée lit le détail.
- **Tous vos agendas ensemble, ou un seul** (Ctrl+G) : le vôtre, ceux que vous avez créés, ceux qu'on vous a partagés.
- **Créer, modifier, supprimer** des rendez-vous, avec invités, lieu et description. À la création, on choisit l'agenda.
- **Rendez-vous qui se répètent** : tous les jours, du lundi au vendredi, toutes les semaines, tous les mois, tous les ans, ou « Anniversaire ».
- **Alarmes** : 1 heure, 30 minutes, 15 minutes avant ou à l'heure exacte. Une sonnerie retentit et une fenêtre lue par le lecteur d'écran passe au premier plan. Les alarmes sont enregistrées dans Google : le téléphone prévient aussi.
- **Résumé du matin** : au premier démarrage de la journée, les rendez-vous du jour et les alarmes prévues.
- **Toujours prêt** : l'agenda démarre avec Windows et reste près de l'horloge. Ctrl+Alt+A l'ouvre depuis n'importe quelle application.
- **Partage** : partager un agenda avec quelqu'un, s'abonner à un agenda qu'on vous a partagé, créer un agenda.
- **Recherche** dans un agenda ou dans tous.

## Télécharger

[AgendaGoogleAccessible.zip](https://github.com/exatropix/agenda-google-accessible/raw/main/telechargement/AgendaGoogleAccessible.zip)

Décompressez-le dans un dossier, puis lancez AgendaGoogleAccessible.exe. Si Windows affiche « Windows a protégé votre ordinateur », choisissez « Informations complémentaires », puis « Exécuter quand même » : le programme n'est pas signé.

Cette version publique demande un identifiant Google personnel, à créer une seule fois dans Google Cloud (environ 10 minutes, gratuit). Toutes les étapes sont dans le [mode d'emploi](LISEZMOI.txt), partie 2.

## Les commandes essentielles

- F6 : passer des jours aux rendez-vous. Entrée : le détail. Échap : revenir aux jours.
- Ctrl+N nouveau, Ctrl+M modifier, Suppr supprimer.
- Ctrl+G : tous les agendas ou un seul. Ctrl+F : rechercher. Ctrl+D : aller à une date. Ctrl+Origine : aujourd'hui.
- Ctrl+L : prochaines alarmes. Ctrl+R : résumé du jour.
- Alt+F4 ferme la fenêtre (les alarmes continuent), Ctrl+Q quitte vraiment.
- F1 aide complète, Ctrl+F1 toutes les touches.

## Confidentialité

L'application parle directement à Google, sans aucun serveur intermédiaire : personne d'autre que vous ne voit votre agenda. Le jeton de connexion est enregistré chiffré par Windows sur votre ordinateur. Voir la [politique de confidentialité](https://exatropix.github.io/agenda-google-accessible/confidentialite.html).

## Compiler

Le code source est dans le dossier `source` (C#, .NET Framework 4.6.2, Windows Forms, sans dépendance externe).
Sous Windows : `dotnet build -c Release` dans `source`. Sous Linux, avec Mono : `sh source/build.sh`.

Ne mettez jamais de fichier d'identifiants Google (`credentials.json`, `agenda-identifiants.json`) dans le dépôt : le fichier `.gitignore` les exclut.

## Licence

Domaine public (Unlicense) : utilisez, copiez, modifiez et redistribuez librement. Voir [LICENSE](LICENSE).

## English

**Accessible Google Calendar** is a free Windows app for managing Google Calendar entirely from the keyboard with NVDA or JAWS: day and event lists, all calendars at once or one at a time, recurring events, audible reminders with a screen-reader friendly alert window, a morning summary, calendar sharing and search. The interface is in French. It talks directly to Google with no intermediate server. [Download](https://github.com/exatropix/agenda-google-accessible/raw/main/telechargement/AgendaGoogleAccessible.zip) · [Privacy policy](https://exatropix.github.io/agenda-google-accessible/confidentialite.html) · Public domain (Unlicense).
