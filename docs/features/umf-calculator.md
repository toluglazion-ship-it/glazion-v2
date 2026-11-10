# UMF calculator (backend port)

**Status:** Implemented and verified: all 40 client fixtures match to 10 decimal places against the client's 2,830-material list.
**Source:** `Realdiamond/glazion` `packages/chemistry` (glazion-main.zip, 2026-10-04), and "Glazion UMF Calculator: How It Works".
**Spec:** PRD §5.5 (RECIPE-04, UMF-01…05), §10 Recipes/UMF.

The calculator now lives in the backend, so the apps, search, and indexing share one implementation.
It is a **line-for-line port of today's agreed client behaviour**, including the known issues, so
results match the browser to 10 decimal places during the switch. Fixes come afterwards as
separate, versioned changes (§6).

---

## 1. What was ported

| Client | Backend |
|--------|---------|
| `calculateRecipe(rows, materials, cone)` in `recipe.ts` | `GlazeChemistry.CalculateRecipe` |
| `computeChemistry` in `chemistry.ts` | `GlazeChemistry.ComputeChemistry` |
| `DEFAULT_FLUX_SET` | `GlazeChemistry.FluxSet`: K2O, Na2O, Li2O, CaO, MgO, BaO, SrO, ZnO |
| `estimateCTE` coefficients | `GlazeChemistry.CteCoefficients` (15 oxides, applied to UMF values) |
| `UMF_GROUPS` | `GlazeChemistry.UmfGroups` |
| `limitsForCone`, `suggestOptimisations` | `LimitsForCone`, `SuggestOptimisations` |
| `clayBodyNote` | `ClayBodyNote`, verbatim (known issue: crazing and shivering reversed), plus `CteBand` (`low`, `medium`, `high`) |
| `oxides.ts`, `materials.json`, `glaze-limits.json` | Read verbatim from `src/Glazion.Api/ChemistryData/` |

Rules carried over exactly:

- Material ids are the name trimmed, lower-cased, with every run of other characters turned into `-`. Empty names are dropped.
- Rows: skip a row with no material or an empty amount; find the material by name ignoring case and surrounding spaces, first match in list order; read the amount with JavaScript `parseFloat` semantics (`"12g"` is 12); skip non-finite amounts. Negative amounts are not rejected.
- The arithmetic looks the material up by id, and the later of two entries with the same id wins.
- Scale to 100; per line and oxide, `grams = p × % / 100` and `moles += grams / MW`; LOI is not used.
- Unknown oxide symbols are skipped with `Unknown oxide {symbol} in material {name}`. An analysis summing over 100 + 1e-6 raises `Material {name} oxide sum > 100%` and is still used. Zero flux leaves the UMF empty and raises `UMF undefined: flux sum is zero. Add flux oxides.` Warnings keep order and repeats.
- `ZERO` is 1e-12. Maps keep first-appearance order and include oxides listed at 0 %. Missing ratios are left out of the JSON. Field names are the client's (`moles`, `molePct`, `analysisPct`, `umf`, `fluxSum`, `ratios.si_al`, `ratios.alkali_over_alkaline`, `ratios.b2o3_pct`, `cte`, `warnings`).
- The result is `null` when the list is empty, no row survives, or the amounts add up to zero or less.

The document's Leach 4321 worked example is a unit test using the analyses it lists, and it reproduces
every published figure: fluxSum 0.26389, SiO2 3.923, Al2O3 0.402, CaO 0.758, Si:Al 9.756, alkali over
alkaline earth 0.306, B2O3 0 %, CTE 20.10, and 89.57 g of oxides.

### Details taken from the client source

The document left a few points open; `chemistry.ts` and `recipe.ts` settle them, and the port follows the code.

| Point | Client code | Why it matters |
|-------|-------------|----------------|
| Oxide grams | `partsPct * (pct / 100)` | Floating-point operation order changes the 10th decimal |
| Mole % and analysis % | `(100 * n) / total` | Same |
| CTE | walks its coefficient table in declaration order | Summation order |
| Material names | trimmed in `normaliseMaterials`, so warnings show the trimmed name | Exact warning strings |
| Slug | `name.toLowerCase().replace(/[^a-z0-9]+/g, '-')` on the trimmed name, leading/trailing `-` kept | Duplicate-id behaviour |
| Warning order per line | oxide-sum check first, then unknown symbols in analysis order | Exact warning list |
| Oxide sum | every value in the analysis, known and unknown symbols | Which materials warn |
| Suggestions | every material in list order, stable sort, `{ oxide, current, target, direction, materials: [{ name, oxideContent, inRecipe }] }` | Same shape as `OptimisationSuggestion` |
| Display groups | keys `r2o`, `ro`, `r2o3`, `ro2`; entries `{ oxide, value }` | Same shape as `UMF_GROUPS` / `presentOxides` |
| Clay body note | the three sentences verbatim, returned as `clayBodyNote` with `cteBand` | Same text as the apps |

