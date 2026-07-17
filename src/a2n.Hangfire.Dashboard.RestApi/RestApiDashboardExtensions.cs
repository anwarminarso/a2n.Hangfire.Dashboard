using a2n.Hangfire.Dashboard.RestApi.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
#if NET9_0_OR_GREATER
using Microsoft.AspNetCore.OpenApi;
#if NET10_0_OR_GREATER
using Microsoft.OpenApi;
#else
using Microsoft.OpenApi.Models;
#endif
#else
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using Swashbuckle.AspNetCore.Swagger;
#endif

namespace a2n.Hangfire.Dashboard.RestApi;

/// <summary>
/// Opt-in registration and mapping entry points for the read-only Hangfire Dashboard REST API.
/// </summary>
/// <remarks>
/// JWT authentication itself is configured by the host application
/// (e.g. <c>AddAuthentication().AddJwtBearer(...)</c>); this package references the host's scheme
/// and applies authorization onto the endpoint group. The REST API package is separate from and
/// not referenced by the core <c>a2n.Hangfire.Dashboard</c> package.
/// </remarks>
public static class RestApiDashboardExtensions
{
    /// <summary>
    /// The API version segment appended after the dashboard path prefix. The endpoint group is
    /// mounted at <c>{pathPrefix}/api/v1</c>.
    /// </summary>
    public const string ApiVersionSegment = "api/v1";

    /// <summary>
    /// The identifier of the JWT bearer security scheme declared in the generated OpenAPI document
    /// and required by every REST API endpoint (Req 11.3).
    /// </summary>
    internal const string JwtSecuritySchemeId = "Bearer";

    /// <summary>
    /// The OpenAPI document name. Combined with the built-in route pattern
    /// (<c>/openapi/{documentName}.json</c>) this yields the default document route
    /// <c>/openapi/v1.json</c>, matching <see cref="RestApiOptions.OpenApiRoutePath"/>.
    /// </summary>
    internal const string OpenApiDocumentName = "v1";

    /// <summary>
    /// Registers the services required by the Hangfire Dashboard REST API (endpoint services and
    /// OpenAPI document generation). This does not map any endpoint; call
    /// <see cref="MapHangfireDashboardRestApi"/> to expose the endpoints.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="configure">An optional callback to configure <see cref="RestApiOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <remarks>
    /// The OpenAPI document is generated from endpoint metadata and declares the JWT bearer security
    /// scheme required by every endpoint (Req 11.1, 11.3). On net9.0/net10.0 the built-in
    /// <c>Microsoft.AspNetCore.OpenApi</c> generator is used (with a document transformer that adds
    /// the security scheme); on net8.0 Swashbuckle's SwaggerGen produces the same document.
    /// </remarks>
    public static IServiceCollection AddHangfireDashboardRestApi(
        this IServiceCollection services,
        Action<RestApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RestApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Ensure the authorization services required by RequireAuthorization are present. This is
        // idempotent: the host typically already calls AddAuthorization() alongside its JWT bearer
        // configuration, so registering again is safe and simply guarantees availability.
        services.AddAuthorization();

#if NET9_0_OR_GREATER
        // net9.0 / net10.0: register the built-in OpenAPI document generator and a document
        // transformer that declares the JWT bearer security scheme required by every endpoint
        // (Req 11.1, 11.3). The document is served as JSON by MapOpenApi (Req 11.2).
        services.AddOpenApi(OpenApiDocumentName, openApi =>
        {
            openApi.AddDocumentTransformer<JwtBearerSecuritySchemeDocumentTransformer>();
        });
#else
        // net8.0: register Swashbuckle SwaggerGen to auto-generate the same OpenAPI document from
        // endpoint metadata (Req 11.1), declaring the JWT bearer security scheme required by every
        // endpoint (Req 11.3). The document is served as JSON by a mapped route (Req 11.2).
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(swagger =>
        {
            swagger.SwaggerDoc(OpenApiDocumentName, new OpenApiInfo
            {
                Title = "a2n.Hangfire.Dashboard REST API",
                Version = OpenApiDocumentName,
                Description = "Read-only REST API for a2n.Hangfire.Dashboard.",
            });

            var bearerScheme = new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT bearer authentication. Provide the token issued by the host's configured JWT scheme.",
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = JwtSecuritySchemeId,
                },
            };

            swagger.AddSecurityDefinition(JwtSecuritySchemeId, bearerScheme);
            swagger.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [bearerScheme] = Array.Empty<string>(),
            });
        });
