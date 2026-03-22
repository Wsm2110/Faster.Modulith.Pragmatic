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

AddModule(builder.Services);
AddValidatorsFromAssembly(builder.Services, typeof(Program).Assembly);

var app = builder.Build();

MapModuleEndpoints(app);

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


IServiceCollection AddModule(IServiceCollection services)
{
    services.AddSingleton<IEventDispatcher, EventDispatcher>();

    var types = Assembly.GetAssembly(typeof(Program))!
        .GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract);

    foreach (var type in types)
    {
        var interfaces = type.GetInterfaces();
        foreach (var iface in interfaces)
        {
            if (iface.IsGenericType)
            {
                var genericTypeDef = iface.GetGenericTypeDefinition();

                // Register Event Handlers
                if (genericTypeDef == typeof(IEventHandler<>))
                {
                    services.AddTransient(iface, type);
                }
                // Register Command Handlers
                else if (genericTypeDef == typeof(ICommandHandler<,>))
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
IEndpointRouteBuilder MapModuleEndpoints(IEndpointRouteBuilder endpoints)
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

/// <summary>
/// Scans the specified assembly and registers all non-abstract classes implementing IValidator{T} as Transient services.
/// </summary>
/// <param name="services">The service collection.</param>
/// <param name="assembly">The assembly to scan.</param>
/// <returns>The updated service collection.</returns>
IServiceCollection AddValidatorsFromAssembly(IServiceCollection services, Assembly assembly)
{
    if (assembly == null) throw new ArgumentNullException(nameof(assembly));

    var validatorOpenGenericType = typeof(IValidator<>);

    var types = assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract);

    foreach (var type in types)
    {
        var interfaces = type.GetInterfaces();
        foreach (var iface in interfaces)
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == validatorOpenGenericType)
            {
                services.AddTransient(iface, type);
            }
        }
    }

    return services;
}