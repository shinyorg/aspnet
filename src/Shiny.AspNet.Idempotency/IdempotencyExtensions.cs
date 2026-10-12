using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.AspNet.Idempotency;

/// <summary>Registering idempotency keys.</summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options, and an in-memory store when nothing else has claimed the role. Calling it
    /// again composes the configuration with the earlier call's.
    /// <code>
    /// builder.Services.AddIdempotency(o => o.Expiration = TimeSpan.FromHours(6));
    /// </code>
    /// </summary>
    public static IServiceCollection AddIdempotency(this IServiceCollection services, Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<IdempotencyOptions>();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<IIdempotencyStore>(static _ => new MemoryIdempotencyStore());

        return services;
    }

    /// <summary>
    /// Keeps keys in the registered <see cref="IDistributedCache"/> instead of in process, so every
    /// instance behind a load balancer sees them. Replaces any store registered earlier. See
    /// <see cref="DistributedCacheIdempotencyStore"/> for what that does and does not guarantee.
    /// <code>
    /// builder.Services.AddStackExchangeRedisCache(o => o.Configuration = "localhost");
    /// builder.Services.AddIdempotency().AddDistributedCacheIdempotencyStore();
    /// </code>
    /// </summary>
    public static IServiceCollection AddDistributedCacheIdempotencyStore(this IServiceCollection services, string keyPrefix = "shiny:idempotency:")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keyPrefix);

        services.Replace(ServiceDescriptor.Singleton<IIdempotencyStore>(
            sp => new DistributedCacheIdempotencyStore(sp.GetRequiredService<IDistributedCache>(), keyPrefix)
        ));

        return services;
    }
}

/// <summary>Putting idempotency keys in the pipeline, and opting endpoints in.</summary>
public static class IdempotencyEndpointExtensions
{
    /// <summary>
    /// Honours <c>Idempotency-Key</c> on the endpoints that opted in.
    /// <para>
    /// Call it <b>after</b> <c>UseRouting()</c> (implicit with <c>WebApplication</c>) and
    /// <c>UseAuthorization()</c>: the caller is part of the key, and a request that is going to be
    /// refused should not claim one.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.ApplicationServices.GetService<IIdempotencyStore>() is null)
            throw new InvalidOperationException("UseIdempotency() needs services.AddIdempotency() to have been called.");

        return app.UseMiddleware<IdempotencyMiddleware>();
    }

    /// <summary>
    /// Requires an <c>Idempotency-Key</c> on these endpoints; a request without one is refused with a 400.
    /// Works on a single endpoint or a <c>RouteGroupBuilder</c> (every endpoint in it, nested groups included).
    /// <code>
    /// app.MapPost("/payments", Charge).RequireIdempotencyKey();
    /// app.MapGroup("/api/payments").RequireIdempotencyKey(TimeSpan.FromHours(1));
    /// </code>
    /// </summary>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder, TimeSpan? expiration = null)
        where TBuilder : IEndpointConventionBuilder
        => Add(builder, required: true, expiration);

    /// <summary>
    /// Honours an <c>Idempotency-Key</c> on these endpoints when the client sends one, and lets requests
    /// without one through.
    /// </summary>
    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder, TimeSpan? expiration = null)
        where TBuilder : IEndpointConventionBuilder
        => Add(builder, required: false, expiration);

    /// <summary>Exempts these endpoints from idempotency, including from a group's convention or a controller's attribute.</summary>
    public static TBuilder DisableIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new IdempotencyMetadata { Disabled = true });
    }

    static TBuilder Add<TBuilder>(TBuilder builder, bool required, TimeSpan? expiration)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (expiration is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiration), "An idempotency expiration must be positive.");

        // A new entry rather than an edit: endpoint metadata is ordered, and the most specific — the
        // one added last — is the one the middleware reads.
        return builder.WithMetadata(new IdempotencyMetadata { Required = required, Expiration = expiration });
    }
}
