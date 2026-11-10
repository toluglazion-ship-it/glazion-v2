# Recipe-to-image search

**Status:** Implemented (slice 4 of the recipe-to-image retrieval engine)
**Sources:** PRD/TRD R2I-01…08, §5.6, §10 Retrieval, §11.3, §14; Retrieval plan §3.1–3.4, §10, §11.4–11.6, §16 Retrieval
**Builds on:** [03-indexing-jobs.md](03-indexing-jobs.md)

A recipe or UMF goes in; ranked, approved, real tile images come out with a score breakdown,
matched fields, firing metadata, and limitations. Nothing is ever fabricated: when no candidate
clears the confidence threshold the response says `no_reliable_match`.

---

## 1. Endpoints

New phase-two routes use the `/api/v2` prefix and RFC 7807 problem details, as PRD §10 requires.
The whole API is on `/api/v2` (the core endpoints moved in [`docs/api/frontend-contract.md`](../../api/frontend-contract.md)).

```text
POST /api/v2/recipe-image/search             Idempotency-Key header honoured
GET  /api/v2/recipe-image/search/{searchId}  owner or admin; results re-authorised on read
POST /api/v2/recipe-image/feedback           { searchId, imageId, feedback: relevant | not_relevant, note? }
```

Search is **system scoped**: every signed-in, active account gets the same search over the whole
approved corpus, whatever its role. `Features:RecipeImageRetrieval` (default **on**) is a switch for
everyone at once; with it off, every account, admins included, gets `403 feature_disabled`.
A saved search run is the one thing tied to a person: it is read back by whoever ran it (or an admin).

## 2. Query types

| `query.type` | Input | Behaviour |
|--------------|-------|-----------|
| `umf` | `umf` object, optional `calculationProfile`, `calculationVersion`, `oxideWeightPercent` | Validated against `oxide-vector-v1` (unknown oxide, negative or non-numeric value, zero flux → 400). `oxideWeightPercent` is stored in the run snapshot but not ranked. |
| `recipe` | `recipeId`, or a lab code in `recipeCode` (`A03-B2-009`) | Any active recipe, whoever created it (recipes are system scoped). Uses its newest calculation snapshot; with none, its chemistry is calculated from its own material rows for this search, nothing is stored, and `warnings` carries `calculated_for_this_search`. No snapshot and no usable rows → `422 recipe_chemistry_unavailable`. Unknown or inactive → `404 recipe_not_found`. |
| `raw_recipe` | `materials: [{rawName, amount, unit}]` (or `materialId` from the materials dropdown) | **The main way to search: the materials of a recipe.** Calculated with the ported client calculator ([umf-calculator.md](../../features/umf-calculator.md)) into a UMF, and its stains read off separately (§2.1). Every row must resolve to a material in the list, otherwise `422 materials_unresolved` lists each row's status and suggestions; rows are never dropped silently (R2I-02, UMF-02). Mixed units are rejected. |

### 2.1 Colorants: metallic oxides and stains

A stain such as Mason 6001 has no oxide analysis in the materials list, so it adds nothing to a UMF: base +
2.5% Alpine Rose, base + 10% Alpine Rose and the plain base have exactly the same UMF. In the production
corpus that is 318 variations of `P01-B1` on one chemistry vector. The UMF alone cannot pick the right tile.

So every query and every tile also carries its **stain mix**: each stain (or colorant whose analysis is
missing) and its amount per 100 parts of the analysed materials (base 100 + 2.5 stain → 2.5), read from the
calculator's lines (`Colourants`, `colourants-v2`). Version 1 counted every material with no analysis;
version 2 counts only the stain and colorant categories, so a data gap such as Marblewhite Whiting is no
longer compared as if it were a stain.

Metallic oxides are in the UMF, but the whole-UMF comparison (§4.1) cannot see them: 0.03 of cobalt is lost
next to 3 of silica, so a copper green scored as close to an iron recipe as another iron tile did. Since
ranker 1.2.0 both kinds are compared together as one **colorant** dimension (`ColorantMatch`,
`colorant-match-v1`):

- **Metallic oxides** are read from each UMF as sent or stored: Fe₂O₃ (FeO counted as Fe₂O₃, two FeO to one
  Fe₂O₃), CoO, CuO, Cr₂O₃, MnO, NiO, V₂O₅ and TiO₂.
