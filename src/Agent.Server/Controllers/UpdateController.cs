// Controllers/UpdateController.cs
using Agent.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;

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
    /// L'agent appelle cet endpoint en passant sa version courante.
    /// Retourne hasUpdate=false si déjà à jour, ou les infos du package sinon.
    /// GET /updates/check?version=1.0.0
    /// </summary>
    [HttpGet("check")]
    public IActionResult Check([FromQuery] string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "Paramètre 'version' requis." });

        if (version == _manifest.LatestVersion)
            return Ok(new { hasUpdate = false });

        return Ok(new
        {
            hasUpdate = true,
            update = new UpdateInfo(_manifest.LatestVersion, _manifest.DownloadUrl, _manifest.Hash)
        });
    }
}

/// <summary>Modèle de configuration lu depuis appsettings.json → "Update".</summary>
public sealed class UpdateManifestConfig
{
    public string LatestVersion { get; init; } = "1.0.0";
    public string DownloadUrl { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
}
