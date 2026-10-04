using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Azure.Storage.Blobs;
using VideoTranslator.Configuration;
using VideoTranslator.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddOptions<UploadOptions>()
    .BindConfiguration(UploadOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<LocalStorageOptions>()
    .BindConfiguration(LocalStorageOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(options => !Path.IsPathRooted(options.RootPath)
        && options.RootPath.Split('/', '\\').All(part => part != ".."),
        "Local storage must use a relative path without parent-directory traversal.")
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<LanguageOptions>, LanguageOptionsValidator>();
builder.Services.AddOptions<LanguageOptions>()
    .BindConfiguration(LanguageOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JobStorageOptions>, JobStorageOptionsValidator>();
builder.Services.AddOptions<JobStorageOptions>()
    .BindConfiguration(JobStorageOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddOptions<FormOptions>()
    .Configure<IOptions<UploadOptions>>((form, upload) =>
        form.MultipartBodyLengthLimit = upload.Value.MaxFileSizeBytes + 1024 * 1024);
builder.WebHost.ConfigureKestrel((context, server) =>
{
    var upload = context.Configuration.GetSection(UploadOptions.SectionName).Get<UploadOptions>()
        ?? new UploadOptions();
    server.Limits.MaxRequestBodySize = upload.MaxFileSizeBytes + 1024 * 1024;
});
builder.Services.AddSingleton<ILanguageService, LanguageService>();
builder.Services.AddSingleton<IUploadValidator, UploadValidator>();
var storageProvider = builder.Configuration.GetSection(JobStorageOptions.SectionName)
    .Get<JobStorageOptions>() ?? new JobStorageOptions();
if (storageProvider.IsLocal)
{
    builder.Services.AddSingleton<LocalJobStorageService>();
    builder.Services.AddSingleton<IJobStorageService>(provider => provider.GetRequiredService<LocalJobStorageService>());
    builder.Services.AddSingleton<ITranscriptStorageService>(provider => provider.GetRequiredService<LocalJobStorageService>());
    builder.Services.AddSingleton<ITranslationStorageService, LocalTranslationStorageService>();
    builder.Services.AddSingleton<IVoicePreviewStorageService, LocalVoicePreviewStorageService>();
    builder.Services.AddSingleton<IGeneratedAudioStorageService, LocalGeneratedAudioStorageService>();
    builder.Services.AddSingleton<IRenderedVideoStorageService, LocalRenderedVideoStorageService>();
}
else if (storageProvider.Provider == "AzureBlob")
{
    builder.Services.AddSingleton<IValidateOptions<BlobStorageOptions>, BlobStorageOptionsValidator>();
    builder.Services.AddOptions<BlobStorageOptions>()
        .BindConfiguration(BlobStorageOptions.SectionName)
        .Validate(options => options.CredentialMode != "AzureCli" || builder.Environment.IsDevelopment(),
            "Azure CLI credentials are permitted only in Development.")
        .ValidateOnStart();
    builder.Services.AddSingleton(provider =>
    {
        var options = provider.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
        var credential = AzureCredentialFactory.Create(options.CredentialMode, options.ManagedIdentityClientId);
        return new BlobServiceClient(new Uri(options.ServiceUri), credential)
            .GetBlobContainerClient(options.ContainerName);
    });
    builder.Services.AddSingleton<BlobJobStorageService>();
    builder.Services.AddSingleton<IJobStorageService>(provider => provider.GetRequiredService<BlobJobStorageService>());
    builder.Services.AddSingleton<ITranscriptStorageService>(provider => provider.GetRequiredService<BlobJobStorageService>());
    builder.Services.AddSingleton<ITranslationStorageService, BlobTranslationStorageService>();
    builder.Services.AddSingleton<IVoicePreviewStorageService, BlobVoicePreviewStorageService>();
    builder.Services.AddSingleton<IGeneratedAudioStorageService, BlobGeneratedAudioStorageService>();
    builder.Services.AddSingleton<IRenderedVideoStorageService, BlobRenderedVideoStorageService>();
    builder.Services.AddHostedService<BlobStorageStartupCheck>();
}
else
{
    throw new InvalidOperationException("JobStorage:Provider must be Local or AzureBlob.");
}
builder.Services.AddScoped<IVideoService, VideoService>();
builder.Services.AddOptions<MediaOptions>()
    .BindConfiguration(MediaOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IMediaProcessRunner, MediaProcessRunner>();
builder.Services.AddSingleton<IAudioService, FFmpegAudioService>();
builder.Services.AddSingleton<MediaAudioInspector>();
builder.Services.AddSingleton<ISpeechTimingService, FFmpegSpeechTimingService>();
builder.Services.AddSingleton<ISubtitleService, WebVttSubtitleService>();
builder.Services.AddSingleton<FFmpegVideoRenderingService>();
builder.Services.AddSingleton<IVideoRenderingService>(provider => provider.GetRequiredService<FFmpegVideoRenderingService>());
builder.Services.AddSingleton<VideoRenderingProcessor>();
builder.Services.AddSingleton<AudioExtractionProcessor>();
builder.Services.AddHostedService<MediaStartupCheck>();
builder.Services.AddSingleton<IValidateOptions<AzureSpeechOptions>, AzureSpeechOptionsValidator>();
builder.Services.AddOptions<AzureSpeechOptions>()
    .BindConfiguration(AzureSpeechOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(options => (!options.Enabled && !options.SynthesisEnabled) || options.CredentialMode != "AzureCli" || builder.Environment.IsDevelopment(),
        "Speech Azure CLI credentials are permitted only in Development.")
    .ValidateOnStart();
if (builder.Configuration.GetValue<bool>("AzureSpeech:Enabled"))
{
    builder.Services.AddHttpClient("Speech", client => client.Timeout = Timeout.InfiniteTimeSpan);
    builder.Services.AddSingleton<ITranscriptionService>(provider =>
    {
        var options = provider.GetRequiredService<IOptions<AzureSpeechOptions>>();
        return new AzureSpeechTranscriptionService(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("Speech"),
            AzureCredentialFactory.Create(options.Value.CredentialMode, options.Value.ManagedIdentityClientId),
            options, provider.GetRequiredService<ILogger<AzureSpeechTranscriptionService>>());
    });
    builder.Services.AddSingleton<TranscriptionProcessor>();
}
if (builder.Configuration.GetValue<bool>("AzureSpeech:SynthesisEnabled"))
{
    builder.Services.AddHttpClient("SpeechSynthesis", client => client.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddSingleton<ISpeechSynthesisService>(provider =>
    {
        var options = provider.GetRequiredService<IOptions<AzureSpeechOptions>>();
        return new AzureSpeechSynthesisService(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("SpeechSynthesis"),
            AzureCredentialFactory.Create(options.Value.CredentialMode, options.Value.ManagedIdentityClientId),
            options, provider.GetRequiredService<ILogger<AzureSpeechSynthesisService>>());
    });
    builder.Services.AddSingleton<VoicePreviewService>();
    builder.Services.AddSingleton<SpeechGenerationProcessor>();
    builder.Services.AddScoped<SpeechGenerationApprovalService>();
}
builder.Services.AddSingleton<IValidateOptions<AzureOpenAIOptions>, AzureOpenAIOptionsValidator>();
builder.Services.AddOptions<AzureOpenAIOptions>()
    .BindConfiguration(AzureOpenAIOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(options => !options.Enabled || options.CredentialMode != "AzureCli" || builder.Environment.IsDevelopment(),
        "OpenAI Azure CLI credentials are permitted only in Development.")
    .ValidateOnStart();
if (builder.Configuration.GetValue<bool>("AzureOpenAI:Enabled"))
{
    builder.Services.AddSingleton<PrivateOpenAIConnection>();
    builder.Services.AddHostedService<PrivateOpenAIStartupCheck>();
    builder.Services.AddHttpClient("PrivateOpenAI", client => client.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<PrivateOpenAIConnection>().CreateHandler());
    builder.Services.AddSingleton<ITranslationService>(provider =>
    {
        var options = provider.GetRequiredService<IOptions<AzureOpenAIOptions>>();
        return new AzureOpenAITranslationService(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("PrivateOpenAI"),
            AzureCredentialFactory.Create(options.Value.CredentialMode, options.Value.ManagedIdentityClientId),
            options, provider.GetRequiredService<ILogger<AzureOpenAITranslationService>>());
    });
    builder.Services.AddSingleton<TranslationProcessor>();
}
builder.Services.AddScoped<ScriptEditingService>();
builder.Services.AddHostedService<AudioExtractionWorker>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "This app supports local development only. Configure production access controls "
        + "and processing services before deploying to Azure.");
}

app.UseExceptionHandler("/Error");

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
