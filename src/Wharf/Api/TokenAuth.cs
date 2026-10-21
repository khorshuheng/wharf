using System.Security.Cryptography;
using System.Text;

namespace Wharf.Api;

/// <summary>
/// Bearer-token authentication. Tokens are compared in constant time. When no
/// token is configured the API is open, which is intended for local development
/// only.
/// </summary>
public static class TokenAuth
{
    public const string ScopeItem = "wharf.scope";

    public static string? Evaluate(string? authorization, WharfOptions options)
    {
        if (!options.AuthEnabled)
            return "write";

        if (string.IsNullOrEmpty(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var token = authorization["Bearer ".Length..].Trim();
        if (token.Length == 0)
            return null;

        if (!string.IsNullOrEmpty(options.Token) && FixedEquals(token, options.Token))
            return "write";

        if (!string.IsNullOrEmpty(options.ReadToken) && FixedEquals(token, options.ReadToken))
            return "read";

        return null;
    }

    public static string? Scope(HttpContext context) => context.Items[ScopeItem] as string;

    public static bool CanWrite(HttpContext context) => Scope(context) == "write";

    public static IApplicationBuilder UseWharfAuth(this IApplicationBuilder app, WharfOptions options)
    {
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/v1"))
            {
                await next();
                return;
            }

            var scope = Evaluate(context.Request.Headers.Authorization.ToString(), options);
            if (scope is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"wharf\"";
                await context.Response.WriteAsJsonAsync(
                    new ErrorEnvelope(new ErrorBody("unauthorized", "Missing or invalid bearer token", null)),
                    ApiJson.Options,
                    context.RequestAborted);
                return;
            }

            context.Items[ScopeItem] = scope;
            await next();
        });
    }

    private static bool FixedEquals(string a, string b)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
