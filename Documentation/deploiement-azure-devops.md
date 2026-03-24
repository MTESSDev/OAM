# Déploiement Azure DevOps — Agent OAM

> **Destinataire :** Équipe DevOps / intégration continue
> **Prérequis :** Azure DevOps avec agents self-hosted Windows (domaine), accès aux serveurs IIS cibles

---

## Vue d'ensemble

OAM se compose de deux artefacts distincts à déployer :

| Artefact | Destination | Responsable |
|---|---|---|
| **Agent.Server** | Serveur IIS par environnement | Pipeline CI/CD |
| **agent.zip** | Postes agents via SMS | Pipeline CI/CD → SMS |
| **Side build** | Déposé sur le serveur (dossier `updates/side/`) | Pipeline CI/CD |

---

## 1. Pipeline de build (`azure-pipelines.yml`)

### 1.1 Vue d'ensemble des étapes

```
Build
 ├── Publish Agent.Server          → artefact : drop-server
 ├── Publish Agent.Service         → tmp
 ├── Publish Agent.TrayClient      → tmp/tray
 ├── Publish Agent.Updater         → tmp
 ├── ZIP → agent.zip               → artefact : drop-agent
 ├── Publish Agent.TrayClient Side → artefact : drop-side
 └── Tests (si applicable)
```

### 1.2 Fichier `azure-pipelines.yml`

```yaml
trigger:
  branches:
    include:
      - master

variables:
  buildConfiguration: 'Release'
  dotnetVersion: '10.x'

stages:

# ── STAGE BUILD ────────────────────────────────────────────────────────────

- stage: Build
  jobs:
  - job: BuildAll
    steps:

    - task: UseDotNet@2
      inputs:
        version: $(dotnetVersion)

    # 1. Agent.Server
    - task: DotNetCoreCLI@2
      displayName: 'Publish Agent.Server'
      inputs:
        command: publish
        projects: 'src/Agent.Server/Agent.Server.csproj'
        arguments: '--configuration $(buildConfiguration) --output $(Build.ArtifactStagingDirectory)/server'
        publishWebProjects: false
        zipAfterPublish: false

    # 2. Agent.Service
    - task: DotNetCoreCLI@2
      displayName: 'Publish Agent.Service'
      inputs:
        command: publish
        projects: 'src/Agent.Service/Agent.Service.csproj'
        arguments: >
          --configuration $(buildConfiguration)
          --runtime win-x64
          --self-contained
          --output $(Agent.TempDirectory)/agent-pkg

    # 3. Agent.TrayClient (Release) → sous-dossier tray\
    - task: DotNetCoreCLI@2
      displayName: 'Publish Agent.TrayClient'
      inputs:
        command: publish
        projects: 'src/Agent.TrayClient/Agent.TrayClient.csproj'
        arguments: >
          --configuration $(buildConfiguration)
          --runtime win-x64
          --self-contained
          --output $(Agent.TempDirectory)/agent-pkg/tray

    # 4. Agent.Updater (single-file)
    - task: DotNetCoreCLI@2
      displayName: 'Publish Agent.Updater'
      inputs:
        command: publish
        projects: 'src/Agent.Updater/Agent.Updater.csproj'
        arguments: >
          --configuration $(buildConfiguration)
          --output $(Agent.TempDirectory)/agent-pkg

    # 5. Créer agent.zip
    - task: ArchiveFiles@2
      displayName: 'Créer agent.zip'
      inputs:
        rootFolderOrFile: '$(Agent.TempDirectory)/agent-pkg'
        includeRootFolder: false
        archiveFile: '$(Build.ArtifactStagingDirectory)/agent/agent.zip'

    # 6. Agent.TrayClient Side (single-file, SIDE_MODE)
    - task: DotNetCoreCLI@2
      displayName: 'Publish Agent.TrayClient Side'
      inputs:
        command: publish
        projects: 'src/Agent.TrayClient/Agent.TrayClient.csproj'
        arguments: >
          --configuration Side
          --output $(Build.ArtifactStagingDirectory)/side

    # Publier les artefacts
    - publish: '$(Build.ArtifactStagingDirectory)/server'
      artifact: drop-server

    - publish: '$(Build.ArtifactStagingDirectory)/agent'
      artifact: drop-agent

    - publish: '$(Build.ArtifactStagingDirectory)/side'
      artifact: drop-side
```

---

## 2. Pipelines de release — par palier

### 2.1 Environnements

| Palier | Serveur | Side disponible | SMS |
|---|---|---|---|
| **DEV** | `oam-dev.intranet` | Non | Non |
| **QA / Acceptation** | `oam-qa.intranet` | Oui | Non |
| **PROD** | `oam.intranet` | Non | Oui |

---

### 2.2 Palier DEV

**Déclenchement :** automatique sur chaque build de `master`

**Étapes :**

1. Déployer `drop-server` sur IIS `oam-dev.intranet`
2. Transformer `appsettings.json` du serveur (variables DEV)
3. Créer/s'assurer que le dossier `updates/` existe dans le répertoire IIS
4. *(Optionnel)* Déposer `agent.zip` dans `updates/` pour tester la MAJ auto

**Variables à substituer dans `Agent.Server/appsettings.json` :**

| Clé | Valeur DEV |
|---|---|
| `Side:HubUrl` | `https://oam-dev.intranet/hub/user` |
| `Side:EnvironmentName` | `DEV` |
| `Side:UpdatePageUrl` | `https://oam-dev.intranet/side-update` |