## 2. Versions

Every result names the data and code that produced it (UMF-03).

| Field | Value |
|-------|-------|
| `calculationProfile` | `glazion-umf-v1` (the 8-oxide flux set above) |
| `calculationVersion` | `glazion-chemistry-1.0.0` |
| `oxidesVersion`, `materialsVersion`, `limitsVersion` | `oxides-sha256:…`, `materials-sha256:…`, `limits-sha256:…`, the first 12 hex of each file's SHA-256 |

## 3. Endpoints

```text
POST /api/v2/umf/calculate                 { rows: [{ materialId | material, amount }], cone? }   stateless, nothing stored
GET  /api/v2/glaze-limits?cone=6           target ranges ("06" or -6 both mean cone 06); also /umf/limits
GET  /api/v2/materials?q=&limit=&offset=   the materials list with analyses
POST /api/v2/recipes/{recipeId}/calculate  calculate a stored recipe and keep the snapshot
POST /api/v2/recipes/by-code/{code}/calculate   same, by lab code (A03-B2-009)
```

### The calculate contract (agreed with the frontend, 2026-10-14)

`POST /umf/calculate` follows the frontend's handoff (`glazion-umf-handoff/API.md`, Realdiamond/glazion
`c28ff2d`) field for field:

```text
{ materialsVersion, skippedRows: [{ index, reason, material }], result: null | {
    lines, moles, molePct, analysisPct, umf, fluxSum, warnings, ratios, cte, suggestions, clayBodyNote } }
```

- **Rows.** A row is `{ materialId, amount }` from the dropdown, or `{ material, amount }` by name for older
  recipes. `materialId` wins when both are given. It resolves to the name of the entry the calculator uses for
  that id, then takes the same name lookup (case and surrounding spaces ignored, first match in file order, no
  aliases). `amount` is a number or the typed text (JavaScript `parseFloat`: `"40g"` is 40).
- **`result`** is `null` when nothing can be calculated (still a 200).
  - `suggestions` are empty without a cone, or for a cone with no target ranges. Only 06, 6, 9 and 10 have them.
- **`skippedRows`** lists each left-out row by its index in the request. Reasons, in the apps' wording:
  `no material`, `no amount`, `unknown material`, `amount is not a number`.
- **`materialsVersion`** is the value stored with snapshots: `materials-sha256:` plus the first 12 hex digits
  of the file's SHA-256, with `+overrides-sha256:…` appended when administrators have corrected materials.
  The handoff shows the bare hash. Treat it as an opaque string.
- **Additive fields:** `cone` (normalised), `limits` (that cone's target ranges, which the apps draw next to
  the UMF), `displayGroups`, `cteBand` and `versions`.
- **Errors:** 400 problem details when `rows` is not an array, and 401 when not signed in.

`GET /materials` returns items in the client's `Material` shape: `{ id, name, type: "raw", loi_pct, oxideAnalysis }`,
plus `hasAnalysis` and the dropdown fields (see `materials-admin.md`).

### Storing chemistry is stricter than calculating it

A stored snapshot feeds indexing and search, so `POST /recipes/{id}/calculate` refuses anything the
interactive calculator would quietly skip (UMF-02, R2I-02):

- every row must name a material in the list and carry a finite amount, otherwise `422 materials_unresolved` with each row's status and up to five name suggestions;
- rows in different units are `422 mixed_units`, because the calculator reads amounts as plain numbers;
- zero flux is `422 zero_flux`, and a recipe without rows is `422 recipe_has_no_materials`.

Rows come from the recipe's `RecipeJson`: `full` for lab-coded variations (base plus additions, so
colourants are scaled with the rest, as the apps do), otherwise `materials` or `rows`. A recipe exported by
the apps' `recipeToJson` (`materials: [{ material, amount, group }]`) calculates as is. Only an admin
or the recipe's owner can calculate it.

