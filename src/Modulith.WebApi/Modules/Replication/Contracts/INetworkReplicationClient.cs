namespace Modulith.WebApi.Modules.Replication.Contracts;

/// <summary>
/// Defines the contract for transmitting track data to external nodes.
/// </summary>
public interface INetworkReplicationClient
{
    /// <summary>
    /// Transmits the track data over the network to a target machine.
    /// </summary>
    /// <param name="trackData">The track data to transmit.</param>
    /// <returns>A task representing the asynchronous operation, returning true if successful.</returns>
    Task<bool> TransmitTrackAsync(ReplicateTrackDto trackData);
}