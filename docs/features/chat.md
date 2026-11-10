# Chat

The main app's assistant (PRD CHAT-01..06), served in the shapes of `@glazion/types` (`Conversation`, `ChatMessage`,
`ChatSource`, `ChatToolCall`) and called by `packages/api-client` `chatApi`.

## Endpoints

| Call | Endpoint | Returns |
|---|---|---|
| list | `GET /api/v2/conversations?cursor=&pageSize=` | `PagedResult<ConversationSummary>`, most recently used first |
| create | `POST /api/v2/conversations` (`{}` or `{ "title" }`) | 201 `Conversation` with `messages: []` |
| open | `GET /api/v2/conversations/{id}` | `Conversation` with its messages, oldest first |
| send | `POST /api/v2/conversations/{id}/messages` `{ "content", "tile"? }` | `{ userMessage, assistantMessage, conversation }` |
| rename | `PATCH /api/v2/conversations/{id}` `{ "title" }` | `ConversationSummary` |
| delete | `DELETE /api/v2/conversations/{id}` | 204; messages and feedback go with it |
| feedback | `POST /api/v2/chat/feedback` `{ messageId, feedback: "helpful" \| "not_helpful", note? }` | 204; one per user and answer, later feedback replaces it |

All need an Active account. A conversation is visible only to the user who started it: anyone else gets 404
`conversation_not_found`. A new conversation is titled from its first question (up to 60 characters).

`tile` (`{ searchId, imageId }`) says the message is about a result of one of the user's own searches, as the apps'
"Ask Glazion about this tile" sends it. The model gets the tile's labels, firing and recipe, with the recipe id for
`get_recipe`, as a system message just before the question. It is stored with the question (`ChatMessages.Context`)
so follow-ups keep it, and never returned: the conversation shows only `content`. A search that isn't the user's is
404 `search_not_found`; an image the search didn't return is 422 `image_not_in_results`.

## How an answer is made

1. The question is embedded and matched against the pottery knowledge base (the first build's Pinecone index).
   Matches at or above `MinScore` go to the model as excerpts and come back as `knowledge` sources.
2. The model (OpenAI chat completions) answers, calling tools when it needs data. Each tool runs one of this API's
   services as the signed-in user, with the same visibility as the matching endpoint:

   | Tool | Runs | Adds |
   |---|---|---|
   | `calculate_umf` | the UMF calculator | — |
   | `search_recipe_images` | recipe-to-image search (honours `Features:RecipeImageRetrieval`) | `searchId`, `image` sources |
   | `get_recipe` | any active recipe (recipes are system scoped) | a `recipe` source |
   | `get_upload_status` | the user's own uploads | — |
   | `get_material` | the materials list | a `material` source |

   Up to `MaxToolRounds` rounds; the last request offers no tools, so the model must answer.

   Tile photos reach the apps only through the `image` sources and the `searchId` (open the search for display URLs).
   The answer text names tiles as "Tile #rank · recipe code". The model is told never to write image links, and any
   markdown image or link to a placeholder host (example.com, `*.example`, relative targets) is removed before the
   answer is stored. Real links stay.
3. `confidence`: `high` when a tool returned data or the best knowledge match scores at least `HighScore`; `medium`
   with weaker knowledge; `low` when nothing grounded the answer, so the UI can say so (CHAT-06).

The question and the answer are stored together only when the model replied. A failed send stores nothing and can be
retried: 503 `chat_unavailable` when chat is not configured, 502 `chat_model_unavailable` when the model did not answer.
A knowledge-base failure does not fail the send; the answer is then marked `low`.

## Configuration

Chat is off until configured. Knowledge can come from several Pinecone namespaces and indexes
(`Chat__Knowledge__Namespaces`, `Chat__Knowledge__Sources__N__*`); see [deploy/pinecone.md](../deploy/pinecone.md).
Railway service variables:

```
Chat__Provider=OpenAI
Chat__OpenAI__ApiKey=<secret>
Chat__OpenAI__Model=gpt-4o-mini

Chat__Knowledge__Provider=Pinecone
Chat__Knowledge__PineconeApiKey=<secret>
Chat__Knowledge__PineconeIndexHost=https://<index>-<project>.svc.<region>.pinecone.io
Chat__Knowledge__Namespace=                      # empty = default namespace (the first build's knowledge entries)
Chat__Knowledge__EmbeddingModel=text-embedding-3-small
```

`EmbeddingModel` must be the model the knowledge vectors were built with. The first build's code refers to both
`text-embedding-ada-002` and `text-embedding-3-small`; both give 1536 numbers but are not interchangeable, so check
which one built the index before switching knowledge on (re-embed a stored entry's text with each and compare it with
the stored vector). The entry's title and body are read from the first metadata field present in
`Chat:Knowledge:TitleFields` / `TextFields` (defaults cover `title`/`question` and `answer`/`content`/`text`).

Other settings: `Chat:HistoryMessages` (12), `Chat:MaxToolRounds` (4), `Chat:MaxMessageLength` (4000),
`Chat:Knowledge:TopK` (5), `MinScore` (0.35), `HighScore` (0.6), `Chat:OpenAI:BaseUrl` for an OpenAI-compatible gateway.

In the apps, set `SAMPLE_DATA.chat = false` in `packages/api-client/src/main.ts` once chat is configured.
