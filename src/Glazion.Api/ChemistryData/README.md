# Chemistry data

The UMF calculator reads three files from this directory. They are the client's data, copied verbatim
from `Realdiamond/glazion` `packages/chemistry`, so the backend and the apps compute identical results.

| File | Source |
|------|--------|
| `oxides.json` | generated from `src/oxides.ts` (the 31 oxides and their molecular weights) |
| `materials.json` | `src/data/materials.json` (2,830 materials) |
| `glaze-limits.json` | `src/data/glaze-limits.json` (42 target ranges) |

The parity fixtures go to `tests/Glazion.Api.Tests/ChemistryData/fixtures.json` (from `test/fixtures.json`).

Sync them with:

```bash
node tools/sync-chemistry-data.mjs ../glazion
```

Until the files are here, `/api/v2/umf/*`, `/api/v2/materials` and raw-recipe search answer
`503 chemistry_data_unavailable`, and the fixture parity test is reported as skipped.
