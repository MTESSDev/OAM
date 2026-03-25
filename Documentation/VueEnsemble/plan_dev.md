# Plan de développement — OAM (Orchestrateur d'Actions Métier)

---

## 1. Infrastructure & Déploiement

### 1.1 Pipeline Azure DevOps
- Pipeline CI/CD complet (build → test → publish → deploy IIS)
- Déploiement sur serveur Windows IIS on-prem via Web Deploy ou xcopy
- Séparation des environnements : DEV / ACCP / PROD

### 1.2 Versionnage automatique
- Version générée automatiquement à chaque build (ex. : `MAJEUR.MINEUR.BUILD`)
- **Checksum des fichiers de configuration** (YAML) à chaque démarrage
  - Détection de modification non contrôlée
  - Journalisation de l'empreinte + date de chargement
  - Rejection ou avertissement si checksum invalide en PROD

### 1.3 Authentification
- ⚠️ **À trancher : éviter NTLM** si possible
  - Préférer Windows Auth négociée (Kerberos) ou JWT selon le contexte
  - Documenter la décision avant le début du développement

---

## 2. Architecture applicative

### 2.1 Moteur de workflow (`Workflow.Core`)
- Bibliothèque .NET 10 centrale
- Exécution des workflows définis en YAML
- Gestion du cycle de vie des instances (démarrage, pause, reprise, erreur, fin)
- **CorrelationId** obligatoire sur chaque instance de workflow
  - Propagé dans tous les logs, appels HTTP sortants et messages SignalR
  - Permet le traçage de bout en bout

### 2.2 Frontend Vue.js
- Interface de suivi des instances de workflow en temps réel
- Connexion SignalR pour les mises à jour push
- Vue par workflow, par instance, par étape

### 2.3 Accès aux données (Entity Framework Core)
- ⚠️ **Attention aux pièges EF** :
  - Utiliser les migrations contrôlées (pas de `EnsureCreated` en PROD)
  - Pas de lazy loading implicite — tout charger explicitement
  - Limiter les requêtes N+1 (utiliser `.Include()` consciemment)
  - Transactions explicites pour les opérations multi-étapes
- Entités principales : `WorkflowInstance`, `StepExecution`, `WorkflowDefinition`

### 2.4 SignalR
- Hub dédié au suivi en temps réel (`WorkflowHub`)
- Diffusion des événements : étape démarrée, terminée, erreur, workflow terminé
- Groupes par `CorrelationId` ou par utilisateur/rôle

---

## 3. Configuration YAML

### 3.1 Fichiers de définition
| Fichier | Rôle |
|---|---|
| `workflow.yml` | Définition du workflow : étapes, transitions, conditions |
| `specs.yml` | Métadonnées, paramètres d'entrée/sortie, contrats d'interface |

### 3.2 Structure d'un workflow
- Chaque étape référence un **type de tâche** (ex. : `http`, `mock`, `mdat`, tâche préconfigurée)
- Variables d'entrée/sortie enchaînées entre étapes via Handlebars/JSONPath
  - Syntaxe : `{{ steps.nomEtape.output.$.chemin.vers.valeur }}`
- Prise en charge des conditions, boucles et branchements

---

## 4. Intégrations & Extensions

### 4.1 `yamlHttpClient` (`anisite/yamlhttpclient`)
- Simplification des appels REST vers les services externes
- Configuration déclarative dans YAML (URL, headers, body, auth)
- Chaque appel HTTP identifié par un `id` réutilisable dans les tâches préconfigurées
- Propagation automatique du `CorrelationId` dans les headers sortants

### 4.2 Mocks
- Système de mock **obligatoire** pour :
  - Les tests automatisés (unitaires / intégration)
  - Les essais en environnement d'acceptation (ACCP) sans dépendances réelles
- Mécanisme de **résolution de mock** (`rechercherMock`) :
  - Recherche par critères dans un catalogue de réponses préconfigurées
  - Possibilité de définir des réponses conditionnelles selon les paramètres d'entrée

### 4.3 `mdat` (`mtessdev/mdat`) — Assertions simplifiées
- Intégration en tant que type d'étape (`type: mdat`)
- API simplifiée : définir les assertions directement dans le YAML de l'étape
- Usage principal : valider les réponses HTTP et l'état du workflow dans les specs de test

---

## 5. Tâches préconfigurées (Task Library)

Bibliothèque de tâches métier réutilisables, déclarées dans `specs.yml` et référencées dans `workflow.yml`.

### 5.1 Structure d'une tâche préconfigurée
```yaml
- id: rechercherRendezVous
  type: http          # référence un id yamlHttpClient
  httpClientId: getRendezVousApi
  mock: rechercherRendezVousMock   # résolveur mock associé
  output:
    rendezVous: "$.data.rendezVous"
```

### 5.2 Wrapper de réponse (pattern « rechercherMock-like »)
- Chaque tâche préconfigurée encapsule **la logique de réponse** :
  - En mode réel → appel HTTP via `yamlHttpClient`
  - En mode mock → résolution via `rechercherMock`
- Le consommateur (workflow) ne connaît pas le mode d'exécution

### 5.3 Tâches « famille » (Cône / Default)
- Possibilité de définir une tâche **parent par famille** (ex. : `GED`, `Agenda`)
- Les tâches spécifiques héritent du comportement par défaut de la famille
- Permet de changer le comportement d'un groupe de tâches en un seul endroit
  ```yaml
  famille: GED
  default:
    auth: bearer
    baseUrl: "{{ config.ged.baseUrl }}"
  ```

### 5.4 Variables & Enchaînement
- Les sorties de chaque tâche sont disponibles comme variables pour les étapes suivantes
- Résolution dynamique à l'exécution (Handlebars + JSONPath)
- Support des variables globales du workflow et des variables locales d'étape

---

## 6. Questions ouvertes / Décisions à prendre

| # | Sujet | État | Notes |
|---|---|---|---|
| 1 | Authentification (NTLM vs Kerberos vs JWT) | ❓ À décider | Impact sur l'intégration IIS |
| 2 | Périmètre simplifié de `mdat` | ❓ À préciser | Quelles assertions minimales suffisent ? |
| 3 | Granularité du `rechercherMock` | ❓ À concevoir | Par service ? par opération ? par scénario ? |
| 4 | Héritage de tâches (cône/famille) | ❓ À valider | Implémentation YAML (ancre ou surcharge explicite ?) |
| 5 | Persistance des mocks en ACCP | ❓ À décider | Base de données ? fichiers statiques ? |

---

## 7. Stack technique résumée

| Couche | Technologie |
|---|---|
| Backend | .NET 10 / C# |
| Frontend | Vue.js 3 |
| Base de données | SQL Server + EF Core |
| Temps réel | SignalR |
| Déploiement | Azure DevOps → IIS Windows on-prem |
| Config | YAML (`workflow.yml`, `specs.yml`) |
| HTTP sortant | `anisite/yamlhttpclient` |
| Assertions/Tests | `mtessdev/mdat` |
