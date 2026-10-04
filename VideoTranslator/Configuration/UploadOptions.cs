using System.ComponentModel.DataAnnotations;

namespace VideoTranslator.Configuration;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    [Range(12, 104857600)]
    public long MaxFileSizeBytes { get; init; } = 104857600;
}
