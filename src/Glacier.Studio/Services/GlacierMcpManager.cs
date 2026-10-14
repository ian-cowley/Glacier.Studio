using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Glacier.Showcase.Services.GlacierStudio;

/// <summary>
/// Production Model Context Protocol (MCP) server manager.
/// Manages configured MCP servers from mcp_config.json, pools live child process clients,
/// dynamically discovers tools over stdio JSON-RPC 2.0, and executes tool calls against real processes.
/// </summary>
public class GlacierMcpManager : IAsyncDisposable, IDisposable
{
    private readonly List<McpServerDefinition> _servers = new();
    private readonly ConcurrentDictionary<string, GlacierMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private readonly string _workspaceRoot;
    private bool _isDisposed = false;

    public IReadOnlyList<McpServerDefinition> Servers => _servers;

    public GlacierMcpManager(string? workspaceRoot = null)
    {
        _workspaceRoot = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : GlacierWorkspaceResolver.ResolveWorkspaceRoot();

        DiscoverConfiguredServers();
    }

    public void DiscoverConfiguredServers()
    {
        _servers.Clear();

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string mcpConfigPath = Path.Combine(userProfile, ".gemini", "config", "mcp_config.json");

        if (File.Exists(mcpConfigPath))
        {
            try
            {
                string json = File.ReadAllText(mcpConfigPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("mcpServers", out var serversElement))
                {
                    foreach (var prop in serversElement.EnumerateObject())
                    {
                        var serverDef = new McpServerDefinition
                        {
                            Name = prop.Name
                        };

                        if (prop.Value.TryGetProperty("command", out var cmd))
                        {
                            serverDef.Command = cmd.GetString() ?? "";
                        }
                        if (prop.Value.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in args.EnumerateArray())
                            {
                                serverDef.Args.Add(a.GetString() ?? "");
                            }
                        }
                        if (prop.Value.TryGetProperty("env", out var envElem) && envElem.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var e in envElem.EnumerateObject())
                            {
                                serverDef.Env[e.Name] = e.Value.GetString() ?? "";
                            }
                        }
                        if (prop.Value.TryGetProperty("disabled", out var dis))
                        {
                            serverDef.IsDisabled = dis.GetBoolean();
                        }
                        if (prop.Value.TryGetProperty("disabledTools", out var disTools) && disTools.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var dt in disTools.EnumerateArray())
                            {
                                serverDef.DisabledTools.Add(dt.GetString() ?? "");
                            }
                        }

                        // Populate initial catalog, filtered by disabledTools
                        var initialTools = GetInitialToolsForServer(prop.Name);
                        serverDef.AvailableTools = initialTools
                            .Where(t => !serverDef.DisabledTools.Contains(t, StringComparer.OrdinalIgnoreCase))
                            .ToList();

                        serverDef.IsActive = !serverDef.IsDisabled;
                        serverDef.StatusMessage = serverDef.IsDisabled ? "Disabled in config" : "Available";

                        _servers.Add(serverDef);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GlacierMcpManager] Error reading mcp_config.json: {ex.Message}");
            }
        }

        // If no servers loaded from file, provide ecosystem defaults
        if (_servers.Count == 0)
        {
            _servers.Add(new McpServerDefinition
            {
                Name = "GlacierVectorEngine",
                Command = "dotnet",
                Args = new List<string> { "Glacier.Vector.Host.dll", "1536" },
                IsActive = true,
                AvailableTools = new List<string> { "add_vector", "search_vectors", "ping" },
                StatusMessage = "In-Process Ready"
            });
            _servers.Add(new McpServerDefinition
            {
                Name = "GlacierGraphEngine",
                Command = "dotnet",
                Args = new List<string> { "Glacier.Graph.Host.dll" },
                IsActive = true,
                AvailableTools = new List<string> { "add_node", "add_edge", "find_shortest_path", "find_neighborhood", "ping" },
                StatusMessage = "In-Process Ready"
            });
            _servers.Add(new McpServerDefinition
            {
                Name = "SharpTools",
                Command = "SharpTools.StdioServer.exe",
                IsActive = true,
                AvailableTools = new List<string> { "SharpTool_ViewDefinition", "SharpTool_FindReferences", "SharpTool_AnalyzeComplexity" },
                StatusMessage = "Roslyn Host Ready"
            });
        }
    }

    /// <summary>
    /// Gets or lazily creates and starts an active GlacierMcpClient for the given server.
    /// Performs JSON-RPC handshake and discovers tools dynamically over stdio.
    /// </summary>
    public async Task<GlacierMcpClient> GetOrStartClientAsync(string serverName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var serverDef = _servers.FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (serverDef == null)
        {
            throw new KeyNotFoundException($"MCP server '{serverName}' is not defined in configuration.");
        }

        if (serverDef.IsDisabled)
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is disabled in configuration.");
        }

        if (_clients.TryGetValue(serverName, out var existingClient) && existingClient.IsRunning)
        {
            return existingClient;
        }

        await _poolLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(serverName, out existingClient) && existingClient.IsRunning)
            {
                return existingClient;
            }

            // Clean up exited instance if any
            if (_clients.TryRemove(serverName, out var oldClient))
            {
                oldClient.Dispose();
            }

            var client = new GlacierMcpClient(
                serverDef.Name,
                serverDef.Command,
                serverDef.Args,
                serverDef.Env,
                _workspaceRoot);

            serverDef.StatusMessage = "Starting...";
            await client.StartAsync(ct).ConfigureAwait(false);
            await client.InitializeAsync(ct).ConfigureAwait(false);

            // Dynamically discover tools over stdio
            var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
            var activeToolNames = tools
                .Select(t => t.Name)
                .Where(name => !serverDef.DisabledTools.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (activeToolNames.Count > 0)
            {
                serverDef.AvailableTools = activeToolNames;
            }

            serverDef.IsActive = true;
            serverDef.StatusMessage = $"Running ({serverDef.AvailableTools.Count} tools)";

            _clients[serverName] = client;
            return client;
        }
        catch (Exception ex)
        {
            serverDef.IsActive = false;
            serverDef.StatusMessage = $"Spawn failed: {ex.Message}";
            throw;
        }
        finally
        {
            _poolLock.Release();
        }
    }

    /// <summary>
    /// Dynamically discovers and refreshes tools for a configured server over stdio.
    /// </summary>
    public async Task<List<string>> DiscoverToolsAsync(string serverName, CancellationToken ct = default)
    {
        var client = await GetOrStartClientAsync(serverName, ct).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);

        var serverDef = _servers.FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        var activeToolNames = tools
            .Select(t => t.Name)
            .Where(name => serverDef == null || !serverDef.DisabledTools.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (serverDef != null && activeToolNames.Count > 0)
        {
            serverDef.AvailableTools = activeToolNames;
        }

        return activeToolNames;
    }

    /// <summary>
    /// Executes an MCP tool call against the live server child process via stdio JSON-RPC 2.0.
    /// If the server is disabled or fails to spawn, returns structured diagnostic error JSON.
    /// </summary>
    public async Task<string> ExecuteMcpToolAsync(string serverName, string toolName, string argumentsJson, CancellationToken ct = default)
    {
        var serverDef = _servers.FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (serverDef == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = true,
                server = serverName,
                tool = toolName,
                message = $"MCP server '{serverName}' is not defined in configuration."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        if (serverDef.IsDisabled)
        {
            return JsonSerializer.Serialize(new
            {
                error = true,
                server = serverName,
                tool = toolName,
                message = $"MCP server '{serverName}' is disabled in configuration."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        if (serverDef.DisabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new
            {
                error = true,
                server = serverName,
                tool = toolName,
                message = $"MCP tool '{toolName}' is disabled on server '{serverName}'."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            var client = await GetOrStartClientAsync(serverName, ct).ConfigureAwait(false);
            string result = await client.CallToolAsync(toolName, argumentsJson, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = true,
                server = serverName,
                tool = toolName,
                message = $"Failed to execute MCP tool '{toolName}' on '{serverName}': {ex.Message}"
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static List<string> GetInitialToolsForServer(string serverName) => serverName switch
    {
        "GlacierVectorEngine" => new List<string> { "add_vector", "search_vectors", "ping" },
        "GlacierGraphEngine" => new List<string> { "add_node", "add_edge", "find_shortest_path", "find_neighborhood", "save_graph", "load_graph", "ping" },
        "SharpTools" => new List<string> { "SharpTool_ViewDefinition", "SharpTool_FindReferences", "SharpTool_GetMembers", "SharpTool_AnalyzeComplexity", "SharpTool_ListImplementations", "SharpTool_SearchDefinitions" },
        "MCPSqlServer" => new List<string> { "get_databases", "get_tables", "get_columns", "get_procedures", "execute_database_query" },
        "everything" => new List<string> { "search_files", "get_file_info", "check_status" },
        "sequential-thinking" => new List<string> { "sequentialthinking" },
        "notebooklm" => new List<string> { "ask_question", "list_notebooks", "get_notebook", "search_notebooks" },
        "goog-dev-knowledge" => new List<string> { "search_documents", "answer_query", "get_documents" },
        "fetch" => new List<string> { "fetch" },
        _ => new List<string> { "ping" }
    };

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        _clients.Clear();
        _poolLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        _clients.Clear();
        _poolLock.Dispose();
    }
}
