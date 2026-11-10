using Glazion.Api.Contracts;
using Glazion.Api.Infrastructure;
using Glazion.Api.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glazion.Api.Controllers;

/// <summary>
/// Capture by code (docs/features/tile-tasks.md): anyone, participant or admin, snaps a tile, types the code written on it, and
/// picks which of its tiles (clay bodies) it is. The photo is then sent with that tile's taskId.
/// </summary>
[ApiController]
[Route("api/v2/capture")]
[Authorize]
public sealed class CaptureController(LabCodeService labCodes, TileTaskService tasks) : ControllerBase
{
    /// <summary>The recipe with this tile code and its tiles: who made each, and whether it can be photographed now.</summary>
    [HttpGet("tiles/{code}")]
    public async Task<ActionResult<CaptureLookupDto>> Tiles(string code, CancellationToken cancellationToken)
    {
        var recipe = await labCodes.FindByCodeAsync(code, cancellationToken);
        if (recipe is null || !recipe.IsActive)
            throw new ApiProblemException(404, "recipe_code_not_found", "Tile code not found", $"No recipe has the code '{code.Trim()}'. Check the code on the back of the tile.");
        var tiles = await tasks.TilesForCaptureAsync(User.GetUserId(), recipe.Id, cancellationToken);
        return Ok(new CaptureLookupDto(recipe.Id, recipe.Code, recipe.Name, recipe.BaseGlaze?.Code, recipe.BaseGlaze?.Name, recipe.FiringCone, recipe.FiringAtmosphere, tiles));
    }
}
