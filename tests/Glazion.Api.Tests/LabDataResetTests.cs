using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glazion.Api.Tests;

public sealed class LabDataResetTests
{
    [Fact]
    public async Task The_dry_run_keeps_the_imported_bases_and_users_and_clears_the_lab_work_without_touching_anything()
    {
        await using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("reset-" + Guid.NewGuid()).Options);
        var admin = new AppUser { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, Status = AccountStatus.Active };
        var ryan = new AppUser { Id = Guid.NewGuid(), Username = "ryan", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "P05" };
        var pilot = new AppUser { Id = Guid.NewGuid(), Username = "pilot", Role = UserRole.User, Status = AccountStatus.Active, ParticipantCode = "P01" };
        db.Users.AddRange(admin, ryan, pilot);
        await db.SaveChangesAsync();
        var labCodes = new LabCodeService(db);
        // P01-B1 came in with the import: one of its variations is an imported recipe. P05-B1 is lab work.
        var imported = await labCodes.RegisterBaseAsync(pilot, "Stain Spectrum", null, [new MaterialRow("Silica", 100, "%")], null, null, admin.Id, CancellationToken.None);
        var stain = await labCodes.RegisterVariationAsync(imported, "B1 + 2% stain", null, [new MaterialRow("Stain 6000", 2, "%")], pilot.Id, CancellationToken.None);
        stain.SourceType = RecipeSourceType.Imported;
        var lab = await labCodes.RegisterBaseAsync(ryan, "Cobalt Tit", null, [new MaterialRow("Silica", 100, "%")], null, null, admin.Id, CancellationToken.None);
        var cobalt = await labCodes.RegisterVariationAsync(lab, "B1 + 1% Cobalt", null, [new MaterialRow("Cobalt Carbonate", 1, "%")], ryan.Id, CancellationToken.None);
        db.TileTasks.Add(new TileTask { Id = Guid.NewGuid(), RecipeId = cobalt.Id, ClayBody = "porcelain", AssignedUserId = ryan.Id, CreatedBy = admin.Id });
        await db.SaveChangesAsync();

        var report = await new LabDataReset(db, NullLogger<LabDataReset>.Instance).RunAsync(null, admin.Id, CancellationToken.None);

        Assert.True(report.DryRun);
        Assert.Null(report.BackupSchema);
        Assert.Equal(["P05-B1"], report.BasesCleared);
        Assert.Equal(["P01-B1"], report.BasesKept);
        Assert.Equal((1, 1, 1), (report.Cleared["Recipes by Id"], report.Cleared["BaseGlazes by Id"], report.Cleared["TileTasks by Id"]));
        Assert.Equal((1, 3), (report.ImportedRecipesKept, report.UsersKept));
        // A dry run changes nothing; a wrong phrase is a dry run too.
        Assert.Equal(2, await db.BaseGlazes.CountAsync());
        Assert.True((await new LabDataReset(db, NullLogger<LabDataReset>.Instance).RunAsync("clear", admin.Id, CancellationToken.None)).DryRun);
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() =>
            new LabDataReset(db, NullLogger<LabDataReset>.Instance).DropBackupAsync("lab_backup_1", "yes", admin.Id, CancellationToken.None))).Code);
    }
}
