namespace VideoTranslator.Services;

public sealed class TranslationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
