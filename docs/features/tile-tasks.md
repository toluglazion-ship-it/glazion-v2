# Tile tasks

An admin gives a participant recipes to make, one tile per clay body, usually with a due date. The participant presses
Start, which shows them the recipe, makes the tile and marks it done. An admin checks the made tile and approves it for
its photo, or returns it with a reason. Once fired, anyone photographs it by the code on its back, and the photo goes
through the usual review; the approved photo is what reaches the tile library. A task whose participant does not come
in can be moved to someone else. Until the admin approves a tile, the admin can change or delete everything about it
(meetings 2026-10-29 and 2026-11-02).

## Stages

| Stage | Meaning | Comes from |
|---|---|---|
| `Assigned` | To make, not started. The participant sees the tile's code, name, clay body and due date, not the recipe; the admin can still change the recipe | Created, or reassigned |
| `Started` | Being made. The recipe is shown to the participant and locked | The participant's Start; also an admin's return (`returnNote` says why) |
| `Done` | Made, waiting for an admin to check it | The participant's "done" |
| `AwaitingPhoto` | Checked and approved: fire it and photograph it; also when a photo was rejected or failed the quality checks (`retakeReason` says why) | The admin's approve |
| `InReview` | Photo sent, waiting for an admin | The latest photo is `Uploaded`, `Validating` or `NeedsReview` |
| `Approved` | Photo approved | The latest photo is `Approved`, `Indexing` or `Indexed` |
| `Cancelled` | Withdrawn by an admin | Cancel |

Only "started", "done", "approved" and "cancelled" are stored on the task. `InReview` and `Approved` are read from the
latest photo sent for the current assignment (`Uploads.TileTaskId`), so a task always agrees with the review queue:
rejecting a photo puts its task back to `AwaitingPhoto` without anything else to update.

`overdue` is true when `dueOn` has passed and the task is still `Assigned` or `Started`.

## Rules

- One task per recipe and clay body (`porcelain`, `stoneware`, `red_body`). A recipe has at most one live (not
  cancelled) task per clay body; the database enforces it. Assigning a tile that already has one skips it and says who
  has it, so sending the same assignment twice creates nothing new.
