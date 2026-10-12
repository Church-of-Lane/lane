namespace Lane.Surfaces.Api;

public sealed class ApiSurfaceOptions
{
    /// <summary>
    /// Where Kestrel listens. Loopback by default: this endpoint speaks for Lane, and a
    /// default that binds every interface is how a personal agent ends up on the internet
    /// by accident.
    /// </summary>
    public string Urls { get; set; } = "http://127.0.0.1:5080";

    /// <summary>Client applications allowed to connect, each with its own key.</summary>
    public List<ApiClientOptions> Clients { get; set; } = [];

    /// <summary>
    /// Serve without a key. Only honoured on a loopback address — an unauthenticated agent
    /// reachable from the network is not a configuration anyone means to write.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Browser origins allowed to call the API. Empty disables CORS entirely.</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>How long a streamed reply may run before the response is closed.</summary>
    public TimeSpan StreamTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Ceiling on <c>?limit=</c> when reading history.</summary>
    public int MaxHistory { get; set; } = 500;

    /// <summary>Most images one message may carry.</summary>
    public int MaxImages { get; set; } = 4;

    /// <summary>Largest image accepted by value, decoded.</summary>
    public int MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Offer <c>WS /v1/sessions/{key}/voice</c>. Needs audio configured to do anything.</summary>
    public bool Voice { get; set; } = true;

    public ApiVoiceOptions VoiceOptions { get; set; } = new();
}

public sealed class ApiVoiceOptions
{
    /// <summary>What Lane's voice is sent back as, unless a socket asks for something else.</summary>
    public int OutputSampleRate { get; set; } = 24000;

    public int OutputChannels { get; set; } = 1;

    /// <summary>Idle time after which a silent socket is closed.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// One client application. Its key is its identity: sessions, and the participants inside
/// them, are namespaced by client id, so two apps cannot reach into each other's
/// conversations or impersonate each other's users.
/// </summary>
public sealed class ApiClientOptions
{
    public string Id   { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>A secret reference — <c>env:LANE_API_KEY</c> — resolved once at composition.</summary>
    public string KeyRef { get; set; } = "";

    /// <summary>Set at composition from <see cref="KeyRef"/>. Never read from config directly.</summary>
    public string? Key { get; set; }

    /// <summary>
    /// Links this client to a person, so User-scoped memory follows them here from Discord.
    /// Left unset, the client is its own unlinked identity.
    /// </summary>
    public string? GlobalUserId { get; set; }

    /// <summary>Lets this client see every active session, not only its own. Read-only.</summary>
    public bool CanObserveAllSessions { get; set; }
}
