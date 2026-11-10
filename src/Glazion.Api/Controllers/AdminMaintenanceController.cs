using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glazion.Api.Controllers;

/// <summary>
/// The fresh start before live participants (meeting 2026-11-09). lab-reset without confirm only reports what it would
/// clear; with confirm "CLEAR LAB DATA" it copies those rows into a backup schema and clears them. The backup stays
/// until it is deleted with confirm "DELETE BACKUP", once the client has approved.
/// </summary>
[ApiController]
[Route("api/v2/admin/maintenance")]
[Authorize(Roles = "Admin")]
public sealed class AdminMaintenanceController(LabDataReset reset) : ControllerBase
{
    public sealed record LabResetRequest(string? Confirm);

    [HttpPost("lab-reset")]
    public Task<LabResetReport> LabReset(LabResetRequest? request, CancellationToken cancellationToken) =>
        reset.RunAsync(request?.Confirm, User.GetUserId(), cancellationToken);

    [HttpGet("backups")]
    public Task<IReadOnlyList<LabBackupInfo>> Backups(CancellationToken cancellationToken) => reset.BackupsAsync(cancellationToken);

    [HttpDelete("backups/{schema}")]
    public async Task<IActionResult> DeleteBackup(string schema, [FromQuery] string? confirm, CancellationToken cancellationToken)
    {
        await reset.DropBackupAsync(schema, confirm, User.GetUserId(), cancellationToken);
        return NoContent();
    }
}
