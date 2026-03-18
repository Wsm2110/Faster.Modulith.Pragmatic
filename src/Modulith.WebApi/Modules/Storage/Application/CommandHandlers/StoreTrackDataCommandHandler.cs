using FluentValidation;
using Modulith.DomainEventDispatcher.Contracts;
using Modulith.Result;
using Modulith.WebApi.Modules.Replication.Contracts;
using Modulith.WebApi.Modules.Storage.Contracts;
using Modulith.WebApi.Modules.Storage.Domain;

namespace Modulith.WebApi.Modules.Storage.Application.CommandHandlers;

/// <summary>
/// Represents the internal command to store track data.
/// </summary>
public record StoreTrackDataCommand(TrackStorageDto TrackData);

/// <summary>
/// Validator for the <see cref="StoreTrackDataCommand"/>.
/// </summary>
public class StoreTrackDataCommandValidator : AbstractValidator<StoreTrackDataCommand>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StoreTrackDataCommandValidator"/> class.
    /// </summary>
    public StoreTrackDataCommandValidator()
    {
        RuleFor(x => x.TrackData).NotNull();
        RuleFor(x => x.TrackData.Callsign).NotEmpty().MaximumLength(50);
        RuleFor(x => x.TrackData.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.TrackData.Longitude).InclusiveBetween(-180, 180);
    }
}

/// <summary>
/// Handler for validating, persisting, and orchestrating replication for track data.
/// </summary>
public class StoreTrackDataCommandHandler
{
    private readonly ILogger<StoreTrackDataCommandHandler> _logger;
    private readonly IValidator<StoreTrackDataCommand> _validator;
    private readonly IFriendlyTrackRepository _repository;
    private readonly IEventDispatcher _eventDispatcher;
    private readonly IReplicationEntryPoint _replicationEntrypoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreTrackDataCommandHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="validator">The fluent validator instance.</param>
    /// <param name="repository">The track repository for data persistence.</param>
    /// <param name="replicationEntrypoint">The replication module entry point to trigger cluster broadcasts.</param>
    public StoreTrackDataCommandHandler(
        ILogger<StoreTrackDataCommandHandler> logger,
        IValidator<StoreTrackDataCommand> validator,
        IFriendlyTrackRepository repository,
        IEventDispatcher eventDispatcher, 
        IReplicationEntryPoint replicationEntryPoint)
    {
        _logger = logger;
        _validator = validator;
        _repository = repository;
        _eventDispatcher = eventDispatcher;
        _replicationEntrypoint = replicationEntryPoint;
    }

    /// <summary>
    /// Handles the command to persist the track data and subsequently trigger replication.
    /// </summary>
    /// <param name="command">The command containing the track data payload.</param>
    /// <returns>A result indicating the success or failure of the storage operation.</returns>
    public async Task<Result<bool>> HandleAsync(StoreTrackDataCommand command)
    {
        var validationResult = await _validator.ValidateAsync(command);
        if (!validationResult.IsValid)
        {
            _logger.LogWarning("[{Timestamp}] Internal storage validation failed for TrackId: {TrackId}. Errors: {Errors}", DateTime.UtcNow.ToString("O"), command.TrackData?.TrackId, validationResult.ToString());
            return Result<bool>.Validation(validationResult.ToString());
        }

        var trackData = command.TrackData!;
        _logger.LogInformation("[{Timestamp}] Executing StoreTrackDataCommandHandler to persist data for TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), trackData.TrackId);

        var friendlyForceTrack = new FriendlyForceTrack(
            trackData.TrackId,
            trackData.Callsign,
            trackData.Latitude,
            trackData.Longitude,
            trackData.Timestamp
        );

        await _repository.SaveAsync(friendlyForceTrack);
        _logger.LogInformation("[{Timestamp}] Track data successfully stored locally for TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), trackData.TrackId);

        var replicationDto = new ReplicateTrackDto(
            friendlyForceTrack.Id,     
            friendlyForceTrack.Callsign,
            "Node1",
            friendlyForceTrack.Latitude,
            friendlyForceTrack.Longitude,
            friendlyForceTrack.Timestamp
        );

        _logger.LogInformation("[{Timestamp}] Requesting cluster replication for newly stored TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), friendlyForceTrack.Id);

        // Note: calling different module - loosely coupled
        var replicationResult = await _replicationEntrypoint.TriggerReplicationAsync(replicationDto);

        // Note: publish an event which will be handled by the replication module.
        // The only difference is that this is a decoupled way to trigger behavior in another module
        await _eventDispatcher.PublishAsync(new FriendlyTrackStoredEvent());

        if (!replicationResult.IsSuccess)
        {
            _logger.LogWarning("[{Timestamp}] Track stored successfully, but cluster replication failed for TrackId: {TrackId}. Error: {ErrorMessage}", DateTime.UtcNow.ToString("O"), friendlyForceTrack.Id, replicationResult.Error!);
        }

        return Result<bool>.Success(true);
    }
}