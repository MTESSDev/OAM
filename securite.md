# Dossier de sécurité — OAM (Outil d'Aide à la Mission)

> **Objet :** Approbation de déploiement via trousse SMS Microsoft\
> **Destinataire :** Équipe de sécurité informatique\
> **Niveau de confidentialité :** Interne

---

## 1. Description fonctionnelle

OAM est un outil de gestion de postes de travail Windows destiné aux postes de bureau. Il se compose de deux parties distinctes : un **agent déployé sur chaque poste client** et un **serveur de gestion centralisé hébergé sur l'intranet**.

L'agent permet au serveur de :
- Connaître en temps réel quelles machines et quels utilisateurs sont connectés
- **Ouvrir une URL dans le navigateur** d'un utilisateur spécifique ou de tous les utilisateurs, à la demande d'un administrateur
- Déployer automatiquement des mises à jour logicielles sur tous les postes

L'agent se manifeste sur le poste par une **icône dans la zone de notification** (system tray). Il ne présente aucune fenêtre au démarrage et n'interrompt pas le travail des utilisateurs.

---

## 2. Architecture globale

```
┌─────────────────────────── POSTE CLIENT ────────────────────────────┐
│                                                                      │
│  ┌──────────────────────────────────┐                               │
│  │  Agent.Service  (LocalSystem)    │  ← Service Windows             │
│  │  · Surveille les sessions        │    · Démarre automatiquement   │
│  │  · Lance le TrayClient           │    · Toujours actif            │
│  │  · Vérifie les mises à jour/h    │                               │
│  └──────────┬──────────────────────┘                               │
│             │ lance dans chaque session                             │
│  ┌──────────▼──────────────────────┐                               │
│  │  Agent.TrayClient  (utilisateur) │  ← Application WinForms        │
│  │  · Icône tray verte/rouge        │    · Une instance par session  │
│  │  · Connexion SignalR au serveur  │    · Credentials Windows auto  │
│  │  · Ouvre les URLs reçues         │                               │
│  └──────────────────────────────────┘                               │
└──────────────────────┬──────────────────────────────────────────────┘
                       │ HTTPS (5001)
                       ▼
┌───────────── SERVEUR INTERNE (Agent.Server) ─────────────────────────┐
│  · Hub SignalR /hub/user   (Windows Auth, pour Agent.TrayClient)      │
│  · API REST /updates/      (distribution des mises à jour)            │
└──────────────────────────────────────────────────────────────────────┘
```

---

## 3. Composants déployés sur le poste client

| Composant | Nature | Compte d'exécution | Démarrage |
|---|---|---|---|
| **Agent.Service** | Service Windows | `LocalSystem` | Automatique au démarrage Windows |
| **Agent.TrayClient** | Application WinForms | Compte de l'utilisateur connecté | Lancé par Agent.Service à l'ouverture de session |
| **Agent.Updater** | Exécutable ponctuel | `LocalSystem` (hérité) | Lancé par Agent.Service uniquement lors d'une mise à jour |

---

## 4. Permissions requises — justification technique

### 4.1 Pourquoi `LocalSystem` pour le service

Le service Windows s'exécute dans la **session 0**, une session système isolée des sessions utilisateur graphiques par Windows. Pour lancer l'icône de notification dans la session de l'utilisateur connecté, le service doit :

1. **Énumérer les sessions actives**
2. **Créer le processus TrayClient** dans la session utilisateur

Le privilège de créer un processus sur la session utilisateur est **exclusivement accordé au compte `LocalSystem`** dans Windows. Aucun compte de service moins privilégié ne permet d'effectuer cette opération.

Ce mécanisme est identique à celui utilisé par les antivirus, les agents MDM (Intune, SCCM) et les outils de monitoring d'entreprise pour afficher une interface dans la session utilisateur depuis un service système. Il est essentiel pour assurer la haute disponibilité du TrayClient et garantir par le fait même la livraison des commandes cruciaux au bon fonctionnement de notre application.

### 4.2 Ce que `LocalSystem` fait concrètement dans ce code

Le service utilise `LocalSystem` **uniquement pour** :
- Lancement du tray
- Écrire des logs dans le Windows Event Log
- Appeler `GET /updates/check` (HTTPS anonyme vers le serveur interne)
- Télécharger et installer les mises à jour

Le service **ne fait pas** :
- Accéder aux données des utilisateurs
- Lire des fichiers personnels ou des secrets
- S'authentifier auprès d'autres systèmes
- Ouvrir de port réseau en écoute

### 4.3 Agent.TrayClient — compte utilisateur standard

