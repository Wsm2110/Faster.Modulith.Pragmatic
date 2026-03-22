using Modulith.DomainEventDispatcher.Contracts;
using Modulith.WebApi.Modules.Storage.Application.CommandHandlers;
using Modulith.WebApi.Modules.Storage.Contracts;

namespace Modulith.WebApi.Modules.Storage.Application;

/// <summary>
/// The concrete implementation of the Storage module API, acting as a facade to internal handlers.
/// </summary>
public class StorageEntryPoint : IStorageEntryPoint
{
    #region Fields

    private readonly ILogger<StorageEntryPoint> _logger;
    private readonly StoreTrackDataCommandHandler _storeTrackDataCommandHandler;
    private readonly GetTrackCommandHandler _getTrackCommandHandler;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageEntryPoint"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="commandHandler">The internal command handler managing the storage logic.</param>
    public StorageEntryPoint(ILogger<StorageEntryPoint> logger,
        StoreTrackDataCommandHandler commandHandler,
        GetTrackCommandHandler trackCommandHandler)
    {
        _logger = logger;
        _storeTrackDataCommandHandler = commandHandler; // Resolved cleanly by the DI container
        _getTrackCommandHandler = trackCommandHandler;
    }

    #endregion

    #region Api

    /// <summary>
    /// Persists the track data to the underlying storage system synchronously by delegating to the internal handler.
    /// </summary>
    /// <param name="trackData">The track data payload to store.</param>
    /// <returns>A result indicating the success or failure of the storage operation.</returns>
    public async Task<Result<bool>> StoreTrackDataAsync(TrackStorageDto trackData)
    {
        _logger.LogInformation("[{Timestamp}] StorageApi delegating request to StoreTrackDataCommandHandler for TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), trackData.TrackId);

        var command = new StoreTrackDataCommand(trackData);

        // The caller doesn't need to know the handler exists
        return await _storeTrackDataCommandHandler.HandleAsync(command);
    }

    /// <summary>
    /// Retrieves a track by its unique identifier by delegating to the internal query handler.
    /// </summary>
    /// <param name="trackId">The unique identifier of the track.</param>
    /// <returns>A result containing the track data or an error if not found.</returns>
    public async Task<Result<FriendlyTrackDto>> GetTrackAsync(Guid trackId)
    {
        var query = new GetFriendlyTrackCommand(trackId);
        return await _getTrackCommandHandler.HandleAsync(query);
    }

    #endregion
}