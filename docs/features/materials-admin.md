# Materials administration

**Status:** Implemented (2026-10-13), from the 2026-10-10 meeting: materials are picked from a list and
corrected in the admin app, not typed as JSON or fixed in the backend.

The calculator's materials come from `ChemistryData/materials.json`, copied verbatim from the client's
`packages/chemistry` so results match the apps. Administrators can now **correct** a material or **add** one.
Their changes live in the `MaterialOverrides` table and are applied over the file when the API starts and
after every change. The file itself is never edited.

## The dropdown

`GET /api/v2/materials?q=&limit=&offset=` is the list the recipe form picks from (any signed-in user).

- Each material id appears **once**. When the file lists a name twice, the entry the calculator actually uses
  is the one shown. This removed 17 repeat lines from the 2,830 entries.
- Each item has an additive `source`: `file`, `override` (an administrator corrected it) or `custom` (an
  administrator added it).
- `materialsVersion` changes whenever an override changes. It is stored with every saved calculation, so a
  snapshot always names the exact analysis it used.

The id is the name's slug, so `CP#7 Ball Clay` and `CP-7 Ball Clay` are the same material (`cp-7-ball-clay`).

### Search, categories and the common list

```text
GET /api/v2/materials?q=epk                  ranked search over names and aliases
GET /api/v2/materials?common=true&limit=200  the studio short list, grouped by category
GET /api/v2/materials?category=frit&q=31     narrowed to one category
GET /api/v2/materials?withAnalysis=true      hides materials with no oxide analysis
GET /api/v2/materials?pageSize=100&cursor=…  pages; follow nextCursor (offset/limit also work), max 500
```

- **Ranking:** exact name, then prefix, then any word starting with the text, then contains. Common
  materials and materials with an analysis rank higher. One typo per word of four letters or more is forgiven
  ("nephaline" finds Nepheline Syenite).
- **Aliases** come from `ChemistryData/material-catalog.json`. These are the names potters type:
  - EPK → EP Kaolin
  - Whiting → Calcium Carbonate
  - RIO → Iron Oxide Red
  - 3134 → Ferro Frit 3134
  - Shell Pink → Stain 6000

  When only an alias matched, the item carries `matchedAlias` (for example "EPK"). An unresolved alias in a stored
  recipe is suggested back as the real name.
- **New fields on each item:**
  - `category`: silica, clay, feldspar, flux, frit, opacifier, colorant, stain, ash, additive or other
  - `common`
  - `aliases`

  The response lists `categories` with their labels.
- The calculator never reads the catalog, so results still match the apps exactly. A test checks that every
  catalog name exists in `materials.json`.

The common list (74 materials) is in `material-catalog.json`. Edit that file to change it.

## Admin routes (role Admin)

```text
GET    /api/v2/admin/materials/duplicates     materials.json names that share an id, conflicting ones first
GET    /api/v2/admin/materials/overrides      every correction and addition
PUT    /api/v2/admin/materials/{materialId}   { name, oxideAnalysis: { "SiO2": 68.5, ... }, loi?, note? }
DELETE /api/v2/admin/materials/{materialId}   back to the file's analysis (or removes an added material)
```

The `PUT` route is checked as follows:

- The name must slug to `{materialId}`, so a correction can't land on another material.
- Oxide symbols must be ones the calculator knows. Case is fixed for you (`b2o3` becomes `B2O3`).
- Each value must be 0–100, and the total at most 105 (rounding allowance).

When `{materialId}` is in the file, every file entry with that id takes the new analysis. Their names are kept,
so recipes written with either spelling still resolve. When it is not in the file, it becomes a new material.

## Duplicates in materials.json

13 ids appear more than once in the file. Today the **later** entry silently wins in the arithmetic.

| Id | Names in the file | Analyses |
|----|-------------------|----------|
| `ceraflux` | Ceraflux ×2 | **differ** |
| `cp-7-ball-clay` | CP-7 Ball Clay, CP#7 Ball Clay | **differ** |
| `hommel-frit-71` | Hommel Frit 71 ×2 | **differ** |
| `lepidolite` | Lepidolite ×2 | **differ** |
| `no-1-ball-clay` | No. 1 Ball Clay ×2 | **differ** |
| `ochre` | Ochre ×2 | **differ** |
| `clay-220` | Clay 220 ×2 | identical |
| `new-record` | New Record ×3 | identical |
| `red-stains` | Red Stains ×2 | identical |
| `stain-blue-` | Stain (blue) ×2 | identical |
| `stain-grey-` | Stain (grey) ×3 | identical |
| `stain-yellow-` | Stain (yellow) ×4 | identical |
| `stain-6339` | Stain 6339 ×2 | identical |

The identical ones need nothing: the dropdown now shows each of them once.

**The six that differ need a decision from someone who knows the materials (Toluwalope).** `GET
…/duplicates` shows each entry's analysis and which one is used now. Settle each one with `PUT` on its id,
giving the correct analysis and a `note` saying where it came from. The group then reports
`resolvedByOverride: true`.

## Things to know

- **Hashes move with the analysis.** Changing any material changes `materialsVersion`, so the next
  `POST /recipes/{id}/calculate` stores a new snapshot even for recipes that don't use that material. The same
  thing happens when `materials.json` is updated. Old snapshots are kept, and images move to the new one.
- **One process.** Overrides reload in the process that saved them and at startup. The API runs as one
  replica on Railway. Running more than one would need a reload signal (or a short cache expiry) before
  they all agree.
