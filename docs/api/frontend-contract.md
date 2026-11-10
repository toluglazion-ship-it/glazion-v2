# Frontend alignment: Realdiamond/glazion vs this backend

**Checked:** 2026-10-04, against `glazion-main.zip` (`packages/types`, `packages/api-client`, `docs/api-notes.md`).

The apps were built against a "Glazion v2 API schema" (`schema.json`, 30 Sept 2026) and a deployed API on
Railway. This backend now serves that contract: every endpoint is under `/api/v2`, and the core auth, account,
capture-code, upload and admin endpoints take and return the shapes in `@glazion/types`. The `/api/v1` routes
are gone.

**How this was checked.** `packages/api-client` itself (not a copy of it) was bundled with a stub platform
file and run against this API on PostgreSQL and an S3 emulator. It signs in to the admin and capture apps,
creates a user, recipes and a capture code, assigns recipes, loads the capture context, creates an upload,
PUTs the file to the presigned URL with the returned headers, completes, refreshes an expired access token
through the client's own 401 handler, reviews, approves to `Indexed`, rejects, revokes, suspends, and runs a
search. Error messages shown to users come from problem-details `detail`, as `messageFor` expects.

## 1. Core contract

| Area | Behaviour |
|------|-----------|
| Enums | `AccountStatus` (0–3), `UploadStatus` (0–10) and `UserRole` (Admin 0, Standard 1) travel as **numbers**, with the client's values. Operational enums that the apps do not use (import, job, quality, approval states) still travel by name. |
| Paging | `PagedResult<T>` `{ items, totalCount, nextCursor }` with `cursor` (opaque) and `pageSize` (default 25, max 100 unless noted): admin users, recipes, capture codes, uploads, `GET /uploads`, conversations, `GET /me/recipes`, `GET /me/base-glazes`, admin imports, import records (max 500), admin images (max 200), jobs (max 500), material overrides and duplicates. Two lists the admin app reads as arrays stay arrays unless `cursor` or `pageSize` is sent: `GET /admin/base-glazes` and `GET /admin/capture-codes/{id}/recipes`. `GET /materials` keeps `{ total, items }` and adds `nextCursor`. |
| Errors | RFC 7807 everywhere, `application/problem+json`, with `detail` (always a sentence the apps can show), `title`, `status`, a stable `code`, `traceId`, and `errors` for field validation. |
| Sign-in | `POST /auth/login` → `LoginResult`. Pending accounts get tokens so the apps can read `accountStatus`; Suspended → 403 `account_suspended`, Disabled → 403 `account_disabled`, wrong password → 401 `invalid_credentials`. 20 attempts a minute per IP (`RateLimits:CredentialsPerMinute`), then 429. |
| Tokens | Opaque random tokens, stored as SHA-256. Access token 60 min (`expiresInSeconds` = 3600), refresh token 30 days. `POST /auth/refresh` rotates: the old pair stops working. Presenting a refresh token that was already exchanged revokes every session of that user. `POST /auth/logout` ends the current session only. |
| Password reset | No self-service reset yet (the backend sends no email). An admin sets a new password with `POST /admin/users/{id}/password` `{ password }` (8–256 characters) → `{ sessionsEnded }`; the user is signed out everywhere and signs in with the new password. |
| Opt-in paging | `GET /admin/base-glazes` and `GET /admin/capture-codes/{id}/recipes` return a plain array without `cursor`/`pageSize`, and `PagedResult` with either. Swagger shows both shapes. |
| Account status | Every endpoint except `GET /me` and `POST /auth/logout` requires an Active account; a Pending account gets 403 `account_pending` with a sentence saying it is waiting for approval (403, not 401, so the client does not loop on refresh). `PATCH /admin/users/{id}/status` to Suspended or Disabled signs the user out everywhere. Admins cannot change their own status. |
| Registration | `POST /auth/register` → 201 `RegisterResult` with `accountStatus: 0` (Pending). 409 `username_taken` / `email_taken`. Same rate limit as sign-in. |
| Capture codes | `GLZ-{LABELPREFIX}-XXXX-XXXX` from an unambiguous alphabet, cryptographically random. Only a SHA-256 hash and the last four characters are stored. `plainCode` is returned once, by `POST /admin/capture-codes`; lists and the capture context show `maskedSuffix`. Existing codes were hashed by the migration. |
| Code recipes | `POST /admin/capture-codes/{id}/recipes` **adds** (the admin UI's assumption); repeating an id is harmless. Additive: `GET /admin/capture-codes/{id}/recipes`, `DELETE /admin/capture-codes/{id}/recipes/{recipeId}`, and `recipeCount` on `CaptureCodeDto`. |
| Revocation | `POST /admin/capture-codes/{id}/revoke` is idempotent. A revoked or expired code drops out of `activeOnly`, stops accepting uploads, and `GET /me/capture-context` returns 404 `no_capture_code` when no usable code is left. |
| Uploads | `contentType` may be empty (a `Blob` can have no type): it is then taken from the file name. `fileName` is optional. `POST /complete` and `/cancel` take no body; complete is idempotent. `recipeId` (or a lab `recipeCode`) may be declared at create. `downloadUrl` is set whenever the original is in storage, including Rejected and Indexed. |
| Upload states | `AwaitingUpload` → `Uploaded` → `Validating` → `NeedsReview` → `Approved` → `Indexed` once the image's vectors are live. `Rejected` keeps the reviewer's reason in `reviewNote`; `failureReason` is for processing failures only. `Indexing` (7) is defined but not used: an approved upload moves straight to `Indexed` when its index sync succeeds. |
| Review | `POST /admin/uploads/{id}/approve` takes `{ note? }` or no body (additive `recipeId` attaches and verifies a recipe). `reject` takes `{ reason }`. Both set `reviewedAt`. |
| Overview | `OverviewDto` exactly. `pendingReview` counts Uploaded, Validating and NeedsReview. |

Fields the apps do not know yet are additive and safe to ignore: `email` and `participantCode` on users,
`code`, `baseCode`, `recipeNumber`, `additionsJson`, `outcomeNote` on recipes (lab codes), `recipeId`,
`recipeName`, `recipeCode`, `imageId`, `nextAction` and `thumbnailUrl` (a 400 px WebP, set once validation has run) on uploads, `qualityStatus` and `thumbnailUrl` on admin uploads. Lists can show `thumbnailUrl` instead of loading full-size originals from `downloadUrl`. `recipeJson`
is `null` when no structured recipe is attached, which is what the admin app's "—" column checks.

## 2. What the apps need to change

| Change | Why |
|--------|-----|
| Point `VITE_API_URL` (web) and the mobile equivalent at this API. | The default in `packages/api-client` is the Railway URL. |
| Set `SAMPLE_DATA.search = false` in `packages/api-client/src/main.ts`. | Search is live and matches `main.ts`. |
| Set `SAMPLE_DATA.account = false` only together with a forgot-password decision. | Registration is live, but the same flag routes `POST /auth/forgot-password`, which is not implemented (see §3). |
| Set `SAMPLE_DATA.chat = false` once chat is configured on the server. | Chat is live in the `chatApi` shapes; it answers once `Chat__*` is set (see [`chat.md`](../features/chat.md)). |

## 3. Remaining gaps

| Gap | Detail |
|-----|--------|
| Forgot password | `POST /auth/forgot-password` is not implemented. It needs an email provider; nothing here sends email. |
| Instruction versions, default tile variants | `CreateCodeRequest.instructionVersionId` / `defaultTileVariantId` are refused with 400 `not_supported` when set. The admin app sends neither. |
| Upload limits | 15 MiB; JPEG, PNG and WebP. The Capture app allows any image type up to 25 MB, so HEIC photos and files over 15 MiB are refused with 400 `unsupported_content_type` or `validation_error`. Raise `Uploads:MaxBytes` if 25 MB is wanted; HEIC needs a conversion step before validation can read it. |
| `downloadUrl` lifetime | 10 minutes (`R2:PresignedReadMinutes`). Lists left open show broken thumbnails after that; refetch on focus. |
| Upload search filters | `GET /admin/uploads` filters by `status` and `ownerUserId` as the types define. Admin users and recipes also accept additive `search`, `status` and `activeOnly` filters the apps do not send yet. |

## 4a. Search by materials (2026-10-17)

The main app's search sends the recipe's materials: `query: { type: "raw_recipe", materials: [{ rawName | materialId, amount, unit }] }`.
Additive response fields: `query.umf` (the calculated UMF, to show the breakdown), `query.colourants`
(`[{ materialId, name, percentOfBase }]`: stains and other materials without an oxide analysis),
`closestMatch` (= `items[0]`) and `similar` (one image per other recipe, best first, to show as "similar
glazes"), and `scoreBreakdown.colourantSimilarity`. A warning `no_oxide_analysis:<name>` names each material
the UMF leaves out. Details: [`recipe-to-image/04-search.md`](../architecture/recipe-to-image/04-search.md) §2.1 and §3.

## 4. Additions from the 2026-10-10 meeting (2026-10-13)

All additive; existing calls behave as before.

| Admin screen need | API |
|-------------------|-----|
| Material dropdown instead of JSON | `GET /materials?q=`: ranked, alias-aware ("EPK", "whiting"), one typo forgiven, each material once. Items carry `category`, `common`, `aliases`, `matchedAlias`, `source`, `hasAnalysis`. `?common=true` with no q gives the studio short list grouped by category for an empty box. See [`materials-admin.md`](../features/materials-admin.md). Build `formulation` / `additions` rows `{ rawName, amount, unit }` from the picked `name`. |
| "Recipes → new" should create a base glaze | Use `POST /admin/base-glazes` (needs a participant). `POST /admin/recipes` still makes an uncoded recipe, which never appears under Base Glazes. |
| Add and tweak variations from Recipes and Base Glazes | `POST /admin/base-glazes/{code}/recipes` (one) or `…/recipes/batch` `{ variations: [...], captureCodeId? }`. Up to 100 at once, numbered in order, all or none. With `captureCodeId` they are assigned to the tester straight away. |
| A variation becomes a new base | `POST /admin/recipes/by-code/{code}/promote` `{ name?, description?, participantCode? }` → `BaseGlazeResponse` with additive `promotedFromRecipeCode`. Participants: `POST /me/recipes/by-code/{code}/promote`. 409 `already_promoted` if done before. |
| Edit materials from the frontend | `GET /admin/materials/duplicates`, `GET /admin/materials/overrides`, `PUT /admin/materials/{id}`, `DELETE /admin/materials/{id}`. See [`materials-admin.md`](../features/materials-admin.md). |
| Clay body at capture | `CreateUploadRequest.clayBody`: `porcelain`, `stoneware` or `red_body` ("Red Body", "red-body" and the apps' older `red` are accepted and stored as `red_body`; anything else is 400). The same rule applies to search `filters.clayBodies` and the admin image filter. Optional for now; `UploadDto`, `AdminUploadDto` and image responses carry `clayBody`. Import manifest rows accept `clayBody`. |
| Clay body on images already loaded | `POST /admin/images/clay-body` `{ clayBody, importBatchId \| imageIds \| participantCode, onlyIfMissing?, dryRun? }`; `GET /admin/images/sources` shows each import batch and participant with how many images already have a clay body. |
| Clay body compulsory at capture | With `Ingestion__RequireClayBodyAtCapture=true` the API refuses `POST /uploads` without `clayBody` (400, field `clayBody`). The Capture app must send it before this is switched on. |
| Search by clay body | `filters.clayBodies: ["stoneware"]` is a hard filter. Results carry `tile.clayBody`; a tile with none says so in `limitations`. |

The capture app should make clay body a required choice. The API accepts uploads without it, so older app
builds keep working.

## 5. Answers to `docs/api-notes.md`

| Question | Answer |
|----------|--------|
| Upload has no recipe (blocking 1) | Solved: `CreateUploadRequest` accepts `recipeId` or a lab `recipeCode`. The recipe must be assigned to the capture code or be the participant's own variation. If the code has exactly one recipe and none is declared, the image is linked to that one. |
| Capture code recipes (blocking 2) | `GET /me/capture-context` lists them; admins also have `GET` and `DELETE` per code. Assigning adds. |
| No capture code | 404 with code `no_capture_code`. |
| Refresh tokens | Yes, rotating; see §1. |
| Checksum | Hex SHA-256, optional on create and complete. |
| Low-fire cones in search | Send negatives (-6 for 06, -5.5 for 05.5). Accepted. |
| Extra oxides in search | Send them. FeO, V2O5 and the other calculator oxides are dropped with a warning `oxide_outside_vector_schema:<oxide>`; nothing fails. |
| Problem-details `code` | On every error. |

## 6. Migration of existing data

`CoreV2Contract` keeps existing rows: `Users.IsActive` becomes `Status` (inactive → Disabled); plain capture
codes are replaced by their hash and last four characters (an inactive code gets `RevokedAt`); a rejected
upload's reason moves from `FailureReason` to `ReviewNote`; approved uploads whose image already has live
vectors become `Indexed`. Old sessions keep working until their access token expires; they have no refresh
token, so those users sign in again after that. Rolling back cannot restore plain codes: reissue them.
