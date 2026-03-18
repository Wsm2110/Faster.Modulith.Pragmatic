using Modulith.WebApi.Modules.Replication.Contracts;

namespace Modulith.WebApi.Modules.Replication.Infrastructure;

/// <summary>
/// A dummy implementation of the replication client simulating network transmission.
/// </summary>
public class DummyNetworkReplicationClient : INetworkReplicationClient
{
    private readonly ILogger<DummyNetworkReplicationClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DummyNetworkReplicationClient"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public DummyNetworkReplicationClient(ILogger<DummyNetworkReplicationClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Simulates transmitting the track data over the network.
    /// </summary>
    /// <param name="trackData">The track data to transmit.</param>
    /// <returns>A task returning true to indicate successful simulated transmission.</returns>
    public async Task<bool> TransmitTrackAsync(ReplicateTrackDto trackData)
    {
        _logger.LogInformation("[{Timestamp}] DummyNetworkReplicationClient opening connection to external cluster node...", DateTime.UtcNow.ToString("O"));

        // Simulate network latency and bandwidth transfer
        await Task.Delay(65);

        _logger.LogInformation("[{Timestamp}] Network transmission complete. Payload delivered for TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), trackData.TrackId);

        return true;
    }
}