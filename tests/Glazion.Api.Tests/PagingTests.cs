using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glazion.Api.Tests;

public sealed class PagingTests
{
    [Fact]
    public void In_memory_pages_walk_the_whole_list_and_stop()
    {
        var all = Enumerable.Range(1, 7).ToList();
        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = Paging.Page(all, cursor, 3);
            Assert.Equal(7, page.TotalCount);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null);
        Assert.Equal(all, seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public void Page_size_defaults_and_is_capped_per_endpoint()
    {
        var all = Enumerable.Range(1, 1000).ToList();
        Assert.Equal(Paging.DefaultPageSize, Paging.Page(all, null, null).Items.Count);
        Assert.Equal(Paging.MaxPageSize, Paging.Page(all, null, 10_000).Items.Count);
        Assert.Equal(500, Paging.Page(all, null, 10_000, maxPageSize: 500).Items.Count);
        Assert.Single(Paging.Page(all, null, 0).Items);
    }

    [Fact]
    public void Paging_is_requested_only_by_a_cursor_or_page_size()
    {
        Assert.False(Paging.Requested(null, null));
        Assert.False(Paging.Requested("  ", null));
        Assert.True(Paging.Requested(null, 10));
        Assert.True(Paging.Requested(Paging.Encode(20), null));
    }

    [Fact]
    public void A_tampered_cursor_is_a_validation_error() =>
        Assert.Equal(400, Assert.Throws<ApiProblemException>(() => Paging.Page(new[] { 1 }, "not-a-cursor", 5)).Status);

    [Fact]
    public async Task Database_pages_follow_next_cursor_without_overlap()
    {
        await using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("paging-" + Guid.NewGuid()).Options);
        db.Users.AddRange(Enumerable.Range(1, 12).Select(i => new Domain.AppUser { Id = Guid.NewGuid(), Username = $"u{i:00}" }));
        await db.SaveChangesAsync();
        var ordered = db.Users.AsNoTracking().OrderBy(x => x.Username);

        var first = await Paging.PageAsync(ordered, null, 5, rows => Task.FromResult<IReadOnlyList<string>>(rows.Select(r => r.Username).ToList()), CancellationToken.None);
        var second = await Paging.PageAsync(ordered, first.NextCursor, 5, rows => Task.FromResult<IReadOnlyList<string>>(rows.Select(r => r.Username).ToList()), CancellationToken.None);
        var last = await Paging.PageAsync(ordered, second.NextCursor, 5, rows => Task.FromResult<IReadOnlyList<string>>(rows.Select(r => r.Username).ToList()), CancellationToken.None);

        Assert.Equal(["u01", "u02", "u03", "u04", "u05"], first.Items);
        Assert.Equal("u06", second.Items[0]);
        Assert.Equal(["u11", "u12"], last.Items);
        Assert.Null(last.NextCursor);
        Assert.Equal(12, last.TotalCount);
    }

    [Fact]
    public void Materials_return_a_next_cursor_until_the_last_page()
    {
        var umf = new UmfCalculationService(new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("paging-mat-" + Guid.NewGuid()).Options),
            new ChemistryDataProvider(TestChemistry.Data()), NullLogger<UmfCalculationService>.Instance);
        var first = umf.ListMaterials(null, 4, 0);
        Assert.Equal(4, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var rest = umf.ListMaterials(null, 100, Paging.Decode(first.NextCursor));
        Assert.Equal(first.Total, first.Items.Count + rest.Items.Count);
        Assert.Null(rest.NextCursor);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(rest.Items.Select(i => i.Id)));
    }
}
