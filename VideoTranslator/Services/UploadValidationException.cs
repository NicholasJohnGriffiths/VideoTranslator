namespace VideoTranslator.Services;

public sealed class UploadValidationException(string message) : Exception(message);
