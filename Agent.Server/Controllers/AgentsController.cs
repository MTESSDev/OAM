// Controllers/AgentsController.cs
using Agent.Server.Hubs;
using Agent.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace Agent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AgentsController : ControllerBase
{
    private readonly IHubContext<AgentHub> _hub;
    private readonly IAgentRegistry _registry;

    public AgentsController(IHubContext<AgentHub> hub, IAgentRegistry registry)
    {
        _hub = hub;
        _registry = registry;
    }

    // ── Consultation ─────────────────────────────────────────────────────────

    /// <summary>Liste tous les agents actuellement connectés.</summary>
    [HttpGet]
    public IActionResult GetAll() => Ok(_registry.GetAll());

    // ── Commandes broadcast (tous les agents) ────────────────────────────────

    /// <summary>Ouvre une URL sur tous les agents connectés.</summary>
    [HttpPost("openurl")]
    public async Task<IActionResult> OpenUrl([FromBody] OpenUrlRequest request)
    {
        await _hub.Clients.Group("agents").SendAsync("OpenUrl", request.Url);
        return Ok(new { sent = true, target = "all" });
    }

    /// <summary>Déclenche une vérification de mise à jour sur tous les agents.</summary>
    [HttpPost("checkupdate")]
    public async Task<IActionResult> CheckUpdate()
    {
        await _hub.Clients.Group("agents").SendAsync("CheckUpdate");
        return Ok(new { sent = true, target = "all" });
    }

    // ── Commandes ciblées (un seul agent par nom de machine) ─────────────────

    /// <summary>Ouvre une URL sur un agent spécifique.</summary>
    [HttpPost("{machineName}/openurl")]
    public async Task<IActionResult> OpenUrl(string machineName, [FromBody] OpenUrlRequest request)
    {
        var connId = _registry.GetConnectionId(machineName);
        if (connId is null)
            return NotFound(new { error = $"Agent '{machineName}' non connecté." });

        await _hub.Clients.Client(connId).SendAsync("OpenUrl", request.Url);
        return Ok(new { sent = true, target = machineName });
    }

    /// <summary>Déclenche une mise à jour sur un agent spécifique.</summary>
    [HttpPost("{machineName}/checkupdate")]
    public async Task<IActionResult> CheckUpdate(string machineName)
    {
        var connId = _registry.GetConnectionId(machineName);
        if (connId is null)
            return NotFound(new { error = $"Agent '{machineName}' non connecté." });

        await _hub.Clients.Client(connId).SendAsync("CheckUpdate");
        return Ok(new { sent = true, target = machineName });
    }
}

public record OpenUrlRequest(string Url);
