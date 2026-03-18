// Controllers/AgentsController.cs
using Agent.Server.Hubs;
using Agent.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Linq;
using System.Threading.Tasks;

namespace Agent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AgentsController : ControllerBase
{
    private readonly IHubContext<AgentHub> _agentHub;
    private readonly IHubContext<UserHub>  _userHub;
    private readonly IAgentRegistry _registry;

    public AgentsController(
        IHubContext<AgentHub> agentHub,
        IHubContext<UserHub>  userHub,
        IAgentRegistry registry)
    {
        _agentHub = agentHub;
        _userHub  = userHub;
        _registry = registry;
    }

    // ── Consultation ─────────────────────────────────────────────────────────

    /// <summary>Liste toutes les machines (Agent.Service) connectées.</summary>
    [HttpGet]
    public IActionResult GetAll() => Ok(_registry.GetAll());

    /// <summary>Liste tous les utilisateurs (Agent.TrayClient) connectés.</summary>
    [HttpGet("users")]
    public IActionResult GetAllUsers() => Ok(_registry.GetAllUsers());

    // ── Commandes broadcast (tous les utilisateurs) ──────────────────────────

    /// <summary>Ouvre une URL sur tous les TrayClients connectés.</summary>
    [HttpPost("openurl")]
    public async Task<IActionResult> OpenUrl([FromBody] OpenUrlRequest request)
    {
        await _userHub.Clients.Group("users").SendAsync("OpenUrl", request.Url);
        return Ok(new { sent = true, target = "all-users" });
    }

    /// <summary>Déclenche une vérification de mise à jour sur toutes les machines.</summary>
    [HttpPost("checkupdate")]
    public async Task<IActionResult> CheckUpdate()
    {
        await _agentHub.Clients.Group("agents").SendAsync("CheckUpdate");
        return Ok(new { sent = true, target = "all-agents" });
    }

    // ── Commandes ciblées par machine ────────────────────────────────────────

    /// <summary>
    /// Ouvre une URL sur tous les TrayClients connectés depuis une machine spécifique.
    /// </summary>
    [HttpPost("{machineName}/openurl")]
    public async Task<IActionResult> OpenUrl(string machineName, [FromBody] OpenUrlRequest request)
    {
        var connectionIds = _registry.GetUserConnectionIdsByMachine(machineName);
        if (!connectionIds.Any())
            return NotFound(new { error = $"Aucun utilisateur connecté sur '{machineName}'." });

        await _userHub.Clients.Clients(connectionIds).SendAsync("OpenUrl", request.Url);
        return Ok(new { sent = true, target = machineName, sessions = connectionIds.Count });
    }

    /// <summary>Déclenche une mise à jour sur une machine spécifique.</summary>
    [HttpPost("{machineName}/checkupdate")]
    public async Task<IActionResult> CheckUpdate(string machineName)
    {
        var connId = _registry.GetConnectionId(machineName);
        if (connId is null)
            return NotFound(new { error = $"Agent '{machineName}' non connecté." });

        await _agentHub.Clients.Client(connId).SendAsync("CheckUpdate");
        return Ok(new { sent = true, target = machineName });
    }
}

public record OpenUrlRequest(string Url);
