# Flux réseau - Génération de correspondance OAM

## Diagramme de séquence

```mermaid
sequenceDiagram
    autonumber

    box Zone IBM Central
        actor Utilisateur
        participant IBM as Système IBM
    end

    box Zone GCO <br>(mes.reseau.intra/<br>Serveur applicatif)
        participant GCO as Serveur GCO<br/>Service Web
    end

    box Zone OAM <br>(mes.reseau.intra/<br>Serveur applicatif)
        participant OAM as Serveur OAM<br/>Agent.Server
    end

    box Zone Agents (Postes clients)
        participant Agent as Poste Agent<br/>(navigateur / client)
    end

    Note over Agent,OAM: Connexion SignalR persistante établie au démarrage

    Agent->>OAM: HTTPS :443 — WebSocket / SignalR (connexion persistante)

    Note over Utilisateur,GCO: Déclenchement de la génération de correspondance

    Utilisateur->>IBM: Déclenche la commande
    IBM->>GCO: HTTPS :443 — Appel service web<br/>(génération de correspondance)
    GCO->>OAM: HTTPS :443 — Appel API OAM<br/>(notifier l'agent cible)
    OAM-->>Agent: SignalR (push temps réel)<br/>Alerte le bon poste agent connecté
    Agent-->>Agent: Affiche la correspondance<br>produite dans une page web

```

---

## Diagramme de flux (zones et sens)

```mermaid
flowchart LR
    subgraph ZONE_IBM ["Zone IBM Central"]
        U([Utilisateur])
        IBM[Système IBM]
    end

    subgraph ZONE_GCO ["Zone GCO"]
        GCO["Serveur GCO\nService Web"]
    end

    subgraph ZONE_OAM ["Zone OAM"]
        OAM["Serveur OAM\nAgent.Server"]
    end

    subgraph ZONE_AGENTS ["Zone Agents (postes clients)"]
        A1["Poste Agent 1"]
        A2["Poste Agent 2"]
        AN["Poste Agent N"]
    end

    U -->|"déclenche"| IBM
    IBM -->|"HTTPS :443\nAppel service web"| GCO
    GCO -->|"HTTPS :443\nAppel API"| OAM
    OAM -.->|"SignalR push\n(WebSocket HTTPS :443)"| A1
    OAM -.->|"SignalR push\n(WebSocket HTTPS :443)"| A2
    OAM -.->|"SignalR push\n(WebSocket HTTPS :443)"| AN

    style ZONE_IBM fill:#dbeafe,stroke:#3b82f6
    style ZONE_GCO fill:#fef9c3,stroke:#ca8a04
    style ZONE_OAM fill:#dcfce7,stroke:#16a34a
    style ZONE_AGENTS fill:#fce7f3,stroke:#db2777
```

---

## Description des flux

| # | Source | Destination | Protocole | Port | Description |
|---|--------|-------------|-----------|------|-------------|
| 1 | Poste Agent | Serveur OAM | HTTPS / WebSocket | 443 | Connexion SignalR persistante (établie au démarrage de l'agent) |
| 2 | Système IBM | Serveur GCO | HTTPS | 443 | Déclenchement de la génération de correspondance |
| 3 | Serveur GCO | Serveur OAM | HTTPS | 443 | Appel API vers Agent.Server pour notifier l'agent cible |
| 4 | Serveur OAM | Poste Agent | SignalR (WebSocket) | 443 | Push temps réel vers le bon poste agent connecté |

> Tous les échanges en production transitent sur **HTTPS port 443**.
> Le flux SignalR (flèche pointillée) est un **push serveur vers client** sur la connexion WebSocket préalablement établie.
