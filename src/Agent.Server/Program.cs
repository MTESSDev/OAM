// Program.cs (Agent.Server)
using Agent.Server.Hubs;
using Agent.Server.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// SignalR — hub auquel les agents (Service) et les utilisateurs (TrayClient) se connectent
builder.Services.AddSignalR();

// Registre thread-safe des agents et utilisateurs connectés (singleton)
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();

// Authentification Windows (Negotiate = Kerberos ou NTLM selon l'environnement)
// Utilisée par les connexions TrayClient — les connexions Service (LocalSystem) restent anonymes
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Point d'entrée SignalR — doit correspondre à l'URL dans appsettings.json des clients
app.MapHub<AgentHub>("/hub");

app.Run();
