using Modulith.DomainEventDispatcher.Contracts;

namespace Modulith.WebApi.Modules.Replication.Contracts;

/// <summary>
/// Defines the explicit entry point for the Replication module.
/// </summary>
public interface IReplicationEntryPoint
{
    /// <summary>
    /// Triggers the replication of a specific track to a target node synchronously.
    /// </summary>
    /// <param name="request">The replication request payload.</param>
    /// <returns>A result indicating the success or failure of the replication process.</returns>
    Task<Result<TrackReplicationResponse>> TriggerReplicationAsync(ReplicateTrackDto request);
}
