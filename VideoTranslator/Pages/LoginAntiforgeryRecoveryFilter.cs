using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace VideoTranslator.Pages;

public sealed class LoginAntiforgeryRecoveryFilter(
    ILogger<LoginAntiforgeryRecoveryFilter> logger) : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not AntiforgeryValidationFailedResult) { return; }

        logger.LogWarning("Sign-in form security token rejected; returning a fresh sign-in page.");
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.Result = new RedirectToPageResult("/Login", new
        {
            expired = true,
            ReturnUrl = context.HttpContext.Request.Query["ReturnUrl"].ToString()
        });
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
