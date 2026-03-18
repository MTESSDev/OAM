// Hubs/UserHub.cs
using Agent.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Agent.Server.Hubs;

/// <summary>
/// Hub dédié aux connexions Agent.TrayClient.
/// L'attribut [Authorize] sur la classe force le challenge Negotiate (Kerberos/NTLM)
/// dès la phase de connexion WebSocket — avant que le hub soit accessible.
/// </summary>
[Authorize]
public class UserHub : Hub
{
    private readonly IAgentRegistry _registry;
    private readonly ILogger<UserHub> _logger;

    public UserHub(IAgentRegistry registry, ILogger<UserHub> logger)
    {
        _registry = registry;
        _logger   = logger;
    }

    /// <summary>
    /// Enregistre l'utilisateur Windows authentifié.
    /// Context.User.Identity.Name est garanti non-null grâce à [Authorize] sur la classe.
    /// </summary>
    public async Task RegisterUser(string machineName)
    {
        string windowsUserName = Context.User!.Identity!.Name!;

        _registry.RegisterUser(Context.ConnectionId, machineName, windowsUserName);
        await Groups.AddToGroupAsync(Context.ConnectionId, "users");
        _logger.LogInformation("Utilisateur enregistré : {User} @ {Machine} ({ConnId})",
            windowsUserName, machineName, Context.ConnectionId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var user = _registry.UnregisterUser(Context.ConnectionId);
        if (user is not null)
            _logger.LogWarning("Utilisateur déconnecté : {User} @ {Machine} ({ConnId})",
                user.WindowsUserName, user.MachineName, Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}
