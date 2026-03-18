using FluentValidation;
using Modulith.Result;
using Modulith.WebApi.Modules.Replication.Contracts;

namespace Modulith.WebApi.Modules.Replication.Application.CommandHandlers;

/// <summary>
/// Represents the internal command to replicate a track.
/// </summary>
public record ReplicateTrackCommand(ReplicateTrackDto TrackData);

/// <summary>
/// Validator for the <see cref="ReplicateTrackCommand"/>.
/// </summary>
public class ReplicateTrackCommandValidator : AbstractValidator<ReplicateTrackCommand>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicateTrackCommandValidator"/> class.
    /// </summary>
    public ReplicateTrackCommandValidator()
    {
        RuleFor(x => x.TrackData).NotNull();
        RuleFor(x => x.TrackData.TrackId).NotEmpty();
    }
}

/// <summary>
/// Handler for orchestrating the network replication of track data.
/// </summary>
public class ReplicateTrackCommandHandler
{
    private readonly ILogger<ReplicateTrackCommandHandler> _logger;
    private readonly IValidator<ReplicateTrackCommand> _validator;
    private readonly INetworkReplicationClient _replicationClient;
     
    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicateTrackCommandHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="validator">The fluent validator instance.</param>
    /// <param name="replicationClient">The infrastructure client for network transmission.</param>
    public ReplicateTrackCommandHandler(
        ILogger<ReplicateTrackCommandHandler> logger,
        IValidator<ReplicateTrackCommand> validator,
        INetworkReplicationClient replicationClient)
    { 
        _logger = logger;
        _validator = validator;
        _replicationClient = replicationClient;
    }

    /// <summary>
    /// Handles the command to replicate a track across the cluster.
    /// </summary>
    /// <param name="command">The command containing the track data.</param>
    /// <returns>A result containing the replication status.</returns>
    public async Task<Result<TrackReplicationResponse>> HandleAsync(ReplicateTrackCommand command)
    {
        var validationResult = await _validator.ValidateAsync(command);
        if (!validationResult.IsValid)
        {
            _logger.LogWarning("[{Timestamp}] Replication command validation failed for TrackId: {TrackId}. Errors: {Errors}", DateTime.UtcNow.ToString("O"), command.TrackData?.TrackId, validationResult.ToString());
            return Result<TrackReplicationResponse>.Validation(validationResult.ToString());
        }

        var trackData = command.TrackData!;
        _logger.LogInformation("[{Timestamp}] Initiating cluster replication orchestrator for TrackId: {TrackId}, Callsign: {Callsign}", DateTime.UtcNow.ToString("O"), trackData.TrackId, trackData.Callsign);

        // Utilize the infrastructure layer to handle the actual network call
        var transmissionSuccess = await _replicationClient.TransmitTrackAsync(trackData);

        if (!transmissionSuccess)
        {
            _logger.LogError("[{Timestamp}] Failed to replicate TrackId: {TrackId} to cluster via infrastructure client.", DateTime.UtcNow.ToString("O"), trackData.TrackId);
            return Result<TrackReplicationResponse>.Failure("Failed to transmit track data to the external node." );
        }

        _logger.LogInformation("[{Timestamp}] Replication orchestrator successfully verified transmission for TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), trackData.TrackId);

        var response = new TrackReplicationResponse(trackData.TrackId, "DummyNode");
        return Result<TrackReplicationResponse>.Success(response);
    }
}