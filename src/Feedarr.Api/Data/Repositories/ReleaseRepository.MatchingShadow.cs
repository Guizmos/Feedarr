using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    internal ReleaseForPoster? MatchingSnapshotForShadow => _matchingSnapshot.Value;
    internal MatchingShadowState? ReadMatchingShadowState(long id)
    {
        using var connection = MatchingReadOnly.Open(_db);
        return MatchingReadOnly.ReadState(connection, id);
    }
}
