using FluentValidation;
using Modulith.DomainEventDispatcher;
using Modulith.DomainEventDispatcher.Contracts;
using Modulith.WebApi.Modules.Replication;
using Modulith.WebApi.Modules.Storage;
using Modulith.WebApi.Modules.Storage.Contracts;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddModule();

var app = builder.Build();

app.MapModuleEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.Run();

/// <summary>
/// Handles the registration of module services, entry points, and HTTP endpoints for the application.
/// </summary>
public static class ModuleDispatcher
{
    /// <summary>
    /// Registers the module services, validators, and explicit entry points in the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddModule(this IServiceCollection services)
    {
        services.AddSingleton<IEventDispatcher, EventDispatcher>();

        var types = Assembly.GetAssembly(typeof(Program)).GetTypes().Where(t => t.IsClass && !t.IsAbstract);

        foreach (var type in types)
        {
            var interfaces = type.GetInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.IsGenericType)
                {
                    var genericTypeDef = iface.GetGenericTypeDefinition();
                    if (genericTypeDef == typeof(IEventHandler<>))
                    {
                        services.AddTransient(iface, type);
                    }
                }
            }
        }

        services.AddStorageModule();
        services.AddReplicationModule();

        return services;
    }

    /// <summary>
    /// Maps the module endpoints to the application route builder.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The updated endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Entry to the system triggers Storage, which subsequently triggers internal Replication
        endpoints.MapPost("/api/storage/tracks", async (TrackStorageDto request, IStorageEntryPoint entrypoint) =>
        {
            var result = await entrypoint.StoreTrackDataAsync(request);

            return result.IsSuccess
                ? Results.Ok(result)
                : Results.NotFound(result.Error);
        })
        .WithName("StoreTrackData");


        endpoints.MapGet("/api/system/status", async () =>
        {
            await Task.Delay(10);
            var statusMessage = "All systems operational.";

            return statusMessage is not null
                ? Results.Ok(statusMessage)
                : Results.NoContent();
        })
        .WithName("GetSystemStatus");


        return endpoints;
    }
}