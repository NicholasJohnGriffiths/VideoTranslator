using System.ComponentModel.DataAnnotations;

namespace VideoTranslator.Configuration;

public sealed class LocalStorageOptions
{
    public const string SectionName = "LocalStorage";

    [Required]
    public string RootPath { get; init; } = "Data/jobs";
}
