using FluentValidation;
using Modulith.DomainEventDispatcher.Contracts;
using Modulith.WebApi.Modules.Replication.Application;
using Modulith.WebApi.Modules.Replication.Application.CommandHandlers;
using Modulith.WebApi.Modules.Replication.Contracts;
using Modulith.WebApi.Modules.Replication.Infrastructure;

namespace Modulith.WebApi.Modules.Replication;

/// <summary>
/// Provides extension methods for registering the Replication module services.
/// </summary>
public static class ReplicationModuleExtensions
{
    /// <summary>
    /// Registers all internal services, infrastructure clients, and the entrypoint for the Replication module.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddReplicationModule(this IServiceCollection services)
    {
        // Internal Infrastructure
        services.AddScoped<INetworkReplicationClient, DummyNetworkReplicationClient>();

        // Internal Application Handlers & Validators
        services.AddScoped<IValidator<ReplicateTrackCommand>, ReplicateTrackCommandValidator>();
        services.AddScoped<ReplicateTrackCommandHandler>();

        services.AddScoped<ICommandHandler<Dummy2Command, Result>, Dummy2CommandHandler>();


        // Public Entrypoint
        services.AddScoped<IReplicationEntryPoint, ReplicationEntrypoint>();

        return services;
    }
}