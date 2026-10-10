namespace Lane.Core.Models;

/// <summary>
/// What a model can actually do. Checked at startup so a tool-using role bound to a
/// model without tool support fails loudly instead of silently misbehaving at runtime.
/// </summary>
[Flags]
public enum ModelCapabilities
{
    None             = 0,
    Tools            = 1 << 0,
    ParallelTools    = 1 << 1,
    Streaming        = 1 << 2,
    Images           = 1 << 3,
    PromptCaching    = 1 << 4,
    StructuredOutput = 1 << 5,
    StopSequences    = 1 << 6,
    Thinking         = 1 << 7,
    Audio            = 1 << 8
}

/// <param name="InstanceId">The configured instance name — "haiku", "ds". Distinguishes two
/// instances of the same underlying model with different settings.</param>
public sealed record ModelDescriptor(
    string            InstanceId,
    string            Provider,
    string            ModelId,
    ModelCapabilities Capabilities)
{
    public bool Supports(ModelCapabilities capability) => (Capabilities & capability) == capability;

    public override string ToString() => $"{InstanceId} ({Provider}/{ModelId})";
}
