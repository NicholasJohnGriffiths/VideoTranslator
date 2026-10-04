namespace VideoTranslator.Services;

public sealed class ScriptConflictException() : Exception(
    "The script was changed in another window. Reload the page before saving; your changes were not written.");
