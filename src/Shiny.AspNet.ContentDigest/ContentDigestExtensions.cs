using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shiny.AspNet.ContentDigest;

/// <summary>Registering RFC 9530 digests.</summary>
public static class ContentDigestServiceCollectionExtensions
{
    /// <summary>
    /// Registers the digest options.
    /// <code>
    /// builder.Services.AddContentDigest(o => o.ResponseDigest = DigestEmission.Always);
    /// </code>
    /// </summary>
    public static IServiceCollection AddContentDigest(
        this IServiceCollection services,
        Action<ContentDigestOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<ContentDigestOptions>();
        if (configure is not null)
            builder.Configure(configure);

        // Fails on first use, with a message, rather than on the first request with a digest in it.
        builder.Validate(
            static o => DigestFields.IsSupported(o.DefaultAlgorithm),
            $"ContentDigestOptions.DefaultAlgorithm must be '{DigestFields.Sha256}' or '{DigestFields.Sha512}'."
        );

        return services;
    }
}

/// <summary>Putting digests in the pipeline, and setting them per endpoint.</summary>
public static class ContentDigestApplicationBuilderExtensions
{
    /// <summary>
    /// Verifies request digests and adds response digests, using the options registered with
    /// <see cref="ContentDigestServiceCollectionExtensions.AddContentDigest"/> (or the defaults).
    /// <para>
    /// Register it <b>after</b> routing — implicit on <c>WebApplication</c>, or an explicit
    /// <c>UseRouting()</c> — so per-endpoint settings are visible, and <b>before</b>
    /// <c>UseRequestDecompression</c> and <c>UseResponseCompression</c>: the digest covers the bytes on
    /// the wire, so it has to see them compressed.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseContentDigest(this IApplicationBuilder app, Action<ContentDigestOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var registered = app.ApplicationServices.GetService<IOptions<ContentDigestOptions>>()?.Value;
        var options = registered ?? new ContentDigestOptions();

        if (configure is not null)
        {
            // A copy, so configuring this pipeline does not reach into another that shares the container.
            options = options.Clone();
            configure(options);
        }

        return app.UseContentDigest(options);
    }

    /// <summary>Verifies and adds digests using options built elsewhere.</summary>
    public static IApplicationBuilder UseContentDigest(this IApplicationBuilder app, ContentDigestOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        if (!DigestFields.IsSupported(options.DefaultAlgorithm))
            throw new ArgumentException(
                $"'{options.DefaultAlgorithm}' is not a supported digest algorithm. Use '{DigestFields.Sha256}' or '{DigestFields.Sha512}'.",
                nameof(options)
            );

        var logger = app.ApplicationServices
            .GetService<ILoggerFactory>()
            ?.CreateLogger<ContentDigestMiddleware>();

        var middleware = new ContentDigestMiddleware(options, logger);
        return app.Use(next => context => middleware.InvokeAsync(context, next));
    }
}

/// <summary>
/// Per-endpoint digest settings, on a single endpoint or a whole <c>MapGroup</c>.
/// <code>
/// app.MapPost("/upload", Upload).RequireContentDigest();
///
/// var signed = app.MapGroup("/signed").WithContentDigest();
/// signed.MapGet("/a", A);
/// signed.MapGet("/b", B).DisableContentDigest();
/// </code>
/// </summary>
public static class ContentDigestEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Requires the endpoint's requests to carry a <c>Content-Digest</c> that verifies — an upload
    /// that must not arrive truncated.
    /// </summary>
    public static TBuilder RequireContentDigest<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => Amend(builder, static m =>
        {
            m.RequireRequestDigest = true;
            m.Disabled = false;
        });

    /// <summary>Gives the endpoint's responses a <c>Content-Digest</c> whether or not the client asked.</summary>
    public static TBuilder WithContentDigest<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => Amend(builder, static m =>
        {
            m.ResponseDigest = DigestEmission.Always;
            m.Disabled = false;
        });

    /// <summary>Exempts the endpoint from digests in both directions, including from a group's convention.</summary>
    public static TBuilder DisableContentDigest<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => Amend(builder, static m => m.Disabled = true);

    /// <summary>
    /// Adds a new <see cref="ContentDigestMetadata"/> that starts from whatever the endpoint already
    /// carries, so a group's <c>WithContentDigest()</c> and an endpoint's <c>RequireContentDigest()</c>
    /// add up, while the newest — most specific — entry is still the one the middleware reads.
    /// </summary>
    static TBuilder Amend<TBuilder>(TBuilder builder, Action<ContentDigestMetadata> change) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpoint =>
        {
            IContentDigestMetadata? current = null;

            for (var i = endpoint.Metadata.Count - 1; i >= 0; i--)
            {
                if (endpoint.Metadata[i] is IContentDigestMetadata found)
                {
                    current = found;
                    break;
                }
            }

            var amended = new ContentDigestMetadata(current);
            change(amended);
            endpoint.Metadata.Add(amended);
        });

        return builder;
    }
}
