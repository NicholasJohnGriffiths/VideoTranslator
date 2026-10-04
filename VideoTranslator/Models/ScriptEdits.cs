namespace VideoTranslator.Models;

public sealed class ScriptEdits
{
    public Dictionary<int, string?> Segments { get; init; } = [];
}

public sealed record ScriptEditSnapshot(ScriptEdits Edits, string? Revision);
