# Using the UMF calculator from the apps

How the web and mobile apps use the backend's UMF calculator: the materials dropdown, the live calculator,
target ranges, and recipe-to-image search. The backend is the single source of UMF, so the apps no longer need to
ship `materials.json` or their own calculator.

Base URL: `https://glazion-api-prod-production.up.railway.app/api/v2`. Every call needs the signed-in user's token
(`Authorization: Bearer <jwt>`); without it the API returns 401.

| Step | Call | When |
|------|------|------|
| 1. Pick materials | `GET /materials` | As the user types in a recipe row |
| 2. Show the UMF | `POST /umf/calculate` | On every recipe edit (debounced) |
| 3. Target ranges | `GET /glaze-limits?cone=6` | When the cone changes (or read `limits` from step 2) |
| 4. Find tiles | `POST /recipe-image/search` | When the user asks "what does this look like?" |

## 1. Materials dropdown: `GET /materials`

```http
GET /api/v2/materials?q=epk&pageSize=20
```

```json
{
  "materialsVersion": "materials-sha256:cf4fe8d4ca33",
  "total": 3,
  "items": [
    {
      "id": "ep-kaolin", "name": "EP Kaolin", "type": "raw", "loi_pct": 13.2,
      "oxideAnalysis": { "SiO2": 45.73, "Al2O3": 37.36 },
      "hasAnalysis": true, "source": "file", "category": "clay", "common": true,
      "aliases": ["EPK", "Edgar Plastic Kaolin"], "matchedAlias": "EPK"
    }
  ],
  "categories": [{ "key": "clay", "label": "Clays & kaolins" }],
  "nextCursor": null
}
```

- **Search:** `q` ranks exact names first, then prefixes, word prefixes, contains, and finally one-typo matches.
  It also matches studio names (`EPK`, `Flint`, `Custer`). Use `matchedAlias` to show "EP Kaolin (EPK)".
- **Before the user types:** `?common=true` returns the studio short list (74 everyday materials), grouped by
  `category`. Use `categories` for the group headings or filter chips.
- **Filters:** `category=clay`, `withAnalysis=true` (hide materials with no oxide analysis).
- **Paging:** pass `pageSize`, then the returned `nextCursor` as `cursor` until it is `null`. At most 500 a page.
- **Keep the `id`.** Store `id` on the recipe row; it is what you send to the calculator and to search. Show `name`.

## 2. Live calculator: `POST /umf/calculate`

Stateless (nothing is saved), fast, and safe to call on each edit. Debounce typing (~300 ms) and drop responses that
arrive out of order.

```http
POST /api/v2/umf/calculate
Content-Type: application/json

{
  "cone": "6",
  "rows": [
    { "materialId": "custer-feldspar", "amount": 40 },
    { "materialId": "silica",          "amount": 30 },
    { "materialId": "calcium-carbonate", "amount": 20 },
    { "material": "EP Kaolin",         "amount": "10" }
  ]
}
```

- **Rows:** `{ materialId, amount }` from the dropdown, or `{ material, amount }` by name for older recipes.
  `materialId` wins when both are given.
- **Amount:** a number or the text the user typed. It is read like JavaScript `parseFloat`, so `"40g"` is 40.
- **Amounts are parts**, not percentages. They don't need to add up to 100; the backend normalises them.
- **`cone`** is optional. `"06"` and `-6` both mean cone 06. Only 06, 6, 9 and 10 have target ranges.

Response (200):

