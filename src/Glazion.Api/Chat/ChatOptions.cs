namespace Glazion.Api.Chat;

/// <summary>Configuration section "Chat". Chat answers only when Provider is OpenAI and an API key is set.</summary>
public sealed class ChatOptions
{
    /// <summary>"OpenAI" (any OpenAI-compatible chat completions endpoint) or "Disabled".</summary>
    public string Provider { get; set; } = "Disabled";
    public ChatModelOptions OpenAI { get; set; } = new();
    public KnowledgeOptions Knowledge { get; set; } = new();
    /// <summary>Earlier messages sent to the model with each new question.</summary>
    public int HistoryMessages { get; set; } = 12;
    /// <summary>Tool rounds per answer before the model must reply in text.</summary>
    public int MaxToolRounds { get; set; } = 4;
    public int MaxMessageLength { get; set; } = 4000;
    public int TitleLength { get; set; } = 60;
}

public sealed class ChatModelOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.2;
    public int MaxOutputTokens { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 3;
}

/// <summary>
/// The pottery knowledge base from the first build (PotteryAPI): entries embedded with an OpenAI embedding model and
/// stored in Pinecone. The embedding model must be the one those vectors were built with.
/// </summary>
public sealed class KnowledgeOptions
{
    /// <summary>"Pinecone" or "None".</summary>
    public string Provider { get; set; } = "None";
    public string PineconeApiKey { get; set; } = string.Empty;
    /// <summary>Data-plane host of the knowledge index, e.g. https://pottery-xxxx.svc.us-east-1-aws.pinecone.io</summary>
    public string PineconeIndexHost { get; set; } = string.Empty;
    /// <summary>Empty = the default namespace, where the first build stored its knowledge entries.</summary>
    public string Namespace { get; set; } = string.Empty;
    /// <summary>More namespaces on the same index, comma-separated (e.g. "glazes,firing,materials"), searched alongside Namespace.</summary>
    public string Namespaces { get; set; } = string.Empty;
    /// <summary>Knowledge in other indexes (Chat__Knowledge__Sources__0__IndexHost, …__Namespace, …__EmbeddingModel).</summary>
    public List<KnowledgeIndexSource> Sources { get; set; } = [];
    public string PineconeApiVersion { get; set; } = "2025-01";
    /// <summary>Embeddings endpoint for questions; defaults to the chat model's OpenAI base URL and key.</summary>
    public string EmbeddingBaseUrl { get; set; } = string.Empty;
    public string EmbeddingApiKey { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public int TopK { get; set; } = 5;
    /// <summary>Matches below this cosine score are not used or cited.</summary>
    public double MinScore { get; set; } = 0.35;
    /// <summary>A best match at or above this score makes a knowledge-grounded answer "high" confidence.</summary>
    public double HighScore { get; set; } = 0.6;
    /// <summary>Metadata fields tried, in order, for an entry's title and body.</summary>
    public List<string> TitleFields { get; set; } = ["title", "question", "topic", "name"];
    public List<string> TextFields { get; set; } = ["answer", "content", "text", "body", "description"];
}

/// <summary>One more place knowledge is stored. EmbeddingModel defaults to Knowledge.EmbeddingModel and must be the model its vectors were built with.</summary>
public sealed class KnowledgeIndexSource
{
    public string IndexHost { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string? EmbeddingModel { get; set; }
}
