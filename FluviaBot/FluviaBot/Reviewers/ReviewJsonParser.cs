using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluviaBot.Models;
using Microsoft.Extensions.Logging;

namespace FluviaBot.Review;

/// <summary>
/// Parses the JSON review object that an LLM returns into a strongly-typed
/// <see cref="CodeReview"/>. This is the single definition of the review JSON
/// contract, shared by every chat-based reviewer.
///
/// Robustness rules:
///   - Strips ```json … ``` fences and surrounding prose before parsing.
///   - On malformed JSON, returns a *degraded* <see cref="CodeReview"/> that
///     carries the raw model output, rather than throwing — a single bad
///     response must not crash the whole webhook.
/// </summary>
public static class ReviewJsonParser
{
    /// <summary>
    /// Parses raw model output into a <see cref="CodeReview"/>. Never throws
    /// for content reasons; the worst case is a degraded review.
    /// </summary>
    /// <param name="raw">The model's reply text.</param>
    /// <param name="logger">Logger for parse failures.</param>
    /// <param name="providerName">Provider id, for log context only.</param>
    public static CodeReview Parse(string raw, ILogger logger, string providerName)
    {
        var json = ExtractJson(raw);

        JsonNode? doc;
        try
        {
            doc = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "{Provider}: failed to parse model response as JSON.", providerName);
            return new CodeReview(
                $"Review could not be parsed. Raw model output:\n\n{raw}",
                Array.Empty<FileReview>());
        }

        if (doc is null)
            return new CodeReview("Model returned an empty response.", Array.Empty<FileReview>());

        var summary = doc["summary"]?.GetValue<string>() ?? "No summary provided.";
        var fileReviews = new List<FileReview>();

        foreach (var fr in doc["fileReviews"]?.AsArray() ?? new JsonArray())
        {
            if (fr is null) continue;

            var findings = new List<Finding>();
            foreach (var f in fr["findings"]?.AsArray() ?? new JsonArray())
            {
                if (f is null) continue;

                var severity = Enum.TryParse<Severity>(
                    f["severity"]?.GetValue<string>(), ignoreCase: true, out var s)
                        ? s : Severity.Info;

                findings.Add(new Finding(
                    Severity: severity,
                    Description: f["description"]?.GetValue<string>() ?? "",
                    AgentPrompt: f["agentPrompt"]?.GetValue<string>() ?? "",
                    Location: f["location"]?.GetValue<string>()));
            }

            fileReviews.Add(new FileReview(
                fr["filename"]?.GetValue<string>() ?? "unknown",
                findings));
        }

        return new CodeReview(summary, fileReviews);
    }

    /// <summary>
    /// Extracts the first JSON object from a string that may be wrapped in
    /// markdown code fences (```json … ```) or padded with surrounding prose.
    /// </summary>
    private static string ExtractJson(string raw)
    {
        // ```json { … } ``` or ``` { … } ```
        var fenceMatch = Regex.Match(raw, @"```(?:json)?\s*(\{[\s\S]*?\})\s*```");
        if (fenceMatch.Success)
            return fenceMatch.Groups[1].Value;

        // Fall back to the outermost { … } span.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
            return raw[start..(end + 1)];

        // Hand it to the parser as-is and let it report the error.
        return raw;
    }
}
