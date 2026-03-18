using FluentValidation;
using Modulith.WebApi.Modules.Storage.Application;
using Modulith.WebApi.Modules.Storage.Application.CommandHandlers;
using Modulith.WebApi.Modules.Storage.Contracts;
using Modulith.WebApi.Modules.Storage.Infrastructure;

namespace Modulith.WebApi.Modules.Storage;

/// <summary>
/// Provides extension methods for registering the Storage module services.
/// </summary>
public static class StorageModuleExtensions
{
    /// <summary>
    /// Registers all internal services, handlers, and the entrypoint for the Storage module.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddStorageModule(this IServiceCollection services)
    {
        // Internal Infrastructure
        services.AddSingleton<IFriendlyTrackRepository, InMemoryTrackRepository>();

        // Internal Application Handlers & Validators
        services.AddScoped<IValidator<StoreTrackDataCommand>, StoreTrackDataCommandValidator>();
        services.AddScoped<StoreTrackDataCommandHandler>();
        services.AddScoped<GetTrackCommandHandler>();

        // Public Entrypoint
        services.AddScoped<IStorageEntryPoint, StorageEntryPoint>();

        return services;
    }
}