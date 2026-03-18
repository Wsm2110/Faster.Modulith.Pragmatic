namespace Modulith.WebApi.Modules.Storage.Domain;

using System;

/// <summary>
/// Represents the internal domain entity for a friendly force track within the storage module.
/// </summary>
public class FriendlyForceTrack
{
    /// <summary>
    /// Gets the unique identifier for the track.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the callsign of the friendly force.
    /// </summary>
    public string Callsign { get; private set; }

    /// <summary>
    /// Gets the latitude coordinate.
    /// </summary>
    public double Latitude { get; private set; }

    /// <summary>
    /// Gets the longitude coordinate.
    /// </summary>
    public double Longitude { get; private set; }

    /// <summary>
    /// Gets the timestamp of the track data.
    /// </summary>
    public DateTime Timestamp { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendlyForceTrack"/> class.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="callsign">The callsign.</param>
    /// <param name="latitude">The latitude coordinate.</param>
    /// <param name="longitude">The longitude coordinate.</param>
    /// <param name="timestamp">The timestamp.</param>
    public FriendlyForceTrack(Guid id, string callsign, double latitude, double longitude, DateTime timestamp)
    {
        Id = id;
        Callsign = callsign;
        Latitude = latitude;
        Longitude = longitude;
        Timestamp = timestamp;
    }
}
