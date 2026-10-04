using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class MediaProcessRunner(
    IOptions<MediaOptions> options, ILogger<MediaProcessRunner> logger) : IMediaProcessRunner
{
    public async Task<string> RunAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(executable))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.ProcessTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            logger.LogError(exception, "Could not start configured media executable {Executable}.", executable);
            throw new MediaProcessingException("The media processing tool is unavailable. Contact the administrator.", exception);
        }

        var outputTask = ReadBoundedAsync(process.StandardOutput, linked.Token);
        var errorTask = ReadBoundedAsync(process.StandardError, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                logger.LogError("Media tool {Executable} exited with {ExitCode}: {Diagnostic}",
                    executable, process.ExitCode, error);
                throw new MediaProcessingException(
                    "The video could not be decoded. Upload a valid MP4 with a video track and an audio track.");
            }
            return output;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new MediaProcessingException("Media processing exceeded its time limit. Try a shorter video.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // Observe both drains, including cancellation, before disposing the process.
            try
            {
                await Task.WhenAll(outputTask, errorTask);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                logger.LogInformation("Media tool output draining cancelled during shutdown or timeout.");
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var retained = Math.Min(count, Math.Max(0, 1048576 - result.Length));
            result.Append(buffer, 0, retained);
        }
        return result.ToString();
    }
}