#endif

        return services;
    }

    /// <summary>
    /// Maps the read-only REST API endpoint group at <c>{pathPrefix}/api/v1</c>, secures every
    /// endpoint with the host's configured JWT bearer scheme, and serves the generated OpenAPI
    /// document as JSON relative to the group so it remains reachable under a custom dashboard path
    /// prefix (Req 11.2, 16.1).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder from the host application.</param>
    /// <param name="pathPrefix">
    /// The base path at which the dashboard is mounted (default <c>/hangfire</c>). The REST API
    /// group is mounted at <c>{pathPrefix}/api/v1</c> so it remains reachable under a custom
    /// dashboard path prefix.
    /// </param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> for chaining.</returns>
    public static IEndpointRouteBuilder MapHangfireDashboardRestApi(
        this IEndpointRouteBuilder endpoints,
        string pathPrefix = "/hangfire")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var groupPrefix = BuildGroupPrefix(pathPrefix);

        // Resolve the REST API options (populated by AddHangfireDashboardRestApi). Fall back to
        // defaults so mapping still works if the host only called the mapping extension.
        var options = endpoints.ServiceProvider.GetService<RestApiOptions>() ?? new RestApiOptions();

        // The read-only endpoint group, mounted relative to the configured path prefix.
        var group = endpoints.MapGroup(groupPrefix);

        // Require authorization on the whole group so every REST endpoint is secured against the
        // host's configured JWT bearer scheme: no valid token → 401 (no job data exposed),
        // authenticated but not authorized → 403, and no anonymous access by default
        // (Req 10.1–10.4, 17.1, 17.2).
        if (!string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
        {
            // Host-defined named policy (e.g. scope/role requirements bound to the JWT scheme).
            group.RequireAuthorization(options.AuthorizationPolicy);
        }
        else if (!string.IsNullOrWhiteSpace(options.AuthenticationScheme))
        {
            // Inline policy requiring an authenticated user against the specified JWT bearer scheme.
            group.RequireAuthorization(
                new AuthorizationPolicyBuilder(options.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build());
        }
        else
        {
            // Require an authenticated user via the host's default authorization policy.
            group.RequireAuthorization();
        }

        RestApiEndpoints.Map(group, options);

        MapOpenApiDocument(group, options);

        return endpoints;
    }

    /// <summary>
    /// Serves the generated OpenAPI document as JSON, relative to the REST API group (default
    /// <c>{pathPrefix}/api/v1/openapi/v1.json</c>). The document endpoint allows anonymous access so
    /// client/code generators can fetch the specification; the document itself declares the JWT
    /// bearer scheme required to call the data endpoints (Req 11.2, 11.3).
    /// </summary>
    private static void MapOpenApiDocument(RouteGroupBuilder group, RestApiOptions options)
    {
#if NET9_0_OR_GREATER
        // Built-in OpenAPI endpoint. The default route pattern (/openapi/{documentName}.json) with
        // the "v1" document yields /openapi/v1.json relative to the group, matching
        // RestApiOptions.OpenApiRoutePath. MapOpenApi endpoints are excluded from the document.
        group.MapOpenApi().AllowAnonymous();
#else
        // net8.0: serve the Swashbuckle-generated document at the configured route relative to the
        // group. Resolve ISwaggerProvider per request and serialize the document to OpenAPI 3.0 JSON.
        var route = NormalizeRelativeRoute(options.OpenApiRoutePath);

        group.MapGet(route, (ISwaggerProvider swaggerProvider) =>
        {
            var document = swaggerProvider.GetSwagger(OpenApiDocumentName);

            using var textWriter = new StringWriter(CultureInfo.InvariantCulture);
            var jsonWriter = new OpenApiJsonWriter(textWriter);
            document.SerializeAsV3(jsonWriter);

            return Results.Text(textWriter.ToString(), "application/json", Encoding.UTF8);
        })
        .AllowAnonymous()
        .ExcludeFromDescription();
#endif
    }

    /// <summary>
    /// Normalizes the supplied dashboard path prefix and appends the API version segment, yielding
    /// a leading-slash, non-trailing-slash route group prefix such as <c>/hangfire/api/v1</c>.
    /// </summary>
    internal static string BuildGroupPrefix(string pathPrefix)
    {
        var prefix = (pathPrefix ?? string.Empty).Trim();

        // Strip surrounding slashes so we can compose a single canonical form.
        prefix = prefix.Trim('/');

        return prefix.Length == 0
            ? $"/{ApiVersionSegment}"
            : $"/{prefix}/{ApiVersionSegment}";
    }

#if !NET9_0_OR_GREATER
    /// <summary>
    /// Ensures a route relative to the group has a single leading slash and no trailing slash.
    /// </summary>
    internal static string NormalizeRelativeRoute(string route)
    {
        var trimmed = (route ?? string.Empty).Trim().Trim('/');
        return trimmed.Length == 0 ? "/openapi/v1.json" : $"/{trimmed}";
    }
#endif
}

#if NET9_0_OR_GREATER
/// <summary>
/// Declares the JWT bearer security scheme on the generated OpenAPI document and marks it as required
/// across the API (Req 11.3). Applied through <c>AddOpenApi(...).AddDocumentTransformer&lt;T&gt;()</c>.
/// </summary>
internal sealed class JwtBearerSecuritySchemeDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Components ??= new OpenApiComponents();

#if NET10_0_OR_GREATER
        // Microsoft.OpenApi 2.0 (net10.0): scheme dictionary is keyed to IOpenApiSecurityScheme and
        // security requirements reference the scheme via OpenApiSecuritySchemeReference.
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[RestApiDashboardExtensions.JwtSecuritySchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT bearer authentication. Provide the token issued by the host's configured JWT scheme.",
        };

        var reference = new OpenApiSecuritySchemeReference(
            RestApiDashboardExtensions.JwtSecuritySchemeId, document, null);

        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [reference] = new List<string>(),
        });
#else
        // Microsoft.OpenApi 1.x (net9.0): the scheme carries its own reference and is used directly
        // as the security-requirement key.
        var scheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT bearer authentication. Provide the token issued by the host's configured JWT scheme.",
            Reference = new OpenApiReference
            {
                Type = ReferenceType.SecurityScheme,
                Id = RestApiDashboardExtensions.JwtSecuritySchemeId,
            },
        };

        document.Components.SecuritySchemes ??= new Dictionary<string, OpenApiSecurityScheme>();
        document.Components.SecuritySchemes[RestApiDashboardExtensions.JwtSecuritySchemeId] = scheme;

        document.SecurityRequirements ??= new List<OpenApiSecurityRequirement>();
        document.SecurityRequirements.Add(new OpenApiSecurityRequirement
        {
            [scheme] = Array.Empty<string>(),
        });
#endif

        return Task.CompletedTask;
    }
}
#endif
