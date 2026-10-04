using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class MediaProcessRunnerTests
{
    [Fact]
    public async Task DrainsBothPipesWithoutDeadlock()
    {
        var (executable, arguments) = Command(
            "[Console]::Out.Write(('a' * 100000)); [Console]::Error.Write(('b' * 100000))",
            "head -c 100000 /dev/zero | tr '\\000' a; head -c 100000 /dev/zero | tr '\\000' b >&2");
        var output = await Runner(10).RunAsync(executable, arguments, CancellationToken.None);
        Assert.Equal(100000, output.Length);
        Assert.All(output, character => Assert.Equal('a', character));
    }

    [Fact]
    public async Task NonzeroExitIsExplicitAndDoesNotExposeDiagnostics()
    {
        var (executable, arguments) = Command(
            "[Console]::Error.Write('private-diagnostic'); exit 7", "echo private-diagnostic >&2; exit 7");
        var exception = await Assert.ThrowsAsync<MediaProcessingException>(() =>
            Runner(10).RunAsync(executable, arguments, CancellationToken.None));
        Assert.DoesNotContain("private-diagnostic", exception.Message);
    }

    [Fact]
    public async Task TimeoutStopsLongRunningProcess()
    {
        var (executable, arguments) = Command("Start-Sleep -Seconds 20", "sleep 20");
        var elapsed = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<MediaProcessingException>(() =>
            Runner(1).RunAsync(executable, arguments, CancellationToken.None));
        Assert.Contains("time limit", exception.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Timeout took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task CancellationStopsLongRunningProcess()
    {
        var (executable, arguments) = Command("Start-Sleep -Seconds 20", "sleep 20");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner(30).RunAsync(executable, arguments, cancellation.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), $"Cancellation took {elapsed.Elapsed}.");
    }

    private static MediaProcessRunner Runner(int seconds) => new(
        Options.Create(new MediaOptions { ProcessTimeoutSeconds = seconds }),
        NullLogger<MediaProcessRunner>.Instance);

    // Fixed test commands exercise OS pipe/process behaviour; the application never invokes a shell.
    private static (string Executable, IReadOnlyList<string> Arguments) Command(string windows, string unix) =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
                new[] { "-NoProfile", "-NonInteractive", "-Command", windows })
            : ("/bin/sh", new[] { "-c", unix });
}