- Only active participants take tasks, not admins or pending accounts. A due date cannot already be past.
- An assigned recipe (its base's materials and firing, its own additions and firing) can be corrected until an admin
  has checked one of its tiles and approved it, even after the participant pressed Start or marked it done (meeting
  2026-11-02). A participant who started it is told: `recipeChangedAt` on their task (until it is approved). Once a tile
  is approved, or photographed, the recipe is locked (409 `base_in_use` / `recipe_in_use`, "A03-B1-003 has been
  checked and approved").
- "Done" needs Start first (409 `task_not_started`). It can be taken back until an admin has checked it
  (409 `task_approved_for_photo`).
- Reassigning is allowed until an admin has approved the tile (`Assigned`, `Started` or `Done`; 409
  `task_awaiting_photo` after that). It starts the new participant from `Assigned` (their own Start, "done" and check).
  Cancelling (deleting) is allowed until a photo is in review or approved. The recipe's code does not change:
  `A03-B2-009` stays A03's variation whoever makes the tile.
- A cancelled tile can be assigned again, or the cancelled task restored (409 `task_exists` if the tile was given out
  again since, `recipe_deleted` while its recipe is deleted).
- Deleting a recipe or a base cancels its open tasks; restoring it brings those back (lab-code-naming.md).
- Every change is in the task's history: assigned, reassigned (from, to, reason), started, marked done, done undone,
  approved, returned (with the reason), updated, cancelled, restored.
- Write requests take an optional `version` (from the task). A stale one is refused with 409 `task_changed`.

## Photos

`POST /api/v2/uploads` takes an optional `taskId`. The photo then gets the task's recipe and clay body (they may be left
out). Refused: a task that was cancelled or does not exist (400 `task_not_found`), a recipe or clay body that differs
from the task's (400 `task_recipe_mismatch`, `task_clay_body_mismatch`), one not made yet (409 `task_not_done`), one
made but not yet checked by an admin (409 `task_not_approved`), or one whose photo is in review or approved
(409 `task_in_review`, `task_approved`).

Only an approved tile (`AwaitingPhoto`) can be photographed, and then by anyone, participant or admin, its maker
included. This is capture by code: snap the tile, type the code written on it, pick which of its tiles (clay body) it is.

A photo sent with `taskId` needs no capture code (`captureCodeId` may be left out). The photo still goes through the
usual review: it reaches the dataset and the library only once an admin approves it. The upload records who took it
(`owner`); the task says who made it (`madeByUsername` in the admin review queue and the image trace).

A photo sent without `taskId`, for a recipe and clay body the caller has a task for, is linked to that task under the
same rule (refused until the tile is approved). So photos taken the old way (picking the recipe) still move the task
along. A live task also lets its participant
photograph the recipe, as a capture-code assignment does. Uploading without a task needs an active capture code;
`participantCanUpload` in assign and reassign responses is false when the participant has none.

### The back of the tile

An upload may carry the back of the tile, with its code written on it: `back: { fileName?, contentType, sizeBytes }` on
create. Presign then returns `back: { method, url, objectKey, expiresAt, headers }`, a second PUT next to the front
(`.../uploads/{id}/back.jpg`), and complete checks it arrived (409 `back_not_found`, 400 `size_mismatch`). The back is
kept with the photo and shown beside it (`backUrl` on the upload, the admin review queue, the recipe library's photos
and the image trace). Only the front is checked for quality, made into derivatives and indexed for search.

### Looking up a code

`GET /api/v2/capture/tiles/{code}` (any active account) → `{ recipeId, code, name, baseCode, baseName, cone, atmosphere,
tiles }`, 404 `recipe_code_not_found` for an unknown code. `tiles` has one entry per live task of the recipe:
`{ taskId, clayBody, stage, status, madeByUsername, mine, doneAt, retakeReason }`. `status` is `ready` (approved by an
admin: photograph it now), `not_done` (not made yet), `not_approved` (made, waiting for an admin to check it),
`in_review` or `approved`. Send the photo with the
chosen tile's `taskId`.

## Endpoints

Admin (`Admin` role):

| Call | Endpoint |
|---|---|
| list | `GET /api/v2/admin/tasks?stage=&overdue=&userId=&recipeId=&clayBody=&q=&cursor=&pageSize=` → `PagedResult<TileTask>`, soonest due first; all but cancelled when `stage` is omitted; `q` matches recipe code or name and username |
| counts | `GET /api/v2/admin/tasks/summary?userId=&recipeId=&clayBody=&q=` → `{ assigned, started, done, awaitingPhoto, inReview, approved, cancelled, overdue }` |
| one | `GET /api/v2/admin/tasks/{id}` → `TileTask` with `history` |
| assign | `POST /api/v2/admin/tasks` `{ recipeIds, clayBodies?, assignedUserId, dueOn?, note? }` → `{ created, skipped, participantCanUpload }`; all three clay bodies when `clayBodies` is omitted; up to 200 recipes |
| change | `PATCH /api/v2/admin/tasks/{id}` `{ dueOn?, clearDueOn?, note?, version? }` (a blank note removes it) |
| reassign | `POST /api/v2/admin/tasks/{id}/reassign` `{ assignedUserId, dueOn?, reason?, version? }` |
| reassign many | `POST /api/v2/admin/tasks/reassign` `{ taskIds, assignedUserId, dueOn?, reason? }` → `{ reassigned, skipped: [{ taskId, reason }], participantCanUpload }` (reasons: `not_found`, `awaiting_photo`, `cancelled`, `in_review`, `approved`, `same_participant`) |
| approve | `POST /api/v2/admin/tasks/approve` `{ taskIds }` → `{ updated, skipped: [{ taskId, reason }] }`: made tiles (`Done`) checked and approved for their photo; the rest are skipped (`not_started`, `not_done`, `awaiting_photo`, `in_review`, `approved`, `cancelled`, `not_found`) |
| return | `POST /api/v2/admin/tasks/return` `{ taskIds, reason }` → same shape: made tiles (or approved ones with no photo yet) go back to `Started` with the reason the participant sees (required) |
| cancel (delete) | `POST /api/v2/admin/tasks/{id}/cancel` `{ reason?, version? }` |
| restore | `POST /api/v2/admin/tasks/{id}/restore` → the task, back where it was |
| participant's activity | `GET /api/v2/admin/tasks/activity/{userId}?limit=` → newest first: `[{ kind, at, taskId, recipeId, recipeCode, recipeName, clayBody, actorUsername, note, uploadId?, uploadStatus? }]`; `kind` is a task event (`Assigned`, `Started`, `MarkedDone`, `CompositionApproved`, `Returned`, `Reassigned`, `Cancelled`, `Restored`…) or `PhotoSent`; up to 500 (default 100) |
| register and assign | `POST /api/v2/admin/base-glazes/with-variations` `{ base? \| baseCode?, variations?, assign?: { assignedUserId, clayBodies?, dueOn?, note? } }` → `{ base, variations, assigned }`: registers a new base (same fields as `POST /base-glazes`) or uses an existing one, numbers its variations, and assigns them, all in one transaction (if any part is refused nothing is saved) |
| recipes page | `GET /api/v2/admin/recipes?search=&baseCode=&assignedUserId=&assigned=&taskStage=&overdue=` → each recipe with `baseName` and `tasks` (its live tasks). `search` also matches the base glaze's name; `assigned=false` lists recipes nobody has yet |

Participant (their own tasks only; cancelled ones never show):

| Call | Endpoint |
|---|---|
| list | `GET /api/v2/me/tasks?stage=` → to make first (soonest due first), then ready to photograph, waiting for a check, in review, approved; a plain array unless `cursor`/`pageSize`. Each started task carries `recipe` (not shown before Start): `{ description, baseCode, baseName, base, additions, materials, cone, atmosphere }`, rows with their `kind` (`colorant` for a metallic oxide, `stain`, `clay`, …), so the app can show the recipe and swipe through them |
| one | `GET /api/v2/me/tasks/{id}` (with `recipe` once started) |
| start | `POST /api/v2/me/tasks/{id}/start` → the task with its `recipe`; locks the recipe (saying it twice changes nothing) |
| done | `POST /api/v2/me/tasks/{id}/done` → sent to an admin to check (409 `task_not_started` before Start; saying it twice changes nothing) |
| undo done | `DELETE /api/v2/me/tasks/{id}/done` (409 `task_approved_for_photo` once checked, `task_photo_sent` once a photo was sent) |

Dates are `YYYY-MM-DD`. "Overdue" uses the UTC date.
