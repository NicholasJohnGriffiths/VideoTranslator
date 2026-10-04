using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class LanguageTests
{
    [Fact]
    public void ApplicationConfigurationContainsAllFourLanguagesAndArabicDirection()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "VideoTranslator", "appsettings.json"));
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        var options = configuration.GetSection(LanguageOptions.SectionName).Get<LanguageOptions>();

        Assert.NotNull(options);
        Assert.True(new LanguageOptionsValidator().Validate(null, options).Succeeded);
        var service = new LanguageService(Options.Create(options));
        Assert.Equal(new[] { "zh-CN", "ar-SA", "es-ES", "hi-IN" },
            service.GetLanguages().Select(language => language.Code));
        Assert.True(service.Find("ar-SA")!.IsRightToLeft);
        Assert.False(service.Find("es-ES")!.IsRightToLeft);
        Assert.Null(service.Find("unsupported"));
    }

    [Fact]
    public void DuplicateLanguageCodesAreRejectedIgnoringCase()
    {
        var options = new LanguageOptions { Supported = [Language("es-ES"), Language("ES-es")] };
        Assert.True(new LanguageOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void EmptyConfigurationAndMissingVoiceAreRejected()
    {
        var validator = new LanguageOptionsValidator();
        Assert.True(validator.Validate(null, new LanguageOptions()).Failed);
        Assert.True(validator.Validate(null, new LanguageOptions
        {
            Supported = [new LanguageOption { Code = "es-ES", DisplayName = "Spanish", SpeechLocale = "es-ES" }]
        }).Failed);
    }

    internal static LanguageOption Language(string code) => new()
    {
        Code = code,
        DisplayName = code,
        SpeechLocale = code,
        VoiceName = "configured-voice"
    };
}
