using Lane.Core;
using Lane.Audio;
using Lane.Audio.Capture;
using Lane.Core.Agent;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Forum;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Lifecycle;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Monologue;
using Lane.Core.Pipeline.Stages;
using Lane.Core.Prompts;
using Lane.Core.Recording;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Core.Surfaces;
using Lane.Host.Configuration;
using Lane.Host.Lifecycle;
using Lane.Host.Logging;
using Lane.Host.Presence;
using Lane.Host.Web;
using Lane.Memory;
using Lane.Memory.Sqlite;
using Lane.Core.Credits;
using Lane.Core.Nodes;
using Lane.Nodes;
using Lane.Nodes.Portal;
using Lane.Tools;
using Lane.Tools.Mcp;
using Lane.Providers.Anthropic;
using Lane.Providers.OpenAi;
using Lane.Providers.TypeSafe;
using Lane.Surfaces.Api;
using Lane.Surfaces.Coding;
using Lane.Surfaces.Discord;
using Lane.Surfaces.Terminal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Host;

public static class LaneHostBuilderExtensions
{
    /// <summary>
    /// Wires the kernel, then instantiates every configured model and surface instance.
    ///
    /// Note what is absent: no static settings singleton, no "which body am I" switch. The
    /// host reads instance arrays and constructs each one through a keyed factory, so
    /// adding a second Discord bot is a config entry rather than a code change.
    /// </summary>
    /// <param name="restart">What the startup check found; null when it was not run.</param>
    public static IHostApplicationBuilder AddLane(
        this IHostApplicationBuilder builder, BufferedLogSink logs, bool runDashboard = true, RestartStartup? restart = null)
    {
        IConfigurationSection section = builder.Configuration.GetSection(LaneOptions.SectionName);

        builder.Services.AddLaneCore();
        builder.Services.AddSingleton<ISecretResolver, SecretResolver>();

        builder.Services.Configure<SessionOptions>(section.GetSection("Sessions"));
        builder.Services.Configure<AgentOptions>(section.GetSection("Agent"));
        builder.Services.Configure<ResponsePolicyOptions>(section.GetSection("ResponsePolicy"));

        PromptOptions prompts = new();
        section.GetSection("Prompts").Bind(prompts);
        builder.Services.AddLanePrompts(prompts);

        RegisterIdentities(builder.Services, section.GetSection("Identities"));
        RegisterSessionDescriptions(builder.Services);
        RegisterMonologue(builder.Services, section.GetSection("Monologue"));
        RegisterEnergy(builder.Services, section.GetSection("Energy"));
        RegisterFace(builder.Services, section.GetSection("Face"));
        RegisterAudio(builder.Services, section.GetSection("Audio"));
        RegisterMemory(builder.Services, section.GetSection("Memory"));
        RegisterTools(builder.Services, section.GetSection("Tools"));
        RegisterSurfaceFactories(builder.Services);
        RegisterNodes(builder.Services, section.GetSection("Nodes"));
        RegisterRecording(builder.Services, section.GetSection("Recording"));
        RegisterModels(builder.Services, section.GetSection("Models"));
        RegisterSurfaces(builder.Services, section.GetSection("Surfaces"));
        RegisterRestart(builder.Services, section.GetSection("Restart"), restart);
        RegisterCoding(builder.Services, section.GetSection("Coding"));

        if (runDashboard) RegisterWebDashboard(builder.Services, section.GetSection("Dashboard"), logs);

        return builder;
    }

    /// <summary>
    /// The dashboard that used to be a Terminal.Gui layout is now a small HTTP server: it
    /// never touches the console, so the terminal surface is free to just be stdin and
    /// stdout again rather than handing its reader and writer to a UI that owned the screen.
    /// </summary>
    private static void RegisterWebDashboard(
        IServiceCollection services, IConfigurationSection section, BufferedLogSink logs)
    {
        services.AddSingleton(logs);
        services.Configure<DashboardOptions>(section);

        services.AddSingleton<IHostedService>(sp => new WebDashboardServer(
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ISessionRegistry>(),
            sp.GetRequiredService<ILanguageModelRegistry>(),
            logs,
            sp.GetRequiredService<IMonologueScheduler>(),
            sp.GetRequiredService<IEnergyService>(),
            sp.GetRequiredService<IOptions<DashboardOptions>>(),
            sp.GetRequiredService<IRestartCoordinator>(),
            sp.GetRequiredService<IAgentKernel>(),
            sp.GetRequiredService<ITranscriptStore>(),
            sp.GetRequiredService<IIdentityResolver>(),
            sp.GetRequiredService<ILogger<WebDashboardServer>>(),
            sp.GetService<NodePortal>(),
            sp.GetService<ICodingWorkspaces>()));
    }

