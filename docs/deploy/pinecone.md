# Setting up Pinecone

Production runs the image vector index **in memory**. It works, but every restart or deploy of the Railway
service rebuilds all ~11,000 images, and search is incomplete until that finishes. Pinecone keeps the vectors
permanently. This guide moves the image index there and connects chat to the knowledge in Pinecone.

Pinecone project: **GlazeAI** (organisation "Gbohunmi's Org", region aws us-east-1). It already has the
index `glazeai-2` (28,856 records, one namespace): the chat knowledge base. **Do not reuse or delete it.**
The images get two new indexes next to it.

## What gets created

A Pinecone index has one fixed vector size ("dimension"), so the two kinds of image vector need one index each.

| Index name | Holds | Dimension | Metric | Type |
|------------|-------|-----------|--------|------|
| `glazion-image-chemistry` | one chemistry vector per image (22 oxide values) | **22** | cosine | dense, serverless, aws us-east-1 |
| `glazion-image-text` | one text vector per image | **1536** (OpenAI `text-embedding-3-small`, recommended) or **256** (built-in hashing embedder) | cosine | dense, serverless, aws us-east-1 |

Each vector carries only filterable facts: approval, tile variant, cone, atmosphere, clay body, recipe id
and code, and stain material ids. It carries no names, emails, photos or passwords.

Cost: storage for ~11,000 small vectors is well inside Pinecone's free allowance. Embedding ~11,000 short
image descriptions once with OpenAI costs a few cents; each search embeds one short query.

## Before you start

You need:

- Owner or editor access to the GlazeAI project in the Pinecone console (app.pinecone.io).
- Access to the Railway project, specifically the **API service's Variables** tab.
- An OpenAI API key, if you take the recommended 1536 option. The chat key works.
- Python 3 on any computer, if you use the script in step 3. It needs no packages.

## Step 1: Deploy the current code

Pinecone support with separate chemistry and text indexes (`ChemistryIndexHost`, `TextIndexHost`), the
automatic re-send, and the safety check are part of `main`. Make sure the API runs the latest `main`
**before** setting any Pinecone variable. Production keeps the in-memory index until step 5, so this
deploy changes nothing about where vectors live.

## Step 2: Create a Pinecone API key

1. Open app.pinecone.io and select organisation **Gbohunmi's Org**, project **GlazeAI**. The breadcrumb at
   the top shows `Gbohunmi's Org / GlazeAI`.
2. In the left menu, under **Manage**, open **API keys**.
3. Create a key. Name it something like `glazion-api-railway`. If a permission choice is offered, it needs
   read and write on the project's indexes.
4. Copy the key when it is shown and keep it somewhere safe for step 5. Pinecone may not show it again.
   Don't paste it into GitHub, chat or a document.

One key can serve both the image index and chat knowledge, because both indexes are in the same project.

## Step 3: Create the two indexes

Choose **one** of the two ways.

### Option A: the script (fastest, nothing to mistype)

On any computer with Python 3 and this repository:

```bash
export PINECONE_API_KEY='<the key from step 2>'
python scripts/setup/create_pinecone_indexes.py
# only if you will keep the built-in hashing embedder instead of OpenAI:
#   python scripts/setup/create_pinecone_indexes.py --text-dimension 256
```

The script:

- creates `glazion-image-chemistry` (22) and `glazion-image-text` (1536 or 256), dense, cosine, serverless on
  aws us-east-1, with **deletion protection on**;
- waits until both are ready;
- prints the exact Railway variables for step 5, hosts included.

It is safe to run again. An index that already exists with the right dimension is reused. One with the wrong
dimension stops the script with a message, and nothing is changed.

### Option B: the Pinecone console

1. Left menu **Database → Indexes**, then **Create index**.
2. **Name:** `glazion-image-chemistry`.
3. Configuration: choose to set the dimension yourself. Don't pick an embedding-model preset, because the
   chemistry vector is not text.
   - **Vector type:** Dense
   - **Dimension:** `22`
   - **Metric:** `cosine`
4. Capacity: **Serverless**, cloud **AWS**, region **us-east-1** (the same as `glazeai-2`).
5. Create, then open the index. Its page shows **Host** next to Region, like `glazeai-2-vegi1sw.svc.aped-4627-b74a.pinecone.io`
   on the `glazeai-2` page. Copy it with the copy button.
6. On its **Configuration** tab, switch **Deletion protection** on.
7. Repeat for `glazion-image-text` with **Dimension `1536`** (or `256` if you keep the hashing embedder),
   metric `cosine`, the same cloud and region. Copy its Host and switch deletion protection on.

If the console mentions a limit on the number of indexes for your plan, check the plan. The project already
has two indexes, and these two make four.

## Step 4: Decide on text embeddings

| Choice | Text index dimension | Railway variables | Effect |
|--------|----------------------|-------------------|--------|
| **OpenAI (recommended)** | 1536 | `Embeddings__Provider=OpenAI`, `Embeddings__OpenAI__ApiKey` | Text matching understands meaning ("pink matte" finds "rose satin") |
| Built-in hashing | 256 | none (default) | Matches shared words only; no OpenAI calls |

