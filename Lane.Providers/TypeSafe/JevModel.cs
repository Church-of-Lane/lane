using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Providers.TypeSafe;

public sealed class JevOptions
{
    public required string InstanceId { get; init; }
    public required string ApiKey     { get; init; }

    public string Model    { get; init; } = "jev-latest";
    public string Endpoint { get; init; } = JevModel.DefaultEndpoint;

    /// <summary>Relative to <see cref="Endpoint"/>.</summary>
    public string EvaluatePath { get; init; } = "v1/systemone";

    public JevRoutingProfile Profile { get; init; } = new();

    /// <summary>Attempts after the first, for 429 and 529 only.</summary>
    public int MaxRetries { get; init; } = 3;
}

/// <summary>
/// TypeSafe's Jev, for the response policy gate and nothing else.
///
/// Jev is a System One model: it does not generate text, it evaluates typed questions
/// against a state and returns structured answers. That makes it unable to serve
/// "respond", "monologue" or "summarize" — there is nothing for it to write — and an
/// unusually good fit for the one role that never wanted prose in the first place. The
/// gate's two fields land on two primitives: enthusiasm on a Score, the emoticon on a
/// Choice, both in a single request, which the API evaluates in parallel.
///
/// The adapter presents this as a forced call to the gate's tool so that
/// <c>ResponsePolicyStage</c> needs no branch for it. The shape is checked on the way in
/// and refused loudly if it is anything else, because the alternative is a model that
/// looks bound correctly and fails mid-conversation.
/// </summary>
public sealed class JevModel : ILanguageModel
{
    public const string DefaultEndpoint = "https://api.typesafe.ai";
    public const string ProviderName    = "typesafe";

    private const string EnthusiasmQuestion = "enthusiasm";
    private const string EmoticonQuestion   = "emoticon";

    /// <summary>Not in <see cref="HttpStatusCode"/>: TypeSafe returns it when the service is saturated.</summary>
    private const int Overloaded = 529;

    private readonly HttpClient _http;
    private readonly JevOptions _options;
    private readonly ILogger<JevModel> _log;

    public JevModel(HttpClient http, JevOptions options, ILogger<JevModel> log)
    {
        _http    = http;
        _options = options;
        _log     = log;

        _http.BaseAddress ??= new Uri(options.Endpoint.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        Descriptor = new ModelDescriptor(
            options.InstanceId, ProviderName, options.Model, ModelCapabilities.StructuredOutput);
    }

    public ModelDescriptor Descriptor { get; }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ToolDescriptor gate = RequireGateTool(request);

        JsonObject body = new()
        {
            ["model"]     = _options.Model,
            ["state"]     = State(request),
            ["questions"] = Questions()
        };

        long started = Stopwatch.GetTimestamp();

        using HttpResponseMessage response = await SendAsync(body, ct).ConfigureAwait(false);

        TimeSpan latency = Stopwatch.GetElapsedTime(started);

        JsonElement root = await response.Content
            .ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{Descriptor} returned {(int)response.StatusCode}: {root.GetRawText()}");

        return new ModelResponse([Answer(root, gate.Name)], StopReason.ToolUse, UsageFrom(root, latency));
    }

