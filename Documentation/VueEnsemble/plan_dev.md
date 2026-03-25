# Plan de développement — OAM (Orchestrateur d'Actions Métier)


Description:

OAM est un système autoportant qui permet d'orchestrer des tâches de simple à complexe, de faire le suivi en temps réel, de faire l'assurance qualité et de configurer via des déploiement azure devops et des endpoints et aussi en partie via une interface fluide et simple à utiliser.
---

## 1. Infrastructure & Déploiement

### 1.1 Pipeline Azure DevOps
- Pipeline CI/CD complet (build → test → publish → deploy IIS) yaml
- Déploiement sur serveur Windows IIS on-prem via tâches classique
- Séparation des environnements : SAT / ACCP / IT / PROD

### 1.3 Authentification
- les utilisateurs s'authentifie par un JWT généré par un endpoint intégré à la solution qui elle utilise NTLM pour le moment
- l'application ne gère que le JWT BEARER partout sauf sur le endpoint de création du JWT (conversion NTLM en JWT par .net)

---

## 2. Architecture applicative

### 2.1 Moteur de workflow (`Workflow.Core`) "pimpé"
- Bibliothèque .NET 10 centrale
- Exécution des workflows définis en YAML -> Simple à utiliser, avec un schema de validation (YamlLanguageServer) pour faciliter la saisie
- Gestion du cycle de vie des instances (démarrage, pause, reprise, erreur, fin)
- **CorrelationId** obligatoire sur chaque instance de workflow
  - Propagé dans tous les logs, appels HTTP sortants et messages SignalR
  - Permet le traçage de bout en bout
- Un GUID unique détermine l'instance en cours
- Un instance de workflow conserve un état figé dans le temps, la version de config étant un hash (un peu comme les commit git)
  - il est donc possible en tout temps via l'interface de suivi, de voir le contenu de la config utilisé, il est aussi possible de la corrigée au besoin.
- Dans une instance, chaque étape du workflow montre les données "INPUT" et "OUTPUT" elles sont entièrement modifiables lorsque que l'étape est en erreur
- une mécanique permet de filtrer sur les critères de base les demandes en erreur et de les reprendre et même de les corriger toutes en meme temps (PATCH)
- Workflow.core utilise le produit de persistance de BD pour SQL serveur
- Les cas de tests sont déloyés sur une autre API et ne changent pas les versions des workflows

### 2.2 Frontend Vue.js
- Interface de suivi des instances de workflow en temps réel
- Connexion SignalR pour les mises à jour push
- Vue par workflow, par instance, par étape
- Une page qui montre les gabarits de worflow gradués pour les équipes autorisés de l'utilisateur connecté
  - Possibilité de voir les versions
  - Visuel montrant le détail de la définition (gabarit) du workflow
  
  - Des outils pour lancer les tests déployés
- Une page qui montre les instances en cours, en temps réel
  - Visuel montrant l'état actuel et les tâches en cours / en erreur

### 2.3 Accès aux données (Entity Framework Core)
- ⚠️ **Attention aux pièges EF** :
  - Utiliser les migrations contrôlées (pas de `EnsureCreated` en PROD)
  - Pas de lazy loading implicite — tout charger explicitement
  - Limiter les requêtes N+1 (utiliser `.Include()` consciemment)
  - Transactions explicites pour les opérations multi-étapes
- Entités principales : `WorkflowInstance`, `StepExecution`, `WorkflowDefinition` à nommer selon le vocabulaire plus bas.

### 2.4 SignalR
- Hub dédié au suivi en temps réel (`WorkflowHub`)
- Diffusion des événements : étape démarrée, terminée, erreur, workflow terminé
- Groupes par `CorrelationId` ou par utilisateur/rôle ou autres selon les besoins

---

## 3. Configuration YAML

### 3.1 Fichiers de définition
| Fichier | Rôle |
|---|---|
| `workflow.yml` | Définition du workflow : étapes, transitions, conditions |
| `extensions.yml` | Les actions métiers permettant de définir des tâches personnalisées |
| `tests.[NOM].md` | Contient tous les cas de tests, avec le détail de ce que le test couvre, pour chacun il y a un bloc yaml "INPUT" et "OUTPUT" |

### 3.2 Structure d'un workflow
- Chaque Tâche (ou Nœud) de la définition référence une implémentation d'exécution, qu'il s'agisse d'un connecteur (ex. : http, pub/sub), ou d'une Action Métier (tâche préconfigurée).
- Variables d'entrée/sortie enchaînées entre étapes via Handlebars.NET
  - Syntaxe : `{{ tache.nomTache.output.chemin.vers.valeur }}`
