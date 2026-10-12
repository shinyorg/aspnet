using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AspNet.Acme.Internal;

/// <summary>
/// Puts the HTTP-01 responder in front of the application's whole pipeline.
/// <para>
/// A startup filter rather than an <c>app.UseAcmeChallenge()</c> the app has to remember: the CA's
/// fetch is a plain, anonymous GET on port 80, and everything an app typically puts first —
/// <c>UseHttpsRedirection</c>, HSTS, authentication, host filtering, a fallback route — would turn it
/// away or answer it with something else. Running before all of it, with nothing to place correctly,
/// is the only position that is always right. Requests that are not a pending challenge fall through
/// untouched.
/// </para>
/// </summary>
sealed class AcmeStartupFilter(IServiceProvider services) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var responders = new List<AcmeChallengeResponder>(2);
        if (services.GetService<AcmeCertificateManager>() is { } manager)
            responders.Add(manager.Challenges);
        if (services.GetService<AcmeCertificateRegistry>() is { } registry)
            responders.Add(registry.Challenges);

        if (responders.Count > 0)
            app.Use(next => context => AcmeChallengeMiddleware.InvokeAsync(context, next, responders));

        next(app);
    };
}

static class AcmeChallengeMiddleware
{
    public static Task InvokeAsync(HttpContext context, RequestDelegate next, IReadOnlyList<AcmeChallengeResponder> responders)
    {
        var request = context.Request;
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && request.Path.Value is { } path
            && path.StartsWith(AcmeChallengeResponder.HttpChallengePrefix, StringComparison.Ordinal))
        {
            var token = path[AcmeChallengeResponder.HttpChallengePrefix.Length..];
            foreach (var responder in responders)
            {
                if (responder.TryGetHttp(token, out var keyAuthorization))
                {
                    // RFC 8555 §8.3: the body is the key authorization, nothing else.
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength = keyAuthorization.Length;
                    return HttpMethods.IsHead(request.Method)
                        ? Task.CompletedTask
                        : context.Response.WriteAsync(keyAuthorization, context.RequestAborted);
                }
            }
        }

        return next(context);
    }
}
