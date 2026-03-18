namespace Modulith.WebApi.Modules.Storage.Contracts;

/// <summary>
/// Data Transfer Object representing a track payload to be stored.
/// </summary>
public record TrackStorageDto(Guid TrackId, string Callsign, double Latitude, double Longitude, DateTime Timestamp);