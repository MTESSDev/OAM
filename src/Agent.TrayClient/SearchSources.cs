// SearchSources.cs
// Sources de recherche concrètes : GED et OAM.
// Chacune fait une requête HTTP GET avec authentification Windows NTLM.
// Extensible : implémenter ISearchSource pour ajouter un nouveau système.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Agent.TrayClient;

/// <summary>
/// Source GED — retourne les documents liés à un code.
/// Attend : GET {baseUrl}/search?code=XXX → GedItem[]
/// </summary>
sealed class GedSearchSource : ISearchSource, IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseDefaultCredentials = true });
    private readonly string     _baseUrl;

    public string Name      => "GED";
    public string HeaderHex => "#1565C0"; // bleu

    public GedSearchSource(string baseUrl) => _baseUrl = baseUrl;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string code, CodeType type, CancellationToken token)
    {
        return new SearchResult[] { new SearchResult("COTD15108796", "13 document(s) dont 2 récent(s)", "https://intra-ged.mes.reseau.intra/CP12/COTD15108796") };

       /* var url = $"{_baseUrl.TrimEnd('/')}/search?code={Uri.EscapeDataString(code)}";
        using var resp = await _http.GetAsync(url, token);


        resp.EnsureSuccessStatusCode();

        var items = await resp.Content.ReadFromJsonAsync<GedItem[]>(token);
        if (items is null) return [];

        var results = new SearchResult[items.Length];
        for (int i = 0; i < items.Length; i++)
            results[i] = new SearchResult(items[i].titre, items[i].date, items[i].url);
        return results;*/
    }

    public void Dispose() => _http.Dispose();

    private record GedItem(string titre, string date, string url);
}

/// <summary>
/// Source OAM — retourne les demandes liées à un code.
/// Attend : GET {baseUrl}/search?code=XXX → OamItem[]
/// </summary>
sealed class OamSearchSource : ISearchSource, IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseDefaultCredentials = true });
    private readonly string     _baseUrl;

    public string Name      => "ASF";
    public string HeaderHex => "#E65100"; // orange

    public OamSearchSource(string baseUrl) => _baseUrl = baseUrl;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string code, CodeType type, CancellationToken token)
    {
        //var url = $"{_baseUrl.TrimEnd('/')}/search?code={Uri.EscapeDataString(code)}";
        //using var resp = await _http.GetAsync(url, token);
        //resp.EnsureSuccessStatusCode();

return new SearchResult[] {new SearchResult("COTD15108796", "Présent à l'aide sociale", "https://intra-ged.mes.reseau.intra/CP12/COTD15108796")};

       /* var items = await resp.Content.ReadFromJsonAsync<OamItem[]>(token);
        if (items is null) return [];

        var results = new SearchResult[items.Length];
        for (int i = 0; i < items.Length; i++)
            results[i] = new SearchResult($"Demande {items[i].numero}", items[i].statut, items[i].url);
        return results;*/
    }

    public void Dispose() => _http.Dispose();

    private record OamItem(string numero, string statut, string url);
}