Le TrayClient s'exécute avec les **permissions de l'utilisateur connecté**, sans élévation. Il se connecte au serveur via SignalR avec les credentials Windows de cet utilisateur (Kerberos/NTLM automatique).

---

## 5. Flux réseau — détail complet

### Depuis le poste client (sortant uniquement — aucun port en écoute)

| Émetteur | Destination | Chemin | Protocole | Auth | Contenu |
|---|---|---|---|---|---|
| Agent.Service | Serveur OAM interne | `GET /updates/check` | HTTPS | Aucune | Demande le hash SHA-256 courant |
| Agent.Service | Serveur OAM interne | `GET /updates/download/agent.zip` | HTTPS | Aucune | Téléchargement du package ZIP (uniquement si mise à jour détectée) |
| Agent.TrayClient | Serveur OAM interne | `/hub/user` (WebSocket) | HTTPS + WSS | Windows Negotiate (Kerberos/NTLM) | Connexion persistante |

### Ce que le TrayClient envoie au serveur (via SignalR)

Lors de la connexion, le TrayClient envoie une seule donnée :
- **`RegisterUser(machineName)`** — le nom NetBIOS de la machine locale (`Environment.MachineName`)

Le serveur extrait l'identité Windows de l'utilisateur directement depuis le ticket Kerberos/NTLM — **le client ne transmet jamais son nom d'utilisateur lui-même**.

### Ce que le TrayClient reçoit du serveur (via SignalR)

Le TrayClient reçoit un seul type de message :
- **`OpenUrl(url)`** — une URL à ouvrir dans le navigateur par défaut de l'utilisateur

---

## 6. Authentification et contrôle d'accès

### 6.1 Hub TrayClient (`/hub/user`) — authentification Windows obligatoire

L'attribut `[Authorize]` est positionné sur la **classe entière** du hub (pas sur les méthodes), ce qui force le challenge Negotiate **dès la phase de connexion WebSocket**, avant que le hub soit accessible. Une connexion sans credentials Windows valides est rejetée immédiatement.

Le serveur enregistre pour chaque session :
- `ConnectionId` SignalR
- `MachineName` (fourni par le client)
- `WindowsUserName` (extrait de `Context.User.Identity.Name` — garanti par Kerberos/NTLM, non forgeable côté client)
- `ConnectedAt`

---

## 7. Mécanisme de mise à jour automatique

Le service vérifie toutes les heures (et au démarrage) si une mise à jour est disponible, en comparant le hash SHA-256 du package installé avec celui publié sur le serveur.

```
[Au démarrage + toutes les heures]

Agent.Service
  │
  ├─► GET /updates/check  →  { hash, downloadUrl }
  │
  ├─ hash == contenu de last-update.sha256  →  rien à faire
  │
  └─ hash différent
       │
       ├─► Téléchargement du ZIP depuis l'URL retournée
       ├─  Calcul SHA-256 du fichier reçu
       ├─  hash reçu ≠ hash calculé  →  fichier supprimé, abandon
       │
       └─  hash valide  →  lancement de Agent.Updater avec args :
                              ServiceName  ← nom du service Windows
                              SourceDir    ← dossier d'extraction du ZIP (%TEMP%)
                              InstallDir   ← répertoire d'installation courant
                              BackupDir    ← dossier de sauvegarde (%TEMP%)
                              NewHash      ← hash à écrire après succès

Agent.Updater (s'exécute en LocalSystem)
  ├─ Arrêt du service Windows (attend max 60 s)
  ├─ Copie de sauvegarde du répertoire d'installation → BackupDir
  ├─ Copie des nouveaux fichiers → InstallDir (écrasement)
  ├─ Écriture du NewHash dans last-update.sha256
  ├─ Redémarrage du service
  └─ Échec à n'importe quelle étape
       └─ Restauration de BackupDir → InstallDir
          Redémarrage du service avec l'ancienne version
```

