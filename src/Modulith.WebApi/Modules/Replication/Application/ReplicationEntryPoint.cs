using Modulith.Result;
using Modulith.WebApi.Modules.Replication.Application.CommandHandlers;
using Modulith.WebApi.Modules.Replication.Contracts;

namespace Modulith.WebApi.Modules.Replication.Application;

/// <summary>
/// The concrete implementation of the Replication module entry point, acting as a facade to internal handlers.
/// </summary>
public class ReplicationEntrypoint : IReplicationEntryPoint
{
    private readonly ILogger<ReplicationEntrypoint> _logger;
    private readonly ReplicateTrackCommandHandler _commandHandler;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicationEntrypoint"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="commandHandler">The internal command handler managing the replication logic.</param>
    public ReplicationEntrypoint(ILogger<ReplicationEntrypoint> logger, ReplicateTrackCommandHandler commandHandler)
    {
        _logger = logger;
        _commandHandler = commandHandler;   
    }

    /// <summary>
    /// Triggers the replication of a specific track to a target node synchronously by delegating to the internal handler.
    /// </summary>
    /// <param name="request">The replication request payload.</param>
    /// <returns>A result indicating the success or failure of the replication process.</returns>
    public async Task<Result<TrackReplicationResponse>> TriggerReplicationAsync(ReplicateTrackDto request)
    {
        _logger.LogInformation("[{Timestamp}] ReplicationEntrypoint delegating request to ReplicateTrackCommandHandler for TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), request.TrackId);
               
        var command = new ReplicateTrackCommand(request);
        return await _commandHandler.HandleAsync(command);
    }
}