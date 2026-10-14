using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Glacier.Showcase.Services.GlacierStudio;

/// <summary>
/// Production JSON-RPC 2.0 client communicating with an external Model Context Protocol (MCP) server
/// over standard input/output (stdio) streaming.
/// </summary>
public class GlacierMcpClient : IAsyncDisposable, IDisposable
{
    private readonly string _serverName;
    private readonly string _command;
    private readonly List<string> _args;
    private readonly Dictionary<string, string> _env;
    private readonly string _workingDirectory;

    private Process? _process;
    private Task? _stdoutReaderTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly StringBuilder _stderrBuffer = new();
    private readonly object _stderrLock = new();

    private long _nextRequestId = 0;
    private bool _isInitialized = false;
    private bool _isDisposed = false;

    public string ServerName => _serverName;
    public bool IsRunning
    {
        get
        {
            if (_isDisposed) return false;
            return _process != null && !_process.HasExited;
        }
    }
    public bool IsInitialized => _isInitialized;

    public GlacierMcpClient(
        string serverName,
        string command,
        IEnumerable<string>? args = null,
        IDictionary<string, string>? env = null,
        string? workingDirectory = null)
    {
        _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _args = args != null ? new List<string>(args) : new List<string>();
        _env = env != null ? new Dictionary<string, string>(env) : new Dictionary<string, string>();
        _workingDirectory = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : GlacierWorkspaceResolver.ResolveWorkspaceRoot();
    }

    /// <summary>
    /// Spawns the child process and starts reading stdout/stderr streams.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _command,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        foreach (var arg in _args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in _env)
        {
            psi.EnvironmentVariables[key] = value;
        }

        _process = new Process { StartInfo = psi };

