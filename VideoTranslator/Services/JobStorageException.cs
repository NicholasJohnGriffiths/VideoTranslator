namespace VideoTranslator.Services;

public sealed class JobStorageException(string message, Exception innerException)
    : Exception(message, innerException);