- **Stains** join only when both sides know them (not for a `umf` query, nor a tile with no recorded
  materials).
- Each amount is counted in **tints**, the amount that gives a light but clear colour in a typical glaze:
  1% Fe₂O₃, 0.1% CoO, 0.5% CuO, 0.2% Cr₂O₃, 1% MnO, 0.5% NiO, 2% V₂O₅, 3% TiO₂ (as UMF amounts in a glaze of
  about 280 g per unity), and 1 part of stain per 100 of base. So 0.1% cobalt weighs as much as 1% iron.
- Less than a quarter of a tint counts as none, which ignores the iron and titanium clays and feldspars carry.
- The two glazes are compared as overlap with one tint of slack: (Σ min + 1) / (Σ max + 1) over oxides and
  stains. The same colorants in the same amounts score 1, and so do two glazes with none. Double the amount of
  the same colorant scores about 0.6, and a different colorant about 0.1–0.25.

845 of the 2,830 shipped materials have no analysis. 333 of them are stains; the rest are data gaps.
Each such material in a query adds a warning `no_oxide_analysis:<name>`, because the UMF the search shows
leaves it out.

Recipe rows stored with a name potters use rather than the material's own (the catalog's aliases, such as
"Shell Pink 6000" for Stain 6000 or "RIO" for Iron Oxide Red) are pointed at that material before they are
calculated (`UmfCalculationService.WithAliases`). Before, the calculator skipped them, so such a stain read as
no stain at all.

A lab-coded variation becomes searchable by its code once `POST /api/v2/recipes/by-code/{code}/calculate`
has stored its snapshot; before that, searching by its code returns `recipe_chemistry_unavailable`.

`firing` takes `coneMin`, `coneMax`, `atmosphere`, `strict`. Negative cones are 0x cones (`-6` is cone 06).

Firing is strict by default (meeting 2026-10-29): a search with a cone shows only tiles fired within ±1 cone of it
(cone 09 shows 010, 09 and 08), and a search with an atmosphere only tiles fired in it. A tile whose cone wasn't
recorded never shows in a cone search. There is no cone 0, so 01 and 1 are neighbours. An admin can turn this off
from the dashboard (`GET`/`PUT /api/v2/admin/settings/search`, `{ "strictFiring": false }`); then firing only
lowers a tile's score unless the search sends `strict: true`. `GET /api/v2/recipe-image/settings` tells any signed-in
app whether it is on.
`filters.tileVariantKeys` is a hard filter. `filters.surface`, `opacity` and `colourFamily` are soft
preferences. `topK` defaults to 12, maximum 50.

## 3. Pipeline (PRD §11.3, plan §11.4)

```
1  validate + canonicalise            → canonical UMF (fixed oxide order), firing, sorted filters, input hash
2  hard filters                       → approvalStatus = approved AND indexable = true
                                        [+ tileVariant IN …] [+ strict: cone ranges overlap ±1, atmosphere =]
3  chemistry KNN   ┐ in parallel      → images-chemistry-oxide-vector-v1, topK = clamp(max(50, 8·topK), …, 400)
3b same-stain KNN  │                  → the same chemistry query, only tiles whose `colourants` hold every one of the query's
3c unstained KNN   │                    (3b) or tiles with `stained = false` (3c); without them, hundreds of tiles on one
   │               │                    UMF would leave the right stain, or the base itself, out of the pool
4  text KNN        ┘                  → images-text-{embeddingVersion}, same pool, query canonical text embedded
6  union candidate IDs
7  load authoritative PostgreSQL rows → approval and quality re-checked; pinned UMF snapshot; labels; recipe links
8  drop stale / inaccessible          → no live IndexRecords row for the namespace that matched = stale vector;
                                        hard filters re-applied to PostgreSQL values
   fill missing text scores           → candidates found only by chemistry KNN get their stored text vector fetched
9  hybrid rank                        → §4
10 top K, signed URLs, persist run    → display/thumbnail URLs signed per item, lab code as download filename
11 closest match + similar            → closestMatch = items[0]; similar = the best image of each other recipe
                                        (Retrieval:SimilarCount, default 8), so suggestions are different glazes
```

The response adds, without changing `items`:

