using System;
using System.ClientModel;

using EverybodyIsJohn;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using OpenAI;
using OpenAI.Chat;

using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpContextAccessor();

builder.Services
    .AddOrleans(static siloBuilder =>
    {
        siloBuilder.UseLocalhostClustering();
        siloBuilder.AddMemoryGrainStorageAsDefault();
    });

builder.Services
    .AddOptions<ChatClientOptions>()
    .Configure<IConfiguration>(static (settings, configuration) =>
        configuration.GetSection("ChatClientOptions").Bind(settings))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddSingleton<ObsessionWeightProvider>()
    .AddSingleton<ChatClient>(static provider =>
    {
        var options = provider.GetRequiredService<IOptions<ChatClientOptions>>().Value;
        return new ChatClient(
            options.Model,
            credential: new ApiKeyCredential(options.ApiKey),
            options: new OpenAIClientOptions()
            {
                Endpoint = options.Endpoint,
            });
    });

var app = builder.Build();

app.Use(static async (context, next) =>
{
    if (!context.Request.Cookies.TryGetValue("john", out var john))
    {
        context.Response.Cookies.Append("john", Guid.NewGuid().ToString());
    }

    await next(context);
});

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();