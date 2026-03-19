# Flux réseau — Agent.Service

## Diagramme de séquence (flux principal)

```mermaid
sequenceDiagram
    actor TC as Transaction Centrale<br/>(Ordinateur central)
    participant GCO as Serveur GCO<br/>(Gestion des correspondances)
    participant SRV as Agent.Server<br/>(ASP.NET Core)<br/>:5000 HTTP / :5001 HTTPS
    participant SVC as Agent.Service<br/>(Windows Service SYSTEM)<br/>Poste de travail
    participant TRAY as Agent.TrayClient<br/>(WinForms / Barre système)<br/>Session utilisateur

    Note over TC,TRAY: ── Flux principal : ouverture d'URL sur un poste ──

    TC->>GCO: Demande métier<br/>(protocole interne GCO)
    GCO->>SRV: POST /api/agents/{machineName}/openurl<br/>{ "url": "https://..." }<br/>HTTPS (port 5001)
    SRV-->>GCO: 200 OK { sent: true, target: machineName }

    Note over SRV,SVC: Connexion persistante SignalR (WebSocket)<br/>maintenue par Agent.Service

    SRV--)SVC: SendAsync("OpenUrl", url)<br/>SignalR push sur connexion WebSocket existante

    Note over SVC,TRAY: IPC local (Named Pipe — même machine)

    SVC->>TRAY: CommandOpenUrl + url<br/>Named Pipe (\\.\pipe\AgentPipe)<br/>ACL : BUILTIN\Users

    TRAY->>TRAY: Process.Start(url)<br/>Ouvre le navigateur par défaut
```

---

## Diagramme d'architecture réseau

```mermaid
flowchart TD
    subgraph RESEAU_CENTRAL["Zone réseau centrale"]
        TC["Transaction Centrale\n(ordinateur central)"]
        GCO["Serveur GCO\n(Gestion des correspondances)"]
    end

    subgraph RESEAU_SERVEUR["Zone DMZ / Serveur applicatif"]
        SRV["Agent.Server\nASP.NET Core 10\nREST API  ->  /api/agents\nSignalR Hub  ->  /hub\nHTTP :5000 / HTTPS :5001"]
    end

    subgraph POSTE["Poste de travail (Session Windows)"]
        subgraph SESSION0["Session 0 — SYSTEM"]
            SVC["Agent.Service\nWindows Service\n(BackgroundService)"]
        end
        subgraph SESSION_USER["Session utilisateur active"]
            TRAY["Agent.TrayClient\nWinForms — Barre système"]
            BROWSER["Navigateur par défaut"]
        end
    end

    TC -->|"Protocole métier GCO"| GCO
    GCO -->|"REST HTTPS\nPOST /api/agents/{machine}/openurl\nPort 5001"| SRV

    SVC -->|"WebSocket TLS\nSignalR — /hub\nPort 5001\n(connexion sortante persistante)"| SRV
    SRV -.->|"SignalR Push\nSendAsync('OpenUrl')"| SVC

    SVC -->|"Named Pipe local\n\\\\.\\pipe\\AgentPipe\nACL: BUILTIN\\Users"| TRAY
    SVC -->|"CreateProcessAsUser\nWTSQueryUserToken\n(lancement dans session user)"| TRAY
    TRAY -->|"Process.Start\n(ShellExecute)"| BROWSER

    style RESEAU_CENTRAL fill:#dbeafe,stroke:#2563eb
    style RESEAU_SERVEUR fill:#dcfce7,stroke:#16a34a
    style POSTE fill:#fef9c3,stroke:#ca8a04
    style SESSION0 fill:#fee2e2,stroke:#dc2626
    style SESSION_USER fill:#f3e8ff,stroke:#9333ea
```

---

## Points d'attention sécurité

| # | Élément | Observation | Risque |
|---|---------|-------------|--------|
| 1 | **REST API sans authentification** | `AgentsController` n'a aucun `[Authorize]` | Tout appelant réseau peut déclencher un `OpenUrl` sur n'importe quel poste |
| 2 | **HTTP exposé** | Port `5000` non chiffré déclaré dans `appsettings.json` | Interception possible si pas de firewall |
| 3 | **URL non validée** | Le champ `url` reçu en JSON est transmis directement à `Process.Start(ShellExecute)` | SSRF / exécution de protocoles arbitraires (ex. `file://`, `ms-excel://`) |
| 4 | **Named Pipe ACL** | Accessible à `BUILTIN\Users` (tout utilisateur local) | Un autre processus utilisateur peut injecter des commandes dans le pipe |
| 5 | **AllowedHosts: `*`** | Pas de restriction d'hôte sur le serveur | Facilite les attaques de type host-header injection |
