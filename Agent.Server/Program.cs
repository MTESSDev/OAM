// Program.cs (Agent.Server)
using Agent.Server.Hubs;
using Agent.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// SignalR — hub auquel les agents se connectent
builder.Services.AddSignalR();

// Registre thread-safe des agents connectés (singleton)
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Point d'entrée SignalR — doit correspondre à l'URL dans Agent.Service/appsettings.json
app.MapHub<AgentHub>("/hub");

app.Run();
