using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jev.Sdk;
using Jev.Sdk.Exceptions;
using Jev.Sdk.Models;
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

    /// <summary>Added to every request. Carries OpenRouter's attribution header when fronted by it.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

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

    /// <summary>
    /// OpenRouter fronts System One on its own shape rather than as chat completions, so the
    /// same body reaches the same path and only the host and the key change. Deliberately not
    /// the "/api/v1" the chat models use: the path below already carries the version.
    /// </summary>
    public const string OpenRouterEndpoint = "https://openrouter.ai/api";

    private const string EnthusiasmQuestion = "enthusiasm";
    private const string EmoticonQuestion   = "emoticon";

    /// <summary>Not in <see cref="HttpStatusCode"/>: TypeSafe returns it when the service is saturated.</summary>
    private const HttpStatusCode Overloaded = (HttpStatusCode)529;

    private readonly JevClient _client;
    private readonly JevOptions _options;
    private readonly ILogger<JevModel> _log;

    public JevModel(HttpClient http, JevOptions options, ILogger<JevModel> log)
    {
        _options = options;
        _log     = log;

        http.BaseAddress ??= new Uri(options.Endpoint.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        foreach ((string name, string value) in options.Headers)
            http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);

        _client = new JevClient(new HttpClient(new RelativePathHandler(http)) { BaseAddress = http.BaseAddress });

        // Unchanged by the transport: the descriptor's provider is what selects the
        // Routing.typesafe template and what the role guard checks.
        Descriptor = new ModelDescriptor(
            options.InstanceId, ProviderName, options.Model, ModelCapabilities.StructuredOutput);
    }

    public ModelDescriptor Descriptor { get; }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ToolDescriptor gate = RequireGateTool(request);

        Request evaluation = new();
        evaluation.SetModel(_options.Model);
        evaluation.SetState(State(request));
        evaluation.SetQuestions(Questions());

        long started = Stopwatch.GetTimestamp();

        Response response;

        try
        {
            response = await EvaluateAsync(evaluation, ct).ConfigureAwait(false);
        }
        catch (JevApiException e)
        {
            throw new HttpRequestException(
                $"{Descriptor} returned {(int)e.StatusCode}: {e.ResponseBody}", e, e.StatusCode);
        }

        TimeSpan latency = Stopwatch.GetElapsedTime(started);

        return new ModelResponse([Answer(response, gate.Name)], StopReason.ToolUse, UsageFrom(response, latency));
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

    private Dictionary<string, Question> Questions()
    {
        Question enthusiasm = new();
        enthusiasm.SetQuestionType(Question.QuestionType.Score);
        enthusiasm.SetInstructions(_options.Profile.Enthusiasm.Instructions);
        enthusiasm.SetCriteria(_options.Profile.Enthusiasm.Levels);

        Question emoticon = new();
        emoticon.SetQuestionType(Question.QuestionType.Choice);
        emoticon.SetInstructions(_options.Profile.Emoticon.Instructions);
        emoticon.SetCriteria(_options.Profile.Emoticon.Faces);

        return new Dictionary<string, Question>
        {
            [EnthusiasmQuestion] = enthusiasm,
            [EmoticonQuestion]   = emoticon
        };
    }

    /// <summary>
    /// Rebuilds the gate's tool call from the two answers. The score comes back as a weighted
    /// mean over the level numbers, so dividing by the top level restores the 0–1 the policy
    /// compares against its threshold — continuous, not quantised to the bands.
    /// </summary>
    private ToolUsePart Answer(Response response, string toolName)
    {
        if (response.GetAnswers() is not { } answers)
            throw new HttpRequestException($"{Descriptor} returned no answers: {response}");

        ScoreAnswer?  scored = answers.GetValueOrDefault(EnthusiasmQuestion) as ScoreAnswer;
        ChoiceAnswer? chosen = answers.GetValueOrDefault(EmoticonQuestion) as ChoiceAnswer;

        float enthusiasm = 0f;

        if (scored?.GetScore() is { } score)
        {
            int top = Math.Max(1, _options.Profile.Enthusiasm.Levels.Count - 1);

            enthusiasm = Math.Clamp((float)score / top, 0f, 1f);
        }

        string emoticon = chosen?.GetChoice() ?? "";

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
    private void LogConfidence(ScoreAnswer? scored, ChoiceAnswer? chosen, float enthusiasm, string emoticon)
    {
        if (!_log.IsEnabled(LogLevel.Debug)) return;

        _log.LogDebug(
            "Jev scored {Enthusiasm:0.00} (confidence {ScoreConfidence:0.00}) and chose {Emoticon} " +
            "(confidence {FaceConfidence:0.00})",
            enthusiasm, scored?.GetConfidence() ?? double.NaN, emoticon, chosen?.GetConfidence() ?? double.NaN);
    }

    /// <summary>Backs off on the two statuses the API documents as retryable. The SDK does not
    /// surface Retry-After, so the wait is always exponential.</summary>
    private async Task<Response> EvaluateAsync(Request request, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await _client.EvaluateAsync(request).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (JevApiException e) when (attempt < _options.MaxRetries &&
                                            e.StatusCode is HttpStatusCode.TooManyRequests or Overloaded)
            {
                TimeSpan wait = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(250));

                _log.LogWarning("{Descriptor} returned {Status}; retrying in {Wait}",
                    Descriptor, (int)e.StatusCode, wait);

                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    private TokenUsage UsageFrom(Response response, TimeSpan latency)
    {
        Usage? usage = response.GetUsage();

        return new TokenUsage(
            usage?.GetInputTokens() ?? 0, usage?.GetOutputTokens() ?? 0, 0, 0, Descriptor.InstanceId, latency);
    }

    /// <summary>
    /// The SDK posts to the rooted "/v1/systemone", which discards the path of an endpoint such as
    /// OpenRouter's "/api". This makes the path relative again and sends it through the configured client.
    /// </summary>
    private sealed class RelativePathHandler(HttpClient inner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpRequestMessage relative = new(
                request.Method, new Uri(request.RequestUri!.PathAndQuery.TrimStart('/'), UriKind.Relative))
            {
                Content = request.Content
            };

            foreach ((string name, IEnumerable<string> values) in request.Headers)
                relative.Headers.TryAddWithoutValidation(name, values);

            return inner.SendAsync(relative, ct);
        }
    }
}
