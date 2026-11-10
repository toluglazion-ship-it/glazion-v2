# Match check

Admins can see every search anyone ran, with what the person typed shown beside each tile the system suggested, and
where each tile came from. This answers the question from the 27 October meeting: before results go public, does what
a person puts in match the chemistry of what they are shown? Nothing here is shown to the public.

## What a search keeps

Each run (`SearchRuns.QuerySnapshotJson`) now also records:

- `input`: the name and the material rows as typed (`rawName`, the `materialId` and `name` each resolved to, `amount`,
  `unit`). Recipe searches made before 27 October 2026 kept only the UMF.
- `pinned`: for each tile shown, the calculation snapshot it was compared on, so the comparison stays the one the person
  saw even after the tile is re-calculated.

Neither is part of the search's input hash, so idempotency and repeat-search behaviour are unchanged.

## Endpoints (`Admin` role)

| Call | Endpoint |
|---|---|
| list | `GET /api/v2/admin/searches?type=&notRelevant=&userId=&q=&cursor=&pageSize=` → `PagedResult<SearchCheckSummary>`, newest first. `type`: `raw_recipe`, `recipe` or `umf`. `notRelevant=true`: only searches where the person marked a result not relevant. `q` matches the person's username or name. |
| one | `GET /api/v2/admin/searches/{id}` → `SearchCheckDetail` |
| image | `GET /api/v2/admin/images/{id}/trace` → `ImageTrace` (for the photo library: the same provenance, recipe and chemistry without a search) |
| tiles | `GET /api/v2/admin/images/traces?approval=&chemistry=&source=&recipeCode=&clayBody=&importBatchId=&participantCode=&cone=&q=&cursor=&pageSize=` → `PagedResult<ImageTrace>`, newest first, up to 50 a page: every image beside its recipe and chemistry, for checking imported photos and new uploads without opening each one (meeting 2026-10-28). Filters as `GET /admin/images`; `source`: `Import` or `UserUpload`. |

`SearchCheckDetail`:

- `query`: type, name, the searched recipe (code, name), `materials` (each with its `kind`: `colorant` for a metallic
  oxide, `stain`, `clay`, …), `materialsAsTyped` (false when the rows are the searched recipe's as it is now), the UMF,
  the stains, firing and clay-body filter.
- `results`: every tile the person was shown. `shown` is `closest` (the big card), `similar` (the row of other glazes) or
  `ranked` (further down the full list). Each has the score and its breakdown, matched fields, limitations, the person's
  feedback, the traced `image`, and `comparison`: one row per oxide either side reports, in schema order, with
  `searched`, `tile` and `difference` (tile minus searched) in UMF, grouped `flux`, `stabiliser`, `glass`, `colourant`
  (Fe2O3, FeO, CoO, CuO, Cr2O3, MnO, NiO, V2O5, TiO2), `opacifier` (ZrO2, SnO2) or `other`.

`ImageTrace`: file name and record id (imports), import batch, the participant who photographed it (uploads), clay body,
firing, approval, its recipes (code, name, base, the full material rows and, for a variation, what it adds to its base),
its UMF (`supplied` when imported as numbers rather than calculated; `comparedAtSearch` says whether it is the snapshot
the search used), and its stains.
