using Modulith.WebApi.Modules.Storage.Contracts;
using Modulith.WebApi.Modules.Storage.Domain;
using System.Collections.Concurrent;

namespace Modulith.WebApi.Modules.Storage.Infrastructure;

/// <summary>
/// An in-memory implementation of the track repository utilizing a concurrent dictionary.
/// </summary>
public class InMemoryTrackRepository : IFriendlyTrackRepository
{
    private readonly ConcurrentDictionary<Guid, FriendlyForceTrack> _store = new(); 
       
    /// <summary>
    /// Saves or updates the friendly force track in the in-memory cache.
    /// </summary>
    /// <param name="track">The track entity to save.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SaveAsync(FriendlyForceTrack track)
    {
        _store.AddOrUpdate(track.Id, track, (_, _) => track);
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Retrieves a track from the in-memory cache.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <returns>A task returning the track entity, or null if not found.</returns>
    public Task<FriendlyForceTrack?> GetByIdAsync(Guid id)
    {
        _store.TryGetValue(id, out var track);
        return Task.FromResult(track);
    }
}