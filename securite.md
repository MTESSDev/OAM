# Dossier de sécurité — OAM (Outil d'Aide aux Agents)

> **Objet :** Approbation de déploiement via trousse SMS Microsoft
> **Destinataire :** Équipe de sécurité informatique
> **Niveau de confidentialité :** Interne

---

## 1. Description fonctionnelle

OAM est un outil de productivité destiné aux agents gouvernementaux. Il s'installe sur les postes de travail Windows et permet de **consulter rapidement des informations agrégées provenant de systèmes internes** (ex. : GED, ASF) à partir d'un code de dossier.

L'agent colle ou tape un code dans une barre de recherche (activée par un raccourci clavier global). L'outil interroge les systèmes internes en parallèle et affiche les résultats. Aucune donnée n'est saisie manuellement au-delà du code de recherche.

---

## 2. Composants déployés sur le poste client

| Composant | Nature | Compte d'exécution | Rôle |
|---|---|---|---|
| **Agent.Service** | Service Windows | `LocalSystem` | Orchestration, mise à jour automatique, lancement du client tray dans les sessions utilisateur |
| **Agent.TrayClient** | Application WinForms/WPF | Compte de l'utilisateur connecté | Icône de notification, fenêtre de recherche, connexion SignalR au serveur interne |
| **Agent.Updater** | Exécutable ponctuel | `LocalSystem` (hérité du service) | Applique les mises à jour : arrête le service, remplace les fichiers, redémarre |

> **Remarque :** Le serveur (`Agent.Server`) est déployé sur l'infrastructure interne, **pas sur le poste client**.

---

## 3. Permissions requises sur le poste

### 3.1 Service Windows — compte `LocalSystem`

Le service s'exécute sous `LocalSystem` pour une raison technique précise et documentée :

**Démarrage du tray dans la session utilisateur depuis la session 0**

Les services Windows s'exécutent dans la session 0, isolée des sessions utilisateur. Pour lancer l'icône de notification dans la session de l'utilisateur connecté, le service doit :
1. Énumérer les sessions actives (`WTSEnumerateSessions`) — nécessite `LocalSystem`
2. Obtenir le jeton de sécurité de l'utilisateur (`WTSQueryUserToken`) — nécessite le privilège `SE_TCB_NAME`, disponible uniquement sous `LocalSystem`
3. Créer le processus dans la session utilisateur (`CreateProcessAsUser`)

**Sans `LocalSystem`, cette fonctionnalité est techniquement impossible** sous Windows. Il n'existe pas de compte moins privilégié qui possède `SE_TCB_NAME`.

> Ce pattern est utilisé de façon standard par les agents de supervision d'entreprise (antivirus, MDM, outils de monitoring).

### 3.2 Client tray — compte utilisateur standard

Le client tray s'exécute **avec les permissions de l'utilisateur connecté**, sans élévation. Il n'a pas accès aux ressources système. Il effectue uniquement :
- Des requêtes HTTP vers des URLs internes configurées
- Une connexion SignalR vers le serveur interne (authentification Windows)

### 3.3 Résumé des accès réseau sortants

| Destination | Protocole | Port | Authentification | Obligatoire |
|---|---|---|---|---|
| Serveur OAM interne | HTTPS | 5001 | Windows Negotiate (Kerberos/NTLM) | Oui |
| GED interne | HTTPS | 443 | Windows NTLM | Optionnel |
| ASF interne | HTTPS | 443 | Windows NTLM | Optionnel |

**Aucun trafic vers Internet.** Toutes les URLs sont configurées dans `appsettings.json` et pointent vers l'intranet gouvernemental.

---

## 4. Authentification et contrôle d'accès

### 4.1 Authentification du client tray → serveur interne

Le client tray utilise l'**authentification Windows intégrée** (protocole Negotiate, soit Kerberos ou NTLM selon l'environnement Active Directory). L'identité de l'utilisateur est transmise automatiquement via ses credentials de session Windows — **aucun mot de passe supplémentaire n'est requis ou stocké**.

Le serveur interne valide le ticket Kerberos/NTLM avant d'accepter la connexion. Un utilisateur dont le compte est désactivé dans l'AD perd immédiatement l'accès.

### 4.2 Authentification du service Windows → serveur interne (mises à jour)

Le service interroge le serveur de mises à jour via HTTPS pour obtenir le hash SHA-256 de la version courante. Cette requête est anonyme (pas de credentials Windows) car le service s'exécute sous `LocalSystem`, qui n'a pas de compte de domaine.

L'endpoint de mise à jour n'expose **aucune donnée sensible** — il retourne uniquement un hash et une URL de téléchargement.

---

## 5. Mécanisme de mise à jour automatique

Le service vérifie toutes les heures si une mise à jour est disponible en comparant un **hash SHA-256** avec la version installée.

**Séquence de mise à jour :**

