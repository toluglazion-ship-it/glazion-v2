# Lab code naming system

**Status:** Implemented
**Format:** `{Group}{Participant}-B{Base}-{Recipe}` → `A03-B2-009`

| Segment | Meaning | Rule |
|---------|---------|------|
| `A03` | Group A, participant 03 | One letter + two digits. Assigned once per account by an admin. Unique across the system. |
| `B2` | Participant 03's second registered base glaze | Integer from 1 to 999, no padding. Issued by the API in order. Never reused. |
| `009` | Ninth variation under that base | Three digits up to 999 (`001`…`999`), then four (`1000`…`2000`), so codes already written on tiles never change. Issued by the API in order. Never reused, including for failed tests. |

The code identifies a record; it does not try to describe the recipe. The database holds the
complete formulation: the base-glaze register entry plus the variation's additions.

```
A03-B1   Original base glaze                 A03-B1-001  B1 + 0.1% cobalt carbonate
A03-B2   Original base + 2% rutile           A03-B2-009  B2 + 2% copper + 4% iron
A04-B1   Participant 04's own Base 1         (B numbers are per participant, so A03-B1 ≠ A04-B1)
```

## How each rule is enforced

| Rule | Enforcement |
|------|-------------|
| Never reuse a recipe number | Numbers are `max(existing) + 1` under a unique index on `(BaseGlazeId, RecipeNumber)`. Recipes are never deleted (only `IsActive = false` or an `OutcomeNote`), so the maximum never goes down. A race on the same number fails the unique index and retries with the next. |
| Never change a base formulation once it is in use | `PATCH /admin/base-glazes/{code}` corrects a base's formulation (and firing) until a tile of one of its variations has been checked and approved by an admin, or photographed, or is in the library (meeting 2026-11-02: the admin keeps full editing until their review). Every variation's `full` rows are then rebuilt from the new base plus its own additions, and a stored UMF is recalculated; participants who already pressed Start see `recipeChangedAt` on their task. After that it answers `409 base_in_use` naming each variation in use and why ("A03-B1-003 has been checked and approved"), and a modified base is registered as a new base with `derivedFromBaseCode` and a required `changeNote` ("Original base + 2% rutile"). So existing photos keep the recipe they were made with. Name, description and change note can always be corrected. `GET /admin/base-glazes/{code}` says which applies in `formulationEditable`, and why in `formulationLockedBecause`. A variation's own additions (its colorants) change the same way: `PATCH /admin/recipes/{id}` `{ additions }`, with `mixEditable`/`mixLockedBecause` on `GET /admin/recipes/{id}`. |
| Every tile carries its firing | A base records `cone` ("6", "06" for cone 06; "Cone 6" and "∆6" are accepted) and `atmosphere` (`oxidation`, `reduction`, `neutral`). Its variations follow it unless one sets its own (`firingCone`/`firingAtmosphere` on a recipe are what its tiles are fired to). The firing locks with the materials once a variation is in use (`409 base_in_use`, `recipe_in_use`). Participants see it on every task, and a photo of the tile is stored with it (meeting 2026-10-29). |
| Deleting keeps the number | `DELETE /admin/recipes/{id}` and `DELETE /admin/base-glazes/{code}` (a base with all its variations) take them out of every list and cancel their open tasks; `POST …/restore` brings them back with those tasks. The number stays taken, so a code written on a tile never comes to mean another glaze. Refused (`409 recipe_in_use` / `base_in_use`) once a tile is checked and approved, or photographed. `?deleted=true` on the admin lists shows what can be restored (meeting 2026-11-02). |
| Three-digit recipe numbers, four past 999 | `LabCode.Recipe()` formats `000`, which gives four digits from 1000; parsing takes three digits or four without a leading zero (`A03-B2-0009` is not a code). |
| Maintain a base-glaze register | `GET /api/v2/admin/base-glazes?participantCode=A03` (and `GET /api/v2/me/base-glazes`) lists every base with its formulation, parent, change note, and variation count. |
| Record the complete recipe separately | `Recipes.RecipeJson` stores `{ baseCode, baseFormulation, additions, full }`; `AdditionsJson` keeps the delta alone. |
| Use the same code everywhere | The code appears on the recipe, the upload (`recipeCode` at capture), the image asset, the import record, vector metadata (`recipeCode`), and the download filename of signed image URLs (`A03-B2-009.jpg`). R2 object keys stay UUID-based by spec; the code is metadata, not the storage address. |
| Participant codes disambiguate bases | `BaseGlazes.Code` is `{ParticipantCode}-B{n}` with a unique index; `ParticipantCode` is snapshotted on the base so it cannot drift. A participant code cannot be changed once that participant has registered a base. |
| 2,000 variations per base, 999 bases per participant | `RegisterVariationAsync` rejects the 2,001st with `409 base_full` and tells the user to register a new base. A batch that would pass 2,000 is refused whole, with the room left. A participant's 1,000th base is refused with `409 participant_full` (meeting 2026-10-29). |
| A variation someone builds on becomes a base | `POST …/recipes/by-code/{code}/promote` registers the next base for that participant with the variation's full formulation (base + additions), `derivedFromBaseCode` = the variation's base, and a generated `changeNote` (`Promoted from A03-B1-004: A03-B1 + 3% Zircopax`). The base records `promotedFromRecipeCode`; a unique index lets each variation be promoted once (`409 already_promoted` names the base). |
| Codes stay three parts | Promotion issues a new `B` number instead of lengthening the code, so a line of descent never reads `P1-B2-V3-D4` (meeting 2026-10-10). The family tree is in the register: `derivedFromBaseCode` and `promotedFromRecipeCode` on each base. |

