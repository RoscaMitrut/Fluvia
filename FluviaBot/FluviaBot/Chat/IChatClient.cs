namespace FluviaBot.Chat;

/// <summary>
/// Provider-agnostic chat transport: takes a system prompt plus a list of
/// turns, sends them to one LLM provider, and returns the assistant's text
/// reply. This is the only provider-specific seam — review and Q&amp;A are both
/// composed on top of it, so a new provider lights up both flows at once.
///
/// LangGraph is intentionally NOT an IChatClient: it is a multi-model
/// fan-out/merge pipeline, not a single completion, so it stays its own
/// <c>ICodeReviewer</c>.
/// </summary>
public interface IChatClient
{
    /// <summary>
    /// A short identifier for the underlying provider ("ollama", "anthropic",
    /// …). Used only for logging.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Send a conversation to the provider and return the assistant's reply
    /// as plain text. Implementations must throw <see cref="HttpRequestException"/>
    /// (with the response body in the message) on a non-success HTTP status,
    /// and must let <see cref="OperationCanceledException"/> propagate.
    /// </summary>
    Task<string> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}

/// <summary>The role of a single turn in a chat conversation.</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>One turn in a chat conversation.</summary>
/// <param name="Role">Who is speaking.</param>
/// <param name="Content">The message text.</param>
public readonly record struct ChatMessage(ChatRole Role, string Content)
{
    public static ChatMessage System(string content) => new(ChatRole.System, content);
    public static ChatMessage User(string content) => new(ChatRole.User, content);
    public static ChatMessage Assistant(string content) => new(ChatRole.Assistant, content);
}

/// <summary>
/// Everything a chat client needs for one completion. The system prompt is
/// kept separate from <see cref="Messages"/> because providers place it
/// differently on the wire (Anthropic: top-level <c>system</c>; Google:
/// <c>systemInstruction</c>; OpenAI-compatible: a <c>system</c> role turn).
/// </summary>
/// <param name="SystemPrompt">Optional system prompt. May be null or empty.</param>
/// <param name="Messages">The conversation turns, in order.</param>
/// <param name="MaxTokens">Upper bound on the response length.</param>
/// <param name="JsonMode">
/// When true, asks the provider to constrain output to JSON where supported.
/// Providers that cannot honour this ignore it, so callers must still tolerate
/// prose-wrapped JSON (see <c>ReviewJsonParser</c>).
/// </param>
public sealed record ChatRequest(
    string? SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    int MaxTokens = 4096,
    bool JsonMode = false);
