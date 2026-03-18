using Modulith.Result;

namespace Modulith.WebApi.Modules.Storage.Contracts;

/// <summary>
/// Defines the explicit entry point for the Storage module.
/// </summary>
public interface IStorageEntryPoint
{
    /// <summary>
    /// Persists the track data to the underlying storage system synchronously.
    /// </summary>
    /// <param name="trackData">The track data payload to store.</param>
    /// <returns>A result indicating the success or failure of the storage operation.</returns>
    Task<Result<bool>> StoreTrackDataAsync(TrackStorageDto trackData);

    /// <summary>
    /// Retrieves a track by its unique identifier.
    /// </summary>
    /// <param name="trackId">The unique identifier of the track.</param>
    /// <returns>A result containing the track data or an error if not found.</returns>
    Task<Result<FriendlyTrackDto>> GetTrackAsync(Guid trackId);
}
