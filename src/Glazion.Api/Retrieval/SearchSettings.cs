using Glazion.Api.Data;
using Glazion.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Retrieval;

/// <summary>Search settings an admin changes from the dashboard. strictFiring: see <see cref="SearchSettings"/>.</summary>
public sealed record SearchSettingsDto(bool StrictFiring, DateTimeOffset? UpdatedAt);

public sealed record UpdateSearchSettingsRequest(bool? StrictFiring);

/// <summary>
/// Strict firing (meeting 2026-10-29), on unless an admin turns it off: a search with a cone shows only tiles fired
/// within ±1 cone of it, and a search with an atmosphere only tiles fired in it. Tiles with no cone recorded are hidden
/// from a cone search. Turned off, firing only lowers a tile's score, and a search can still ask for strict itself.
/// </summary>
public static class SearchSettings
{
    public const string StrictFiringKey = "search.strictFiring";

    public static async Task<SearchSettingsDto> GetAsync(GlazionDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == StrictFiringKey, cancellationToken);
        return new SearchSettingsDto(row?.Value != "false", row?.UpdatedAt);
    }

    public static async Task<bool> StrictFiringAsync(GlazionDbContext db, CancellationToken cancellationToken) =>
        (await GetAsync(db, cancellationToken)).StrictFiring;

    public static async Task<SearchSettingsDto> UpdateAsync(GlazionDbContext db, UpdateSearchSettingsRequest request, Guid adminId, CancellationToken cancellationToken)
    {
        if (request.StrictFiring is { } strict)
        {
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == StrictFiringKey, cancellationToken);
            if (row is null) db.AppSettings.Add(row = new AppSetting { Key = StrictFiringKey });
            row.Value = strict ? "true" : "false";
            row.UpdatedBy = adminId;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return await GetAsync(db, cancellationToken);
    }
}
