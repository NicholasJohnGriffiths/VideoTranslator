using Microsoft.Extensions.Options;

namespace VideoTranslator.Configuration;

public sealed class JobStorageOptions
{
    public const string SectionName = "JobStorage";
    public string Provider { get; init; } = "Local";
    public bool IsLocal => Provider == "Local";
}

public sealed class JobStorageOptionsValidator : IValidateOptions<JobStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, JobStorageOptions options) =>
        options.Provider is "Local" or "AzureBlob"
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("JobStorage:Provider must be Local or AzureBlob.");
}
