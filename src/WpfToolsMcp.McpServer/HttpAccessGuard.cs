using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace WpfToolsMcp.McpServer;

/// <summary>
/// Admits only MCP clients that are not web pages and that present the access
/// token, when one is configured.
/// </summary>
/// <remarks>
/// MCP clients do not send an Origin header; browsers send one with every
/// cross-site request. Rejecting requests with an Origin header keeps web pages,
/// including DNS-rebinding pages on a loopback endpoint, from driving the desktop.
/// </remarks>
internal sealed class HttpAccessGuard
{
    private readonly byte[]? _tokenHash;
    private readonly ILogger _logger;

    public HttpAccessGuard(string? token, ILogger logger)
    {
        _tokenHash = token is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Headers.ContainsKey(HeaderNames.Origin))
        {
            _logger.LogWarning(
                "Rejected an HTTP request from {RemoteAddress} with Origin {Origin}: browser requests are not accepted.",
                context.Connection.RemoteIpAddress,
                context.Request.Headers.Origin.ToString());
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Requests from web pages are not accepted.");
            return;
        }

        if (_tokenHash is not null && !HasValidToken(context.Request))
        {
            _logger.LogWarning(
                "Rejected an HTTP request from {RemoteAddress}: missing or wrong access token.",
                context.Connection.RemoteIpAddress);
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "A valid bearer token is required.");
            return;
        }

        await next(context);
    }

    private bool HasValidToken(HttpRequest request)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Compare fixed-length hashes in constant time, so that neither the
        // content nor the length of the token leaks through timing.
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header[scheme.Length..].Trim()));
        return CryptographicOperations.FixedTimeEquals(presented, _tokenHash);
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}
