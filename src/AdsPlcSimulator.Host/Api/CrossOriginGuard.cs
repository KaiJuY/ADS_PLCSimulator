namespace AdsPlcSimulator.Host.Api;

// A page on any site can send a simple POST (no body) to localhost without a preflight; the browser adds its Origin header.
// curl, scripts and the integration driver send no Origin header, so they pass.
internal static class CrossOriginGuard
{
    public const string Error = "Cross-origin requests cannot change the Host state.";

    public static bool IsRejected(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            return false;

        string? origin = request.Headers.Origin;
        if (origin is null)
            return false;

        return !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    public static ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (IsRejected(context.HttpContext.Request))
            return ValueTask.FromResult<object?>(Results.Json(new { error = Error }, statusCode: StatusCodes.Status403Forbidden));
        return next(context);
    }
}