**Garanties de sécurité du mécanisme :**
- Le package n'est téléchargé que depuis l'URL retournée par le serveur interne
- La vérification SHA-256 est faite **en mémoire** avant d'écrire le fichier sur disque — un package altéré ou corrompu en transit n'est jamais extrait
- La sauvegarde automatique garantit qu'un échec d'installation ne laisse pas le poste sans service fonctionnel
- Protection contre le path traversal côté serveur : le nom de fichier demandé est validé (rejet explicite de `/`, `\`, `..`)

---

## 8. Données stockées sur le poste client

| Donnée | Emplacement | Contenu | Persistante |
|---|---|---|---|
| Hash de mise à jour | `<répertoire d'install>\last-update.sha256` | Chaîne hexadécimale SHA-256 du dernier package installé | Oui |
| Logs applicatifs | Windows Event Log (journal : Application, source : MonServiceSecure) | Événements de démarrage, mise à jour, erreurs | Selon politique de rétention Windows |

**Aucune donnée personnelle, aucun contenu de dossier, aucun credential** n'est stocké sur le poste.

---

## 9. Données stockées en mémoire sur le serveur

Le serveur maintient en mémoire (non persisté sur disque) un registre des connexions actives :

| Donnée | Source | Usage |
|---|---|---|
| Nom de la machine (`MachineName`) | `Environment.MachineName` côté client | Ciblage des commandes par machine |
| Identité Windows de l'utilisateur (`WindowsUserName`) | Extrait du ticket Kerberos/NTLM par le serveur | Journalisation, ciblage |
| ID de connexion SignalR (`ConnectionId`) | Généré par SignalR | Routage des messages |
| Heure de connexion (`ConnectedAt`) | Horodatage serveur | Journalisation |

Ces données sont **purgées automatiquement** lors de la déconnexion du client (fermeture de session, arrêt du service, coupure réseau).

---

## 10. Ce que l'outil ne fait pas

| Capacité | Présent dans le code |
|---|---|
| Capture de frappes clavier (keylogger) | Non |
| Accès aux fichiers de l'utilisateur | Non |
| Capture d'écran | Non |
| Exécution de commandes arbitraires sur le poste | Non — seul `OpenUrl` est reçu du serveur |
| Transmission de données de navigation ou d'activité | Non |
| Modification du registre Windows | Non |
| Connexion vers Internet | Non — toutes les URLs pointent vers l'intranet |
| Écoute de port réseau sur le poste | Non |

---

## 11. Surface d'attaque et mesures en place

| Vecteur | Risque | Mitigation |
|---|---|---|
| Interception du package de mise à jour (MITM) | Installation de code malveillant | Vérification SHA-256 obligatoire ; rejet si le hash ne correspond pas |
| Falsification du hash servi par `/updates/check` | Forcer une mise à jour vers un package malveillant | L'URL du serveur est configurée statiquement ; la communication est en HTTPS ; le hash du package doit correspondre |
| Compromission du serveur OAM | Envoi d'URLs malveillantes à tous les utilisateurs via `OpenUrl` | L'impact est limité à l'ouverture d'une URL dans le navigateur — aucune exécution de code arbitraire côté client - Une sécurité limite aussi les urls atteignables aux quelques unes requises |
| Usurpation d'identité sur `/hub/user` | Connexion sous une fausse identité Windows | Kerberos : le ticket est émis et signé par le contrôleur de domaine, non forgeable côté client |
| Abus du compte `LocalSystem` | Exécution de code arbitraire avec privilèges système | Le service ne charge aucun plugin ni code externe ; seul le package déployé via SMS ou mis à jour par le mécanisme interne (hash vérifié) est exécuté |
| Path traversal sur `/updates/download/{filename}` | Lecture de fichiers arbitraires sur le serveur | Validation explicite dans le code : rejet de `/`, `\` et `..` |

---

## 12. Empreinte sur le poste de travail

| Élément | Valeur |
|---|---|
| Répertoire d'installation | Configurable via SMS |
| Espace disque | ~30 Mo (hors runtime .NET 10 si déployé séparément) |
| Prérequis | .NET 10 Runtime Windows x64 |
| Processus actifs | `Agent.Service.exe` (session 0) + `Agent.TrayClient.exe` (1 par session utilisateur active) |
| Ports ouverts en écoute sur le poste | **Aucun** |
| Connexions réseau sortantes | 1 WebSocket HTTPS persistante (TrayClient → serveur) + 1 requête HTTP/h (Service → `/updates/check`) |

---

## 13. Déploiement via SMS Microsoft

### Commandes d'installation

```bat
sc create MonServiceSecure binPath= "C:\Program Files\OAM\Agent.Service.exe" start= auto DisplayName= "Agent OAM"
sc config MonServiceSecure obj= "LocalSystem"
sc description MonServiceSecure "Agent OAM - Surveillance et mise a jour"
sc start MonServiceSecure
```

Le script fourni (`Manage-AgentService.ps1 install`) exécute ces étapes avec vérification post-installation.

### Désinstallation propre

```bat
sc stop MonServiceSecure
sc delete MonServiceSecure
```

### Critères de détection pour SMS

| Méthode | Valeur |
|---|---|
| Fichier de présence | `<répertoire d'install>\Agent.Service.exe` |
| Service Windows | Nom : `MonServiceSecure`, état : `Running` |

---

## 14. Contacts

| Rôle | Contact |
|---|---|
| Responsable applicatif | Dany Côté |
| Responsable infrastructure serveur | MCN |
| Équipe de développement | SPFS |

---
