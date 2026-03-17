namespace Agent.Shared;

public static class AppConstants
{
    public const string PipeName = "MonProjetSecurePipe";
    public const string ServiceName = "MonServiceSecure";
    // Pour l'IPC
    public const byte CommandOpenUrl = 0x01;
    public const byte CommandStatusUpdate = 0x02;
}

public record UpdateInfo(string Version, string Url, string Hash);