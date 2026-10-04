namespace VideoTranslator.Services;

public interface IUploadValidator
{
    string ValidateMetadata(string fileName, string contentType, long length);
    Task ValidateContentAsync(Stream content, CancellationToken cancellationToken);
}
