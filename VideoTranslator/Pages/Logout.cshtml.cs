using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Pages;

public sealed class LogoutModel(IOptions<AzureHostingOptions> hosting) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        if (!hosting.Value.UsesSingleAccountLogin) { return NotFound(); }
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
