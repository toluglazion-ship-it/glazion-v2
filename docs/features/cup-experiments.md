# Cup experiments

Many recipes from a little glaze (meeting 2026-11-02, "Glaze Research Matrix"). A participant mixes a base glaze into
10 to 20 cups and adds materials in stages, dipping tiles after every change. The same cups carry on from step to step,
so ten cups and five stages can make a hundred recipes.

## Set up (admin)

- A base glaze, the participant, the number of cups (1–20, 10 by default), the dry glaze and water per cup (100 g and
  60 g), the tiles dipped after every change (3) and their clay bodies (all three by default; the same clay body three
  times means three tiles of that clay, one tile task), and the reserve (10 g: a cup that would drop below it with the
  next dip asks for fresh glaze).
- Up to five stages, each with its own material:
  - Stage 1 always varies by cup: cup n gets `start + step × (n − 1)` %.
  - A later stage is **constant** (the same % in every cup, one step) or **variable** (`start`, `start + step`, …, one
    step per level, the same in every cup).
- No maximum % (the client's decision). Each stage adds its material on top of everything added before.
- The admin can change any stage that hasn't started (stage 3 while the participant is on stage 2), and a started
  variable stage can gain levels. Deleting an experiment takes it from the participant; its recipes and tiles stay.

## From the Register base form

Each colorant row in the Register base form is a stage, so registering a base with colorants sets up its cup
experiment in the same step (`POST /api/v2/admin/base-glazes/with-variations` with `experiment`; all or nothing).
Since the meeting of 2026-11-09 every stage is **one round across the cups**: the participant adds it to every cup,
dips, and says "Stage 1 completed".

- Series: from, to and steps, where steps is how many places to split into (0.5 to 5 in 10 steps is 0.5, 1 … 5);
  cup n gets the nth amount. Fixed: an amount and steps, every cup gets the amount.
- Stage 1's steps set the number of cups (1–20); every later stage covers the same cups.
- Each recipe keeps everything added before it, so 10 cups and 3 stages make 30 recipes.
- The experiment takes the base's name and its participant, with 100 g dry glaze and 60 g water per cup and 3
  tiles a stage, and shows in Cup experiments and on the participant's phone straight away.
- No weighing for now: when a stage is completed without a weight, each tile is taken to use 5 g of glaze
  (`ExperimentService.PlaceholderGramsPerTile`) until the measured leftovers after stages 1, 2 and 3 replace it.

Edit base is the same form, filled in from the base's experiment (`GET /api/v2/admin/experiments?baseCode=`). Before
the first step everything can change, the number of cups, the glaze in them and who mixes them too; after it, a
stage that has started stays as it is. Deleting a base deletes its experiments with it, and restoring it brings them back.

A user created without a participant code gets the next free one (P01, P02 … P99, then Q01).

## The participant's steps

1. **Start** the step: every cup gets the grams of the stage's material to add and its own new recipe code (written on
   the back of its tiles). The grams are the target % of the **dry base still in the cup**, less what it already holds.
2. Mix, dip the tiles, **weigh one cup** (glaze only) and enter it. Dipping takes glaze, water and every material out in
   the same proportion, so the share left in the weighed cup (`weighed / before`) is applied to every cup. The
   percentages already in a cup don't change; only the grams for the next step do.
3. The dipped tiles become tile tasks already marked made, so they wait for the admin's check and then a photo, like any
   other (tile-tasks.md). The next step opens straight away; the participant doesn't wait for the check.

A step started by mistake can be undone before dipping (its new recipes are deleted, keeping their numbers).

## Fresh glaze

A cup that would drop below the reserve with another dip like the last one is marked as needing glaze and sits out the
next steps (no recipe for it) until fresh glaze is added, with the admin. Glazion gives the grams of every material
already in the cup to add to the fresh glaze, at the cup's percentages, so the cup stays the same recipe.

## Example (the spec's acceptance figures)

Cup 1: 100 g dry + 60 g water. 0.5% cobalt carbonate adds 0.5 g (160.5 g wet). Three tiles take 15 g: 145.5 g left,
so 90.654 g of dry base. 2% titanium dioxide then adds 1.813 g.

## API

Admin (`/api/v2/admin/experiments`, admin role):

```text
GET    ?participantUserId=&baseCode=&deleted=      list, newest first
POST   { name?, baseCode, participantUserId, cups?, dryGrams?, waterGrams?, tilesPerStep?, clayBodies?, minReserveGrams?,
         stages: [{ material, mode: variable|constant, start?, step?, levels?, constant? }] }
GET    /{id}
PATCH  /{id}  { name?, stages?, tilesPerStep?, clayBodies?, minReserveGrams?, cups?, dryGrams?, waterGrams?, participantUserId? }
       409 stage_started for a started stage; 409 experiment_started for cups/glaze/participant after the first step
DELETE /{id}  POST /{id}/restore
POST   /{id}/steps/start | /steps/cancel | /steps/dipped { cupNumber, wetGrams } | /cups/{n}/refill { dryGrams, waterGrams }
```

Participant (`/api/v2/me/experiments`, their own only): `GET`, `GET /{id}`, and the same `steps/…` and `cups/{n}/refill`.

Every answer is the whole experiment: its stages (with a summary and how many steps are done), each cup now (grams and
%), `current` (the open step, or the next one as a preview with the grams to add), `history` (steps and refills done)
and `progress` (steps, recipes, tiles). Refusals: `experiment_finished`, `cups_need_glaze`, `no_open_step`, `step_open`
(refill during an open step), `base_full` (the base has no more recipe numbers).