The snapshot is immutable and idempotent on an input hash over the versions and the scaled lines.
It stores `moles`, `analysisPct`, `umf`, `fluxSum`, the ratios with CTE and band, display groups with
`molePct`, warnings, and the rows and lines it came from. Images linked to the recipe that were
waiting for chemistry, or pinned to an older snapshot of it, are re-pinned and re-indexed. That is
what makes a lab-coded variation such as `A03-B2-009` searchable by its code.

## 4. Search integration

- `query.type: "raw_recipe"` now runs this calculator. The same strict resolution applies: an unknown material is a `422 materials_unresolved` listing every row, never a dropped row.
- The UMF the apps produce can contain oxides the search vector (`oxide-vector-v1`, 22 oxides) has no slot for, such as FeO, V2O5, CeO2 or MoO3. Those are now dropped with a warning `oxide_outside_vector_schema:FeO` instead of failing with `400 unknown_oxide`. A symbol neither side knows is still an error. A wider vector schema is the proper fix; it means a new `oxide-vector-v2` namespace and a re-index.
- `oxideWeightPercent` in the search request is the client's `analysisPct`, as the document states.
- The apps send `calculationProfile: "flux-to-1"`, their name for this calculation. Search treats it as `glazion-umf-v1`, so results are not flagged as compared across profiles.

## 5. Data and parity

The client's data is committed in `src/Glazion.Api/ChemistryData/` (`oxides.json`, `materials.json`,
`glaze-limits.json`, with provenance in `SOURCE.txt`) and the fixtures in
`tests/Glazion.Api.Tests/ChemistryData/fixtures.json`. `materials.json` and `fixtures.json` are byte-identical
copies; `oxides.json` holds the 31 weights from the `OXIDES` export of `oxides.ts`.

`ClientFixtureParityTests` performs the same checks as `test/chemistry.test.ts`: identical oxide sets in every
map and values to 1e-10, `fluxSum` and `cte` to 1e-10, identical warning lists, ratio presence and values,
2,830 materials, and 40 fixtures. It passes.

**Golden cases.** `GoldenUmfTests` runs the frontend's 1,072 golden recipes
(`tests/Glazion.Api.Tests/ChemistryData/golden-cases.json`) through `POST /umf/calculate`'s service. They cover:

| Group | Recipes |
|-------|--------:|
| Base glazes | 24 |
| Base + colourant variations | 672 |
| Random recipes | 300 |
| Edge cases | 76 |

It serialises each response as the API does and compares it the way their `golden/verify.mjs` does:
- numbers within 1e-9 relative;
- lists in order;
- oxide maps with the same oxides.

All 1,072 match. Their `verify.mjs` run over HTTP against this API also reports
`1072 of 1072 cases match`. Run golden cases with no material overrides in place, because an override changes
the numbers for recipes that use that material.

`materials.json` and `glaze-limits.json` must stay byte-identical to the apps' files, since `materialsVersion` is
a hash of the bytes. `.gitattributes` pins `ChemistryData/**` to LF, so a Windows checkout cannot change them.

When the client changes its data, re-sync and re-run:

```bash
node tools/sync-chemistry-data.mjs ../glazion    # path to a Realdiamond/glazion checkout
dotnet test
```

The script refuses to write an oxide table it cannot verify (31 oxides, SiO2 = 60.0843, and identical tables
when `oxides.ts` exports more than one). A data change produces new `materialsVersion` / `oxidesVersion`
hashes, so new snapshots are distinguishable from old ones.

## 6. Known issues, unchanged on purpose

Today's behaviour is ported as is, per the document. Each fix needs the decision shown and a new
`calculationVersion`, which forces new snapshots and a re-index of affected images.

| Issue | Decision |
|-------|----------|
| PbO is not in the flux set (lead glazes get no UMF) | Client |
| FeO, MnO, CoO, NiO, CuO are not in the flux set but display in RO | Client |
| CTE coefficients are applied to UMF on the wrong scale (Leach 4321 shows 20.1, not about 64) | Client |
| Clay body note has crazing and shivering reversed | Client |
| Duplicate names: lookup by first name, arithmetic by last id | Data |
| Skipped symbols: KNaO, ZrO, MnO2, F | Data |
| About 15 analyses on a fired basis, overstated without the LOI step | Data |
| Chrome Oxide 131.58 % Cr2O3, Ceramic Rutile 21 % K2O, 845 materials without analysis | Data |

Data fixes must keep material names, so saved recipes and lab-coded rows still resolve.
