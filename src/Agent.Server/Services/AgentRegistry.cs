// Services/AgentRegistry.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Agent.Server.Services;

// ── Connexion machine (Agent.Service / LocalSystem) ──────────────────────────
public record AgentInfo(
    string ConnectionId,
    string MachineName,
    string Version,
    DateTimeOffset ConnectedAt);

// ── Connexion utilisateur (Agent.TrayClient / Windows Auth) ──────────────────
public record UserInfo(
    string ConnectionId,
    string MachineName,
    string WindowsUserName,   // Prouvé par Negotiate (Kerberos/NTLM) — ne pas faire confiance au paramètre client
    DateTimeOffset ConnectedAt);

public interface IAgentRegistry
{
    // Agents (machines)
    void Register(string connectionId, string machineName, string version);
    AgentInfo? Unregister(string connectionId);
    IReadOnlyList<AgentInfo> GetAll();
    string? GetConnectionId(string machineName);

    // Utilisateurs (TrayClients authentifiés)
    void RegisterUser(string connectionId, string machineName, string windowsUserName);
    UserInfo? UnregisterUser(string connectionId);
    IReadOnlyList<UserInfo> GetAllUsers();
    IReadOnlyList<string> GetUserConnectionIdsByMachine(string machineName);
}

/// <summary>
/// Registre thread-safe des connexions SignalR.
/// - AgentInfo  : connexion depuis Agent.Service (LocalSystem, anonyme)
/// - UserInfo   : connexion depuis Agent.TrayClient (authentifiée Windows)
/// </summary>
public sealed class AgentRegistry : IAgentRegistry
{
    // ── Agents (machines) ────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, AgentInfo> _agentsByConnection = new();
    private readonly ConcurrentDictionary<string, string>    _agentsByMachine    = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string connectionId, string machineName, string version)
    {
        var info = new AgentInfo(connectionId, machineName, version, DateTimeOffset.UtcNow);
        _agentsByConnection[connectionId] = info;
        _agentsByMachine[machineName]     = connectionId;
    }

    public AgentInfo? Unregister(string connectionId)
    {
        if (!_agentsByConnection.TryRemove(connectionId, out var info))
            return null;

        if (_agentsByMachine.TryGetValue(info.MachineName, out var stored) && stored == connectionId)
            _agentsByMachine.TryRemove(info.MachineName, out _);

        return info;
    }

    public IReadOnlyList<AgentInfo> GetAll() =>
        _agentsByConnection.Values.OrderBy(a => a.MachineName).ToList();

    public string? GetConnectionId(string machineName) =>
        _agentsByMachine.TryGetValue(machineName, out var id) ? id : null;

    // ── Utilisateurs (TrayClients) ───────────────────────────────────────────

    // connectionId → UserInfo
    private readonly ConcurrentDictionary<string, UserInfo> _usersByConnection = new();
    // machineName (lower) → liste de connectionIds (plusieurs sessions possible par machine)
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _usersByMachine =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterUser(string connectionId, string machineName, string windowsUserName)
    {
        var info = new UserInfo(connectionId, machineName, windowsUserName, DateTimeOffset.UtcNow);
        _usersByConnection[connectionId] = info;
        _usersByMachine.GetOrAdd(machineName, _ => new ConcurrentBag<string>()).Add(connectionId);
    }

    public UserInfo? UnregisterUser(string connectionId)
    {
        if (!_usersByConnection.TryRemove(connectionId, out var info))
            return null;

        // ConcurrentBag ne supporte pas la suppression ciblée — on recrée sans le connectionId retiré
        if (_usersByMachine.TryGetValue(info.MachineName, out var bag))
        {
            var updated = new ConcurrentBag<string>(bag.Where(id => id != connectionId));
            _usersByMachine[info.MachineName] = updated;
        }

        return info;
    }

    public IReadOnlyList<UserInfo> GetAllUsers() =>
        _usersByConnection.Values.OrderBy(u => u.MachineName).ThenBy(u => u.WindowsUserName).ToList();

    public IReadOnlyList<string> GetUserConnectionIdsByMachine(string machineName) =>
        _usersByMachine.TryGetValue(machineName, out var bag)
            ? bag.Where(id => _usersByConnection.ContainsKey(id)).ToList()
            : [];
}
