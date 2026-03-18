namespace Modulith.WebApi.Modules.Replication.Contracts;

/// <summary>
/// Represents the response after attempting to replicate a track.
/// </summary>
public record TrackReplicationResponse(Guid TrackId, string TargetNode);