```json
{
  "materialsVersion": "materials-sha256:cf4fe8d4ca33",
  "skippedRows": [{ "index": 3, "reason": "unknown material", "material": "Potash Feldspar" }],
  "result": {
    "lines": [{ "materialId": "custer-feldspar", "partsPct": 40 }],
    "moles":       { "SiO2": 0.95 },
    "molePct":     { "SiO2": 64.1 },
    "analysisPct": { "SiO2": 61.2 },
    "umf":         { "SiO2": 3.21, "Al2O3": 0.37, "CaO": 0.76 },
    "fluxSum": 0.31,
    "warnings": [],
    "ratios": { "si_al": 8.7, "alkali_over_alkaline": 0.3, "b2o3_pct": 0 },
    "cte": 6.6,
    "suggestions": [
      { "oxide": "Al2O3", "current": 0.21, "target": "0.25 - 0.45", "direction": "increase",
        "materials": [{ "name": "EP Kaolin", "oxideContent": 37.36, "inRecipe": true }],
        "minUmf": 0.25, "maxUmf": 0.45, "notes": null }
    ],
    "clayBodyNote": "Low CTE: Fits tight, may craze on high-expansion bodies"
  },
  "cone": "6",
  "limits": [{ "cone": "6", "oxide": "SiO2", "minUmf": 2.5, "maxUmf": 3.5, "notes": null }],
  "displayGroups": [{ "key": "ro", "label": "Fluxes, RO", "oxides": [{ "oxide": "CaO", "value": 0.76 }] }],
  "cteBand": "low",
  "versions": { "calculationProfile": "glazion-umf-v1", "calculationVersion": "…", "oxidesVersion": "…",
                "materialsVersion": "materials-sha256:cf4fe8d4ca33", "limitsVersion": "…" }
}
```

(Numbers shortened for readability.)

What to render:

| Field | Show it as |
|-------|------------|
| `result.umf` | The UMF table. `displayGroups` gives the same values already grouped in display order (R₂O, RO, R₂O₃, RO₂). |
| `limits` | Each oxide's target range for the cone, next to its UMF value (in range / under / over). |
| `result.suggestions` | "Al2O3 is under 0.25 - 0.45: add EP Kaolin" (`direction` is `increase` or `decrease`). `inRecipe` says whether the material is already in the recipe. Empty without a cone. |
| `result.ratios` | Si:Al and alkali:alkaline-earth. A ratio that can't be worked out is left out of the JSON, so treat missing as "n/a". |
| `result.cte`, `cteBand`, `result.clayBodyNote` | Thermal expansion, its band (`low` / `medium` / `high`) and the fit note. |
| `result.warnings` | As given, e.g. when a recipe has no flux. |
| `skippedRows` | Flag the row at `index` (0-based position in `rows`) with its `reason`. |

**When something is wrong:**

- **Skipped rows are not errors.** Rows with `no material`, `no amount`, `unknown material` or
  `amount is not a number` are left out and listed in `skippedRows`; the rest of the recipe is still calculated.
  Highlight those rows in the editor.
- **`result: null`** (still 200) means nothing could be calculated, for example an empty recipe. Show an empty state,
  not an error.
- **400** means `rows` isn't an array (a bug in the request). **401** means the token is missing or expired.

**`materialsVersion`** changes when the materials list changes, including when an administrator corrects a material.
Treat it as an opaque string. Store it with a saved recipe so you know which data the numbers came from.

## 3. Target ranges: `GET /glaze-limits?cone=6`

Returns `limits` for one cone (same shape as in the calculate response). `/umf/limits` is the same endpoint. You
only need this if you show ranges before the user has a recipe; otherwise use `limits` from the calculate response.

## 4. Recipe to image: `POST /recipe-image/search`

The search runs the same calculator on the recipe, then finds the tiles whose chemistry is closest (nearest-neighbour
search, re-ranked on chemistry, firing and labels). You don't need to call `/umf/calculate` first.

```http
POST /api/v2/recipe-image/search
Content-Type: application/json
Idempotency-Key: 3f1c…   (optional: a retry with the same key returns the same search)

{
  "query": {
    "type": "raw_recipe",
    "name": "My celadon",
    "materials": [
      { "materialId": "custer-feldspar", "amount": 40, "unit": "%" },
      { "materialId": "silica",          "amount": 30, "unit": "%" },
      { "materialId": "calcium-carbonate", "amount": 20, "unit": "%" },
      { "rawName": "EP Kaolin",          "amount": 10, "unit": "%" }
    ]
  },
  "firing":  { "coneMin": 6, "coneMax": 6, "atmosphere": "oxidation", "strict": false },
  "filters": { "clayBodies": ["porcelain"], "surface": ["glossy"] },
  "topK": 12
}
```

- **Rows** take `materialId` (wins) or `rawName`, the same as the calculator. All rows must use one `unit`.
- **Stricter than the calculator.** Search won't drop a row: an unknown material returns
  `422 materials_unresolved` with a `resolutions` list (`position`, `rawName`, `status`, `suggestions`). Show the
  suggestions so the user can fix the row and search again. A recipe with no flux returns `422 zero_flux`.
