// Hubs/AgentHub.cs
using Agent.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Agent.Server.Hubs;

/// <summary>
/// Hub SignalR unique pour les deux types de connexions :
///   - Agent.Service  (LocalSystem, anonyme)    → RegisterAgent
///   - Agent.TrayClient (Windows Auth)           → RegisterUser  [Authorize]
/// </summary>
public class AgentHub : Hub
{
    private readonly IAgentRegistry _registry;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(IAgentRegistry registry, ILogger<AgentHub> logger)
    {
        _registry = registry;
        _logger   = logger;
    }

    // ── Connexion machine (Agent.Service / LocalSystem) ──────────────────────

    /// <summary>
    /// Enregistre la machine dans le registre et l'ajoute au groupe "agents".
    /// Appelé par Agent.Service à chaque (re)connexion.
    /// </summary>
    public async Task RegisterAgent(string machineName, string version)
    {
        _registry.Register(Context.ConnectionId, machineName, version);
        await Groups.AddToGroupAsync(Context.ConnectionId, "agents");
        _logger.LogInformation("Agent enregistré : {Machine} v{Version} ({ConnId})",
            machineName, version, Context.ConnectionId);
    }

    // ── Connexion utilisateur (Agent.TrayClient / Windows Auth) ─────────────

    /// <summary>
    /// Enregistre l'utilisateur dans le registre et l'ajoute au groupe "users".
    /// Appelé par Agent.TrayClient. Nécessite une authentification Windows valide.
    /// L'identité (Context.User.Identity.Name) est prouvée par Negotiate — ne pas utiliser le paramètre.
    /// </summary>
    [Authorize]
    public async Task RegisterUser(string machineName)
    {
        string windowsUserName = Context.User!.Identity!.Name!;

        _registry.RegisterUser(Context.ConnectionId, machineName, windowsUserName);
        await Groups.AddToGroupAsync(Context.ConnectionId, "users");
        _logger.LogInformation("Utilisateur enregistré : {User} @ {Machine} ({ConnId})",
            windowsUserName, machineName, Context.ConnectionId);
    }

    // ── Cycle de vie ─────────────────────────────────────────────────────────

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // Tente la désinscription depuis le registre machine
        var agent = _registry.Unregister(Context.ConnectionId);
        if (agent is not null)
        {
            _logger.LogWarning("Agent (machine) déconnecté : {Machine} ({ConnId})",
                agent.MachineName, Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }

        // Tente la désinscription depuis le registre utilisateur
        var user = _registry.UnregisterUser(Context.ConnectionId);
        if (user is not null)
        {
            _logger.LogWarning("Utilisateur déconnecté : {User} @ {Machine} ({ConnId})",
                user.WindowsUserName, user.MachineName, Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
