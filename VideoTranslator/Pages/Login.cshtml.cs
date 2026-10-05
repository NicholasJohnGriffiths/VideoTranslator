using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[EnableRateLimiting(SingleAccountCredentials.LoginRateLimitPolicy)]
[TypeFilter(typeof(LoginAntiforgeryRecoveryFilter))]
public sealed class LoginModel(
    IOptions<AzureHostingOptions> hosting, ILogger<LoginModel> logger,
    SingleAccountCredentials? credentials = null) : PageModel
{
    [BindProperty, Required, StringLength(64)]
    public string Username { get; set; } = string.Empty;

    [BindProperty, Required, StringLength(256), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Expired { get; set; }

    public IActionResult OnGet()
    {
        if (!hosting.Value.UsesSingleAccountLogin) { return NotFound(); }
        if (Expired)
        {
            ModelState.AddModelError(string.Empty,
                "Your sign-in form is no longer valid. Please enter your username and password again. If this keeps happening, check that cookies are enabled in your browser.");
        }
        return User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl()) : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!hosting.Value.UsesSingleAccountLogin) { return NotFound(); }
        if (credentials is null) { throw new InvalidOperationException("Single-account credentials are not configured."); }
        if (!ModelState.IsValid || !credentials.Verify(Username, Password))
        {
            logger.LogWarning("Single-account sign-in rejected.");
            ModelState.Remove(nameof(Password));
            Password = string.Empty;
            ModelState.AddModelError(string.Empty, "The username or password is incorrect.");
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Page();
        }
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            credentials.CreatePrincipal(), new AuthenticationProperties { IsPersistent = false });
        logger.LogInformation("Single-account sign-in succeeded.");
        return LocalRedirect(SafeReturnUrl());
    }

    private string SafeReturnUrl() =>
        ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
