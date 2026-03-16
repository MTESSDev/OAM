// Services/AgentRegistry.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Agent.Server.Services;

public record AgentInfo(
    string ConnectionId,
    string MachineName,
    string Version,
    DateTimeOffset ConnectedAt);

public interface IAgentRegistry
{
    void Register(string connectionId, string machineName, string version);
    AgentInfo? Unregister(string connectionId);
    IReadOnlyList<AgentInfo> GetAll();
    string? GetConnectionId(string machineName);
}

/// <summary>
/// Registre thread-safe (ConcurrentDictionary) des agents SignalR connectés.
/// Clé principale : ConnectionId (unique par connexion SignalR).
/// Index secondaire : MachineName → ConnectionId pour le ciblage par machine.
/// </summary>
public sealed class AgentRegistry : IAgentRegistry
{
    // connectionId → AgentInfo
    private readonly ConcurrentDictionary<string, AgentInfo> _byConnection = new();
    // machineName (lower) → connectionId  (dernier connexion gagne en cas de doublon)
    private readonly ConcurrentDictionary<string, string> _byMachine = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string connectionId, string machineName, string version)
    {
        var info = new AgentInfo(connectionId, machineName, version, DateTimeOffset.UtcNow);
        _byConnection[connectionId] = info;
        _byMachine[machineName] = connectionId;
    }

    public AgentInfo? Unregister(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var info))
            return null;

        // Supprimer l'index machine seulement si il pointe encore vers cette connexion
        if (_byMachine.TryGetValue(info.MachineName, out var storedConnId)
            && storedConnId == connectionId)
        {
            _byMachine.TryRemove(info.MachineName, out _);
        }

        return info;
    }

    public IReadOnlyList<AgentInfo> GetAll() =>
        _byConnection.Values.OrderBy(a => a.MachineName).ToList();

    public string? GetConnectionId(string machineName) =>
        _byMachine.TryGetValue(machineName, out var id) ? id : null;
}