    /// <summary>
    /// Jev answers all at once, so this is a single <see cref="ModelStreamEvent.Completed"/>.
    /// The gate never streams; the method exists because the port requires it.
    /// </summary>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ModelStreamEvent.Completed(await CompleteAsync(request, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Refuses any request that is not the response policy's forced call, since a System One
    /// model has no answer for one.
    /// </summary>
    private ToolDescriptor RequireGateTool(ModelRequest request)
    {
        if (request.ToolChoice.Mode != ToolChoiceMode.Specific || request.ToolChoice.ToolName is not { } name)
            throw new NotSupportedException(
                $"{Descriptor} evaluates typed questions and cannot generate text. It serves the " +
                "'routing' role only, which forces a single tool call.");

        ToolDescriptor? gate = request.Tools.FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.Ordinal));

        if (gate is null)
            throw new NotSupportedException($"{Descriptor} was forced to call '{name}', which was not supplied.");

        if (!HasProperty(gate, EnthusiasmQuestion) || !HasProperty(gate, EmoticonQuestion))
            throw new NotSupportedException(
                $"{Descriptor} answers the response policy's '{EnthusiasmQuestion}' and " +
                $"'{EmoticonQuestion}' questions; tool '{name}' asks for something else.");

        return gate;
    }

    private static bool HasProperty(ToolDescriptor tool, string name) =>
        tool.InputSchema.ValueKind == JsonValueKind.Object &&
        tool.InputSchema.TryGetProperty("properties", out JsonElement properties) &&
        properties.TryGetProperty(name, out _);

    /// <summary>
    /// What Jev is asked to evaluate. The system blocks carry the conversation as the
    /// provider-specific template renders it — context only, since the judgement itself has
    /// moved into the criteria.
    /// </summary>
    private static JsonObject State(ModelRequest request)
    {
        JsonArray latest = [];

        foreach (LaneMessage message in request.Messages)
        {
            if (message.TextContent is not { Length: > 0 } text) continue;

            latest.Add(new JsonObject
            {
                ["speaker"] = message.Author.DisplayName,
                ["text"]    = text
            });
        }

        return new JsonObject
        {
            ["context"] = string.Join("\n\n", request.System.Select(b => b.Text)),
            ["latest"]  = latest
        };
    }

    private JsonObject Questions()
    {
        JsonArray levels = [.. _options.Profile.Enthusiasm.Levels.Select(l => (JsonNode)JsonValue.Create(l))];

        JsonObject faces = [];

        foreach ((string face, string meaning) in _options.Profile.Emoticon.Faces)
            faces[face] = meaning;

        return new JsonObject
        {
            [EnthusiasmQuestion] = new JsonObject
            {
                ["type"]         = "score",
                ["instructions"] = _options.Profile.Enthusiasm.Instructions,
                ["criteria"]     = levels
            },
            [EmoticonQuestion] = new JsonObject
            {
                ["type"]         = "choice",
                ["instructions"] = _options.Profile.Emoticon.Instructions,
                ["criteria"]     = faces
            }
        };
    }

    /// <summary>
    /// Rebuilds the gate's tool call from the two answers. The score comes back as a weighted
    /// mean over the level numbers, so dividing by the top level restores the 0–1 the policy
    /// compares against its threshold — continuous, not quantised to the bands.
    /// </summary>
    private ToolUsePart Answer(JsonElement root, string toolName)
    {
        if (!root.TryGetProperty("answers", out JsonElement answers))
            throw new HttpRequestException($"{Descriptor} returned no answers: {root.GetRawText()}");

        float enthusiasm = 0f;

        if (answers.TryGetProperty(EnthusiasmQuestion, out JsonElement scored) &&
            scored.TryGetProperty("score", out JsonElement score) &&
            score.ValueKind == JsonValueKind.Number)
        {
            int top = Math.Max(1, _options.Profile.Enthusiasm.Levels.Count - 1);

            enthusiasm = Math.Clamp(score.GetSingle() / top, 0f, 1f);
        }

        string emoticon = answers.TryGetProperty(EmoticonQuestion, out JsonElement chosen) &&
                          chosen.TryGetProperty("choice", out JsonElement face)
            ? face.GetString() ?? ""
            : "";

        LogConfidence(scored, chosen, enthusiasm, emoticon);

        JsonObject arguments = new()
        {
            [EnthusiasmQuestion] = enthusiasm,
            [EmoticonQuestion]   = emoticon
        };

        return new ToolUsePart(
            Guid.NewGuid().ToString("n"), toolName, JsonSerializer.SerializeToElement(arguments));
    }

    /// <summary>Jev reports how spread its probabilities were, which nothing upstream has a field
    /// for. Logged rather than dropped: a gate that is confidently wrong and one that is guessing
    /// look identical in the response policy's own logs.</summary>
    private void LogConfidence(JsonElement scored, JsonElement chosen, float enthusiasm, string emoticon)
    {
        if (!_log.IsEnabled(LogLevel.Debug)) return;

        _log.LogDebug(
            "Jev scored {Enthusiasm:0.00} (confidence {ScoreConfidence:0.00}) and chose {Emoticon} " +
            "(confidence {FaceConfidence:0.00})",
            enthusiasm, Confidence(scored), emoticon, Confidence(chosen));
    }

    private static float Confidence(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object &&
        answer.TryGetProperty("confidence", out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : float.NaN;

    /// <summary>Backs off on the two statuses the API documents as retryable. Everything else is
    /// returned as-is for the caller to turn into an exception.</summary>
    private async Task<HttpResponseMessage> SendAsync(JsonObject body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage response = await _http
                .PostAsJsonAsync(_options.EvaluatePath.TrimStart('/'), body, ct).ConfigureAwait(false);

            int status = (int)response.StatusCode;

            if (attempt >= _options.MaxRetries ||
                (status != (int)HttpStatusCode.TooManyRequests && status != Overloaded))
                return response;

            TimeSpan wait = response.Headers.RetryAfter?.Delta
                            ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(250));

            response.Dispose();

            _log.LogWarning("{Descriptor} returned {Status}; retrying in {Wait}", Descriptor, status, wait);

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private TokenUsage UsageFrom(JsonElement root, TimeSpan latency)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage))
            return new TokenUsage(0, 0, 0, 0, Descriptor.InstanceId, latency);

        int Count(string name) =>
            usage.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;

        return new TokenUsage(
            Count("input_tokens"), Count("output_tokens"), 0, 0, Descriptor.InstanceId, latency);
    }
}
