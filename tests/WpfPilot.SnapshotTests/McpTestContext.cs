using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WpfPilot.SnapshotTests;

internal sealed class McpTestContext : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly McpClient _client;
    private readonly ConcurrentQueue<string> _stderrLines;

    private McpTestContext(McpClient client, ConcurrentQueue<string> stderrLines)
    {
        _client = client;
        _stderrLines = stderrLines;
    }

    public IReadOnlyCollection<string> ServerStderrLines => _stderrLines.ToArray();

    public static async Task<McpTestContext> StartAsync(
        string serverExePath,
        string? toolProfile = "diagnostics",
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExePath);
        if (!File.Exists(serverExePath))
        {
            throw new FileNotFoundException($"MCP server executable not found: '{serverExePath}'.", serverExePath);
        }

        var stderrLines = new ConcurrentQueue<string>();

        var transportOptions = new StdioClientTransportOptions
        {
            Command = serverExePath,
            Name = "WpfPilot.McpServer (tests)",
            Arguments = string.IsNullOrWhiteSpace(toolProfile)
                ? []
                : ["--tool-profile", toolProfile],
            StandardErrorLines = line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    stderrLines.Enqueue(line);
                }
            }
        };

        if (environmentVariables is not null)
        {
            transportOptions.EnvironmentVariables = new Dictionary<string, string?>(environmentVariables, StringComparer.OrdinalIgnoreCase);
        }

        var transport = new StdioClientTransport(transportOptions, NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, cancellationToken);
        return new McpTestContext(client, stderrLines);
    }

    public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        await _client.ListToolsAsync(cancellationToken: cancellationToken);

    public async Task<T> CallToolAsync<T>(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var nonNullArguments = new Dictionary<string, object?>();
        if (arguments is not null)
        {
            foreach (var kvp in arguments)
            {
                if (kvp.Value is not null)
                {
                    nonNullArguments[kvp.Key] = kvp.Value;
                }
            }
        }

        var result = await _client.CallToolAsync(toolName, nonNullArguments, progress: null, options: null, cancellationToken);
        if (result.IsError is true)
        {
            var details = ExtractText(result);
            var stderrTail = GetStderrTail(maxLines: 30);
            if (stderrTail.Count > 0)
            {
                details += $"{Environment.NewLine}--- server stderr (tail) ---{Environment.NewLine}{string.Join(Environment.NewLine, stderrTail)}";
            }

            throw new InvalidOperationException($"Tool '{toolName}' failed: {details}");
        }

        return Deserialize<T>(toolName, ExtractJson(result));
    }

    private static T Deserialize<T>(string toolName, string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new JsonException($"Deserialized '{toolName}' response to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidOperationException($"Failed to deserialize '{toolName}' response JSON.", ex);
        }
    }

    private static string ExtractJson(CallToolResult result)
    {
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock text)
            {
                return text.Text;
            }
        }

        if (result.StructuredContent is not null)
        {
            return result.StructuredContent.ToJsonString();
        }

        throw new InvalidOperationException("Tool returned no text or structured content.");
    }

    private static string ExtractText(CallToolResult result)
    {
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock text)
            {
                return text.Text;
            }
        }

        throw new InvalidOperationException("Tool returned no text content.");
    }

    private IReadOnlyList<string> GetStderrTail(int maxLines)
    {
        if (maxLines <= 0)
        {
            return Array.Empty<string>();
        }

        var lines = _stderrLines.ToArray();
        if (lines.Length == 0)
        {
            return Array.Empty<string>();
        }

        var take = Math.Min(maxLines, lines.Length);
        return lines[^take..];
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}
