using Modulith.DomainEventDispatcher.Contracts;
using Modulith.WebApi.Modules.Storage.Contracts;

namespace Modulith.WebApi.Modules.Storage.Application.CommandHandlers;

/// <summary>
/// Represents the internal query to retrieve a track.
/// </summary>
public record GetFriendlyTrackCommand(Guid TrackId);

/// <summary>
/// Handler for retrieving a track from the underlying storage.
/// </summary>
public class GetTrackCommandHandler
{
    private readonly ILogger<GetTrackCommandHandler> _logger;
    private readonly IFriendlyTrackRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetTrackCommandHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="repository">The track repository.</param>
    public GetTrackCommandHandler(ILogger<GetTrackCommandHandler> logger, IFriendlyTrackRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    /// <summary>
    /// Handles the query to retrieve a track.
    /// </summary>
    /// <param name="query">The query containing the track identifier.</param>
    /// <returns>A result containing the track data or a not found error.</returns>
    public async Task<Result<FriendlyTrackDto>> HandleAsync(GetFriendlyTrackCommand query)
    {
        _logger.LogInformation("[{Timestamp}] Executing GetTrackQueryHandler for TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), query.TrackId);

        var track = await _repository.GetByIdAsync(query.TrackId);

        if (track is null)
        {
            _logger.LogWarning("[{Timestamp}] Track not found in repository for TrackId: {TrackId}", DateTime.UtcNow.ToString("O"), query.TrackId);
            return Result<FriendlyTrackDto>.Failure("The specified track was not found in storage.");
        }

        var dto = new FriendlyTrackDto(track.Id, track.Callsign, track.Latitude, track.Longitude, track.Timestamp);
        return Result<FriendlyTrackDto>.Success(dto);
    }
}