- **Firing:** strict by default: only tiles within ±1 cone and in the same atmosphere show, and tiles with no cone
  recorded are left out. An admin can turn this off (`GET /api/v2/recipe-image/settings` says which); then the cone
  and atmosphere only affect the score, unless the search sends `strict: true`.
- **Filters:** `clayBodies` (`porcelain`, `stoneware`, `red_body`) and `tileVariantKeys` are hard filters.
  `surface`, `opacity` and `colourFamily` are preferences that only raise the score.
- **Other query types:** `"umf"` (send a UMF object directly) and `"recipe"` (a saved recipe by `recipeId` or lab
  code `recipeCode`, e.g. `A03-B2-009`).

Response:

```json
{
  "searchId": "…",
  "query": { "type": "raw_recipe", "inputHash": "sha256:…", "calculationProfile": "glazion-umf-v1", "recipeCode": null },
  "ranker": { "version": "…", "candidateCount": 96, "returnedCount": 12, "minimumConfidence": 0.45 },
  "items": [
    {
      "rank": 1, "imageId": "…", "displayUrl": "https://…", "thumbnailUrl": "https://…",
      "score": 0.87, "confidenceBand": "high",
      "scoreBreakdown": { "chemistrySimilarity": 0.97, "firingCompatibility": 1.0, "labelOrTextSimilarity": 0.6 },
      "matchedFields": ["silica", "alumina", "fluxBalance", "cone"],
      "tile": { "variant": "…", "surface": "glossy", "opacity": "transparent", "colourFamily": "green", "clayBody": "porcelain" },
      "firing": { "coneMin": 6, "coneMax": 6, "atmosphere": "oxidation" },
      "source": { "recordType": "…", "recipeId": "…", "recipeCode": "A03-B2-009", "recipeName": "B2 + 2% copper", "baseCode": "A03-B2", "metadataVisibility": "…" },
      "limitations": ["Exact firing schedule was not recorded."]
    }
  ],
  "warnings": [],
  "createdAt": "…"
}
```

- **Show** `thumbnailUrl` in the grid and `displayUrl` when a tile is opened. They are signed links that expire, so
  don't store them. To show a past search again, call `GET /recipe-image/search/{searchId}`, which returns fresh
  links.
- **Explain the match:** `confidenceBand` (`high` / `medium` / `low`), `matchedFields` (what lines up: silica,
  alumina, boron, flux balance, Si:Al ratio, cone, atmosphere) and `limitations` (what isn't known about that tile).
- **No results:** `items` is empty and `warnings` contains `no_reliable_match`. Say nothing close was found rather
  than showing weak matches.
- **Feedback:** `POST /recipe-image/feedback` with `{ "searchId", "imageId", "feedback": "relevant" | "not_relevant", "note" }`.
- **Recipe section:** send `includeRecipeMetadata: true`. From a result, open the recipe with
  `GET /recipes/{source.recipeId}` (all materials, base, additions, UMF and every approved photo). Open its base and
  every variation of it with `GET /base-glazes/{source.baseCode}` and `GET /base-glazes/{source.baseCode}/recipes`
  (paged, each with `coverThumbnailUrl`). To browse everything, use `GET /recipes?search=` and `GET /base-glazes?search=`.
  These are read only and work for every signed-in account. Details: [04-search.md §4.7](../architecture/recipe-to-image/04-search.md).

## Checking the apps and the backend agree

RealDiamond's golden set (1,072 recipes) runs against the live endpoint:

```bash
TOKEN=<jwt> node golden/verify.mjs https://glazion-api-prod-production.up.railway.app/api/v2/umf/calculate
```

It should print `1072 of 1072 cases match`. Run it while no material corrections are in place, since a correction
changes the numbers for recipes that use that material.

See also: [umf-calculator.md](../features/umf-calculator.md) (how the calculation works),
[materials-admin.md](../features/materials-admin.md) (dropdown data and corrections),
[recipe-to-image/04-search.md](../architecture/recipe-to-image/04-search.md) (search internals).
