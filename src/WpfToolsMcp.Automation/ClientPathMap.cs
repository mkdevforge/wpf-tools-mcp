namespace WpfToolsMcp.Automation;

/// <summary>
/// One folder that the server and the MCP client see under different paths, for
/// example a host folder shared into a virtual machine.
/// </summary>
public sealed record PathMapping(string ServerPath, string ClientPath);

/// <summary>
/// Translates file paths between the server machine and the MCP client.
/// </summary>
/// <remarks>
/// The server maps every path that it publishes (tool responses and files that it
/// writes for the client, such as sequence manifests) with <see cref="ToClient"/>,
/// and every file path that a client sends with <see cref="ToServer"/>. Paths
/// outside the mapped folders stay unchanged: they are server paths, and the
/// client can open them only if it runs on the same machine.
/// </remarks>
public sealed class ClientPathMap
{
    private static ClientPathMap s_current = new([]);

    private readonly PathMapping[] _byServerPath;
    private readonly PathMapping[] _byClientPath;

    public ClientPathMap(IEnumerable<PathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var normalized = mappings
            .Select(mapping => new PathMapping(
                NormalizeRoot(mapping.ServerPath, "server"),
                NormalizeRoot(mapping.ClientPath, "client")))
            .ToArray();

        // Longest prefix first, so that nested folders win over their parents.
        _byServerPath = normalized.OrderByDescending(mapping => mapping.ServerPath.Length).ToArray();
        _byClientPath = normalized.OrderByDescending(mapping => mapping.ClientPath.Length).ToArray();
        Mappings = normalized;
    }

    /// <summary>The map that the server uses. Configured once at startup.</summary>
    public static ClientPathMap Current
    {
        get => Volatile.Read(ref s_current);
        set => Volatile.Write(ref s_current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public IReadOnlyList<PathMapping> Mappings { get; }

    /// <summary>
    /// Parses "serverPath=clientPath". Paths that contain '=' are not supported.
    /// </summary>
    public static PathMapping ParseMapping(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split('=');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new ArgumentException(
                $"Path mapping '{value}' is not valid. Use serverPath=clientPath, for example " +
                "\"\\\\vmware-host\\Shared Folders\\wpf-qa=D:\\wpf-qa\\shared\".");
        }

        return new PathMapping(parts[0].Trim(), parts[1].Trim());
    }

    /// <summary>Returns the path under which the client sees a server path.</summary>
    public string ToClient(string serverPath) => Translate(serverPath, _byServerPath, toClient: true);

    /// <summary>Returns the server path of a path that the client sent.</summary>
    public string ToServer(string clientPath) => Translate(clientPath, _byClientPath, toClient: false);

    public string? ToClientOrNull(string? serverPath) => serverPath is null ? null : ToClient(serverPath);

    public string? ToServerOrNull(string? clientPath) =>
        string.IsNullOrWhiteSpace(clientPath) ? clientPath : ToServer(clientPath);

    private static string Translate(string path, PathMapping[] mappings, bool toClient)
    {
        if (mappings.Length == 0 || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var candidate = path.Trim().Replace('/', '\\');
        foreach (var mapping in mappings)
        {
            var from = toClient ? mapping.ServerPath : mapping.ClientPath;
            var to = toClient ? mapping.ClientPath : mapping.ServerPath;
            if (!candidate.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.Length == from.Length || (candidate.Length == from.Length + 1 && candidate[^1] == '\\'))
            {
                return to.EndsWith(':') ? to + '\\' : to;
            }

            // Match whole folder names only: C:\share must not map C:\shared.
            if (candidate[from.Length] == '\\')
            {
                return to + candidate[from.Length..];
            }
        }

        return path;
    }

    private static string NormalizeRoot(string path, string side)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"The {side} path of a path mapping is empty.");
        }

        var normalized = path.Trim().Replace('/', '\\');
        if (!Path.IsPathFullyQualified(normalized))
        {
            throw new ArgumentException(
                $"The {side} path '{path}' of a path mapping must be absolute, for example D:\\shared or \\\\server\\share.");
        }

        // Store folders without a trailing separator; a drive root becomes "Z:".
        var trimmed = normalized.TrimEnd('\\');
        if (trimmed.Length < 2)
        {
            throw new ArgumentException($"The {side} path '{path}' of a path mapping is not a folder.");
        }

        return trimmed;
    }
}
