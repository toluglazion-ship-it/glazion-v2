namespace Glazion.Api.Retrieval;

/// <summary>
/// What a search shows a regular user (meeting 2026-10-29): the tiles, their match score and band, firing and labels,
/// but not how the match was worked out. The chemistry behind it (the score breakdown, the oxide values of the search,
/// chemistry matched fields and chemistry notes) stays with admins, who see it all in Match check.
/// </summary>
public static class SearchVisibility
{
    /// <summary>Matched fields that come from comparing oxides; the rest (cone, atmosphere, labels, colourants) are kept.</summary>
    private static readonly HashSet<string> ChemistryFields = new(StringComparer.Ordinal) { "fluxBalance", "silica", "alumina", "boron", "silicaAluminaRatio" };

    /// <summary>Words that mark a limitation as being about the chemistry comparison.</summary>
    private static readonly string[] ChemistryWords = ["chemistry", "UMF", "calculation profile", "oxide", "unmeasured"];

    public static RecipeImageSearchResponse ForViewer(RecipeImageSearchResponse response, bool isAdmin)
    {
        if (isAdmin) return response;
        return response with
        {
            Query = response.Query with { Umf = null },
            Items = response.Items.Select(Hide).ToList(),
            ClosestMatch = response.ClosestMatch is null ? null : Hide(response.ClosestMatch),
            Similar = response.Similar?.Select(Hide).ToList()
        };
    }

    public static SearchResultItem Hide(SearchResultItem item) => item with
    {
        ScoreBreakdown = new SearchScoreBreakdown(null, null, null, null, null, null, null, null, null),
        MatchedFields = item.MatchedFields.Where(f => !ChemistryFields.Contains(f)).ToList(),
        Limitations = item.Limitations.Where(l => !ChemistryWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList()
    };
}
