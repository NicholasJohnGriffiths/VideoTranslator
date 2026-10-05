using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;
using Azure.Storage.Blobs;
using VideoTranslator.Configuration;
using VideoTranslator.Services;

var builder = WebApplication.CreateBuilder(args);
var hosting = builder.Configuration.GetSection(AzureHostingOptions.SectionName).Get<AzureHostingOptions>() ?? new();
if (hosting.AuthenticationMode is not ("MicrosoftEntra" or "SingleAccount"))
{ throw new InvalidOperationException("AzureHosting:AuthenticationMode must be MicrosoftEntra or SingleAccount."); }
if (builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")))
{ throw new InvalidOperationException("Development mode is not permitted on Azure App Service."); }
if (!builder.Environment.IsDevelopment() || hosting.Enabled)
{
    hosting.Validate(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"),
        Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED"),
        builder.Configuration.GetSection(JobStorageOptions.SectionName).Get<JobStorageOptions>() ?? new(),
        builder.Configuration.GetSection(BlobStorageOptions.SectionName).Get<BlobStorageOptions>() ?? new(),
        builder.Configuration.GetSection(AzureSpeechOptions.SectionName).Get<AzureSpeechOptions>() ?? new(),
        builder.Configuration.GetSection(AzureOpenAIOptions.SectionName).Get<AzureOpenAIOptions>() ?? new());
}

builder.Services.AddOptions<AzureHostingOptions>().BindConfiguration(AzureHostingOptions.SectionName);
builder.Services.AddRazorPages(options =>
{
    if (hosting.UsesSingleAccountLogin)
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/Login");
        options.Conventions.AllowAnonymousToPage("/Error");
    }
});
if (hosting.UsesSingleAccountLogin)
{
    var login = builder.Configuration.GetSection(SingleAccountLoginOptions.SectionName)
        .Get<SingleAccountLoginOptions>() ?? new();
    login.ReadPasswordHash();
    builder.Services.AddOptions<SingleAccountLoginOptions>().BindConfiguration(SingleAccountLoginOptions.SectionName);
    builder.Services.AddSingleton<SingleAccountCredentials>();
    var protection = builder.Services.AddDataProtection().SetApplicationName("VideoTranslator.SingleAccount");
    if (hosting.Enabled)
    {
        var directory = login.ValidateKeyDirectory(
            builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"));
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        protection.PersistKeysToFileSystem(new DirectoryInfo(directory));
    }
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/Login";
            options.Cookie.Name = "__Host-VideoTranslator.Session";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = async context =>
            {
                if (!context.HttpContext.RequestServices.GetRequiredService<SingleAccountCredentials>()
                    .IsCurrentPrincipal(context.Principal))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });
    builder.Services.AddAuthorization(options => options.FallbackPolicy =
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(SingleAccountCredentials.LoginRateLimitPolicy, context =>
            HttpMethods.IsPost(context.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter("single-account", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                })
                : RateLimitPartition.GetNoLimiter("login-page"));
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("SingleAccountLogin").LogWarning("Single-account sign-in rate limit exceeded.");
            context.HttpContext.Response.Headers["Retry-After"] = "60";
            await context.HttpContext.Response.WriteAsync(
                "Too many sign-in attempts. Wait one minute and try again.", cancellationToken);
        };
    });
}
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
builder.Services.AddOptions<JobCostOptions>().BindConfiguration("JobCosts")
    .Validate(options => options.TranscriptionPerHour >= 0 && options.SynthesisPerMillionCharacters >= 0
        && options.InputPerThousandTokens >= 0 && options.CachedInputPerThousandTokens >= 0
        && options.OutputPerThousandTokens >= 0 && !string.IsNullOrWhiteSpace(options.RateDate),
        "Job cost rates must be nonnegative and have a rate date.").ValidateOnStart();
builder.Services.AddSingleton<JobUsageService>();
if (storageProvider.IsLocal)
{
    builder.Services.AddSingleton<LocalJobStorageService>();
    builder.Services.AddSingleton<IJobStorageService>(provider => provider.GetRequiredService<LocalJobStorageService>());
    builder.Services.AddSingleton<ITranscriptStorageService>(provider => provider.GetRequiredService<LocalJobStorageService>());
    builder.Services.AddSingleton<ITranslationStorageService, LocalTranslationStorageService>();
    builder.Services.AddSingleton<IVoicePreviewStorageService, LocalVoicePreviewStorageService>();
    builder.Services.AddSingleton<IGeneratedAudioStorageService, LocalGeneratedAudioStorageService>();
    builder.Services.AddSingleton<IRenderedVideoStorageService, LocalRenderedVideoStorageService>();
    builder.Services.AddSingleton<IJobUsageStorage>(provider => new LocalJobUsageStorage(
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,
            provider.GetRequiredService<IOptions<LocalStorageOptions>>().Value.RootPath))));
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
    builder.Services.AddSingleton<IJobUsageStorage, BlobJobUsageStorage>();
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
            options, provider.GetRequiredService<ILogger<AzureSpeechTranscriptionService>>(),
            provider.GetRequiredService<JobUsageService>(), provider.GetRequiredService<MediaAudioInspector>());
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
            options, provider.GetRequiredService<ILogger<AzureSpeechSynthesisService>>(),
            provider.GetRequiredService<JobUsageService>());
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
            options, provider.GetRequiredService<ILogger<AzureOpenAITranslationService>>(),
            provider.GetRequiredService<JobUsageService>());
    });
    builder.Services.AddSingleton<TranslationProcessor>();
}
builder.Services.AddScoped<ScriptEditingService>();
if (hosting.ProcessingEnabled)
{ builder.Services.AddHostedService<AudioExtractionWorker>(); }

var app = builder.Build();
if (!hosting.ProcessingEnabled)
{
    app.Logger.LogInformation("Background job processing is disabled. This instance will not scan or process stored jobs.");
}

if (!app.Environment.IsDevelopment() || hosting.Enabled || hosting.UsesSingleAccountLogin)
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!context.Request.IsHttps || !hosting.UsesSingleAccountLogin && !AppServiceAccess.IsAllowed(
            context.Request.Headers["X-MS-CLIENT-PRINCIPAL"], hosting.TenantId, hosting.AllowedUserObjectId))
        {
            app.Logger.LogWarning("Unauthorised Azure app request rejected.");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    });
}

app.UseExceptionHandler("/Error");

app.UseRouting();

if (hosting.UsesSingleAccountLogin)
{
    app.UseRateLimiter();
    app.UseAuthentication();
}
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
