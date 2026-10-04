namespace VideoTranslator.Services;

public sealed class TranscriptionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
