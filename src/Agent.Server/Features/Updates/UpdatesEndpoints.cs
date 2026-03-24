using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Security.Cryptography;

namespace Agent.Server.Features.Updates;

public static class UpdatesEndpoints
{
    private const string ZipFileName = "agent.zip";

    public static void Map(WebApplication app)
    {
        app.MapGet("/updates/check",              Check);
        app.MapGet("/updates/download/{filename}", Download);
    }

    private static IResult Check(HttpContext ctx)
    {
        string filePath = Path.Combine(AppContext.BaseDirectory, "updates", ZipFileName);

        if (!File.Exists(filePath))
            return Results.NotFound(new { error = "Aucune mise a jour disponible." });

        using var stream = File.OpenRead(filePath);
        string hash        = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        string downloadUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/updates/download/{ZipFileName}";

        return Results.Ok(new { hash, downloadUrl });
    }

    private static IResult Download(string filename)
    {
        if (filename.Contains('/') || filename.Contains('\\') || filename.Contains(".."))
            return Results.BadRequest(new { error = "Nom de fichier invalide." });

        string filePath = Path.Combine(AppContext.BaseDirectory, "updates", filename);

        if (!File.Exists(filePath))
            return Results.NotFound(new { error = $"Fichier '{filename}' introuvable." });

        return Results.File(filePath, "application/zip", filename);
    }
}