    private static void RegisterRestart(IServiceCollection services, IConfigurationSection section, RestartStartup? startup)
    {
        RestartOptions options = new();
        section.Bind(options);

        services.AddSingleton(options);
        services.AddSingleton<IProcessRunner>(ProcessRunner.Instance);

        services.AddSingleton(sp => new SelfUpdater(
            options,
            startup ?? new RestartStartup(null, ExitNow: false),
            AppContext.BaseDirectory,
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<ILogger<SelfUpdater>>()));
        services.AddSingleton<IRestartCoordinator>(sp => sp.GetRequiredService<SelfUpdater>());
        services.AddHostedService(sp => sp.GetRequiredService<SelfUpdater>());
    }

    /// <summary>
    /// Reads the coding section. An omitted rule list takes its default and an empty one stays
    /// empty. Token references are resolved.
    /// </summary>
    internal static CodingOptions ReadCodingOptions(IConfigurationSection section)
    {
        CodingOptions options = new();
        section.Bind(options);

        if (!section.GetSection("AutoAllow").Exists())       options.AutoAllow       = [.. CodingOptions.DefaultAutoAllow];
        if (!section.GetSection("AlwaysDeny").Exists())      options.AlwaysDeny      = [.. CodingOptions.DefaultAlwaysDeny];
        if (!section.GetSection("PassEnvironment").Exists()) options.PassEnvironment = [.. CodingOptions.DefaultPassEnvironment];

        SecretResolver secrets = new();

        options.GitHubToken = secrets.Resolve(options.GitHubTokenRef);

        foreach (CodingWorkspaceOptions workspace in options.Workspaces)
            workspace.GitHubToken = secrets.Resolve(workspace.GitHubTokenRef);

        return options;
    }

    private static void RegisterCoding(IServiceCollection services, IConfigurationSection section)
    {
        CodingOptions options = ReadCodingOptions(section);

        if (!options.Enabled) return;

        services.AddLaneCoding(options);
    }

    /// <summary>
    /// Fails fast when a role that will be asked to call tools is bound to a model that
    /// cannot call them. With no prompt-JSON fallback, silence here would mean discovering
    /// it mid-conversation.
    /// </summary>
    public static IHost ValidateLane(this IHost host)
    {
        ILanguageModelRegistry registry = host.Services.GetRequiredService<ILanguageModelRegistry>();

        if (registry is LanguageModelRegistry concrete)
            concrete.ValidateCapabilities([ModelRole.Respond, ModelRole.Monologue]);

        ValidateEvaluatorRoles(registry);

        return host;
    }

    /// <summary>
    /// Holds System One models to the one role they can serve.
    ///
    /// Jev evaluates typed questions and produces no prose, so a binding to "respond" or
    /// "summarize" is not a degraded answer but no answer at all. The capability check above
    /// cannot see this — the roles it guards are the ones this model has no business in — so
    /// the binding itself is what gets checked, at startup, where every other misbinding is caught.
    /// </summary>
    private static void ValidateEvaluatorRoles(ILanguageModelRegistry registry)
    {
        List<string> problems = [];

        foreach ((string role, string instance) in registry.RoleBindings)
        {
            if (role.Equals(ModelRole.Routing.Value, StringComparison.OrdinalIgnoreCase)) continue;

            ILanguageModel model = registry.All.First(m =>
                m.Descriptor.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase));

            if (model.Descriptor.Provider.Equals(JevModel.ProviderName, StringComparison.OrdinalIgnoreCase))
                problems.Add($"role '{role}' → {model.Descriptor}");
        }