The chemistry and stain matching that decide most of the ranking are the same either way. The text index's
dimension must match the choice. The API checks this at startup (step 6).

## Step 5: Set the Railway variables

1. Open the Railway project, then the **API service**, then the **Variables** tab.
2. Add these, one by one or through **Raw Editor**:

   ```
   VectorIndex__Provider=Pinecone
   VectorIndex__Pinecone__ApiKey=<Pinecone API key from step 2>
   VectorIndex__Pinecone__ChemistryIndexHost=<Host of glazion-image-chemistry>
   VectorIndex__Pinecone__TextIndexHost=<Host of glazion-image-text>
   Embeddings__Provider=OpenAI
   Embeddings__OpenAI__ApiKey=<OpenAI API key>
   ```

   - The two variable names have **double underscores**.
   - A host can be pasted as Pinecone shows it (`glazion-image-text-xxxx.svc.aped-4627-b74a.pinecone.io`)
     or with `https://` in front.
   - Leave out the two `Embeddings__…` lines if you chose hashing (256) in step 4.
   - Railway variables are encrypted. To also hide the two keys from people who can open the Variables
     tab, use Railway's "seal" option on them.

3. Railway asks to deploy the change. Deploy it, and it restarts the API.

## Step 6: Watch the first start

In the service's **Deploy Logs**, within a minute of the start, you should see one of these:

- `Pinecone vector index: queued 10920 approved images not yet stored there`. This is correct. The API is
  now sending every approved image to Pinecone in the background. For ~11,000 images expect it to take a
  while, likely tens of minutes; the backlog in step 7 shows progress.
- `Pinecone: The text index at … has dimension 1536; this API writes 256 …` (or similar). This means the
  index dimension and the embedding choice disagree. The API sends nothing. Fix the variable, or recreate the
  text index with the right dimension, then redeploy.
- `Pinecone could not be checked at startup`. The API key or a host is wrong, or Pinecone is unreachable.
  Index jobs keep retrying. Check the key and the hosts.

Search keeps working while the images are sent, but only finds images already in Pinecone.

## Step 7: Confirm it worked

1. As admin, call `GET /api/v2/admin/index/status`, or run `python scripts/e2e/smoke_prod.py`, which is read only:
   - `vectorProvider` is `Pinecone`;
   - `backlog` falls to `0`, and `indexedImages` equals `approvedImages`;
   - under `namespaces`, `providerVectorCount` matches `indexed`;
   - `providerError` is `null`.
2. In the Pinecone console, the two new indexes show record counts close to 11,000. `glazeai-2` is unchanged.
3. Run one search by materials and check that the closest match and suggestions look right.
4. Redeploy once more. The log must **not** show the "queued … images" line again, because nothing needs
   re-sending. Restarts are now instant for search.

## Undo

Delete `VectorIndex__Provider`, or set it to `InMemory`, and redeploy. The API goes back to the in-memory
index and rebuilds it at start, as before. Nothing in Pinecone is deleted, so switching back later re-sends
only what changed in between.

## Chat knowledge from Pinecone

Chat answers from the knowledge in `glazeai-2`. To connect it, and later add more knowledge without code
changes, set on the same Railway service:

```
Chat__Provider=OpenAI
Chat__OpenAI__ApiKey=<OpenAI API key>
Chat__Knowledge__Provider=Pinecone
Chat__Knowledge__PineconeApiKey=<Pinecone API key>
Chat__Knowledge__PineconeIndexHost=<Host of glazeai-2>
Chat__Knowledge__Namespace=                    # the namespace glazeai-2 uses (Namespaces tab; empty = the default namespace)
Chat__Knowledge__EmbeddingModel=text-embedding-3-small
```

- **Check the embedding model first.** It must be the model the 28,856 knowledge vectors were built with.
  `text-embedding-ada-002` and `text-embedding-3-small` both make 1536 numbers but don't match each other.
  On the `glazeai-2` page, check the model shown in its configuration. If none is shown, ask whoever built
  it. A wrong model gives poor matches but no error.
- **More knowledge in the same index:** put it in new namespaces and list them, comma-separated, with
  `Chat__Knowledge__Namespaces=glazes,firing,materials`.
- **Knowledge in another index:** `Chat__Knowledge__Sources__0__IndexHost=<host>`,
  `Chat__Knowledge__Sources__0__Namespace=<namespace>`, and `Chat__Knowledge__Sources__0__EmbeddingModel=<model>`
  if it differs. Use `__1__` for the next one, and so on.
- Every source is searched for each question and the best 5 matches overall are used (`Chat__Knowledge__TopK`;
  matches below `MinScore` 0.35 are ignored). A source that fails is skipped.
- New knowledge entries need their text in a metadata field chat reads: title in `title` or `question`, body
  in `answer`, `content`, `text`, `body` or `description`.

Then set `SAMPLE_DATA.chat = false` in the apps (`packages/api-client/src/main.ts`). More detail: [chat.md](../features/chat.md).