**Variables à substituer dans `Agent.Service/appsettings.json` :**

| Clé | Valeur DEV |
|---|---|
| `Agent:UpdateUrl` | `https://oam-dev.intranet/updates/check` |
| `Agent:PortalUrl` | `https://portail-dev.intranet` |

---

### 2.3 Palier QA / Acceptation

**Déclenchement :** manuel (approbation requise)

**Étapes :**

1. Déployer `drop-server` sur IIS `oam-qa.intranet`
2. Transformer `appsettings.json` du serveur (variables QA)
3. Déposer `agent.zip` dans `<iis-root>/updates/` *(MAJ auto pour les agents QA)*
4. **Déposer `drop-side/Agent.TrayClient.exe` dans `<iis-root>/updates/side/`** *(Side build)*

**Variables `Agent.Server/appsettings.json` :**

| Clé | Valeur QA |
|---|---|
| `Side:HubUrl` | `https://oam-qa.intranet/hub/user` |
| `Side:EnvironmentName` | `ACCEPTATION` |
| `Side:UpdatePageUrl` | `https://oam-qa.intranet/side-update` |

**Variables `Agent.Service/appsettings.json` :**

| Clé | Valeur QA |
|---|---|
| `Agent:UpdateUrl` | `https://oam-qa.intranet/updates/check` |
| `Agent:PortalUrl` | `https://portail-qa.intranet` |

> Les agents QA téléchargent leur Side build via `GET /updates/side/download` — l'`appsettings.json` est généré dynamiquement par le serveur à partir de sa propre configuration.

---

### 2.4 Palier PROD

**Déclenchement :** manuel avec double approbation

**Étapes :**

1. Déployer `drop-server` sur IIS `oam.intranet`
2. Transformer `appsettings.json` du serveur (variables PROD)
3. **Publier `agent.zip` dans `<iis-root>/updates/`** → déclenche la MAJ auto sur tous les postes agents (nuit suivante, entre 1h et 6h)
4. *(Parallèle)* Fournir `agent.zip` à l'équipe SMS pour déploiement sur les nouveaux postes

**Variables `Agent.Server/appsettings.json` :**

| Clé | Valeur PROD |
|---|---|
| `Side:HubUrl` | *(laisser vide ou retirer — pas de Side en prod)* |
| `Side:EnvironmentName` | *(idem)* |

**Variables `Agent.Service/appsettings.json` :**

| Clé | Valeur PROD |
|---|---|
| `Agent:UpdateUrl` | `https://oam.intranet/updates/check` |
| `Agent:PortalUrl` | `https://portail.intranet` |

---

## 3. Transformation des fichiers de configuration

Utiliser la tâche **FileTransform** (ou `jsonPatch`) pour substituer les valeurs par environnement.

```yaml
- task: FileTransform@2
  displayName: 'Transformer appsettings.json (Server)'
  inputs:
    folderPath: '$(Pipeline.Workspace)/drop-server'
    jsonTargetFiles: '**/appsettings.json'
```

Les variables du release pipeline sont automatiquement mappées sur les clés JSON via la notation pointée : `Side.HubUrl`, `Side.EnvironmentName`, etc.

> Définir ces variables dans les **variable groups** Azure DevOps par environnement (Library → Variable groups), et lier chaque stage au bon groupe.

---

## 4. Infrastructure IIS requise

### Prérequis sur le serveur IIS

- Windows Server joint au domaine Active Directory
- .NET Hosting Bundle installé (version correspondant au `TargetFramework` du projet)
- Module IIS `WindowsAuthentication` activé
- Site IIS configuré avec :
  - **Authentication :** Windows Authentication activé, Anonymous désactivé
  - **Application Pool :** `No Managed Code`, identité = compte de service AD (pour Negotiate)
- Certificat SSL valide (wildcard ou SAN couvrant le FQDN)

### Structure des dossiers IIS

```
<iis-root>/
  Agent.Server.exe
  appsettings.json        ← transformé par le pipeline
  updates/
    agent.zip             ← déposé par le pipeline release
    side/
      Agent.TrayClient.exe  ← déposé par le pipeline release (QA seulement)
```

### Permissions

| Dossier | Compte | Droits |
|---|---|---|
| `<iis-root>/` | Compte app pool | Lecture |


---

## 5. Stratégie de mise à jour sur les postes agents

| Scénario | Mécanisme |
|---|---|
| Nouveau poste | SMS déploie `agent.zip` + installation via script |
| Poste existant | Service vérifie chaque nuit → télécharge + applique automatiquement |
| Urgence (forcer MAJ) | Déposer le nouveau `agent.zip` dans `updates/` → les postes se mettront à jour la nuit même |
| Rollback | Remplacer `agent.zip` par l'ancienne version → MAJ automatique vers l'ancienne version la nuit suivante |

---

## 6. Checklist de mise en production

- [ ] Build validé en DEV
- [ ] Tests fonctionnels passés en QA (agents QA avec Side build)
- [ ] `appsettings.json` PROD validé (URLs, credentials)
- [ ] Certificat SSL PROD valide et installé
- [ ] Dossier `updates/` accessible en écriture par l'app pool
- [ ] Approbation release PROD (double)
- [ ] Déploiement serveur effectué
- [ ] `agent.zip` déposé dans `updates/`
- [ ] Vérification : `GET /updates/check` retourne un hash
- [ ] Notification à l'équipe SMS si nouveaux postes à déployer
