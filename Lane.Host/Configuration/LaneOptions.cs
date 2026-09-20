using Microsoft.Extensions.Configuration;

namespace Lane.Host.Configuration;

/// <summary>
/// The root of Lane's configuration.
///
/// Every part that can exist more than once is an array of instances with a <c>Type</c>,
/// an <c>Id</c> and a free-form <c>Options</c> section. That shape is the whole reason two
/// Discord bots, three microphones or four models can coexist: nothing is addressed by
/// type alone.
/// </summary>
public sealed class LaneOptions
{
    public const string SectionName = "Lane";

    public List<SurfaceInstanceOptions> Surfaces { get; set; } = [];
    public ModelsOptions                Models   { get; set; } = new();
}

/// <summary>
/// The host-owned half of a surface entry. The sibling <c>Options</c> section is
/// deliberately not a property here — the binder would try to construct it, and more to
/// the point only the surface's own factory knows its shape.
/// </summary>
public sealed class SurfaceInstanceOptions
{
    /// <summary>Selects the registered <c>ISurfaceFactory</c>: "terminal", "discord", "api".</summary>
    public string Type { get; set; } = "";

    /// <summary>Unique instance name; becomes the <c>SurfaceId</c> and namespaces its sessions.</summary>
    public string Id { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

public sealed class ModelsOptions
{
    public List<ModelInstanceOptions> Instances { get; set; } = [];

    /// <summary>role name → instance id, e.g. <c>{"respond": "haiku"}</c>.</summary>
    public Dictionary<string, string> Roles { get; set; } = [];

    public List<ModelOverrideOptions> Overrides { get; set; } = [];
}

public sealed class ModelInstanceOptions
{
    public string Id       { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model    { get; set; } = "";

    /// <summary>A secret reference such as <c>env:ANTHROPIC_API_KEY</c>, resolved at bind time.</summary>
    public string KeyRef { get; set; } = "";

    /// <summary>Overrides the provider's default base URL. Required for "openai-compatible".</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>For the "node" provider: which pool of connected nodes answers this instance.</summary>
    public string Pool { get; set; } = "";

    /// <summary>
    /// Reach this provider's own API through OpenRouter instead of directly.
    ///
    /// Only meaningful for providers OpenRouter fronts on their native shape rather than as
    /// chat completions — "typesafe" is the one today, whose System One endpoint OpenRouter
    /// proxies verbatim. The provider is unchanged by this: only the host it is asked at, and
    /// therefore which key <see cref="KeyRef"/> has to name. An explicit
    /// <see cref="Endpoint"/> still wins over it.
    /// </summary>
    public bool ViaOpenRouter { get; set; }

    /// <summary>
    /// What this model can do, e.g. <c>["Tools","Streaming"]</c>. Declared per instance
    /// because an endpoint like OpenRouter fronts models with wildly different support.
    /// Omit to assume tool calling, which startup validation then holds the model to.
    /// </summary>
    public List<string> Capabilities { get; set; } = [];
}

public sealed class ModelOverrideOptions
{
    public string Surface  { get; set; } = "";
    public string Role     { get; set; } = "";
    public string Instance { get; set; } = "";
}
