using Lane.Core.Lifecycle;
using Lane.Host.Lifecycle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class SelfUpdaterTests : IDisposable
{
    private readonly string _root   = Directory.CreateTempSubdirectory("lane-update-").FullName;
    private string Target  => Path.Combine(_root, "target");
    private string Staging => Path.Combine(_root, "staging");
    private string Source  => Path.Combine(_root, "source");

    public SelfUpdaterTests()
    {
        Directory.CreateDirectory(Target);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Source);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(Target, relative));

    [Fact]
    public void An_overlay_replaces_code_keeps_data_and_can_be_rolled_back()
    {
        Write(Path.Combine(Target, "lane.dll"), "old code");
        Write(Path.Combine(Target, "appsettings.json"), "live config");
        Write(Path.Combine(Target, "lane.db"), "memories");

        Write(Path.Combine(Staging, "lane.dll"), "new code");
        Write(Path.Combine(Staging, "appsettings.json"), "checkout config");
        Write(Path.Combine(Staging, "Prompts", "Persona.md"), "new prompt");

        int installed = SelfUpdater.Overlay(Staging, Target);

        Assert.Equal(2, installed);
        Assert.Equal("new code", Read("lane.dll"));
        Assert.Equal("new prompt", Read(Path.Combine("Prompts", "Persona.md")));
        Assert.Equal("live config", Read("appsettings.json"));
        Assert.Equal("memories", Read("lane.db"));
        Assert.Empty(Directory.EnumerateFiles(Target, "*.lane-new", SearchOption.AllDirectories));

        SelfUpdater.Rollback(Target);

        Assert.Equal("old code", Read("lane.dll"));
        Assert.False(File.Exists(Path.Combine(Target, "Prompts", "Persona.md")));
        Assert.Equal("memories", Read("lane.db"));
        Assert.False(Directory.Exists(Path.Combine(Target, SelfUpdater.RollbackDir)));
    }

    private void PendingUpdate(int attempts) => Write(Path.Combine(Target, SelfUpdater.MarkerFile), $$"""
        {"Reason":"faster replies","Commit":"abc123 Speed up","RequestedAt":"2026-10-05T12:00:00+00:00","Attempts":{{attempts}},"RolledBack":false}
        """);

    [Fact]
    public void Each_start_of_a_new_build_is_counted_until_it_settles()
    {
        PendingUpdate(attempts: 0);

        RestartStartup first = SelfUpdater.CheckOnStartup(Target, new RestartOptions(), TextWriter.Null);

        Assert.False(first.ExitNow);
        Assert.Equal("faster replies", first.Previous?.Reason);
        Assert.Equal("abc123 Speed up", first.Previous?.Commit);
        Assert.False(first.Previous?.RolledBack);
        Assert.Contains("\"Attempts\": 1", File.ReadAllText(Path.Combine(Target, SelfUpdater.MarkerFile)));

        Assert.Null(SelfUpdater.CheckOnStartup(Path.Combine(_root, "nowhere"), new RestartOptions(), TextWriter.Null).Previous);
    }

    [Fact]
    public void A_build_that_keeps_failing_to_start_is_rolled_back()
    {
        Write(Path.Combine(Target, "lane.dll"), "old code");
        Write(Path.Combine(Staging, "lane.dll"), "broken code");
        SelfUpdater.Overlay(Staging, Target);

        PendingUpdate(attempts: 2);

        RestartStartup startup = SelfUpdater.CheckOnStartup(Target, new RestartOptions { MaxFailedStarts = 2 }, TextWriter.Null);

        Assert.True(startup.ExitNow);
        Assert.Equal("old code", Read("lane.dll"));

        RestartStartup next = SelfUpdater.CheckOnStartup(Target, new RestartOptions { MaxFailedStarts = 2 }, TextWriter.Null);

        Assert.False(next.ExitNow);
        Assert.True(next.Previous?.RolledBack);
    }

    private sealed class ScriptedRunner(Func<ProcessSpec, ProcessResult> respond) : IProcessRunner
    {
        public List<ProcessSpec> Runs { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Runs.Add(spec);
            return Task.FromResult(respond(spec));
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() => Stopped.TrySetResult();
    }

    private (SelfUpdater Updater, ScriptedRunner Runner, FakeLifetime Lifetime, List<int> ExitCodes) Build(
        RestartMode mode, Func<ProcessSpec, ProcessResult> respond)
    {
        ScriptedRunner runner = new(respond);
        FakeLifetime lifetime = new();
        List<int> exitCodes = [];

        SelfUpdater updater = new(
            new RestartOptions { Mode = mode },
            new RestartStartup(null, ExitNow: false),
            Target, lifetime, runner, NullLogger<SelfUpdater>.Instance,
            setExitCode: exitCodes.Add);

        return (updater, runner, lifetime, exitCodes);
    }

    [Fact]
    public async Task A_failed_build_restarts_nothing()
    {
        (SelfUpdater updater, ScriptedRunner runner, _, _) = Build(RestartMode.Supervisor,
            _ => new ProcessResult(1, "Program.cs(3,1): error CS1002: ; expected\nBuild FAILED.", TimedOut: false));

        RestartOutcome outcome = await updater.BuildAndRestartAsync(Source, "test", TestContext.Current.CancellationToken);

        Assert.Equal(RestartStatus.BuildFailed, outcome.Status);
        Assert.Contains("CS1002", outcome.Log);
        Assert.Equal("build", Assert.Single(runner.Runs).Arguments[0]);
        Assert.False(File.Exists(Path.Combine(Target, SelfUpdater.MarkerFile)));
    }

    [Fact]
    public async Task Outside_a_service_manager_a_good_build_is_reported_but_not_installed()
    {
        (SelfUpdater updater, ScriptedRunner runner, _, _) = Build(RestartMode.Relaunch,
            _ => new ProcessResult(0, "Build succeeded.", TimedOut: false));

        RestartOutcome outcome = await updater.BuildAndRestartAsync(Source, "test", TestContext.Current.CancellationToken);

        Assert.Equal(RestartStatus.NotSupported, outcome.Status);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task A_good_build_is_installed_and_lane_exits_for_the_service_manager()
    {
        Write(Path.Combine(Target, "lane.dll"), "old code");

        (SelfUpdater updater, ScriptedRunner runner, FakeLifetime lifetime, List<int> exitCodes) = Build(RestartMode.Supervisor, spec =>
        {
            if (spec.Arguments[0] == "publish")
            {
                string output = spec.Arguments[spec.Arguments.ToList().IndexOf("-o") + 1];
                Write(Path.Combine(output, "lane.dll"), "new code");
            }

            return new ProcessResult(0, spec.FileName == "git" ? "abc123 Make it faster" : "ok", TimedOut: false);
        });

        RestartOutcome outcome = await updater.BuildAndRestartAsync(Source, "faster", TestContext.Current.CancellationToken);

        Assert.Equal(RestartStatus.Restarting, outcome.Status);
        Assert.Contains("abc123", outcome.Message);
        Assert.Equal("new code", Read("lane.dll"));
        Assert.True(File.Exists(Path.Combine(Target, SelfUpdater.MarkerFile)));
        Assert.Equal(["build", "publish"], runner.Runs.Where(r => r.FileName == "dotnet").Select(r => r.Arguments[0]));

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal([75], exitCodes);
    }
}
