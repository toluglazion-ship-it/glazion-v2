# AI copies

The ~10,000 imported photos are not Glazion's to show the public. Decided in the 27 October 2026 meeting: each one
can be remade once by an image model, from the photo itself, as a new photograph of the same glaze. An admin compares
the copy with the original and approves it, and the approved copy is what every app shows from then on. Copies are
made once and kept, never per search, so a photo costs at most a few copies.

Participants' own tiles (the 30,000 being made now) are Glazion's and need no copy.

## What the model is asked

`AiCopyPrompt` (Infrastructure/AiCopies.cs). The photo is the authority:

- **Kept exactly:** the glaze's colour and hue, lightness, saturation, gloss (glossy, satin or matte), opacity, and only
  the variation the photo shows. No added texture, crackle, drips or effects; nothing smoother or shinier.
- **Changed:** only the photography. The same kind of tile, slightly different position and framing, plain neutral
  background, soft even daylight, no text or props, so it is a new photograph and not the same one.
- **Context only:** the variation's colorants and the firing (cone, atmosphere, clay body). The prompt says the photo
  decides how it looks, so the model does not "correct" a colour towards what a recipe might suggest.

An admin can add an instruction for one copy ("less shine", "keep the speckles") and make another.

Each copy stores the whole prompt it was made with, the model, the quality and the model's token usage.

## Flow

1. Photo library → a photo → **Make AI copy**: `POST /api/v2/admin/images/{id}/ai-copies` `{ instruction?, quality? }`
   → 202, status `Queued`. One copy per photo at a time.
2. The `image.ai-copy` job sends the photo (its upright display derivative, else the original) to the model, then
   stores the result at display and thumbnail size under `…/images/{id}/ai/{copyId}.webp`. It becomes `Ready`.
   A rate limit or outage is retried. A refusal, such as a safety rejection or a key without access to the model,
   fails the copy with the model's reason.
3. **Use this copy**: `POST …/ai-copies/{copyId}/approve`. The image's `DisplayKey` and `ThumbnailKey` now point at the
   copy, and `DisplayCopyId` records which copy. Search results, Match check and chat show it. The original and its
   own derivatives are untouched, and remaking derivatives does not take the copy away.
4. **Show the photo again**: `POST …/ai-copies/revert`. The copy stays `Ready` and can be approved again.
   **Discard**: `POST …/ai-copies/{copyId}/reject`. If it was the one shown, the photo is shown again.

`GET /api/v2/admin/images/{id}/ai-copies` lists an image's copies, newest first, with signed URLs. All of this is
`Admin` only.

## Configuration (`AiCopies`)

| Key | Default | |
|---|---|---|
| `Provider` | `OpenAI` | `Disabled` turns copies off |
| `ApiKey` | empty | Empty uses the chat's key, `Chat:OpenAI:ApiKey` |
| `Model` | `gpt-image-2.5-sunburst` | OpenAI's most precise editing model. `gpt-image-1` was shut down on 23 Oct 2026; `gpt-image-2.5-flare` is faster at the same price |
| `Quality` | `high` | `low`, `medium`, `high`, `xhigh`, `max`; can be set per copy |
| `Size` | `1024x1024` | |
| `OutputCompression` | `90` | WebP |
| `TimeoutSeconds` | `240` | |
| `MaxInFlightPerImage` | `1` | |

## Cost

The model bills by tokens: the photo and prompt in, the picture out. As reported in October 2026 (check OpenAI's
pricing page):

- **Rates:** $5 per million text input tokens, $8 per million image input tokens, $30 per million image output tokens.
- **Per copy:** at `high` quality, a 1024×1024 copy is roughly $0.05–0.07.
- **Whole corpus:** 10,000 photos at one copy each is roughly $500–700.

Every copy records its usage, so the real cost of the first samples can be read before anything is done in bulk.

## Caution

An AI-edited copy of someone else's photograph can still count as a derivative of it. If copyright is the concern, a
quick legal check, or the owners' permission, is safer than relying on the edit.
