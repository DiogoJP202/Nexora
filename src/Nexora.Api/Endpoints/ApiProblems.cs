namespace Nexora.Api.Endpoints;

internal static class ApiProblems
{
    internal static IResult Result(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    internal static IResult InvalidRequest() => Result(StatusCodes.Status400BadRequest,
        "invalid_request", "Os dados da requisição são inválidos.");

    internal static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }
}