        _process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                lock (_stderrLock)
                {
                    if (_stderrBuffer.Length > 8192)
                    {
                        _stderrBuffer.Remove(0, 2048);
                    }
                    _stderrBuffer.AppendLine(e.Data);
                }
            }
        };

        try
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException($"Failed to launch MCP child process for '{_serverName}'.");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to spawn MCP server '{_serverName}' with command '{_command}': {ex.Message}", ex);
        }

        _process.BeginErrorReadLine();
        _stdoutReaderTask = Task.Run(() => ReadStdoutLoopAsync(_cts.Token));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Background reader loop consuming newline-delimited JSON-RPC 2.0 messages from stdout.
    /// </summary>
    private async Task ReadStdoutLoopAsync(CancellationToken ct)
    {
        var stdout = _process?.StandardOutput;
        if (stdout == null) return;

        try
        {
            while (!ct.IsCancellationRequested && _process != null && !_process.HasExited)
            {
                string? line = await stdout.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("id", out var idElem) && idElem.TryGetInt64(out long id))
                    {
                        if (_pendingRequests.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(root.Clone());
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-JSON lines or diagnostic banners that some servers write to stdout
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal
        }
        catch (Exception ex)
        {
            FailAllPendingRequests(new IOException($"MCP server '{_serverName}' stdout reader error: {ex.Message}", ex));
        }
        finally
        {
            string lastStderr = GetRecentStderr();
            FailAllPendingRequests(new IOException($"MCP server '{_serverName}' process terminated or closed stdout. Recent stderr:\n{lastStderr}"));
        }
    }

    private void FailAllPendingRequests(Exception ex)
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// Sends a JSON-RPC 2.0 request and awaits the correlated response.
    /// </summary>
    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!IsRunning)
        {
            throw new InvalidOperationException($"MCP server '{_serverName}' is not running.");
        }

        long id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        var requestObj = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };

        if (parameters != null)
        {
            requestObj["params"] = parameters;
        }

        string json = JsonSerializer.Serialize(requestObj);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(json + "\n");

        await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process?.StandardInput == null)
            {
                throw new InvalidOperationException($"StandardInput is not available for '{_serverName}'.");
            }

            await _process.StandardInput.BaseStream.WriteAsync(payloadBytes, 0, payloadBytes.Length, ct).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _stdinLock.Release();
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var registration = linkedCts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(id, out var removedTcs))
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    removedTcs.TrySetException(new TimeoutException($"Request '{method}' (id: {id}) to MCP server '{_serverName}' timed out after 45s. Stderr: {GetRecentStderr()}"));
                }
                else
                {
                    removedTcs.TrySetCanceled(ct);
                }
            }
        });

        JsonElement response;
        try
        {
            response = await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(id, out _);
            throw;
        }

        if (response.TryGetProperty("error", out var errorProp))
        {
            int code = errorProp.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : -32000;
            string message = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Unknown error" : errorProp.ToString();
            throw new McpRpcException(code, message);
        }

        if (response.TryGetProperty("result", out var resultProp))
        {
            return resultProp.Clone();
        }

        return response;
    }

    /// <summary>
    /// Sends a JSON-RPC 2.0 notification without expecting a response.
    /// </summary>
    public async Task SendNotificationAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!IsRunning)
        {
            throw new InvalidOperationException($"MCP server '{_serverName}' is not running.");
        }

        var notificationObj = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };

        if (parameters != null)
        {
            notificationObj["params"] = parameters;
        }

        string json = JsonSerializer.Serialize(notificationObj);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(json + "\n");

        await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process?.StandardInput == null)
            {
                throw new InvalidOperationException($"StandardInput is not available for '{_serverName}'.");
            }

            await _process.StandardInput.BaseStream.WriteAsync(payloadBytes, 0, payloadBytes.Length, ct).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    /// <summary>
    /// Executes the MCP handshake: `initialize` (protocolVersion 2024-11-05) followed by `notifications/initialized`.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_isInitialized) return;

        if (!IsRunning)
        {
            await StartAsync(ct).ConfigureAwait(false);
        }

        var initParams = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { listChanged = false }
            },
            clientInfo = new
            {
                name = "Glacier.Showcase",
                version = "1.0.0"
            }
        };

        await SendRequestAsync("initialize", initParams, ct).ConfigureAwait(false);
        await SendNotificationAsync("notifications/initialized", new { }, ct).ConfigureAwait(false);

        _isInitialized = true;
    }

    /// <summary>
    /// Discovers all available tools exposed by the MCP server over stdio.
    /// </summary>
    public async Task<List<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
    {
        if (!_isInitialized)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
        }

        var result = await SendRequestAsync("tools/list", new { }, ct).ConfigureAwait(false);
        var tools = new List<McpToolDefinition>();

        if (result.TryGetProperty("tools", out var toolsArray) && toolsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in toolsArray.EnumerateArray())
            {
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                JsonElement schema = item.TryGetProperty("inputSchema", out var s) ? s.Clone() : default;

                if (!string.IsNullOrEmpty(name))
                {
                    tools.Add(new McpToolDefinition
                    {
                        Name = name,
                        Description = description,
                        InputSchema = schema
                    });
                }
            }
        }

        return tools;
    }

    /// <summary>
    /// Calls a specific tool on the MCP server with JSON arguments and extracts the content.
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (!_isInitialized)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
        }

        JsonElement parsedArguments;
        try
        {
            using var argDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            parsedArguments = argDoc.RootElement.Clone();
        }
        catch (JsonException)
        {
            parsedArguments = JsonDocument.Parse("{}").RootElement.Clone();
        }

        var callParams = new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["arguments"] = parsedArguments
        };

        var result = await SendRequestAsync("tools/call", callParams, ct).ConfigureAwait(false);

        // Standard MCP tool return format: { content: [ { type: "text", text: "..." } ], isError?: false }
        if (result.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in contentElem.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textElem) && textElem.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(textElem.GetString());
                }
                else
                {
                    sb.AppendLine(item.ToString());
                }
            }
            return sb.ToString().TrimEnd();
        }

        return result.ToString();
    }

    public string GetRecentStderr()
    {
        lock (_stderrLock)
        {
            return _stderrBuffer.ToString();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _cts.Cancel();
        }
        catch { }

        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1000);
            }
        }
        catch { }

        _process?.Dispose();
        _stdinLock.Dispose();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }
}
