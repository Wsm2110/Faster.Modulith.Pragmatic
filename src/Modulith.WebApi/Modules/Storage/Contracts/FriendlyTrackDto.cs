namespace Modulith.WebApi.Modules.Storage.Contracts;

/// <summary>
/// Data Transfer Object representing a retrieved track payload.
/// </summary>
public record FriendlyTrackDto(Guid TrackId, string Callsign, double Latitude, double Longitude, DateTime Timestamp);