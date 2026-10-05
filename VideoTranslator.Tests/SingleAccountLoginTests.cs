using System.Security.Claims;
using System.Security.Cryptography;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using VideoTranslator.Configuration;
using VideoTranslator.Pages;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class SingleAccountLoginTests
{
    private const string TestPassword = "test-only-password";
    private static readonly string Hash = CreateHash(TestPassword);
    private static string CreateHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, SingleAccountLoginOptions.Iterations,
            HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256:600000:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
    private static SingleAccountCredentials Credentials(string? hash = null) => new(Options.Create(
        new SingleAccountLoginOptions { Username = "TestAccount", PasswordHash = hash ?? Hash }));

    [Fact]
    public void CredentialsRequireCorrectPasswordAndUsername()
    {
        var credentials = Credentials();
        Assert.True(credentials.Verify("TestAccount", TestPassword));
        Assert.True(credentials.Verify("testaccount", TestPassword));
        Assert.False(credentials.Verify("WrongAccount", TestPassword));
        Assert.False(credentials.Verify("TestAccount", "wrong"));
        Assert.False(credentials.Verify("TestAccount", ""));
        Assert.False(credentials.Verify(new string('x', 65), TestPassword));
        Assert.False(credentials.Verify("TestAccount", new string('x', 257)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext-is-not-a-hash")]
    [InlineData("pbkdf2-sha256:1:AAAA:AAAA")]
    [InlineData("pbkdf2-sha256:600000:invalid!:AAAA")]
    [InlineData("pbkdf2-sha256:600000:AAAA:AAAA")]
    public void InvalidHashFailsClosed(string hash) =>
        Assert.Throws<InvalidOperationException>(() => Credentials(hash));

    [Theory]
    [InlineData("")]
    [InlineData(" account ")]
    [InlineData("account\n")]
    public void InvalidUsernameFailsClosed(string username) =>
        Assert.Throws<InvalidOperationException>(() =>
            new SingleAccountLoginOptions { Username = username, PasswordHash = Hash }.ReadPasswordHash());

    [Fact]
    public void CredentialRotationInvalidatesExistingPrincipals()
    {
        var credentials = Credentials();
        var principal = credentials.CreatePrincipal();
        Assert.True(credentials.IsCurrentPrincipal(principal));
        Assert.False(Credentials(CreateHash("a-new-test-password")).IsCurrentPrincipal(principal));
        Assert.False(credentials.IsCurrentPrincipal(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(credentials.IsCurrentPrincipal(null));
    }

    [Fact]
    public void PersistentKeysMustStayOutsidePublicWebRoot()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "login-tests", "wwwroot");
        foreach (var directory in new[] { "", "relative", webRoot, Path.Combine(webRoot, "keys") })
        {
            Assert.Throws<InvalidOperationException>(() =>
                new SingleAccountLoginOptions { KeyDirectory = directory }.ValidateKeyDirectory(webRoot));
        }
        var keys = Path.Combine(Path.GetTempPath(), "login-tests", "private-keys");
        Assert.Equal(Path.GetFullPath(keys),
            new SingleAccountLoginOptions { KeyDirectory = keys }.ValidateKeyDirectory(webRoot));
    }

    private static LoginModel Login(FakeAuthentication authentication)
    {
        var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        var model = new LoginModel(
            Options.Create(new AzureHostingOptions { AuthenticationMode = "SingleAccount" }),
            NullLogger<LoginModel>.Instance, Credentials())
        {
            PageContext = new PageContext(new ActionContext(context, new RouteData(), new PageActionDescriptor())),
            Username = "TestAccount", Password = TestPassword
        };
        model.Url = new UrlHelper(model.PageContext);
        return model;
    }

    [Theory]
    [InlineData("/Upload", "/Upload")]
    [InlineData("https://outside.invalid/", "/")]
    [InlineData("//outside.invalid/", "/")]
    [InlineData("/\\outside.invalid/", "/")]
    [InlineData(null, "/")]
    public async Task SuccessfulLoginOnlyRedirectsLocally(string? returnUrl, string expected)
    {
        var authentication = new FakeAuthentication();
        var model = Login(authentication);
        model.ReturnUrl = returnUrl;
        var result = Assert.IsType<LocalRedirectResult>(await model.OnPostAsync());
        Assert.Equal(expected, result.Url);
        Assert.True(Credentials().IsCurrentPrincipal(authentication.SignedIn));
        Assert.False(authentication.Properties?.IsPersistent);
    }

    [Fact]
    public async Task IncorrectPasswordDoesNotIssueSessionAndIsCleared()
    {
        var authentication = new FakeAuthentication();
        var model = Login(authentication);
        model.Password = "wrong";
        Assert.IsType<PageResult>(await model.OnPostAsync());
        Assert.Equal(StatusCodes.Status401Unauthorized, model.Response.StatusCode);
        Assert.Null(authentication.SignedIn);
        Assert.Empty(model.Password);
        Assert.False(model.ModelState.IsValid);
    }

    [Fact]
    public async Task InvalidModelDoesNotIssueSession()
    {
        var authentication = new FakeAuthentication();
        var model = Login(authentication);
        model.ModelState.AddModelError("Username", "Username is required.");
        Assert.IsType<PageResult>(await model.OnPostAsync());
        Assert.Null(authentication.SignedIn);
    }

    [Fact]
    public void StaleSignInTokenRedirectsToFreshFormWithoutSigningIn()
    {
        var authentication = new FakeAuthentication();
        var model = Login(authentication);
        model.Request.QueryString = new QueryString("?ReturnUrl=%2FUpload");
        var context = new ResultExecutingContext(model.PageContext, [],
            new AntiforgeryValidationFailedResult(), model);
        var filter = new LoginAntiforgeryRecoveryFilter(
            NullLogger<LoginAntiforgeryRecoveryFilter>.Instance);

        filter.OnResultExecuting(context);

        var redirect = Assert.IsType<RedirectToPageResult>(context.Result);
        Assert.Equal("/Login", redirect.PageName);
        Assert.Equal(true, redirect.RouteValues!["expired"]);
        Assert.Equal("/Upload", redirect.RouteValues["ReturnUrl"]);
        Assert.False(redirect.PreserveMethod);
        Assert.Equal("no-store", model.Response.Headers.CacheControl.ToString());
        Assert.Null(authentication.SignedIn);
    }

    [Fact]
    public void FreshFormExplainsTokenFailureWithoutRetainingPassword()
    {
        var model = Login(new FakeAuthentication());
        model.Password = string.Empty;
        model.Expired = true;

        Assert.IsType<PageResult>(model.OnGet());

        var error = Assert.Single(model.ModelState[string.Empty]!.Errors);
        Assert.Contains("sign-in form is no longer valid", error.ErrorMessage);
        Assert.Empty(model.Password);
    }

    [Fact]
    public void RecoveryDoesNotReplaceOtherBadRequests()
    {
        var model = Login(new FakeAuthentication());
        var result = new BadRequestResult();
        var context = new ResultExecutingContext(model.PageContext, [], result, model);

        new LoginAntiforgeryRecoveryFilter(NullLogger<LoginAntiforgeryRecoveryFilter>.Instance)
            .OnResultExecuting(context);

        Assert.Same(result, context.Result);
    }

    [Fact]
    public async Task HttpLoginRejectsStaleTokenThenAcceptsFreshForm()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LoginModel).Assembly.GetName().Name,
            EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorPages();
        builder.Services.AddSingleton<IOptions<AzureHostingOptions>>(
            Options.Create(new AzureHostingOptions { AuthenticationMode = "SingleAccount" }));
        builder.Services.AddSingleton(Credentials());
        var authentication = new FakeAuthentication();
        builder.Services.AddSingleton<IAuthenticationService>(authentication);
        builder.Services.AddRateLimiter(options => options.AddPolicy(
            SingleAccountCredentials.LoginRateLimitPolicy,
            _ => RateLimitPartition.GetNoLimiter("test")));

        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.MapRazorPages();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address) };
        using var rejected = await client.PostAsync("/Login?ReturnUrl=%2FUpload",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = "TestAccount", ["Password"] = TestPassword,
                ["__RequestVerificationToken"] = "stale-token"
            }));
        Assert.Equal(HttpStatusCode.Found, rejected.StatusCode);
        Assert.Null(authentication.SignedIn);
        Assert.NotNull(rejected.Headers.Location);

        using var fresh = await client.GetAsync(rejected.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var html = await fresh.Content.ReadAsStringAsync();
        Assert.Contains("sign-in form is no longer valid", html);
        Assert.DoesNotContain(TestPassword, html);
        var token = Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        using var accepted = await client.PostAsync("/Login?ReturnUrl=%2FUpload",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = "TestAccount", ["Password"] = TestPassword,
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
            }));
        Assert.Equal(HttpStatusCode.Found, accepted.StatusCode);
        Assert.Equal("/Upload", accepted.Headers.Location?.OriginalString);
        Assert.NotNull(authentication.SignedIn);
    }

    [Fact]
    public async Task LogoutRequiresPostAndClearsSession()
    {
        var authentication = new FakeAuthentication();
        var login = Login(authentication);
        var logout = new LogoutModel(Options.Create(new AzureHostingOptions { AuthenticationMode = "SingleAccount" }))
        { PageContext = login.PageContext };
        Assert.Equal(405, Assert.IsType<StatusCodeResult>(logout.OnGet()).StatusCode);
        Assert.False(authentication.SignedOut);
        Assert.IsType<RedirectToPageResult>(await logout.OnPostAsync());
        Assert.True(authentication.SignedOut);
    }

    private sealed class FakeAuthentication : IAuthenticationService
    {
        public ClaimsPrincipal? SignedIn { get; private set; }
        public AuthenticationProperties? Properties { get; private set; }
        public bool SignedOut { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedIn = principal;
            Properties = properties;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOut = true;
            return Task.CompletedTask;
        }
    }
}
