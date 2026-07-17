namespace a2n.Hangfire.Dashboard.RestApi;

/// <summary>
/// Configuration for the optional read-only Hangfire Dashboard REST API
/// (<c>a2n.Hangfire.Dashboard.RestApi</c>).
/// </summary>
/// <remarks>
/// The REST API is opt-in and shipped as a separate NuGet package that the core dashboard
/// package does not reference. It is registered with
/// <see cref="RestApiDashboardExtensions.AddHangfireDashboardRestApi"/> and mapped with
/// <see cref="RestApiDashboardExtensions.MapHangfireDashboardRestApi"/>.
/// </remarks>
public sealed class RestApiOptions
{
    /// <summary>
    /// The authorization policy name applied to every REST API endpoint via
    /// <c>RequireAuthorization</c>. When <see langword="null"/>, the endpoints require an
    /// authenticated request against the host's default authentication scheme (typically the
    /// JWT bearer scheme configured by the host). This ensures job data is never exposed
    /// anonymously by default.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// The name of the authentication scheme(s) the REST API endpoints authenticate against.
    /// When <see langword="null"/>, the host's default authentication scheme is used. Set this
    /// to the JWT bearer scheme name configured by the host when it differs from the default.
    /// </summary>
    public string? AuthenticationScheme { get; set; }

    /// <summary>
    /// The route path (relative to the group's <c>/api/v1</c> base) at which the generated
    /// OpenAPI document is served as JSON. Defaults to <c>/openapi/v1.json</c>.
    /// </summary>
    public string OpenApiRoutePath { get; set; } = "/openapi/v1.json";

    /// <summary>
    /// The default number of records returned per page for paged endpoints when the client
    /// does not specify a page size.
    /// </summary>
    public int DefaultPageSize { get; set; } = 20;

    /// <summary>
    /// The maximum number of records a client may request per page. Requests exceeding this
    /// value are treated as invalid.
    /// </summary>
    public int MaxPageSize { get; set; } = 500;
}
