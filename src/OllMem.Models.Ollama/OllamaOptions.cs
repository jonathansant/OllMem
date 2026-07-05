namespace OllMem.Models.Ollama;

/// <summary>
/// Configuration for the Ollama model backend. Ollama serves open-source models
/// locally over HTTP (default <c>http://localhost:11434</c>), so the default
/// OllMem model integration is completely free and needs no API key.
/// </summary>
public sealed class OllamaOptions
{
    /// <summary>
    /// Whether to use the Ollama backend at all. Enabled by default; when no
    /// server is reachable OllMem falls back to its offline defaults anyway, so
    /// disabling is only needed to pin the deterministic backend explicitly
    /// (recommended for persistent databases you have already populated).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of the Ollama server.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Open-source embedding model tag. <c>nomic-embed-text</c> (Apache-2.0,
    /// 768 dimensions) is small, fast and strong for retrieval. Any model tag
    /// pulled into Ollama works — the integration is model-agnostic.
    /// </summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>
    /// Open-weight chat model tag used for LLM fact extraction. Any Ollama chat
    /// model works; smaller tags trade accuracy for speed.
    /// </summary>
    public string ChatModel { get; set; } = "llama3.2";

    /// <summary>
    /// Use <see cref="ChatModel"/> for open-domain fact extraction. When the
    /// model is unavailable or returns unusable output, extraction transparently
    /// falls back to the built-in rule-based extractor.
    /// </summary>
    public bool UseChatExtraction { get; set; } = true;

    /// <summary>Timeout for the one-time availability probe at startup.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Timeout for individual embed/chat requests.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