## API

Admin (role Admin):

```text
POST  /api/v2/admin/users                       { ..., participantCode? }
PATCH /api/v2/admin/users/{userId}               { participantCode?, displayName? }   (status: PATCH …/{userId}/status)
POST  /api/v2/admin/users/{userId}/password      { password }   sets a forgotten password; signs the user out everywhere
POST  /api/v2/admin/base-glazes                  { participantCode | userId, name, description?, formulation: [{rawName, amount, unit}], derivedFromBaseCode?, changeNote?, cone?, atmosphere? }
GET   /api/v2/admin/base-glazes?participantCode= register
GET   /api/v2/admin/base-glazes/{code}           one base with its variations, formulationEditable and formulationLockedBecause
PATCH /api/v2/admin/base-glazes/{code}           { name?, description?, formulation?, changeNote?, cone?, atmosphere? } → { base, variationsRebuilt, recalculated, recalculationFailed: [{ recipeCode, code, message }] }
POST  /api/v2/admin/base-glazes/{code}/recipes   { name, description?, additions: [{rawName, amount, unit}], cone?, atmosphere? } → next code (cone/atmosphere only when it fires differently from its base)
POST  /api/v2/admin/base-glazes/{code}/recipes/batch  { variations: [{ name, description?, additions }], captureCodeId? } → codes in order (max 100; all or none)
POST  /api/v2/admin/recipes/by-code/{code}/promote    { name?, description?, participantCode? } → new base (default: the variation owner's register)
GET   /api/v2/admin/recipes/by-code/{code}
PATCH /api/v2/admin/recipes/by-code/{code}       { outcomeNote?, isActive?, cone?, atmosphere? }   (never the number; its own firing until it is in use, "" to follow its base)
GET   /api/v2/admin/recipes/{id}                 with mixEditable and mixLockedBecause
PATCH /api/v2/admin/recipes/{id}                 { name?, description?, isActive?, cone?, atmosphere?, additions? }   (additions and firing until a tile is approved)
DELETE /api/v2/admin/recipes/{id}                POST …/{id}/restore
DELETE /api/v2/admin/base-glazes/{code}          POST …/{code}/restore   (with its variations)
```

Participant (own records only):

```text
GET   /api/v2/me/base-glazes
POST  /api/v2/me/base-glazes
GET   /api/v2/me/base-glazes/{code}
POST  /api/v2/me/base-glazes/{code}/recipes
POST  /api/v2/me/base-glazes/{code}/recipes/batch       (no captureCodeId: only admins assign)
GET   /api/v2/me/recipes/by-code/{code}
POST  /api/v2/me/recipes/by-code/{code}/promote         own or assigned variation → new base in the caller's register
```

Where the code is consumed:

```text
POST /api/v2/uploads                       { ..., recipeCode? }   must be the user's own variation, or a recipe assigned to the capture code
POST /api/v2/admin/imports/{id}/records    rows may give recipeCode instead of recipeId
```

Units accepted in material rows: `%`, `g`, `kg`, `oz`, `lb` (matching the retrieval plan's `recipe_materials.unit`).

## Worked example

```
POST /admin/users               { username: "potter3", participantCode: "A03", ... }
POST /admin/base-glazes         { participantCode: "A03", name: "Original base", formulation: [...] }         → A03-B1
POST /admin/base-glazes/A03-B1/recipes { name: "0.1% cobalt", additions: [{ rawName: "Cobalt carbonate", amount: 0.1, unit: "%" }] } → A03-B1-001
POST /admin/base-glazes         { participantCode: "A03", name: "Base + rutile", formulation: [...], derivedFromBaseCode: "A03-B1", changeNote: "Original base + 2% rutile" } → A03-B2
POST /admin/base-glazes/A03-B2/recipes × 9                                                                       → A03-B2-001 … A03-B2-009
POST /uploads                   { captureCodeId, recipeCode: "A03-B2-009", fileName: "IMG_0042.jpg", ... }
```

Building on a result (the zircon example from the 2026-10-10 meeting):

```
POST /admin/base-glazes/A03-B1/recipes/batch { variations: [3%, 6%, 9% Zircopax] }        → A03-B1-001 … 003
POST /admin/recipes/by-code/A03-B1-001/promote                                           → A03-B3 = B1 + 3% Zircopax
POST /admin/base-glazes/A03-B3/recipes/batch { variations: [1%…10% iron oxide] }          → A03-B3-001 … 010
```

The tile is labelled `A03-B2-009`; the photograph downloads as `A03-B2-009.jpg`; the search index
carries `recipeCode = "A03-B2-009"`; the recipe row holds the complete formulation.
