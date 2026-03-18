// Controllers/UpdateController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;

namespace Agent.Server.Controllers;

[ApiController]
[Route("updates")]
public class UpdateController : ControllerBase
{
    private readonly UpdateManifestConfig _manifest;

    public UpdateController(IConfiguration configuration)
    {
        _manifest = configuration.GetSection("Update").Get<UpdateManifestConfig>()
            ?? throw new InvalidOperationException("Section 'Update' manquante dans appsettings.json.");
    }

    /// <summary>
    /// Retourne le hash SHA-256 du package courant et son URL de téléchargement.
    /// Le client compare ce hash avec son dernier hash appliqué — si différent, il met à jour.
    /// GET /updates/check
    /// </summary>
    [HttpGet("check")]
    public IActionResult Check()
    {
        return Ok(new
        {
            hash        = _manifest.Hash,
            downloadUrl = _manifest.DownloadUrl
        });
    }

    /// <summary>
    /// Sert le fichier ZIP de mise à jour depuis le dossier local "updates/".
    /// GET /updates/download/agent.zip
    /// </summary>
    [HttpGet("download/{filename}")]
    public IActionResult Download(string filename)
    {
        if (filename.Contains('/') || filename.Contains('\\') || filename.Contains(".."))
            return BadRequest(new { error = "Nom de fichier invalide." });

        string updatesDir = Path.Combine(AppContext.BaseDirectory, "updates");
        string filePath   = Path.Combine(updatesDir, filename);

        if (!System.IO.File.Exists(filePath))
            return NotFound(new { error = $"Fichier '{filename}' introuvable." });

        return PhysicalFile(filePath, "application/zip", filename);
    }
}

public sealed class UpdateManifestConfig
{
    public string Hash        { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
}
