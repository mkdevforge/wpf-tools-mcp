using System.Net;
using WpfToolsMcp.Automation;

namespace WpfToolsMcp.McpServer;

internal enum ServerTransport
{
    Stdio,
    Http
}

/// <summary>Command-line and environment settings of the server process.</summary>
internal sealed record ServerOptions
{
    public const string DefaultHttpUrl = "http://127.0.0.1:5765/mcp";
    public const int MinimumTokenLength = 32;

    public ToolProfile Profile { get; init; } = ToolProfile.Core;
    public ServerTransport Transport { get; init; } = ServerTransport.Stdio;
    public Uri HttpUrl { get; init; } = new(DefaultHttpUrl);
    public string? HttpToken { get; init; }
    public string? ArtifactDirectory { get; init; }
    public IReadOnlyList<PathMapping> PathMappings { get; init; } = [];
    public string[] HostArgs { get; init; } = [];

    /// <summary>True when the HTTP endpoint accepts connections from other machines.</summary>
    public bool HttpListensBeyondLoopback => !IsLoopback(HttpUrl);

    public static ServerOptions Parse(string[] args, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var remaining = new List<string>(args.Length);
        string? profile = null;
        string? transport = null;
        string? httpUrl = null;
        string? tokenFile = null;
        string? artifactDirectory = null;
        var pathMaps = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (TryReadValue(args, ref i, "--tool-profile", out var value))
            {
                profile = value;
            }
            else if (TryReadValue(args, ref i, "--transport", out value))
            {
                transport = value;
            }
            else if (TryReadValue(args, ref i, "--http-url", out value))
            {
                httpUrl = value;
            }
            else if (TryReadValue(args, ref i, "--http-token-file", out value))
            {
                tokenFile = value;
            }
            else if (TryReadValue(args, ref i, "--artifact-dir", out value))
            {
                artifactDirectory = value;
            }
            else if (TryReadValue(args, ref i, "--path-map", out value))
            {
                pathMaps.Add(value);
            }
            else
            {
                remaining.Add(arg);
            }
        }

        if (pathMaps.Count == 0 && getEnvironmentVariable("WPF_TOOLS_MCP_PATH_MAP") is { } pathMapVariable)
        {
            // '|' cannot occur in Windows paths, so it separates entries.
            pathMaps.AddRange(pathMapVariable.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var options = new ServerOptions
        {
            Profile = ToolProfileOptions.ParseProfile(profile ?? getEnvironmentVariable("WPF_TOOLS_MCP_TOOL_PROFILE")),
            Transport = ParseTransport(transport ?? getEnvironmentVariable("WPF_TOOLS_MCP_TRANSPORT")),
            HttpUrl = ParseHttpUrl(httpUrl ?? getEnvironmentVariable("WPF_TOOLS_MCP_HTTP_URL") ?? DefaultHttpUrl),
            ArtifactDirectory = string.IsNullOrWhiteSpace(artifactDirectory) ? null : artifactDirectory,
            PathMappings = pathMaps.Select(ClientPathMap.ParseMapping).ToArray(),
            HostArgs = remaining.ToArray()
        };

        if (options.Transport != ServerTransport.Http)
        {
            if (tokenFile is not null || httpUrl is not null)
            {
                throw new ArgumentException("--http-url and --http-token-file require --transport http.");
            }

            return options;
        }

        var token = tokenFile is not null
            ? ReadTokenFile(tokenFile)
            : getEnvironmentVariable("WPF_TOOLS_MCP_HTTP_TOKEN")?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            token = null;
        }

        if (token is not null && token.Length < MinimumTokenLength)
        {
            throw new ArgumentException(
                $"The HTTP access token must have at least {MinimumTokenLength} characters.");
        }

        if (token is null && !IsLoopback(options.HttpUrl))
        {
            throw new ArgumentException(
                $"--http-url {options.HttpUrl} accepts connections from other machines, so it requires an access token. " +
                "Use --http-token-file <path> or set WPF_TOOLS_MCP_HTTP_TOKEN.");
        }

        return options with { HttpToken = token };
    }

    private static bool TryReadValue(string[] args, ref int index, string name, out string value)
    {
        var arg = args[index];
        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(name.Length + 1)..];
            return true;
        }

        if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{name} requires a value.");
            }

            value = args[++index];
            return true;
        }

        value = "";
        return false;
    }

    private static ServerTransport ParseTransport(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("stdio", StringComparison.OrdinalIgnoreCase))
        {
            return ServerTransport.Stdio;
        }

        if (value.Trim().Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return ServerTransport.Http;
        }

        throw new ArgumentException($"Unknown transport '{value}'. Valid values: stdio, http.");
    }

    private static Uri ParseHttpUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) ||
            !string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"HTTP URL '{value}' is not valid. Use http://<address>:<port>/<path>, for example {DefaultHttpUrl}. " +
                "HTTPS is not supported; use an SSH tunnel for connections over untrusted networks.");
        }

        if (!string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            throw new ArgumentException($"HTTP URL '{value}' cannot have a query or fragment.");
        }

        if (url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) &&
            !url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"HTTP URL '{value}' must use an IP address or localhost, for example 0.0.0.0 to listen on all addresses.");
        }

        return url;
    }

    private static bool IsLoopback(Uri url) =>
        url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(url.Host, out var address) && IPAddress.IsLoopback(address));

    private static string ReadTokenFile(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Cannot read the HTTP token file '{path}': {ex.Message}", ex);
        }
    }
}
