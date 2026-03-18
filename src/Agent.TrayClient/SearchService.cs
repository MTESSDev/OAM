// SearchService.cs
// Modèles, interface source, service de recherche et view-model WPF.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Agent.TrayClient;

// ── Types de codes reconnus ───────────────────────────────────────────────────

public enum CodeType { Unknown, CP12, Dossier, Requete }

// ── Modèles de données ────────────────────────────────────────────────────────

public sealed record SearchResult(string Title, string Subtitle, string Url);

public sealed record SearchGroup(string SystemName, string HeaderHex, IReadOnlyList<SearchResult> Results);

// ── View-model WPF (SolidColorBrush prêt pour le binding) ────────────────────

public sealed class SearchGroupViewModel
{
    public string SystemName                   { get; init; } = "";
    public SolidColorBrush HeaderBrush         { get; init; } = Brushes.Gray;
    public IReadOnlyList<SearchResult> Results { get; init; } = [];

    public static SearchGroupViewModel From(SearchGroup g) => new()
    {
        SystemName  = g.SystemName.ToUpperInvariant(),
        HeaderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(g.HeaderHex)),
        Results     = g.Results,
    };
}

// ── Interface source (extensible) ────────────────────────────────────────────

public interface ISearchSource
{
    string Name      { get; }
    string HeaderHex { get; }   // couleur de l'en-tête de groupe, ex: "#1565C0"
    Task<IReadOnlyList<SearchResult>> SearchAsync(string code, CodeType type, CancellationToken token);
}

// ── Service de recherche ──────────────────────────────────────────────────────

public sealed partial class SearchService
{
    private readonly IReadOnlyList<ISearchSource> _sources;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cacheDuration;

    // Cache simple : code → (expiry, groupes)
    private readonly ConcurrentDictionary<string, (DateTime Expiry, SearchGroup[] Groups)> _cache = new();

    public SearchService(IReadOnlyList<ISearchSource> sources, TimeSpan timeout, TimeSpan cacheDuration)
    {
        _sources       = sources;
        _timeout       = timeout;
        _cacheDuration = cacheDuration;
    }

    // ── Détection de type ─────────────────────────────────────────────────────

    public static CodeType DetectType(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return CodeType.Unknown;
        if (RegexCP12().IsMatch(code))       return CodeType.CP12;
        if (RegexDossier().IsMatch(code))    return CodeType.Dossier;
        if (RegexRequete().IsMatch(code))    return CodeType.Requete;
        return CodeType.Unknown;
    }

    // ── Recherche parallèle avec résultats progressifs ────────────────────────

    /// <summary>
    /// Interroge toutes les sources en parallèle.
    /// Chaque groupe est yielded dès que la source répond (affichage progressif).
    /// Timeout global configurable (défaut 2 s). Cache 30 s par code.
    /// </summary>
    public async IAsyncEnumerable<SearchGroup> SearchAsync(
        string code,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        if (_sources.Count == 0) yield break;

        // Retour rapide depuis le cache
        if (_cache.TryGetValue(code, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            foreach (var g in cached.Groups) yield return g;
            yield break;
        }

        var type = DetectType(code);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(_timeout);

        // Channel non-borné : chaque source écrit son groupe dès qu'il est prêt
        var channel = Channel.CreateUnbounded<SearchGroup>();
        int pending = _sources.Count;

        foreach (var source in _sources)
        {
            var s = source;
            _ = Task.Run(async () =>
            {
                try
                {
                    var results = await s.SearchAsync(code, type, timeoutCts.Token);
                    if (results.Count > 0)
                        await channel.Writer.WriteAsync(
                            new SearchGroup(s.Name, s.HeaderHex, results), token);
                }
                catch { /* source indisponible — silencieux */ }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0)
                        channel.Writer.Complete();
                }
            }, token);
        }

        var groups = new List<SearchGroup>();
        await foreach (var group in channel.Reader.ReadAllAsync(token))
        {
            groups.Add(group);
            yield return group;
        }

        // Mise en cache des résultats
        _cache[code] = (DateTime.UtcNow.Add(_cacheDuration), groups.ToArray());
    }

    // ── Regex patterns ────────────────────────────────────────────────────────

    [GeneratedRegex(@"^[A-Z]{4}\d{8}$")]  static public partial Regex RegexCP12();
    [GeneratedRegex(@"^DOS-\d{6}$")]       static public partial Regex RegexDossier();
    [GeneratedRegex(@"^REQ\d{7}$")]        static public partial Regex RegexRequete();
}
