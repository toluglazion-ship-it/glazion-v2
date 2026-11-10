# Index representations and the vector index abstraction

**Status:** Implemented (slice 2 of the recipe-to-image retrieval engine)
**Sources:** PRD/TRD §7.2, §11.2–11.3; Retrieval plan §11.1–11.3, §12.3, §14, §17
**Builds on:** [image-ingestion-pipeline.md](image-ingestion-pipeline.md)

This slice turns an approved `ImageAsset` into the deterministic representations the retrieval
engine will index and query, and puts the vector store behind one provider-neutral interface.
No indexing jobs run yet (that is slice 3); everything here is pure, testable code.

---

## 1. Representations (`src/Glazion.Api/Indexing`)

| Representation | Builder | Version | Hash purpose |
|----------------|---------|---------|--------------|
| Chemistry vector | `ChemistryVectorBuilder` | `oxide-vector-v1` (`ChemistrySchema.Version`) | `chemistry_vector_hash`: skip re-embedding when unchanged |
| Canonical text | `CanonicalTextBuilder` | `canonical-text-v1` | `canonical_text_hash`: same |
| Index document | `ImageIndexDocumentBuilder` | carries all pinned versions | assembled per image per `SourceVersion` |

### 1.1 Chemistry vector

22 floats in the fixed oxide order from PRD §11.2. Each position also carries a `Measured` flag:
an oxide absent from the UMF is `0` in `Values` but `false` in `Measured`, so completeness
(`MeasuredCount / 22`) is available to the ranker and a measured zero is never confused with
"not analysed" (plan §11.1). `Normalized()` returns the unit-length form for cosine providers;
a zero vector stays zero rather than producing NaN. The hash covers the schema version plus the
canonical UMF JSON, so a schema bump always invalidates every stored hash.

### 1.2 Canonical text

Built from controlled fields only, in a fixed line order, with 4-decimal invariant formatting.
Example output for the spec's reference UMF:

```
record_type: glaze_tile
umf_profile: traditional-umf-v1
fluxes: Na2O=0.1800; K2O=0.1200; MgO=0.1800; CaO=0.5200
stabilizers: Al2O3=0.3500
b2o3: 0.2000
glass_formers: SiO2=3.2000
silica_alumina_ratio: 9.1429
cone: 6
atmosphere: oxidation
tile_variant: white
surface: glossy
opacity: opaque
colour_family: blue
```

Oxides inside a group are emitted in `oxide-vector-v1` order (the plan's example lists CaO before
MgO; a single order was chosen for determinism). Groups: fluxes, stabilizers (Al2O3), b2o3,
glass_formers (SiO2), opacifiers (TiO2, ZrO2, SnO2), colourants (Fe2O3, Cr2O3, CuO, CoO, MnO, NiO),
other_oxides (P2O5). Lines are omitted when the value is absent; no placeholders are written.
An image without chemistry gets `chemistry: unavailable` so the text embedding still carries that fact.
Labels are the controlled types from plan §5.5 in a fixed order; unknown label keys are ignored.

### 1.3 Index document and metadata allow-list

`ImageIndexDocument` is the versioned unit of work for the indexing jobs. `Indexable` is true only
for `ApprovalStatus = Approved` and `QualityStatus ≠ Failed` (plan §3.2). Vector metadata is an
explicit allow-list (`VectorMetadataKeys`): image id, source version, approval, indexable,
visibility, tile variant, cone range, atmosphere, chemistry status and completeness, surface,
opacity, colour family, quality status, recipe id, modality. Nothing about owners, object keys,
capture codes, or notes is ever written to the vector store (plan §14).

---

## 2. `IVectorIndex`

```csharp
Task UpsertAsync(string ns, IReadOnlyList<VectorRecord> records, CancellationToken ct);
Task DeleteAsync(string ns, IReadOnlyList<string> ids, CancellationToken ct);
Task<IReadOnlyList<VectorMatch>> QueryAsync(VectorQuery query, CancellationToken ct);
Task<IReadOnlyDictionary<string, VectorRecord>> FetchAsync(string ns, IReadOnlyList<string> ids, CancellationToken ct);
Task<VectorIndexStats> DescribeAsync(CancellationToken ct);
```

- **Namespaces** (`VectorNamespaces`): `images-chemistry-{schemaVersion}`, `images-text-{embeddingVersion}`,
  `images-visual-{embeddingVersion}`. A new embedding version is a new namespace, so old and new
  vectors coexist until the cut-over, satisfying "a new source version must be indexed before the
  old vector is superseded" (PRD §11.3).
- **Vector IDs** (`VectorIds`): `image:{imageId}:{version}`. Deterministic, so re-running an upsert
  overwrites the same vector (plan §12.3).
- **Filters** (`VectorFilter`): equality, membership, `>=`, `<=`, and conjunction. Deliberately
  small so every provider can translate it exactly. `Matches()` evaluates the filter locally,
  which is what the in-memory index uses and what the contract test compares against the
  Pinecone translation.
- **Errors** (`VectorIndexException`): `InvalidRequest`, `Unauthorized`, `NotFound`, `RateLimited`,
  `Unavailable`, `Unknown`; `IsTransient` tells the job worker whether to retry.

Results are candidates only. The search path must load each match's source record from
PostgreSQL and apply ownership and approval checks before returning it (PRD §3, §7.3).

### 2.1 Pinecone adapter

`PineconeVectorIndex` talks to the data-plane REST API (`vectors/upsert`, `query`, `vectors/delete`,
`vectors/fetch`, `describe_index_stats`) with `Api-Key` and `X-Pinecone-API-Version` headers.
It batches upserts (default 100), retries `429` and `5xx` with exponential backoff honouring
`Retry-After` (default 4 retries), never retries `4xx`, and maps status codes to the neutral
error codes above. Error messages carry the response body but never the API key.

### 2.2 In-memory implementation

`InMemoryVectorIndex` is exact cosine search with the same filter semantics. It is the default
provider (`VectorIndex:Provider = InMemory`) so local development, tests, and the importer smoke
test need no Pinecone account.

---

## 3. Configuration

```bash
VectorIndex__Provider=Pinecone
VectorIndex__Pinecone__ApiKey=<secret>
VectorIndex__Pinecone__IndexHost=https://<index>-<project>.svc.<region>.pinecone.io
```

The Pinecone index must be created with **dimension 22, cosine metric** for the chemistry
namespace; text and visual embeddings will need their own indexes (or a dimension-matched index
per modality) when slice 3 chooses the embedding model, because Pinecone fixes one dimension per
index. `IVectorIndex` is namespace-scoped, so that choice does not change application code.

---

## 4. Next (slice 3)

- `index.image.build_document → embed_text → upsert` jobs enqueued at approval with dedupe key
  `image:{imageId}:{embeddingVersion}:{namespace}`.
- `IndexRecords` table written only after the provider confirms, superseding the previous version.
- Text embedding provider abstraction (OpenAI-compatible) behind the same adapter discipline.
