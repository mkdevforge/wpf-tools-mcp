using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class HttpTransportTests
{
    private const string Token = "http-transport-test-token-0123456789abcdef";

    [Test]
    public async Task Http_transport_serves_tools_to_a_client_with_the_token()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var server = await HttpServerProcess.StartAsync(Token, cts.Token);

        await using var mcp = await McpTestContext.ConnectHttpAsync(server.Endpoint, Token, cts.Token);
        var tools = await mcp.ListToolsAsync(cts.Token);
        Assert.That(tools.Select(tool => tool.Name), Does.Contain("get_server_environment"));

        var environment = await mcp.CallToolAsync<GetServerEnvironmentResponse>(
            "get_server_environment",
            cancellationToken: cts.Token);
        Assert.That(environment.Server.ProcessId, Is.EqualTo(server.ProcessId));
    }

    [Test]
    public async Task Http_transport_rejects_requests_without_the_token_or_from_web_pages()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var server = await HttpServerProcess.StartAsync(Token, cts.Token);
        using var http = new HttpClient();

        using var noToken = await http.SendAsync(CreateInitializeRequest(server.Endpoint, token: null), cts.Token);
        using var wrongToken = await http.SendAsync(
            CreateInitializeRequest(server.Endpoint, token: Token + "x"),
            cts.Token);
        var fromBrowser = CreateInitializeRequest(server.Endpoint, token: Token);
        fromBrowser.Headers.Add("Origin", "http://evil.example");
        using var browserResponse = await http.SendAsync(fromBrowser, cts.Token);
        using var valid = await http.SendAsync(CreateInitializeRequest(server.Endpoint, token: Token), cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(noToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(noToken.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
            Assert.That(wrongToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(browserResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task Http_transport_refuses_to_listen_beyond_loopback_without_a_token()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        using var process = Process.Start(new ProcessStartInfo(serverExe)
        {
            ArgumentList = { "--transport", "http", "--http-url", $"http://0.0.0.0:{GetFreePort()}/mcp" },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var stderr = await process.StandardError.ReadToEndAsync();
        Assert.That(process.WaitForExit(30_000), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.EqualTo(2));
            Assert.That(stderr, Does.Contain("requires an access token"));
        });
    }

    private static HttpRequestMessage CreateInitializeRequest(Uri endpoint, string? token)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "http-test", ["version"] = "0" }
            }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    internal static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A server process that listens on a free loopback port.</summary>
    internal sealed class HttpServerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _stderr;

        private HttpServerProcess(Process process, Uri endpoint, ConcurrentQueue<string> stderr)
        {
            _process = process;
            Endpoint = endpoint;
            _stderr = stderr;
        }

        public Uri Endpoint { get; }

        public int ProcessId => _process.Id;

        public static async Task<HttpServerProcess> StartAsync(
            string token,
            CancellationToken cancellationToken,
            IEnumerable<string>? extraArguments = null,
            string toolProfile = "diagnostics")
        {
            var endpoint = new Uri($"http://127.0.0.1:{GetFreePort()}/mcp");
            var startInfo = new ProcessStartInfo(McpServerPaths.FindMcpServerExecutable())
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "--transport", "http", "--http-url", endpoint.ToString(), "--tool-profile", toolProfile }
                         .Concat(extraArguments ?? []))
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["WPF_TOOLS_MCP_HTTP_TOKEN"] = token;

            var stderr = new ConcurrentQueue<string>();
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process { StartInfo = startInfo };
            DataReceivedEventHandler onLine = (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                stderr.Enqueue(e.Data);
                if (e.Data.Contains("Listening for MCP clients", StringComparison.Ordinal))
                {
                    listening.TrySetResult();
                }
            };
            process.ErrorDataReceived += onLine;
            process.OutputDataReceived += onLine;
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var server = new HttpServerProcess(process, endpoint, stderr);
            try
            {
                await listening.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                await WaitForPortAsync(endpoint, cancellationToken);
            }
            catch (Exception ex)
            {
                await server.DisposeAsync();
                throw new InvalidOperationException(
                    $"The HTTP server did not start: {ex.Message}{Environment.NewLine}{string.Join(Environment.NewLine, stderr)}",
                    ex);
            }

            return server;
        }

        private static async Task WaitForPortAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
                    return;
                }
                catch (SocketException) when (attempt < 50)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(10_000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
