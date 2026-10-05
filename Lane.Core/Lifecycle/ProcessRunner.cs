using System.Diagnostics;
using System.Text;

namespace Lane.Core.Lifecycle;

/// <param name="Output">Standard output and standard error interleaved, trimmed to the run's cap from the front.</param>
public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public sealed record ProcessSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }

    /// <summary>Set to null to remove an inherited variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Characters of output kept; older output is dropped first.</summary>
    public int MaxOutput { get; init; } = 64_000;
}

/// <summary>Runs a command to completion. Never through a shell.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public static ProcessRunner Instance { get; } = new();

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ProcessStartInfo psi = new(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            WorkingDirectory       = spec.WorkingDirectory ?? Environment.CurrentDirectory
        };

        foreach (string arg in spec.Arguments) psi.ArgumentList.Add(arg);

        foreach ((string key, string? value) in spec.Environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        TailBuffer output = new(spec.MaxOutput);

        using Process process = new() { StartInfo = psi };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(spec.Timeout);

        bool timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }

            if (ct.IsCancellationRequested) throw;

            timedOut = true;
        }

        // The parameterless wait drains the asynchronous output readers.
        if (!timedOut) process.WaitForExit();

        return new ProcessResult(timedOut ? -1 : process.ExitCode, output.ToString(), timedOut);
    }

    private sealed class TailBuffer(int max)
    {
        private readonly StringBuilder _sb = new();
        private readonly Lock _lock = new();

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                _sb.AppendLine(line);

                if (_sb.Length > max) _sb.Remove(0, _sb.Length - max);
            }
        }

        public override string ToString()
        {
            lock (_lock) return _sb.ToString();
        }
    }
}
