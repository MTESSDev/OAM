// Program.cs (Agent.Server)
using Agent.Server.Features.Agents;
using Agent.Server.Features.Updates;
using Agent.Server.Hubs;
using Agent.Server.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();

builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddAuthorization();

var app = builder.Build();

Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "updates"));

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

UpdatesEndpoints.Map(app);
AgentsEndpoints.Map(app);

app.MapHub<AgentHub>("/hub/agent");
app.MapHub<UserHub>("/hub/user").RequireAuthorization();

app.Run();
