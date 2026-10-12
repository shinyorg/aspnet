using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AspNet.DocumentDb.Internal;
using Shiny.DocumentDb;

namespace Shiny.AspNet.DocumentDb;

/// <summary>
/// Turns a document type into a complete HTTP resource in one line: list, by-id, count, create, replace,
/// patch (merge or JSON Patch), delete and a live SSE tail.
/// </summary>
/// <remarks>
/// Every route is a <see cref="Microsoft.AspNetCore.Http.RequestDelegate"/> that reads the context itself, so
/// nothing binds parameters by reflection and the whole resource is trim- and AOT-clean. The routes are mapped
/// into one <see cref="RouteGroupBuilder"/>, which is what is returned — so authorization, CORS, rate limiting,
/// output caching and OpenAPI conventions stated once land on every route the resource mapped, including the
/// ones a later <see cref="DocumentEndpoints"/> flag adds.
/// </remarks>
public static class DocumentEndpointExtensions
{
    /// <summary>
    /// Maps <typeparamref name="T"/> as a REST resource under <paramref name="prefix"/>. Returns the route
    /// group, so endpoint conventions compose across every route it registered.
    /// </summary>
    /// <example>
    /// <code>
    /// app.MapDocuments&lt;Order&gt;("/orders", o =>
    /// {
    ///     o.Operations = DocumentEndpoints.All;
    ///     o.AllowFilterOn(x => x.Status, x => x.Total);
    ///     o.TypeInfo = AppJsonContext.Default.Order;
    ///     o.Scope&lt;ITenantContext&gt;((tenant, _) => x => x.TenantId == tenant.TenantId);
    /// })
    /// .RequireAuthorization("orders");
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapDocuments<T>(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        Action<DocumentEndpointOptions<T>>? configure = null
    ) where T : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var options = new DocumentEndpointOptions<T>();
        configure?.Invoke(options);

        Validate(endpoints.ServiceProvider, options.Operations, options.RequiredServices, options.StoreName, typeof(T).Name);

        var group = endpoints.MapGroup(prefix);
        DocumentEndpointHandlers<T>.Map(group, options);

        return group;
    }

    /// <summary>
    /// Maps a <b>schema-free</b> JSON collection (<c>store.Collection(name)</c>) as a REST resource. Documents
    /// are plain JSON with no CLR type, filtered with the string grammar. Relational providers only.
    /// </summary>
    public static RouteGroupBuilder MapDocumentCollection(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        string collectionName,
        Action<DocumentCollectionEndpointOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var options = new DocumentCollectionEndpointOptions();
        configure?.Invoke(options);

        Validate(endpoints.ServiceProvider, options.Operations, options.RequiredServices, options.StoreName, collectionName);

        var group = endpoints.MapGroup(prefix);
        DocumentCollectionEndpointHandlers.Map(group, collectionName, options);

        return group;
    }

    /// <summary>
    /// Startup checks, so the failure is at boot rather than on the first request that needs the thing:
    /// every <c>Scope&lt;TService&gt;</c> service must be registered, and <c>/stream</c> is refused outright on
    /// a provider without change monitoring.
    /// </summary>
    static void Validate(
        IServiceProvider services,
        DocumentEndpoints operations,
        IReadOnlyList<Type> requiredServices,
        string? storeName,
        string resource
    )
    {
        // A scoped service cannot be resolved from the root provider, so ask the container whether it is
        // registered rather than trying to build one.
        var registered = services.GetService<IServiceProviderIsService>();

        foreach (var serviceType in requiredServices)
        {
            var known = registered?.IsService(serviceType) ?? services.GetService(serviceType) is not null;
            if (!known)
                throw new InvalidOperationException(
                    $"'{resource}' registers a Scope<{serviceType.Name}>(…) but no '{serviceType}' is registered. "
                    + "A scope that cannot resolve its service refuses every request, so this is a startup error."
                );
        }

        if (operations.HasFlag(DocumentEndpoints.Stream))
        {
            var store = storeName is null
                ? services.GetService<IDocumentStore>()
                : services.GetKeyedService<IDocumentStore>(storeName);

            if (store is not null and not IObservableDocumentStore)
                throw new InvalidOperationException(
                    $"'{resource}' maps DocumentEndpoints.Stream, but '{store.GetType().Name}' does not support "
                    + "change monitoring (IObservableDocumentStore). Drop the Stream flag or move to a provider "
                    + "that does — a /stream route that can only ever return 501 is worse than no route."
                );
        }
    }
}
