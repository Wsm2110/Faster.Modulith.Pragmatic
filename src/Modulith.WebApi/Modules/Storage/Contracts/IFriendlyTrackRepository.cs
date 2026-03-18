using Modulith.WebApi.Modules.Storage.Domain;

namespace Modulith.WebApi.Modules.Storage.Contracts;

/// <summary>
/// Defines the contract for track data storage operations.
/// </summary>
public interface IFriendlyTrackRepository
{
    /// <summary>
    /// Saves the friendly force track to the underlying storage system.
    /// </summary>
    /// <param name="track">The track entity to save.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveAsync(FriendlyForceTrack track);

    /// <summary>
    /// Retrieves a track by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <returns>A task returning the track entity, or null if not found.</returns>
    Task<FriendlyForceTrack?> GetByIdAsync(Guid id);
}
