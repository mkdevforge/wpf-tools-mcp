using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfPilot.AgentProtocol;

namespace WpfPilot.Automation;

internal sealed class AgentClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan DefaultCallTimeout = ResolveDefaultCallTimeout();

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private AgentClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
    }

    public bool IsConnected => _pipe.IsConnected;

    public static async Task<AgentClient> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(5);
        }

        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await pipe.ConnectAsync(cts.Token);
            return new AgentClient(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public async Task<T> CallAsync<T>(string method, object? @params, CancellationToken cancellationToken)
    {
        var paramsNode = @params is null ? null : JsonSerializer.SerializeToNode(@params, JsonOptions);
        var result = await CallRawAsync(method, paramsNode, cancellationToken);
        var value = result is null ? default : result.Deserialize<T>(JsonOptions);
        if (value is null)
        {
            throw new InvalidOperationException($"Agent call '{method}' returned null.");
        }

        return value;
    }

    public async Task<JsonNode?> CallRawAsync(string method, JsonNode? @params, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (!_pipe.IsConnected)
        {
            throw new InvalidOperationException("Agent pipe is not connected.");
        }

        var request = new AgentRequest(Guid.NewGuid().ToString("N"), method, @params);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultCallTimeout);
        var callToken = cts.Token;

        var lockTaken = false;
        try
        {
            await _mutex.WaitAsync(callToken);
            lockTaken = true;

            await PipeProtocol.WriteAsync(_pipe, request, callToken);
            var response = await PipeProtocol.ReadAsync<AgentResponse>(_pipe, callToken);

            if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Agent protocol error: response ID mismatch.");
            }

            if (!response.Ok)
            {
                var message = response.Error?.Message ?? "Agent call failed.";
                var details = response.Error?.Details;
                if (!string.IsNullOrWhiteSpace(details))
                {
                    message += $"{Environment.NewLine}{details}";
                }

                throw new InvalidOperationException(message);
            }

            return response.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Agent call '{method}' timed out after {DefaultCallTimeout.TotalSeconds:0.###}s. " +
                "Set WPF_PILOT_AGENT_CALL_TIMEOUT_MS to override.");
        }
        finally
        {
            if (lockTaken)
            {
                _mutex.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            _pipe.Dispose();
        }
        finally
        {
            _mutex.Release();
            _mutex.Dispose();
        }
    }

    private static TimeSpan ResolveDefaultCallTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("WPF_PILOT_AGENT_CALL_TIMEOUT_MS");
        if (int.TryParse(raw, out var ms) && ms > 0)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        return TimeSpan.FromSeconds(10);
    }
}
