using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shiny.AspNet.Webhooks.Internal;

namespace Shiny.AspNet.Webhooks;

/// <summary>Registering named webhook verifiers and shared webhook settings.</summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WebhookOptions"/>. Only needed for named verifiers, or for settings every
    /// webhook endpoint shares — <c>MapWebhook</c> with a verifier inline works without it.
    /// <code>
    /// builder.Services.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(secret)));
    /// </code>
    /// </summary>
    public static IServiceCollection AddWebhooks(this IServiceCollection services, Action<WebhookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<WebhookOptions>();
        if (configure is not null)
            options.Configure(configure);

        return services;
    }
}

/// <summary>Mapping webhook receivers, requiring signatures on endpoints, and putting verification in the pipeline.</summary>
public static class WebhookEndpointExtensions
{
    /// <summary>
    /// Maps a <c>POST</c> webhook receiver that only runs for deliveries whose signature verifies.
    /// <code>
    /// app.MapWebhook("/hooks/github", WebhookSignature.GitHub(secret), async (WebhookContext ctx) =>
    /// {
    ///     if (ctx.EventType == "push")
    ///         await builds.QueueAsync(ctx.ReadFromJson(AppJson.Default.PushEvent)!);
    /// });
    /// </code>
    /// <para>
    /// The body is buffered (up to <see cref="WebhookOptions.MaxBodySize"/>), verified over its exact
    /// bytes, and handed to the handler as <see cref="WebhookContext.Body"/>. A delivery that fails is
    /// answered 401 with a problem body that does not say which check failed; the reason is logged.
    /// A handler that writes nothing answers 200, which is all any sender wants.
    /// </para>
    /// <para>
    /// Verification is part of the route itself rather than a separate middleware, so there is no
    /// ordering to get wrong and nothing to forget to install. The returned builder is ASP.NET's own,
    /// so <c>RequireHost</c>, <c>RequireRateLimiting</c>, <c>ExcludeFromDescription</c> and the rest
    /// apply as usual.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The app or route group to map the route on.</param>
    /// <param name="pattern">The route template; the route answers <c>POST</c> only.</param>
    /// <param name="verifier">Checks the sender's signature, e.g. <see cref="WebhookSignature.GitHub"/>.</param>
    /// <param name="handler">Runs only for a verified delivery.</param>
    /// <param name="configure">Adjusts the shared <see cref="WebhookOptions"/> for this endpoint alone — a larger body limit, duplicate suppression.</param>
    public static IEndpointConventionBuilder MapWebhook(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        IWebhookVerifier verifier,
        Func<WebhookContext, Task> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(handler);
        return endpoints.MapWebhookCore(pattern, verifier, configure, ctx => handler(ctx.GetWebhook()));
    }

    /// <summary><see cref="MapWebhook(IEndpointRouteBuilder, string, IWebhookVerifier, Func{WebhookContext, Task}, Action{WebhookOptions}?)"/> for a handler that returns a result.</summary>
    public static IEndpointConventionBuilder MapWebhook(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        IWebhookVerifier verifier,
        Func<WebhookContext, Task<IResult>> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(handler);
        return endpoints.MapWebhookCore(pattern, verifier, configure, Execute(handler));
    }

    /// <summary>
    /// Maps a webhook receiver against a verifier registered by name with <c>AddWebhooks</c>. The name
    /// is resolved now, so a missing verifier fails at startup rather than on the first delivery.
    /// </summary>
    public static IEndpointConventionBuilder MapWebhook(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string verifierName,
        Func<WebhookContext, Task> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(verifierName);

        return endpoints.MapWebhook(pattern, SharedOptions(endpoints.ServiceProvider).GetVerifier(verifierName), handler, configure);
    }

    /// <summary>Maps a webhook receiver against a named verifier, for a handler that returns a result.</summary>
    public static IEndpointConventionBuilder MapWebhook(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string verifierName,
        Func<WebhookContext, Task<IResult>> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(verifierName);

        return endpoints.MapWebhook(pattern, SharedOptions(endpoints.ServiceProvider).GetVerifier(verifierName), handler, configure);
    }

    /// <summary>
    /// Verifies deliveries to every endpoint marked with <c>[RequireWebhookSignature]</c> or
    /// <c>.RequireWebhookSignature(…)</c>. Other endpoints are untouched.
    /// <para>
    /// It needs the endpoint, so it runs after routing: anywhere with <c>WebApplication</c>, or after
    /// an explicit <c>UseRouting()</c>. Put it after anything that must see every request unaltered
    /// and before nothing in particular — it only replaces the request body with a buffered copy of
    /// itself.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseWebhookVerification(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<WebhookVerificationMiddleware>();
    }

    /// <summary>
    /// Requires a verified signature on these endpoints, from a verifier registered by name. Enforced
    /// by <see cref="UseWebhookVerification"/>.
    /// <code>
    /// app.MapPost("/hooks/stripe", Handler).RequireWebhookSignature("stripe");
    /// app.MapGroup("/hooks/github").RequireWebhookSignature("github");
    /// </code>
    /// </summary>
    public static TBuilder RequireWebhookSignature<TBuilder>(this TBuilder builder, string verifierName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(verifierName);

        builder.WithMetadata(new WebhookMetadata { VerifierName = verifierName });
        return builder;
    }

    /// <summary>
    /// Requires a verified signature on these endpoints, from the given verifier. Enforced by
    /// <see cref="UseWebhookVerification"/>.
    /// </summary>
    public static TBuilder RequireWebhookSignature<TBuilder>(this TBuilder builder, IWebhookVerifier verifier)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(verifier);

        builder.WithMetadata(new WebhookMetadata { Verifier = verifier });
        return builder;
    }

    static IEndpointConventionBuilder MapWebhookCore(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        IWebhookVerifier verifier,
        Action<WebhookOptions>? configure,
        RequestDelegate handler
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(verifier);

        var services = endpoints.ServiceProvider;
        var options = SharedOptions(services);

        if (configure is not null)
        {
            options = options.CloneSettings();
            configure(options);
        }

        ILogger logger = services.GetService<ILoggerFactory>()?.CreateLogger<WebhookVerificationMiddleware>()
            ?? (ILogger)NullLogger<WebhookVerificationMiddleware>.Instance;

        RequestDelegate run = ctx => WebhookPipeline.RunAsync(ctx, verifier, options, logger, handler);

        // The metadata describes the route (and lets UseWebhookVerification, if it gets there first,
        // do the work with this route's settings and leave nothing for the delegate); the delegate
        // is what enforces it.
        return endpoints
            .MapPost(pattern, run)
            .WithMetadata(new WebhookMetadata { Verifier = verifier, Options = configure is null ? null : options });
    }

    static RequestDelegate Execute(Func<WebhookContext, Task<IResult>> handler)
        => async ctx =>
        {
            var result = await handler(ctx.GetWebhook()).ConfigureAwait(false);
            if (result is not null)
                await result.ExecuteAsync(ctx).ConfigureAwait(false);
        };

    static WebhookOptions SharedOptions(IServiceProvider services)
        => services.GetService<IOptions<WebhookOptions>>()?.Value ?? new WebhookOptions();
}
