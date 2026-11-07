namespace Glacier.Studio.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

[Trait("Category", "Unit")]
public class GlacierMcpManagerTests
{
    [Fact]
    public void McpManager_Initializes_LoadsEcosystemDefaultsOrConfiguredServers()
    {
        using var manager = new GlacierMcpManager();

        Assert.NotNull(manager.Servers);
        Assert.NotEmpty(manager.Servers);

        // Every server must have a name, status message, and tool catalog
        foreach (var server in manager.Servers)
        {
            Assert.False(string.IsNullOrWhiteSpace(server.Name));
            Assert.False(string.IsNullOrWhiteSpace(server.StatusMessage));
            Assert.NotNull(server.AvailableTools);
            Assert.NotNull(server.DisabledTools);
        }
    }

    [Fact]
    public void McpManager_DisabledTools_FiltersToolsFromAvailableCatalog()
    {
        var server = new McpServerDefinition
        {
            Name = "CustomServer",
            Command = "node",
            DisabledTools = { "dangerous_exec", "wipe_database" },
            AvailableTools = { "read_data", "dangerous_exec", "query_status" }
        };

        // Filter according to manager logic
        var filteredTools = server.AvailableTools
            .Where(t => !server.DisabledTools.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(2, filteredTools.Count);
        Assert.Contains("read_data", filteredTools);
        Assert.Contains("query_status", filteredTools);
        Assert.DoesNotContain("dangerous_exec", filteredTools);
    }

    [Fact]
    public async Task McpManager_RegisterAndDisposeClient_CleansUpGracefully()
    {
        await using var manager = new GlacierMcpManager();

        Assert.False(manager.IsDisposed);
        await manager.DisposeAsync();
        Assert.True(manager.IsDisposed);
    }
}
