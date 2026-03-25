// Hubs/AgentHub.cs
using Agent.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Agent.Server.Hubs;

/// <summary>
/// Hub dédié aux connexions Agent.Service (LocalSystem, anonyme).
/// Gère l'enregistrement machine et les commandes de mise à jour.
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

    public async Task RegisterAgent(string machineName, string version)
    {
        _registry.Register(Context.ConnectionId, machineName, version);
        await Groups.AddToGroupAsync(Context.ConnectionId, "agents");
        _logger.LogInformation("Agent enregistré : {Machine} v{Version} ({ConnId})",
            machineName, version, Context.ConnectionId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var agent = _registry.Unregister(Context.ConnectionId);
        if (agent is not null)
            _logger.LogWarning("Agent déconnecté : {Machine} ({ConnId})",
                agent.MachineName, Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}
