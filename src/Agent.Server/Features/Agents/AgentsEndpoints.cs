using Agent.Server.Hubs;
using Agent.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using System.Linq;
using System.Threading.Tasks;

namespace Agent.Server.Features.Agents;

public static class AgentsEndpoints
{
    public static void Map(WebApplication app)
    {
        // ── Consultation ─────────────────────────────────────────────────────
        app.MapGet("/api/agents",       GetAll);
        app.MapGet("/api/agents/users", GetAllUsers);

        // ── Broadcast ────────────────────────────────────────────────────────
        app.MapPost("/api/agents/openurl",     OpenUrlAll);
        app.MapPost("/api/agents/checkupdate", CheckUpdateAll);

        // ── Ciblé par machine ────────────────────────────────────────────────
        app.MapPost("/api/agents/{machineName}/openurl",     OpenUrlMachine);
        app.MapPost("/api/agents/{machineName}/checkupdate", CheckUpdateMachine);
    }

    private static IResult GetAll(IAgentRegistry registry)
        => Results.Ok(registry.GetAll());

    private static IResult GetAllUsers(IAgentRegistry registry)
        => Results.Ok(registry.GetAllUsers());

    private static async Task<IResult> OpenUrlAll(
        OpenUrlRequest request,
        IHubContext<UserHub> userHub)
    {
        await userHub.Clients.Group("users").SendAsync("OpenUrl", request.Url);
        return Results.Ok(new { sent = true, target = "all-users" });
    }

    private static async Task<IResult> CheckUpdateAll(IHubContext<AgentHub> agentHub)
    {
        await agentHub.Clients.Group("agents").SendAsync("CheckUpdate");
        return Results.Ok(new { sent = true, target = "all-agents" });
    }

    private static async Task<IResult> OpenUrlMachine(
        string machineName,
        OpenUrlRequest request,
        IAgentRegistry registry,
        IHubContext<UserHub> userHub)
    {
        var connectionIds = registry.GetUserConnectionIdsByMachine(machineName);
        if (!connectionIds.Any())
            return Results.NotFound(new { error = $"Aucun utilisateur connecté sur '{machineName}'." });

        await userHub.Clients.Clients(connectionIds).SendAsync("OpenUrl", request.Url);
        return Results.Ok(new { sent = true, target = machineName, sessions = connectionIds.Count });
    }

    private static async Task<IResult> CheckUpdateMachine(
        string machineName,
        IAgentRegistry registry,
        IHubContext<AgentHub> agentHub)
    {
        var connId = registry.GetConnectionId(machineName);
        if (connId is null)
            return Results.NotFound(new { error = $"Agent '{machineName}' non connecté." });

        await agentHub.Clients.Client(connId).SendAsync("CheckUpdate");
        return Results.Ok(new { sent = true, target = machineName });
    }
}

public record OpenUrlRequest(string Url);
