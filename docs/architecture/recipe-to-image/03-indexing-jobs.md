# Indexing jobs: approval to searchable

**Status:** Implemented (slice 3 of the recipe-to-image retrieval engine)
**Sources:** PRD/TRD IMAGE-06, §11.3–11.4, §13; Retrieval plan §12, §13.2 (stages 6–7), §15
**Builds on:** [02-index-representations-and-vector-index.md](02-index-representations-and-vector-index.md)

This slice closes the loop from "an admin approved an image" to "the image is in the vector index
with a manifest row that proves it". Nothing is searchable yet (slice 4 adds the search endpoint),
but the index is now populated and auditable.

---

## 1. What triggers indexing

| Event | Job enqueued | Same transaction as |
|-------|--------------|---------------------|
| `POST /admin/uploads/{id}/approve` or `POST /admin/images/{imageId}/approve` | `index.image.upsert` | the approval (IMAGE-06, PRD §13) |
| Worker auto-approval for a batch with `autoApprove = true` | `index.image.upsert` | the validation result |
| `POST /admin/uploads/{id}/reject` or `POST /admin/images/{imageId}/reject` on a previously approved image | `index.image.remove` | the rejection |
| `POST /admin/index/rebuild` | `index.image.upsert` per approved image | n/a (operator action) |

Dedupe keys are `index.image.upsert:{imageId}` and `index.image.remove:{imageId}`. A queued job
for an image is never duplicated; a finished one is re-armed.

## 2. One reconciliation, two job names

Both job types run `ImageIndexer.SyncAsync(imageId)`, which makes the index match PostgreSQL for
that image **as it is now**. Ordering between an upsert and a remove for the same image therefore
cannot leave a stale vector behind: whichever runs last reads the current approval state and does
the right thing. The two names exist for dashboards and intent, not for behaviour.

```
load asset (+ pinned UMF snapshot, recipe links) ─► build ImageIndexDocument
   │
   ├─ not indexable (pending / rejected / failed) ─► delete every live vector, mark records superseded
   │
   └─ indexable
        ├─ chemistry present ─► upsert normalised 22-d vector into images-chemistry-{schema}
        │                       (no provider call; vector is computed locally)
        ├─ chemistry absent  ─► delete any previous chemistry vector for this image
        ├─ text              ─► embed canonical text ─► upsert into images-text-{embeddingVersion}` (version sanitised to `[A-Za-z0-9._-]`, so `hashing-256:v1` → `images-text-hashing-256-v1`; `
        │                       reuse the stored vector when canonical_text_hash is unchanged
        ├─ confirm           ─► write/refresh IndexRecords rows (indexed_at, source_version, hashes, metadata)
        └─ supersede         ─► delete older-version vectors of each modality, set superseded_at
                                (only after the new vectors are confirmed live)
```

Invariants carried from plan §12.3:

- **Deterministic vector IDs** (`image:{imageId}:{version}`): re-running overwrites, never duplicates.
- **Manifest after confirmation**: `IndexRecords` is written only once the provider accepted the upsert.
- **Failure leaves the last good state**: a provider error records `last_error_code` on the existing
  rows and returns `Retry` (rate limit, outage) or `Fail` (bad request, auth) to the worker without
  touching `indexed_at` or `indexable`. The worker's backoff and dead-letter rules apply.
- **New version before old is removed**: a changed embedding version is a new namespace; the old
  vector is deleted and its row marked superseded only after the new one is live.

## 3. Embeddings

`ITextEmbeddingProvider` exposes `Model`, `Version`, `Dimension`, and batch `EmbedAsync`. Both are
stored on every text `IndexRecords` row, so changing either forces re-indexing into a new namespace.

| Provider | Config | Use |
|----------|--------|-----|
| `OpenAiCompatibleTextEmbeddingProvider` | `Embeddings:Provider=OpenAI`, `Embeddings:OpenAI:*` | Any OpenAI-compatible `/embeddings` endpoint. Batched, retried on 429/5xx with `Retry-After`, dimension checked against configuration, key never logged. |
| `HashingTextEmbeddingProvider` | `Embeddings:Provider=Hashing` (default) | Deterministic feature hashing of canonical-text lines. **Not semantic**; version string `hashing-256:v1` says so. Exists so the pipeline runs end to end locally and in tests. |

The chemistry modality never calls a provider: its vector is computed from the pinned UMF.

Cost control: a metadata-only change (approval, source version bump without text change) reuses
the stored text vector via `FetchAsync` and skips the embedding call. The test suite asserts this.

## 4. Pinecone and dimensions

Pinecone fixes one dimension per index. Chemistry vectors are 22-d; text vectors are model-sized
(1536 for `text-embedding-3-small`). Configure one index per modality and route by namespace prefix:

```bash
VectorIndex__Provider=Pinecone
VectorIndex__Pinecone__ApiKey=<secret>
VectorIndex__Pinecone__IndexHost=https://glazion-chem-xxxx.svc.<region>.pinecone.io        # 22-d, cosine
VectorIndex__Pinecone__NamespaceIndexHosts__images-text-=https://glazion-text-xxxx.svc.<region>.pinecone.io  # 1536-d, cosine
```

Longest prefix wins; unmatched namespaces use `IndexHost`. `DescribeAsync` aggregates across hosts.
The in-memory index enforces dimension per namespace for the same reason.

## 5. Admin surface

Approval now lives in one service (`ImageApprovalService`) used by both the upload-scoped and the
image-scoped endpoints, so imported images that landed in review can be approved or rejected
(`POST /api/v2/admin/images/{imageId}/approve|reject`) exactly like mobile uploads. This closed a
gap from slice 1: a batch with `autoApprove = false` previously had no approval path.

```text
POST /api/v2/admin/images/{imageId}/approve   { note?, recipeId? } — any source; gated on validation; queues index sync
POST /api/v2/admin/images/{imageId}/reject    { reason? }   — queues index removal if the image was approved
GET  /api/v2/admin/index/status      approved vs indexed images, backlog, per-namespace manifest counts
                                     (indexed / superseded / errored) alongside the provider's own counts,
                                     index job counts, provider name, embedding model/version, schema version
POST /api/v2/admin/index/rebuild     { imageIds?: [...], includeNonApproved?: bool } → enqueues sync jobs
GET  /api/v2/admin/jobs              ?status=&jobType=&limit=   any job type
POST /api/v2/admin/jobs/{id}/retry   re-queue a Failed / DeadLetter job after fixing the cause
```

`GET /admin/imports/{batchId}` now reports `indexed` next to `approved`, which is the
"indexable records versus indexed records" metric from plan §15.

## 6. Database

`IndexRecords` follows plan §5.7 with one addition, `modality` (chemistry | text | visual), so a
per-modality supersession can be expressed without parsing the namespace. Unique on
`(entity_type, entity_id, embedding_version, vector_namespace)` and `(vector_namespace, vector_id)`.
Vectors are not stored in PostgreSQL.

## 7. Next (slice 4)

Backfill the imported corpus (`POST /admin/index/rebuild` once embeddings are configured), then the
search endpoint: hard filters, chemistry and text KNN in parallel, union, PostgreSQL re-authorisation,
the versioned hybrid ranker, `no_reliable_match`, and persisted search runs.
