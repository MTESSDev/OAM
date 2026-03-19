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
