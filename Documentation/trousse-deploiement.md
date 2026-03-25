# Trousse de déploiement — Agent OAM

> **Destinataire :** Responsable de la trousse SMS Microsoft\
> **Application :** Agent OAM\
> **Version minimale de Windows :** Windows 10 x64\
> **Prérequis sur le poste :** Aucun (.NET embarqué dans le package)

---

## 1. Contenu du package

Le package de déploiement contient un seul fichier :

| Fichier | Description |
|---|---|
| `agent.zip` | Archive contenant l'ensemble de l'application (service + tray + updater) |

---

## 2. Installation

### 2.1 Répertoire d'installation

Choisir un répertoire fixe sur le poste. Exemple recommandé :

```
C:\Program Files\OAM\
```

> Le répertoire doit être le même sur tous les postes. Le service de mise à jour automatique en dépend.

### 2.2 Étapes d'installation

**Étape 1 — Extraire le ZIP**

Extraire le contenu de `agent.zip` dans le répertoire d'installation :

```bat
powershell -Command "Expand-Archive -Path agent.zip -DestinationPath 'C:\Program Files\OAM' -Force"
```

**Étape 2 — Créer la source EventLog**

```bat
powershell -Command "New-EventLog -LogName Application -Source 'AgentOAM'"
```

**Étape 3 — Créer le service Windows**

```bat
sc create AgentOAM binPath= "C:\Program Files\OAM\Agent.Service.exe" start= auto
sc config AgentOAM obj= LocalSystem DisplayName= "Agent OAM"
sc description AgentOAM "Outil d'aide à la mission"
```

**Étape 4 — Démarrer le service**

```bat
sc start AgentOAM
```

---

## 3. Vérification post-installation

| Vérification | Commande | Résultat attendu |
|---|---|---|
| Service en cours d'exécution | `sc query AgentOAM` | `STATE : 4 RUNNING` |
| Fichier présent | Présence de `Agent.Service.exe` dans le répertoire d'installation | Fichier existant |
| Icône tray visible | Observation de la session utilisateur | Icône verte dans la zone de notification |

---

## 4. Désinstallation

```bat
sc stop AgentOAM
sc delete AgentOAM
powershell -Command "Remove-EventLog -Source 'AgentOAM'"
```

Supprimer ensuite le répertoire d'installation manuellement si nécessaire.

---

## 5. Critères de détection pour SMS

| Méthode | Valeur |
|---|---|
| Fichier de présence | `<répertoire d'install>\Agent.Service.exe` |
| Service Windows | Nom : `AgentOAM`, état : `Running` |
| Clé de registre (tray au démarrage) | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AgentOAMTray` |

---

## 7. Contacts

| Rôle | Contact |
|---|---|
| Responsable applicatif | Dany Côté |
| Responsable infrastructure serveur | MCN |
| Équipe de développement | SPFS |
