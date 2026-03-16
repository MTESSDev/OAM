// Hubs/AgentHub.cs
using Agent.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Agent.Server.Hubs;

/// <summary>
/// Hub SignalR auquel chaque instance de Agent.Service se connecte.
/// Toutes les commandes serveur → agent transitent par ici.
/// </summary>
public class AgentHub : Hub
{
    private readonly IAgentRegistry _registry;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(IAgentRegistry registry, ILogger<AgentHub> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    // ── Appelé par l'agent à chaque (re)connexion ────────────────────────────

    /// <summary>
    /// Enregistre l'agent dans le registre et l'ajoute au groupe "agents".
    /// </summary>
    public async Task RegisterAgent(string machineName, string version)
    {
        _registry.Register(Context.ConnectionId, machineName, version);
        await Groups.AddToGroupAsync(Context.ConnectionId, "agents");
        _logger.LogInformation("Agent enregistré : {Machine} v{Version} ({ConnId})",
            machineName, version, Context.ConnectionId);
    }

    // ── Cycle de vie ─────────────────────────────────────────────────────────

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var info = _registry.Unregister(Context.ConnectionId);
        if (info is not null)
            _logger.LogWarning("Agent déconnecté : {Machine} ({ConnId})",
                info.MachineName, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
