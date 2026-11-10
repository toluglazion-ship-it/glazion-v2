# Glazion API

The backend for Glazion, a platform for glaze research: participants mix and photograph test tiles, every tile is
tied to its exact recipe and chemistry, and anyone can search the photographed corpus by recipe to see how a glaze
will look before mixing it.

Built with .NET 10, ASP.NET Core and PostgreSQL (EF Core / Npgsql). Images are stored in Cloudflare R2.

## Contents

- [Features](#features)
- [Repository layout](#repository-layout)
- [Getting started](#getting-started)
- [Configuration](#configuration)
- [API overview](#api-overview)
- [Bulk import](#bulk-import)
- [Testing](#testing)
- [Deployment](#deployment)
- [Documentation](#documentation)

## Features

- **Accounts and roles**: admin-managed users, participant self-registration with admin activation, rotating
  refresh tokens.
- **Lab codes**: one short code (`A03-B2-009`: participant, base glaze, variation) links every tile, recipe sheet,
  photograph and record. See [lab code naming](docs/features/lab-code-naming.md).
- **Base glazes and variations**: immutable, numbered recipes with firing (cone, atmosphere); a base locks once one
  of its tiles is approved or photographed.
- **Cup experiments**: many recipes from a few cups of glaze, colorant stage by colorant stage, as Series or Fixed
  amounts. See [cup experiments](docs/features/cup-experiments.md).
- **Tile tasks**: recipes assigned to participants per clay body, marked done, checked by an admin and photographed.
  See [tile tasks](docs/features/tile-tasks.md).
- **Uploads**: direct-to-R2 uploads through short-lived presigned URLs, server-side validation, derivatives and
  quality scoring, admin review.
- **UMF calculator**: the shared chemistry engine, identical to the frontend's to 10 decimal places.
  See [UMF calculator](docs/features/umf-calculator.md).
- **Recipe-to-image search**: search the approved corpus by materials, stored recipe or UMF, with explained scores
  and an honest "no reliable match". See [search](docs/architecture/recipe-to-image/04-search.md).
- **Match check**: every search beside the tiles it suggested, for admins to verify matches.
  See [match check](docs/features/match-check.md).
- **Materials admin, AI copies and chat**: see [materials](docs/features/materials-admin.md),
  [AI copies](docs/features/ai-copies.md) and [chat](docs/features/chat.md).

## Repository layout

```text
.
├── src/Glazion.Api/            API host
│   ├── Controllers/            HTTP endpoints (thin)
│   ├── Contracts/              request and response records
│   ├── Domain/                 entities
│   ├── Data/                   EF Core context and migrations
│   ├── Infrastructure/         lab codes, storage, auth and shared services
│   ├── Tasks/  Experiments/    tile tasks and cup experiments
│   ├── Chemistry/              UMF calculator (with ChemistryData/)
│   ├── Indexing/  Retrieval/   image indexing and recipe-to-image search
│   └── Chat/                   assistant and its tools
├── tests/Glazion.Api.Tests/    xUnit tests
├── tools/
│   ├── Glazion.Importer/       bulk importer for the labelled image corpus
│   └── sync-chemistry-data.mjs keeps the chemistry data in step with the frontend
├── scripts/
│   ├── e2e/                    end-to-end and smoke checks against a running API
│   └── setup/                  one-time infrastructure setup (Pinecone indexes)
├── docs/
│   ├── features/               feature specifications
│   ├── api/                    API contract and client guides
│   ├── architecture/           recipe-to-image design
│   └── deploy/                 deployment guides
├── Dockerfile  docker-compose.yml  railway.json
└── Glazion.slnx
```

## Getting started

Prerequisites: .NET 10 SDK and Docker.

```bash
cp .env.example .env                 # set POSTGRES_PASSWORD and BOOTSTRAP_ADMIN_PASSWORD
docker compose up -d glazion-postgres
dotnet run --project src/Glazion.Api --environment Development
```

The API listens on `http://localhost:5136`. Migrations run on startup, and the first admin is created from the
`BootstrapAdmin__*` settings when no admin exists. Swagger is available in Development at `/swagger`.

To run the API and database together in containers: `docker compose up -d`.

## Configuration

All settings are environment variables (or `appsettings.*.json` in development). `.env.example` lists them. Never
commit real values.

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__GlazionDb` | PostgreSQL connection string |
| `Database__ApplyMigrationsOnStartup` | `false` when migrations run as a separate step |
| `BootstrapAdmin__Username`, `BootstrapAdmin__Password` | first admin account |
| `R2__ServiceUrl`, `R2__Region`, `R2__AccessKey`, `R2__SecretKey`, `R2__Bucket` | Cloudflare R2 storage |
| `VectorIndex__*` | vector index provider (in-memory or Pinecone), see [Pinecone](docs/deploy/pinecone.md) |
| `Features__RecipeImageRetrieval` | turns recipe-to-image search on or off |
| `Worker__Enabled` | runs the background validation and indexing worker in this instance |

Clients never receive R2 credentials: they create an upload through the API, receive a scoped presigned `PUT`, upload
directly to R2 and then call complete. The API verifies the object before accepting it.

## API overview

Everything is under `/api/v2`. Lists are paged (`PagedResult`), errors are RFC 7807 problem details with a `code`.
The full contract the clients are built on is in [frontend contract](docs/api/frontend-contract.md).

Typical capture flow:

1. Sign in: `POST /auth/login`, renew with `POST /auth/refresh`.
2. Admin registers a base glaze and its variations, or a cup experiment, and assigns tile tasks to a participant.
3. The participant mixes and marks tiles done: `GET /me/tasks`, `POST /me/tasks/{id}/done`.
4. Upload a photo: `POST /uploads` (with the tile's `recipeCode`), `POST /uploads/{id}/presign`, `PUT` to R2,
   `POST /uploads/{id}/complete`.
5. The worker validates the image, writes `display.webp` and `thumbnail.webp`, scores sharpness and exposure, and
   queues it for review.
6. Admin review: `GET /admin/uploads?status=4`, then approve or reject. Approved images are indexed for search.

Search:

```bash
curl -X POST http://localhost:5136/api/v2/recipe-image/search \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -H "Idempotency-Key: $(uuidgen)" \
  -d '{"query":{"type":"raw_recipe","materials":[
         {"rawName":"Minspar 200","amount":16},{"rawName":"Gerstley Borate","amount":20},
         {"rawName":"Ball Clay","amount":18},{"rawName":"Ferro Frit 3124","amount":23},
         {"rawName":"Silica","amount":9},{"rawName":"Mason Stain 6001","amount":2.5}]},
       "firing":{"coneMin":6,"coneMax":6,"atmosphere":"oxidation"},"topK":12}'
```

`src/Glazion.Api/Glazion.Api.http` holds ready-made requests for the main endpoints.

## Bulk import

The importer registers manifest rows with the API, receives API-generated object keys and presigned URLs, uploads
straight to R2 with bounded concurrency, and asks the API to verify each object and create its image asset and
recipe link. It never holds R2 credentials, and a run can be resumed.

```bash
dotnet run --project tools/Glazion.Importer -- \
  --api http://localhost:5136 --token "$ADMIN_TOKEN" \
  --manifest ./corpus/manifest.jsonl --files ./corpus/images \
  --batch-name corpus-2026-09 --concurrency 16
```

Manifest format and the pipeline:
[image ingestion pipeline](docs/architecture/recipe-to-image/image-ingestion-pipeline.md).

## Testing

```bash
dotnet test Glazion.slnx -c Release
```

The suite covers the domain rules, the chemistry engine (including golden UMF cases shared with the frontend),
search ranking, ingestion and the HTTP contract. `scripts/e2e/` holds end-to-end and read-only smoke checks to run
against a staging API.

## Deployment

- **Container**: the multi-stage `Dockerfile` builds the runtime image; GitHub Actions tests every push and publishes the
  image to GitHub Container Registry from `main`.
- **Railway**: `railway.json` configures the service; set the variables from `.env.example` in the service.
- Put the API behind HTTPS with a request size limit consistent with `Uploads__MaxBytes`.
- The worker runs in the API host by default. To scale it separately, run another instance with the worker on and
  set `Worker__Enabled=false` on the API instances; jobs are claimed with `FOR UPDATE SKIP LOCKED`, so several
  workers are safe.
- Moving the vector index to Pinecone: [Pinecone guide](docs/deploy/pinecone.md).

## Documentation

| Area | Documents |
| --- | --- |
| Features | [cup experiments](docs/features/cup-experiments.md), [tile tasks](docs/features/tile-tasks.md), [lab codes](docs/features/lab-code-naming.md), [match check](docs/features/match-check.md), [materials](docs/features/materials-admin.md), [UMF calculator](docs/features/umf-calculator.md), [AI copies](docs/features/ai-copies.md), [chat](docs/features/chat.md) |
| API | [frontend contract](docs/api/frontend-contract.md), [UMF guide](docs/api/umf-guide.md) |
| Architecture | [recipe-to-image](docs/architecture/recipe-to-image/) |
| Deployment | [Pinecone](docs/deploy/pinecone.md) |