| Field | Meaning |
|-------|---------|
| `query.umf` | The UMF the search used: calculated from the materials for `raw_recipe`, the snapshot's for `recipe`. |
| `query.colourants` | `[{ materialId, name, percentOfBase }]`; null for a `umf` query (unknown). |
| `query.colorantMaterials` | Every colorant in the recipe, for showing: `[{ materialId, name, kind, percentOfBase }]`, `kind` `metallic_oxide` or `stain` (the material's category), per 100 parts of the rest of the recipe, largest first. Null for a `umf` query. `colourants` stays the stain mix the ranking compares (§2.1). |
| `closestMatch` | The best image (same object as `items[0]`). |
| `similar` | Other recipes, one image each, best first; may include recipes beyond `topK`. Feedback is accepted on them. |
| `scoreBreakdown.colourantSimilarity` | The colorant dimension, both kinds (§2.1); null only when the tile has no chemistry. |

Image KNN (step 5) runs only for image queries, which this release does not accept.

If one modality's vector query fails, search continues on the other and adds
`chemistry_search_unavailable` or `text_search_unavailable` to `warnings`. If both fail the
response is `503 vector_index_unavailable`.

## 4. Ranker `hybrid-ranker-1.2.0`

```
finalScore = Σ wᵢ·sᵢ / Σ wᵢ   over available dimensions
             w = chemistry 0.55, firing 0.20, labelOrText 0.15, image 0.10, colourant 0.45   (Retrieval:Weights)
```

1.1.0 added a stain dimension. 1.2.0 makes it the colorant dimension, both kinds (§2.1), and raises its weight
from 0.30 to 0.45: the colour is what a person sees first. It is dropped only for a tile with no chemistry. A
`umf` query still takes part with its metallic oxides; stains are left out when either side's are unknown, and
the result says so in `limitations`. A colorant similarity below 0.5 lowers the confidence band by one (a
clearly different colour is not a close match, however close the base glaze), and below 0.8 the result says
"Colorants differ: you searched …; this tile has …".

Unavailable dimensions are dropped and the rest renormalised (PRD §5.6). Weights, thresholds,
and every version are written into the run snapshot.

### 4.1 Chemistry similarity

Computed from the candidate's pinned UMF snapshot in PostgreSQL, not from the vector score.

```
chemistrySimilarity = 0.5 · cosine + 0.5 · 1 / (1 + d)
d = √( Σ wᵢ ((qᵢ − cᵢ) / sᵢ)² / Σ wᵢ )   over oxides reported by either side
```

| Oxides | Scale `s` | Importance `w` |
|--------|-----------|----------------|
| SiO2 | 0.50 | 1.5 |
| Al2O3 | 0.10 | 1.5 |
| B2O3 | 0.10 | 1.2 |
| Fluxes (Li2O … PbO) | 0.10 | 1.0 |
| TiO2, ZrO2, SnO2 | 0.05 | 1.0 |
| Fe2O3, Cr2O3, CuO, CoO, MnO, NiO | 0.02 | 1.0 |
| P2O5 | 0.02 | 0.5 |

Cosine alone is dominated by silica's magnitude, so a recipe with double the silica still scores
cosine ≈ 0.99. The bounded weighted distance catches that. Both appear in the breakdown as
`chemistryCosine` and `chemistryDistance` so rank changes are diagnosable (plan §11.5).
`chemistryCoverage` is the share of the query's oxides the tile reports. An unreported oxide is
not treated as zero.

### 4.2 Firing compatibility

The plan's table, with extensions for cases it does not list. Cone ranges within
`Retrieval:ConeTolerance` (default 1 cone) of each other count as "within range".

| Cone | Atmosphere | Score | Source |
|------|------------|-------|--------|
| exact | exact | 1.00 | plan |
| within range | exact | 0.85 | plan |
| exact or within range | unrecorded | 0.60 | plan |
| out of range | any | 0.30 | plan |
| any | conflicting | 0.30 | plan |
| unrecorded | exact | 0.60 | extension, mirrors the row above |
| unrecorded | unrecorded | 0.50 | extension |
| query gives cone only | — | 1.00 / 0.85 / 0.50 / 0.30 for exact / range / unrecorded / out of range | extension |
| query gives atmosphere only | — | 1.00 / 0.60 / 0.30 for exact / unrecorded / conflict | extension |
| query gives no firing | — | dimension unavailable, renormalised | extension |

The plan's 0.00 "explicitly incompatible hard filter" never reaches the ranker, because strict
filters remove those candidates first.

### 4.3 Label or text similarity

Text-embedding cosine between the query's canonical text and the tile's. When the request names
soft label preferences, it is blended 50/50 with the share of requested label types the tile matches.

### 4.4 Confidence and threshold

| Band | Score |
|------|-------|
| high | ≥ 0.80 |
| medium | ≥ 0.60 |
| low | ≥ 0.45 (`MinimumConfidence`) |

The band drops one level for each missing modality the query did offer: no chemistry on the
tile, chemistry coverage under 75 %, or no text similarity. Image similarity is never offered
by a query in this release, so its absence carries no penalty. This matches the plan's sample
response, which shows `imageSimilarity: null` with `confidenceBand: "high"`.

Candidates below `MinimumConfidence` are not returned. An empty list carries
`warnings: ["no_reliable_match"]` (R2I-07).

Ties break deterministically on: a curator-verified recipe link, then quality `Passed` over
`NeedsReview`, then chemistry coverage, then image ID. These are the plan's soft preferences
(§11.6), used for ordering rather than folded into the score so the score stays transparent.

### 4.5 Matched fields and limitations

`matchedFields` can contain `cone`, `atmosphere`, `tileVariant`, `fluxBalance` (flux sub-vector
cosine ≥ 0.95), `silica` (|ΔSiO2| ≤ 0.25), `alumina` (≤ 0.05), `boron` (≤ 0.05),
`silicaAluminaRatio` (within 10 %), and any matched soft label.

`limitations` states what the evidence cannot support. Examples: supplied rather than calculated
UMF, chemistry compared across calculation profiles (UMF-05), unreported query oxides,
unrecorded cone or atmosphere, an unverified recipe link, approval with quality notes, and
"Exact firing schedule was not recorded." (no schedule is captured anywhere yet).

### 4.6 Recipe metadata visibility

`includeRecipeMetadata: true` returns `source.recipeId`, `source.recipeCode` and `source.recipeName` with
`metadataVisibility: "recipe"` for every result that has a recipe, to any signed-in account: recipes are
system scoped (decided 2026-10-17, closing PRD §17's open question). Without it, or for a tile with no
recipe, the source is `image_only`.

`recipeName` lets a result be named when its recipe has no lab code, as with most of the imported corpus
("Absence ∆10 · Lab 1 · Without Colorants"); it is null for a blank name. A saved run read back with
`GET /search/{id}` takes the name from the recipe as it is now, so runs saved before the field existed have it too.

`source.baseCode` is the base the recipe builds on (`P01-B1` for `P01-B1-005`), null for a recipe outside the
base register. It is always read from the recipe as it is now. Results open the recipe section (§4.7).

### 4.6.1 What a regular user sees of the match

Meeting 2026-10-29: users see the tiles a search found, each with its score, confidence band, firing and labels, but
not how the match was worked out. For a non-admin, `POST /recipe-image/search` and `GET /recipe-image/search/{id}`
return `scoreBreakdown` with every value null, `query.umf` null, `matchedFields` without the chemistry fields
(`fluxBalance`, `silica`, `alumina`, `boron`, `silicaAluminaRatio`), and `limitations` without the notes about the
chemistry comparison. The assistant's tile search is given the same view. Admins get the full response, and Match check
keeps everything (`SearchVisibility`).

Strictly, nothing else shows a regular user chemistry either: search by UMF (`query.type: "umf"`) is refused with
403 `umf_search_not_available`; `POST /recipes/{id}/calculate` is admin only; `GET /recipes/{id}` and
`/recipes/by-code/{code}` return `chemistry: null`; and the assistant has no search by UMF, no UMF in `get_recipe`, and
is told never to explain how matches are found. The UMF calculator (`POST /umf/calculate`, `GET /umf/limits`,
`GET /glaze-limits`, and the assistant's `calculate_umf`) stays open to everyone: it only works out the chemistry of the
recipe the user types. The materials list (and a material's own analysis) stays open too: it is what a recipe is typed
with. No user-facing response carries another person's username.

### 4.7 Recipe section

Read-only and system scoped, like search: any signed-in account sees every active recipe and every base. Editing
stays on the admin and `/me` routes. Retired recipes (`isActive: false`) return 404 and are left out of lists.

| Route | Returns |
|---|---|
| `GET /api/v2/recipes/{recipeId}` / `GET /api/v2/recipes/by-code/{code}` | one recipe: `materials` (the complete recipe: base + additions for a variation), `additions`, `outcomeNote`, `base` (its formulation and active `variationCount`), `chemistry` (latest stored UMF, null until calculated), `photos` (approved tiles, verified first, with signed `displayUrl`/`thumbnailUrl`, cone, atmosphere, clay body) |
| `GET /api/v2/base-glazes/{code}` | one base: `formulation`, `variationCount`, `derivedFromBaseCode`, `changeNote`, `promotedFromRecipeCode` |
| `GET /api/v2/base-glazes/{code}/recipes` | the base's variations by number, paged, each with `photoCount` and `coverThumbnailUrl` |
| `GET /api/v2/recipes?search=&baseCode=` | every active recipe by lab code (uncoded ones last), paged; `search` matches name or code |
| `GET /api/v2/base-glazes?search=&participantCode=` | every base by participant and number, paged |

Lists take `cursor` and `pageSize` (default 25, max 100) and return `{ items, totalCount, nextCursor }`. Errors:
`recipe_code_not_found` / `not_found` and `base_not_found` (404), and `validation_error` (400) for a `baseCode`
that is not a base code.

## 5. Persistence

`SearchRuns` stores the requester, query type, input hash, optional idempotency key, the full
query snapshot (canonical UMF, canonical text, every version, weights, thresholds, pool size,
and match diagnostics), filters, counts, ranker version, latency, the ranked items without URLs,
and warnings. `GET /search/{id}` re-signs URLs and withholds items whose image is no longer
approved, adding `results_changed_since_search`. Ranks stay as they were at query time
(PRD §3.3: historical results retain their versions).

Idempotency: the same key with the same input returns the stored run. The same key with a
different input returns `422 idempotency_key_reused`.

`SearchFeedback` stores one judgement per user, search, and image; resubmitting updates it.
Feedback is only accepted for images the search returned and only from the search's owner.
It is evaluation data and never changes weights or labels (R2I-08).

## 6. Local development

The in-memory vector index lives in the API process and starts empty, while `IndexRecords`
persist. On startup with that provider the API re-queues index sync for every approved image,
so local search matches the manifest.

With Pinecone (production, [deploy/pinecone.md](../../deploy/pinecone.md)) vectors survive restarts. Each
`IndexRecords` row records which store confirmed it (`VectorProvider`); on startup the API re-sends only the
approved images that have no live record from the configured store. After the switch from the in-memory
index that is every image once; on later starts it is none. `GET /admin/index/status` counts as indexed only
what the current store confirmed, so the backlog shows the re-send draining.

## 7. Verified

Colourants (2026-10-17): unit tests cover the mix arithmetic, the similarity, list filters, and a search in
which 60 tiles of another stain share the query's exact UMF; the 2.5% tile still ranks first, 5% of the same
stain is the first suggestion, then the other stain and the plain base, and a saved search returns the same.
End to end on PostgreSQL 16 with the in-memory index, then again after switching to Pinecone stand-ins
(22- and 256-dimension indexes): all 63 images were re-sent at startup and the search gave the same results.

Colorants (2026-10-27, ranker 1.2.0): unit tests cover the tint arithmetic, stains joining only when both
sides know them, FeO counted as iron, aliases in recipe rows, and data gaps no longer counted as stains. A
search for base + 1.5% red iron oxide ranks the iron tiles first, then the plain base, then copper and
chrome greens of the same base with a lower band; a UMF typed by hand ranks the same way. A before/after run
of 7 searches over a real cone 6 base with 12 colour variations (real materials and calculator) is written up
in the matching report of 27 October 2026.

Unit tests cover the firing table, chemistry comparison, renormalisation, bands, hard filters,
stale and rejected vector exclusion, `no_reliable_match`, idempotency, re-authorisation on read,
feedback rules, recipe queries, and validation. End to end against PostgreSQL 16 and an S3
emulator: a UMF identical to an indexed tile ranks it first at 0.988 "high"; a reduction cone-10
tile ranks low with explicit limitations; strict oxidation firing excludes it and strict
reduction finds it; raw recipes and code-only recipes return typed 422s; the feature flag returns
403 to participants and 200 to admins.

## 8. Not in this slice

- Image queries and image KNN (needs a true image encoder; PRD §11.2 forbids calling a vision
  description an image embedding).
- Offline benchmark (Recall@5, MRR) and admin search metrics. Feedback rows are the input for both.
- The chat tool `search_recipe_images`, which should call this service.