        if (problems.Count > 0)
            throw new ModelBindingException(
                $"The '{JevModel.ProviderName}' provider evaluates typed questions and cannot generate " +
                $"text, so it serves the '{ModelRole.Routing}' role only: " + string.Join("; ", problems) + ".");
    }

    /// <summary>
    /// Links one person's accounts across surfaces, so User-scoped memory follows them from
    /// Discord to the terminal to the API rather than fragmenting per surface.
    ///
    /// Two sources, and they are not equals: the configured map below, and whatever
    /// <c>link_identity</c> has proved in conversation. Registered whether or not anything
    /// is configured, since the second source does not need the first — and started before
    /// any surface, so the first message of a run resolves to the right person.
    /// </summary>
    private static void RegisterIdentities(IServiceCollection services, IConfigurationSection section)
    {
        Dictionary<string, IReadOnlyList<string>> identities = [];

        foreach (IConfigurationSection person in section.GetChildren())
        {
            string[] accounts = [.. person.GetChildren().Select(c => c.Value).OfType<string>()];

            if (accounts.Length > 0) identities[person.Key] = accounts;
        }

        services.AddSingleton<IdentityDirectory>();
        services.AddSingleton<IIdentityDirectory>(sp => sp.GetRequiredService<IdentityDirectory>());
        services.AddHostedService(sp => sp.GetRequiredService<IdentityDirectory>());

        // Voices are kept apart from accounts deliberately: a link somebody proved and a
        // voice that merely sounded right are different kinds of claim, and merging the two
        // stores would leave no way to ask afterwards which one a memory rested on.
        services.AddSingleton<VoiceprintDirectory>();
        services.AddSingleton<IVoiceprintDirectory>(sp => sp.GetRequiredService<VoiceprintDirectory>());
        services.AddHostedService(sp => sp.GetRequiredService<VoiceprintDirectory>());

        services.AddSingleton<IIdentityResolver>(sp => new IdentityResolver(
            identities,
            sp.GetService<ILogger<IdentityResolver>>(),
            sp.GetRequiredService<IIdentityDirectory>()));
    }

    /// <summary>
    /// What Lane has written about each conversation, read into memory at startup for the
    /// same reason identity links are: it is wanted while a prompt is being built, on every
    /// turn, and a store round-trip there buys nothing.
    /// </summary>
    private static void RegisterSessionDescriptions(IServiceCollection services)
    {
        services.AddSingleton<SessionDescriptions>();
        services.AddSingleton<ISessionDescriptions>(sp => sp.GetRequiredService<SessionDescriptions>());
        services.AddHostedService(sp => sp.GetRequiredService<SessionDescriptions>());

        services.AddSingleton<SessionThresholds>();
        services.AddSingleton<ISessionThresholds>(sp => sp.GetRequiredService<SessionThresholds>());
        services.AddHostedService(sp => sp.GetRequiredService<SessionThresholds>());
    }

    private static void RegisterMonologue(IServiceCollection services, IConfigurationSection section)
    {
        MonologueOptions options = new();
        section.Bind(options);

        if (!options.Enabled) return;

        services.AddLaneMonologue(o =>
        {
            o.Enabled          = options.Enabled;
            o.Interval         = options.Interval;
            o.AfterMessage     = options.AfterMessage;
            o.MinInterval      = options.MinInterval;
            o.MaxInterval      = options.MaxInterval;
            o.StartupDelay     = options.StartupDelay;
            o.Prompt           = options.Prompt;
            o.MaxOutputTokens  = options.MaxOutputTokens;
            o.SeeAllMessages   = options.SeeAllMessages;
            o.AllMessagesLimit = options.AllMessagesLimit;
        });
    }

    /// <summary>
    /// Reads the energy section, taking care over the lists in it.
    ///
    /// <c>Bind</c> *appends* to a list that already has items rather than replacing it, so
    /// binding straight onto the code defaults would merge the two: every configured entry
    /// would arrive twice, and <c>"DenyTools": []</c> could never turn a default off — which is
    /// the one thing an operator writing an empty array is trying to do. So the lists are
    /// cleared first and restored only where the file says nothing, leaving an omitted list
    /// with its default and an empty one taken at its word.
    ///
    /// Internal rather than private so the shape of the shipped configuration can be asserted
    /// without standing up a host and its secrets.
    /// </summary>
    internal static EnergyOptions ReadEnergyOptions(IConfigurationSection section)
    {
        EnergyOptions defaults = new();
        EnergyOptions options  = new();

        options.WakeWords.Clear();
        options.Tired.DenyTools.Clear();
        options.Weary.DenyTools.Clear();

        section.Bind(options);

        if (!section.GetSection("WakeWords").Exists())       options.WakeWords       = defaults.WakeWords;
        if (!section.GetSection("Tired:DenyTools").Exists()) options.Tired.DenyTools = defaults.Tired.DenyTools;
        if (!section.GetSection("Weary:DenyTools").Exists()) options.Weary.DenyTools = defaults.Weary.DenyTools;

        return options;
    }

    /// <summary>
    /// Her metabolism. Bound and re-applied field by field, like the monologue, so that the
    /// options the stages read are the same object the enabled check was made against — and so
    /// a disabled section leaves <c>BoundlessEnergy</c> in place rather than a service that has
    /// to remember to do nothing.
    /// </summary>
    private static void RegisterEnergy(IServiceCollection services, IConfigurationSection section)
    {
        EnergyOptions options = ReadEnergyOptions(section);

        if (!options.Enabled) return;

        if (options.Budget <= 0)
            throw new InvalidOperationException("Lane:Energy:Budget must be positive when energy is enabled.");

        if (options.AccrualInterval <= TimeSpan.Zero || options.Window <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "Lane:Energy:Window and Lane:Energy:AccrualInterval must both be positive. " +
                "Note that a whole day is \"1.00:00:00\", not \"24:00:00\", which TimeSpan rejects.");

        services.AddLaneEnergy(o =>
        {
            o.Enabled              = options.Enabled;
            o.Budget               = options.Budget;
            o.Window               = options.Window;
            o.AccrualInterval      = options.AccrualInterval;
            o.TiredBelow           = options.TiredBelow;
            o.WearyBelow           = options.WearyBelow;
            o.WakeAt               = options.WakeAt;
            o.CacheReadWeight      = options.CacheReadWeight;
            o.StartFull            = options.StartFull;
            o.SkipInDirectSessions = options.SkipInDirectSessions;
            o.WakeWords            = options.WakeWords;
            o.Tired                = options.Tired;
            o.Weary                = options.Weary;
        });
    }

    private static void RegisterFace(IServiceCollection services, IConfigurationSection section)
    {
        services.Configure<FaceOptions>(section);
        services.AddHostedService<FaceServer>();
    }

    private static void RegisterAudio(IServiceCollection services, IConfigurationSection section)
    {
        AudioSetupOptions options = new();
        section.Bind(options);

        if (!options.Enabled) return;

        // Resolved at composition, as with every other credential — a part that fetches its
        // own can only ever have one set.
        SecretResolver secrets = new();

        options.Recognition.Key    = secrets.Resolve(section["Recognition:KeyRef"]) ?? options.Recognition.Key;
        options.Recognition.Region = secrets.Resolve(section["Recognition:RegionRef"]) ?? options.Recognition.Region;
        options.Synthesis.ApiKey   = secrets.Resolve(section["Synthesis:KeyRef"]) ?? options.Synthesis.ApiKey;

        // Throws on an unknown name, here rather than at the first clause.
        SpeechProvider provider = options.ResolveProvider();

        if (string.IsNullOrWhiteSpace(options.Recognition.Key))
        {
            throw new InvalidOperationException(
                "Lane:Audio is enabled but the recognition key is missing. Set Recognition:KeyRef " +
                "and Recognition:RegionRef, or turn Audio off.");
        }

        // Checked here rather than at the first utterance. A missing model does not stop
        // Lane telling the people in a room apart; it stops her recognising one of them
        // tomorrow, and that is not a difference anybody would notice until the day after.
        if (options.Voiceprints.Enabled && !File.Exists(options.Voiceprints.ModelPath))
        {
            throw new InvalidOperationException(
                $"Lane:Audio:Voiceprints is enabled but the model '{options.Voiceprints.ModelPath}' " +
                "does not exist. Download a WeSpeaker ONNX export to that path, or turn Voiceprints off.");
        }

        // Only the provider that is actually going to speak has to be configured — flite
        // runs locally and has no key at all, which is most of the reason it is here.
        if (provider is SpeechProvider.ElevenLabs && string.IsNullOrWhiteSpace(options.Synthesis.ApiKey))
        {
            throw new InvalidOperationException(
                "Lane:Audio:Provider is 'elevenlabs' but Synthesis:KeyRef resolved to nothing. Set it, " +
                "switch Provider to 'flite', or turn Audio off.");
        }

        services.AddLaneAudio(options);
    }

    private static void RegisterMemory(IServiceCollection services, IConfigurationSection section)
    {
        SqliteOptions sqlite = new();
        section.GetSection("Sqlite").Bind(sqlite);

        MemoryOptions memory = new();
        section.Bind(memory);

        // Handlers bind by hand because Scope and Slot are enums the binder would happily
        // leave at their defaults on a typo — and a handler that silently becomes Session
        // scope instead of Global is the kind of bug you notice months later.
        memory.Handlers = [.. section.GetSection("Handlers").GetChildren().Select(BindHandler)];

        if (memory.Handlers.Count == 0)
            throw new InvalidOperationException(
                "Lane:Memory:Handlers is empty. Lane would not remember anything between turns.");

        services.AddLaneMemory(sqlite, o =>
        {
            o.Handlers      = memory.Handlers;
            o.IdleEviction  = memory.IdleEviction;
            o.FlushInterval = memory.FlushInterval;
            o.MaxInstances  = memory.MaxInstances;
        });
    }

    private static void RegisterTools(IServiceCollection services, IConfigurationSection section)
    {
        services.Configure<ToolOptions>(section);

        ToolsSetupOptions setup = new();
        section.GetSection("Books").Bind(setup.Books);
        section.GetSection("Search").Bind(setup.Search);
        section.GetSection("Identity").Bind(setup.Identity);

        // Resolved here, at composition, rather than read from inside the tool. A part that
        // fetches its own credentials can only ever have one set of them — which is exactly
        // why v2's speech synthesiser could never hold two voices.
        setup.Search.ApiKey = new SecretResolver().Resolve(section["Search:KeyRef"]);

        services.AddLaneBuiltinTools(setup);

        RegisterMcp(services, section.GetSection("Mcp"));
    }

    /// <summary>
    /// Connects the MCP servers named in configuration, and only those.
    ///
    /// There is deliberately no discovery. Connecting to a server means running code it
    /// controls and putting text it wrote into Lane's prompt, which should be something
    /// somebody chose rather than something that happened.
    /// </summary>
    private static void RegisterMcp(IServiceCollection services, IConfigurationSection section)
    {
        if (!section.Exists()) return;

        McpOptions options = new();
        section.Bind(options);

        // Servers are bound by hand for the same reason memory handlers are: Transport is an
        // enum the binder would leave at its default on a typo, and a server silently
        // falling back to stdio when it was meant to be http fails in a confusing way.
        options.Servers = [.. section.GetSection("Servers").GetChildren().Select(BindMcpServer)];

        if (options.Servers.Count(s => s.Enabled) == 0) return;

        SecretResolver secrets = new();

        // Environment values and headers are resolved once, here, so a server can be handed
        // a token without that token living in the config file.
        foreach (McpServerOptions server in options.Servers)
        {
            foreach (string key in server.Env.Keys.ToList())
                server.Env[key] = secrets.Resolve(server.Env[key]) ?? server.Env[key];

            foreach (string key in server.Headers.Keys.ToList())
                server.Headers[key] = secrets.Resolve(server.Headers[key]) ?? server.Headers[key];
        }

        services.AddLaneMcp(options);
    }

    private static McpServerOptions BindMcpServer(IConfigurationSection section)
    {
        McpServerOptions server = new();
        section.Bind(server);

        server.Id = section["Id"] ?? throw new InvalidOperationException(
            $"An MCP server at {section.Path} is missing its Id.");

        string? transport = section["Transport"];

        if (!string.IsNullOrWhiteSpace(transport))
        {
            if (!Enum.TryParse(transport, ignoreCase: true, out McpTransport parsed))
                throw new InvalidOperationException(
                    $"MCP server '{server.Id}' has Transport='{transport}'. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames<McpTransport>())}.");

            server.Transport = parsed;
        }

        return server;
    }

    private static MemoryHandlerOptions BindHandler(IConfigurationSection section)
    {
        string id = section["Id"] ?? throw new InvalidOperationException(
            $"A memory handler at {section.Path} is missing its Id.");

        string type = section["Type"] ?? throw new InvalidOperationException(
            $"Memory handler '{id}' is missing its Type.");

        return new MemoryHandlerOptions
        {
            Id                 = id,
            Type               = type,
            Scope              = ParseEnum<MemoryScope>(section["Scope"], id, nameof(MemoryHandlerOptions.Scope)),
            Slot               = ParseEnum<MemorySlot>(section["Slot"], id, nameof(MemoryHandlerOptions.Slot)),
            Order              = section.GetValue("Order", 0),
            SectionTitle       = section["SectionTitle"],
            Pinned             = section.GetValue("Pinned", false),
            DirectSessionsOnly = section.GetValue("DirectSessionsOnly", false),
            MaxMessages        = section.GetValue("MaxMessages", 20),
            MaxEntries         = section.GetValue("MaxEntries", 20),
            PromptName         = section["PromptName"] ?? "",
            Kinds              = section.GetSection("Kinds").Get<MessageKind[]>()
        };
    }

    private static T ParseEnum<T>(string? value, string handlerId, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Memory handler '{handlerId}' is missing {field}.");

        if (!Enum.TryParse(value, ignoreCase: true, out T parsed))
            throw new InvalidOperationException(
                $"Memory handler '{handlerId}' has {field}='{value}'. " +
                $"Valid values: {string.Join(", ", Enum.GetNames<T>())}.");

        return parsed;
    }

    private static void RegisterSurfaceFactories(IServiceCollection services)
    {
        // Keyed by config discriminator, so config can name the same type twice with
        // different ids and options.
        services.AddKeyedSingleton<ISurfaceFactory, TerminalSurfaceFactory>("terminal");

        services.AddSingleton<IDiscordTokenSource, SecretTokenSource>();
        services.AddKeyedSingleton<ISurfaceFactory>("discord",
            (sp, _) => new DiscordSurfaceFactory(sp.GetRequiredService<IDiscordTokenSource>()));

        // The streaming half of the API has to be registered whether or not an API surface
        // is configured: the turn pipeline is built at startup, and the observer that feeds
        // a client's stream is part of it. It offers nothing to turns nobody is watching.
        services.AddLaneApi();

        services.AddSingleton<IApiKeySource, SecretApiKeySource>();
        services.AddKeyedSingleton<ISurfaceFactory>("api",
            (sp, _) => new ApiSurfaceFactory(sp.GetRequiredService<IApiKeySource>()));
    }

    /// <summary>Bridges the Discord factory to the host's secret resolution.</summary>
    private sealed class SecretTokenSource(ISecretResolver secrets) : IDiscordTokenSource
    {
        public string Resolve(string reference, string instanceId) =>
            secrets.Require(reference, $"Discord surface '{instanceId}'");
    }

    /// <summary>The same, for API client keys.</summary>
    private sealed class SecretApiKeySource(ISecretResolver secrets) : IApiKeySource
    {
        public string Resolve(string reference, string clientId) =>
            secrets.Require(reference, $"API client '{clientId}'");
    }

    /// <summary>Writes every model call, name-redacted, to JSONL for training a node answer classifier.</summary>
    private static void RegisterRecording(IServiceCollection services, IConfigurationSection section)
    {
        ResponseRecordingOptions options = new();
        section.Bind(options);

        if (!options.Enabled) return;

        services.AddSingleton(sp => new JsonlResponseRecorder(
            options,
            () => sp.GetService<ISessionRegistry>()?.Active.SelectMany(s => s.Descriptor.KnownParticipants) ?? [],
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<JsonlResponseRecorder>>()));

        services.AddSingleton<IResponseRecorder>(sp => sp.GetRequiredService<JsonlResponseRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<JsonlResponseRecorder>());
    }

    private static void RegisterModels(IServiceCollection services, IConfigurationSection section)
    {
        ModelsOptions options = new();
        section.Bind(options);

        if (options.Instances.Count == 0)
            throw new InvalidOperationException(
                "Lane:Models:Instances is empty. At least one model instance must be configured.");

        foreach (ModelInstanceOptions instance in options.Instances)
        {
            ModelInstanceOptions captured = instance;

            services.AddSingleton<ILanguageModel>(sp => CreateModel(captured, sp));
        }

        Dictionary<(string, string), string> overrides = options.Overrides
            .ToDictionary(o => (o.Surface, o.Role), o => o.Instance);

        services.AddSingleton<ILanguageModelRegistry>(sp => new LanguageModelRegistry(
            sp.GetServices<ILanguageModel>(),
            options.Roles,
            overrides,
            sp.GetService<ILogger<LanguageModelRegistry>>()));
    }

    private static readonly Dictionary<string, string> KnownEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openrouter"] = "https://openrouter.ai/api/v1",
        ["deepseek"]   = "https://api.deepseek.com/v1",
        ["typesafe"]   = JevModel.DefaultEndpoint
    };

    /// <summary>The pool is registered whether or not the listener is enabled, so a "node" model
    /// instance can be constructed and fail with a clear message instead of a missing service.</summary>
    private static void RegisterNodes(IServiceCollection services, IConfigurationSection section)
    {
        NodesOptions options = new();
        section.Bind(options);

        services.AddSingleton(options);
        services.AddSingleton(sp => new NodePool(
            sp.GetRequiredService<IEventBus>(), sp.GetService<ILogger<NodePool>>()));
        services.AddSingleton<INodeResponseValidator, AcceptAllNodeValidator>();

        services.AddSingleton(sp => new NodeBookkeeper(
            sp.GetRequiredService<INodeDirectory>(),
            sp.GetRequiredService<ICreditLedger>(),
            options,
            sp.GetRequiredService<ILogger<NodeBookkeeper>>()));

        if (!options.Enabled) return;

        services.AddSingleton<ILaneStatusSource, HostLaneStatus>();

        services.AddSingleton<ISponsoredAccess>(sp => new SponsoredAccess(
            sp.GetRequiredService<ISponsorships>(), options, sp.GetRequiredService<ILogger<SponsoredAccess>>()));

        services.AddSingleton(sp => new NodePortal(
            sp.GetRequiredService<NodePool>(),
            sp.GetRequiredService<INodeDirectory>(),
            sp.GetRequiredService<ICreditLedger>(),
            sp.GetRequiredService<ISponsorships>(),
            sp.GetRequiredService<IForum>(),
            sp.GetRequiredService<ILaneStatusSource>(),
            options));

        services.AddSingleton<IHostedService>(sp => new NodeListener(
            sp.GetRequiredService<NodePool>(),
            options,
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<NodeBookkeeper>(),
            sp.GetRequiredService<NodePortal>()));
    }

    private static ILanguageModel CreateModel(ModelInstanceOptions options, IServiceProvider sp)
    {
        ISecretResolver secrets = sp.GetRequiredService<ISecretResolver>();
        IEventBus       bus     = sp.GetRequiredService<IEventBus>();

        string RequireKey() => secrets.Require(options.KeyRef, $"model instance '{options.Id}'");

        ILanguageModel model = options.Provider.ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicModel(
                new AnthropicModelOptions { InstanceId = options.Id, Model = options.Model, ApiKey = RequireKey() },
                sp.GetRequiredService<ILogger<AnthropicModel>>()),

            "node" => CreateNodeModel(options, sp),

            "typesafe" => CreateJevModel(options, sp, RequireKey()),

            // One adapter serves every OpenAI-compatible endpoint; only the URL differs.
            "openrouter" or "deepseek" or "openai-compatible" => new OpenAiCompatibleModel(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient($"model:{options.Id}"),
                new OpenAiCompatibleOptions
                {
                    InstanceId   = options.Id,
                    Model        = options.Model,
                    ApiKey       = RequireKey(),
                    Endpoint     = ResolveEndpoint(options),
                    Capabilities = ParseCapabilities(options)
                },
                sp.GetRequiredService<ILogger<OpenAiCompatibleModel>>()),

            _ => throw new InvalidOperationException(
                $"Model instance '{options.Id}' names unknown provider '{options.Provider}'. " +
                "Known providers: anthropic, openrouter, deepseek, openai-compatible, typesafe, node.")
        };

        if (sp.GetService<IResponseRecorder>() is { } recorder) model = new RecordingLanguageModel(model, recorder);

        // Wrapped so every provider reports usage without knowing the bus exists. No role
        // is attached here: one instance can serve several roles, and the dashboard reads
        // the bindings from the registry anyway.
        return new TelemetryLanguageModel(model, bus);
    }

    /// <summary>
    /// Jev takes its criteria from a question set beside the prompt templates rather than from
    /// a prompt, so the roster can be retuned without a rebuild.
    /// </summary>
    private static JevModel CreateJevModel(ModelInstanceOptions options, IServiceProvider sp, string apiKey)
    {
        PromptOptions prompts = sp.GetRequiredService<PromptOptions>();

        string directory = Path.IsPathRooted(prompts.Directory)
            ? prompts.Directory
            : Path.Combine(AppContext.BaseDirectory, prompts.Directory);

        return new JevModel(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient($"model:{options.Id}"),
            new JevOptions
            {
                InstanceId = options.Id,
                ApiKey     = apiKey,
                Model      = string.IsNullOrWhiteSpace(options.Model) ? "jev-latest" : options.Model,
                Endpoint   = JevEndpoint(options),
                Headers    = options.ViaOpenRouter
                    ? new Dictionary<string, string> { ["X-Title"] = "Lane" }
                    : new Dictionary<string, string>(),
                Profile    = JevRoutingProfile.Load(Path.Combine(directory, JevQuestionSet))
            },
            sp.GetRequiredService<ILogger<JevModel>>());
    }

    /// <summary>Sits next to Routing.typesafe.md, which carries the conversation it evaluates.</summary>
    private const string JevQuestionSet = "Routing.typesafe.json";

    /// <summary>
    /// An explicit endpoint always wins, then the OpenRouter toggle, then the provider's own
    /// API. The toggle is not the chat models' OpenRouter base URL — System One is proxied on
    /// its own path, and reusing that entry would send the request to /api/v1/v1/systemone.
    /// </summary>
    internal static string JevEndpoint(ModelInstanceOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) return options.Endpoint;

        return options.ViaOpenRouter ? JevModel.OpenRouterEndpoint : ResolveEndpoint(options);
    }

    private static NodeLanguageModel CreateNodeModel(ModelInstanceOptions options, IServiceProvider sp)
    {
        NodesOptions nodes = sp.GetRequiredService<NodesOptions>();

        if (!nodes.Enabled)
            throw new InvalidOperationException(
                $"Model instance '{options.Id}' uses provider 'node', but Lane:Nodes:Enabled is false.");

        if (string.IsNullOrWhiteSpace(options.Pool))
            throw new InvalidOperationException(
                $"Model instance '{options.Id}' uses provider 'node' and needs a Pool.");

        return new NodeLanguageModel(
            options.Id,
            options.Pool,
            ParseCapabilities(options),
            sp.GetRequiredService<NodePool>(),
            sp.GetRequiredService<INodeResponseValidator>(),
            nodes,
            sp.GetRequiredService<ILogger<NodeLanguageModel>>(),
            sp.GetRequiredService<NodeBookkeeper>(),
            () => ResolveFallbackModel(options.Id, sp));
    }

    /// <summary>The instance a node model hands unanswerable requests to. Resolved per request rather
    /// than at construction, since the fallback is itself one of the instances being built.</summary>
    private const string FallbackInstanceId = "default";

    private static ILanguageModel? ResolveFallbackModel(string instanceId, IServiceProvider sp)
    {
        if (instanceId.Equals(FallbackInstanceId, StringComparison.OrdinalIgnoreCase)) return null;

        return sp.GetServices<ILanguageModel>().FirstOrDefault(m =>
            m.Descriptor.InstanceId.Equals(FallbackInstanceId, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveEndpoint(ModelInstanceOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) return options.Endpoint;

        if (KnownEndpoints.TryGetValue(options.Provider, out string? known)) return known;

        throw new InvalidOperationException(
            $"Model instance '{options.Id}' uses provider '{options.Provider}' and needs an explicit Endpoint.");
    }

    /// <summary>
    /// Capabilities are declared per instance because OpenRouter fronts hundreds of models
    /// whose support differs completely. Defaults assume tool calling; a model without it
    /// must say so, and the registry then refuses to bind it to a tool-using role.
    /// </summary>
    private static ModelCapabilities ParseCapabilities(ModelInstanceOptions options)
    {
        if (options.Capabilities is not { Count: > 0 }) return ModelCapabilities.Tools |
                                                               ModelCapabilities.Streaming |
                                                               ModelCapabilities.StopSequences;

        ModelCapabilities parsed = ModelCapabilities.None;

        foreach (string name in options.Capabilities)
        {
            if (!Enum.TryParse(name, ignoreCase: true, out ModelCapabilities one))
                throw new InvalidOperationException(
                    $"Model instance '{options.Id}' declares unknown capability '{name}'. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames<ModelCapabilities>())}.");

            parsed |= one;
        }

        return parsed;
    }


    private static void RegisterSurfaces(IServiceCollection services, IConfigurationSection section)
    {
        List<(SurfaceInstanceOptions Instance, IConfigurationSection Options)> configured = [];

        foreach (IConfigurationSection child in section.GetChildren())
        {
            SurfaceInstanceOptions instance = new();
            child.Bind(instance);

            // Options is passed through as raw configuration; only the factory knows its shape.
            configured.Add((instance, child.GetSection("Options")));
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach ((SurfaceInstanceOptions instance, IConfigurationSection surfaceOptions) in configured)
        {
            if (!instance.Enabled) continue;

            if (string.IsNullOrWhiteSpace(instance.Id))
                throw new InvalidOperationException($"A '{instance.Type}' surface is missing its Id.");

            if (!seen.Add(instance.Id))
                throw new InvalidOperationException(
                    $"Two surfaces share the id '{instance.Id}'. Ids namespace sessions and must be unique.");

            SurfaceInstanceOptions  captured        = instance;
            IConfigurationSection   capturedOptions = surfaceOptions;

            // The surface is built lazily inside its host, so a bad token or endpoint costs
            // that one surface rather than aborting startup for every other one.
            services.AddSingleton<IHostedService>(sp => new SurfaceHost(
                () =>
                {
                    ISurfaceFactory factory =
                        sp.GetKeyedService<ISurfaceFactory>(captured.Type)
                        ?? throw new InvalidOperationException(
                            $"No surface factory registered for type '{captured.Type}' (instance '{captured.Id}').");

                    return factory.Create(new SurfaceId(captured.Id), capturedOptions, sp);
                },
                new SurfaceId(captured.Id),
                sp.GetRequiredService<ILogger<SurfaceHost>>()));
        }

        if (seen.Count == 0)
            throw new InvalidOperationException("No surfaces are enabled; Lane would have no way to be reached.");
    }
}
