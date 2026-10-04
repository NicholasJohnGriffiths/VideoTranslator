namespace VideoTranslator.Services;

public sealed class MediaProcessingException(string message, Exception? innerException = null)
    : Exception(message, innerException);
