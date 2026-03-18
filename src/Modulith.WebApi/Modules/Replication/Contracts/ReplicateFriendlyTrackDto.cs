namespace Modulith.WebApi.Modules.Replication.Contracts;

/// <summary>
/// Data Transfer Object representing the request to replicate a track.
/// </summary>
public record ReplicateTrackDto(Guid TrackId, String Callsign, string TargetNode, double Latitude, double Longitude, DateTime Timestamp)
{

}
