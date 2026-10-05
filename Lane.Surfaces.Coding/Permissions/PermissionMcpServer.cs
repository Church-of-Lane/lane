using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lane.Surfaces.Coding.Permissions;

/// <summary>
/// The MCP server Claude Code calls through <c>--permission-prompt-tool</c>. Loopback only. Each
/// workspace's Claude Code gets its own bearer token, which identifies the workspace.
/// </summary>
public sealed class PermissionMcpServer(
    PermissionBroker broker,
    int port,
    ILogger<PermissionMcpServer> log) : IHostedService, IAsyncDisposable
{
    public const string ServerName = "lane";
    public const string ToolName   = "permission_prompt";
    private const string WorkspaceClaim = "lane:workspace";

    private readonly ConcurrentDictionary<string, string> _workspaceByToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokenByWorkspace = new(StringComparer.Ordinal);

    private WebApplication? _app;

    public string Endpoint => $"http://127.0.0.1:{port}/mcp";

    /// <summary>The <c>--mcp-config</c> JSON that lets one workspace's Claude Code reach this server.</summary>
    public string McpConfigFor(string workspaceId)
    {
        string token = _tokenByWorkspace.GetOrAdd(workspaceId, id =>
        {
            string minted = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            _workspaceByToken[minted] = id;
            return minted;
        });

        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"]    = "http",
                    ["url"]     = Endpoint,
                    ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" }
                }
            }
        }.ToJsonString();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        builder.Services.AddHttpContextAccessor();
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = ServerName, Version = "1.0" })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [Describe()] }))
            .WithCallToolHandler(CallAsync);

        WebApplication app = builder.Build();

        app.Use(async (context, next) =>
        {
            string? header = context.Request.Headers.Authorization;
            string? token  = header?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? header[7..].Trim() : null;

            if (token is null || !_workspaceByToken.TryGetValue(token, out string? workspace))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkspaceClaim, workspace)], "bearer"));

            await next(context).ConfigureAwait(false);
        });

        app.MapMcp("/mcp");

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "The Claude Code permission server could not listen on {Endpoint}; " +
                             "Claude Code's permission requests will be denied", Endpoint);
            await app.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _app = app;

        log.LogInformation("Claude Code permission server listening on {Endpoint}", Endpoint);
    }

    private static Tool Describe() => new()
    {
        Name        = ToolName,
        Description = "Asks Lane whether Claude Code may use a tool.",
        InputSchema = JsonSerializer.SerializeToElement(new
        {
            type       = "object",
            properties = new
            {
                tool_name   = new { type = "string" },
                input       = new { type = "object" },
                tool_use_id = new { type = "string" }
            },
            required = new[] { "tool_name", "input" }
        })
    };

    private async ValueTask<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        if (request.Params?.Name != ToolName) return Error($"Unknown tool '{request.Params?.Name}'.");

        string? workspace =
            request.JsonRpcRequest.Context?.User?.FindFirst(WorkspaceClaim)?.Value ??
            request.Services?.GetService<IHttpContextAccessor>()?.HttpContext?.User.FindFirst(WorkspaceClaim)?.Value;

        if (workspace is null) return Error("This call is not tied to a workspace.");

        IDictionary<string, JsonElement> args = request.Params.Arguments ?? new Dictionary<string, JsonElement>();

        string tool = args.TryGetValue("tool_name", out JsonElement name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()!
            : "unknown";

        JsonElement input = args.TryGetValue("input", out JsonElement value) ? value : JsonSerializer.SerializeToElement(new { });

        PermissionVerdict verdict = await broker.RequestAsync(workspace, tool, input, ct).ConfigureAwait(false);

        log.LogInformation("Claude Code in {Workspace} {Verdict} {Tool}",
            workspace, verdict.Allow ? "may use" : "may not use", tool);

        return new CallToolResult { Content = [new TextContentBlock { Text = verdict.ToToolResult(input) }] };
    }

    private static CallToolResult Error(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is null) return;

        try { await _app.StopAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { log.LogWarning(ex, "The permission server did not stop cleanly"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync().ConfigureAwait(false);
    }
}