```
Service → GET https://serveur-oam/updates/check  (retourne: hash + URL)
         ↓ hash différent de last-update.sha256
Service → Téléchargement du ZIP depuis le serveur interne
         ↓ Vérification SHA-256 du fichier téléchargé
         ↓ Hash invalide → abandon immédiat (protection contre la corruption/substitution)
         ↓ Hash valide → extraction dans %TEMP%
Service → Lancement de Agent.Updater
         ↓ Arrêt du service
         ↓ Sauvegarde de la version actuelle (backup dans %TEMP%)
         ↓ Copie des nouveaux fichiers
         ↓ Écriture du nouveau hash dans last-update.sha256
         ↓ Redémarrage du service
         ↓ Échec → restauration automatique du backup
```

**Garanties de sécurité du mécanisme de mise à jour :**
- Le package est téléchargé **uniquement depuis le serveur interne** (URL configurée statiquement)
- Le hash SHA-256 est vérifié **avant toute installation** — un package modifié ou corrompu en transit est rejeté
- En cas d'échec d'installation, la version précédente est **restaurée automatiquement**
- Le service ne télécharge rien si le hash correspond à la version installée (pas de téléchargement inutile)

---

## 6. Données traitées

| Type de donnée | Stockage | Transmission | Durée de rétention |
|---|---|---|---|
| Code de recherche saisi | Mémoire vive uniquement | Vers les systèmes internes (HTTPS) | Durée de la session (cache 30 s) |
| Résultats de recherche | Mémoire vive uniquement | Non transmis | Durée de la session |
| Identité Windows de l'utilisateur | Non stockée | Via Kerberos/NTLM vers serveur interne | Non retenue |
| Hash de mise à jour | `last-update.sha256` (fichier texte local) | Non | Permanent (fichier de contrôle) |
| Logs applicatifs | Windows Event Log (Application) | Non | Selon politique de rétention Windows |

**Aucune donnée personnelle ou de dossier n'est persistée sur le poste client.**

---

## 7. Raccourci clavier global

L'outil enregistre le raccourci **Ctrl + Win + Alt + Espace** via l'API Windows `RegisterHotKey`. Ce raccourci :
- Ouvre la fenêtre de recherche locale
- N'intercepte **aucune autre frappe**
- Est désenregistré à la fermeture du tray ou à l'arrêt du service
- N'enregistre aucune activité de frappe (keylogger : **non**)

---

## 8. Registre Windows

L'outil **ne modifie pas** le registre Windows pour son fonctionnement normal.

Une entrée optionnelle de démarrage automatique (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) est prévue dans le code mais est **désactivée** dans la version actuelle (commentée).

---

## 9. Surface d'attaque et mesures de mitigation

| Vecteur | Risque | Mitigation |
|---|---|---|
| Substitution du package de mise à jour | Déploiement de code malveillant | Vérification SHA-256 obligatoire avant installation |
| Interception réseau (MITM) | Lecture des résultats de recherche | HTTPS obligatoire sur tous les endpoints |
| Usurpation d'identité | Accès aux systèmes internes sous une fausse identité | Authentification Kerberos (non forgeable sans accès AD) |
| Élévation de privilèges via le service | Compromission de `LocalSystem` | Le service ne charge aucun code externe ni plugin ; seul le package signé peut être déployé |
| Path traversal sur l'endpoint de téléchargement | Lecture de fichiers arbitraires | Validation du nom de fichier côté serveur (rejet de `/`, `\`, `..`) |

---

## 10. Empreinte sur le poste de travail

| Élément | Valeur |
|---|---|
| Répertoire d'installation | Configurable via SMS (ex. : `C:\Program Files\OAM\`) |
| Espace disque approximatif | ~30 Mo (runtime .NET 10 requis séparément ou inclus) |
| Prérequis | .NET 10 Runtime Windows |
| Processus en mémoire | 2 (service + tray par session utilisateur) |
| Ports ouverts en écoute | **Aucun** — le poste client n'ouvre aucun port |
| Connexions réseau sortantes | 1 connexion persistante SignalR HTTPS vers le serveur interne |

---

## 11. Déploiement via SMS Microsoft

### Commande d'installation recommandée
```
sc create MonServiceSecure binPath= "C:\Program Files\OAM\Agent.Service.exe" start= auto
sc config MonServiceSecure obj= "LocalSystem"
sc start MonServiceSecure
```

Ou via le script PowerShell fourni (`Manage-AgentService.ps1 install`) qui exécute les étapes ci-dessus avec vérification d'intégrité.

### Désinstallation propre
```
sc stop MonServiceSecure
sc delete MonServiceSecure
```

### Détection pour SMS
- **Fichier de présence :** `C:\Program Files\OAM\Agent.Service.exe`
- **Service :** `MonServiceSecure` (état `Running`)

---

## 12. Contacts

| Rôle | Contact |
|---|---|
| Responsable applicatif | À compléter |
| Responsable infrastructure | À compléter |
| Équipe de développement | À compléter |

---

*Document généré à partir du code source du dépôt OAM — à mettre à jour lors de chaque modification architecturale significative.*
