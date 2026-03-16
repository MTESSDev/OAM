// Program.cs (Agent.Service)
using Agent.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

// Utilisation du builder moderne (.NET 8+)
var builder = Host.CreateApplicationBuilder(args);

// 1. CRITIQUE : Configurer l'application pour tourner comme un Service Windows
// (Nécessite le package NuGet Microsoft.Extensions.Hosting.WindowsServices)
builder.Services.AddWindowsService(options =>
{
    // C'est le nom officiel sous lequel le service sera enregistré dans services.msc
    options.ServiceName = "MonServiceSecure";
});

// 2. Configuration des logs
// Comme un service tourne en "Session 0" (sans interface), les Console.WriteLine sont invisibles.
// On redirige les logs vers l'Observateur d'événements Windows (Event Viewer).
builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.AddEventLog(new EventLogSettings
    {
        SourceName = "MonServiceSecure" // Nom de la source dans l'Event Viewer
    });
});

// 3. Enregistrement de notre boucle principale (Le cerveau IPC + SignalR)
builder.Services.AddHostedService<MainWorker>();

// 4. Construction et exécution bloquante
var host = builder.Build();
host.Run();