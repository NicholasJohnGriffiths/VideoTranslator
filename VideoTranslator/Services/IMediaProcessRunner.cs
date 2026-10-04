namespace VideoTranslator.Services;

public interface IMediaProcessRunner
{
    Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
