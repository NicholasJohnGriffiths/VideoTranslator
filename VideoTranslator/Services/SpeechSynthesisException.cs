namespace VideoTranslator.Services;

public sealed class SpeechSynthesisException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class VoicePreviewBusyException() : Exception(
    "Another voice preview is being generated. Wait for it to finish, then try again.");