- Prise en charge des conditions, boucles et branchements
- Un système externe peut appeler un endpoint avec un guid d'instance de workflow et le nom de la tâche "hook" qu'il souhaite reprendre pour relancer un workflow en pause/attente externe.

### 3.3 Versionnage automatique des configurations de workflow

- **Checksum des fichiers de configuration** (YAML)
  - Les équipes sont autonomes pour déployer leurs workflow via un endpoint de OAM
  - Journalisation de l'empreinte + date de chargement

---

## 4. Intégrations & Extensions

### 4.1 `yamlHttpClient` (github `anisite/yamlhttpclient`)
- Simplification des appels REST vers les services externes
- Configuration déclarative dans YAML (URL, headers, body, auth)
- Chaque appel HTTP identifié par un `id` réutilisable dans les tâches préconfigurées
- Propagation automatique du `CorrelationId` dans les headers sortants

### 4.2 Mocks
- Système de mock **obligatoire** pour :
  - Les tests automatisés lancés en QA via l'interface de OAM avec résultats à l'écran
  - Les essais en environnement d'acceptation (QA) sans dépendances réelles
- Mécanisme de **résolution de mock** (`rechercherMock`) :
  - Recherche par critères dans un catalogue de réponses préconfigurées
  - Possibilité de définir des réponses conditionnelles selon les paramètres d'entrée
  - toute tâche ou extension de tâche est "mockable" dans le fichier tests.[NOM].md
  - un bouton à l'écran permet d'extraire automatiquement un fichier tests.[NOM].md avec tous les INPUT/OUTPUT de toute les étapes par défault et il est simple de désactiver le mock d'une étape de tâche ou de modifier sa valeur pour créer un autre cas de test.

### 4.3 `mdat` (github `mtessdev/mdat`) — Assertions simplifiées
- Intégration en tant que type d'étape (`type: mdat`)
- API simplifiée : définir les assertions directement dans le YAML de l'étape
- Usage principal : valider les réponses HTTP, ou n'importe quel retour, et l'état du workflow dans les specs de test

---

## 5. Tâches préconfigurées (Task Library)

Bibliothèque de tâches métier réutilisables, déclarées dans `global.yml` et référencées dans `workflow.yml` ou dans `extensions.yml`.

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
  default:
    auth: bearer
    baseUrl: "{{ config.ged.baseUrl }}"
  ```

### 5.4 Variables & Enchaînement
- Les sorties de chaque tâche sont disponibles comme variables pour les étapes suivantes
- Résolution dynamique à l'exécution (Handlebars + JSONPath)
- Support des variables globales du workflow et des variables locales d'étape

---

## 6. Vocabulaire 

Voici le tableau en format Markdown brut :


| Concept | Terme Suggéré (Français) | Description & Rôle dans l'Architecture |
| :--- | :--- | :--- |
| Gabarit du workflow | Définition de Workflow | Le plan abstrait. Il contient la structure, l'ordre des étapes et les règles de transition, mais aucune donnée d'exécution. |
| Traitement débuté | Instance de Workflow | Une exécution spécifique d'une définition. Elle possède un état (En cours, En erreur, Terminé), un identifiant unique et des données de contexte (payload). |
| Étape ou tâche | Tâche | Un bloc unitaire au sein du workflow. Un nœud peut être une action automatisée, une attente, ou une décision logique (if/else). |
| Code C# réutilisable (ex: http pur) | Connecteur | Le code bas niveau, technique et générique (ex: HttpActivity). Il ne connaît aucune logique d'affaires, il ne fait qu'exécuter un protocole. |
| Pré-config (ex: Notifier GED) | Action Métier | Une enveloppe (wrapper) qui prend une Primitive et lui injecte un contexte métier prédéfini (ex: un URL spécifique, des headers d'authentification GED). |
| Encapsulation par les clients | Extension Client | Une Action Métier créée spécifiquement par ou pour un client. Elle implémente vos interfaces standards pour être injectée dans le moteur. |
| Mock | Mock | Une implémentation alternative d'une Primitive ou d'une Action Métier utilisée pour contourner les vrais systèmes externes lors des tests d'intégration. |
| Données partagées | Contexte d'Exécution | L'objet qui voyage d'une tâche à l'autre dans l'instance, permettant à la Tâche B de lire les résultats de la Tâche A. |



## 7. Stack technique résumée

| Couche | Technologie |
|---|---|
| Backend | .NET 10 / C# |
| Frontend | Vue.js 3 |
| Base de données | SQL Server + EF Core |
| Temps réel | SignalR |
| Déploiement | Azure DevOps → IIS Windows on-prem |
| HTTP sortant | UNIQUEMENT avec `anisite/yamlhttpclient` |
| Assertions/Tests | `mtessdev/mdat` |
| Assertions/Tests/LIVE QA | `mtessdev/mdat` |


