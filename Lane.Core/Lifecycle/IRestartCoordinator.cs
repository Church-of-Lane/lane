namespace Lane.Core.Lifecycle;

public enum RestartStatus
{
    Restarting,
    BuildFailed,
    PublishFailed,

    /// <summary>The build passed, but this process is not configured to replace itself.</summary>
    NotSupported,

    /// <summary>Another restart is already in progress.</summary>
    Busy
}

/// <param name="Log">The tail of the build or publish output, for a failure.</param>
public sealed record RestartOutcome(RestartStatus Status, string Message, string? Log = null);

/// <param name="RolledBack">The new build failed to start and the previous one was put back.</param>
public sealed record RestartRecord(string Reason, string? Commit, DateTimeOffset RequestedAt, bool RolledBack);

/// <summary>Stops Lane so that she comes back, optionally on a freshly built copy of her own source.</summary>
public interface IRestartCoordinator
{
    /// <summary>The self-update that led to this run, if this run is the result of one.</summary>
    RestartRecord? PreviousRestart { get; }

    /// <summary>Restarts the running build as it is.</summary>
    void Restart(string reason);

    /// <summary>
    /// Builds <paramref name="sourceDirectory"/>, and only if that succeeds, installs the result over the
    /// running build and restarts onto it. Returns before the process exits.
    /// </summary>
    Task<RestartOutcome> BuildAndRestartAsync(string sourceDirectory, string reason, CancellationToken ct);
}
