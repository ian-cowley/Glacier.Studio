namespace Glacier.Studio.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Glacier.Studio.Components;
using Glacier.Showcase.Services.GlacierStudio;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public class GlacierStudioUiComponentTests : IDisposable
{
    private readonly string _tempDir;

    public GlacierStudioUiComponentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glacier_bunit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
        {
            _handler = handler ?? DefaultHandler;
        }

        private static HttpResponseMessage DefaultHandler(HttpRequestMessage req)
        {
            string path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/v1/sleep/status", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"enabled":true,"state":"Idle","completed_cycles":2,"total_dreams_consolidated":8,"pending_dreams":1}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            if (path.EndsWith("/v1/sleep/trigger", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"triggered":true,"state":"Idle","expert_slot_index":0,"total_dreams":1,"accepted_dreams":1,"acceptance_rate":1.0,"initial_loss":0.5,"final_loss":0.2,"synaptic_pruning_succeeded":true}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            if (path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"test-model"}]}""", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private (TestContext Ctx, GlacierStudioState State, GlacierRuntimeLauncherService Launcher) CreateStudioTestContext(
        Func<HttpRequestMessage, HttpResponseMessage>? httpHandler = null)
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var httpClient = new HttpClient(new StubHttpMessageHandler(httpHandler));
        var console = new GlacierConsoleService(_tempDir);
        var mcp = new GlacierMcpManager();
        var state = new GlacierStudioState(_tempDir);
        var docPreview = new GlacierDocTreePreviewService();
        var graph = new GlacierWorkspaceGraphService();
        var launcher = new GlacierRuntimeLauncherService(
            state,
            console,
            workspaceRoot: _tempDir,
            probeClient: httpClient,
            startBackgroundProber: false,
            autoDiscover: false,
            autoProbe: false);
        var engine = new GlacierAgentEngine(httpClient, state, console, docPreview, mcp, graph, launcher);

        ctx.Services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(httpClient));
        ctx.Services.AddSingleton(console);
        ctx.Services.AddSingleton(mcp);
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(docPreview);
        ctx.Services.AddSingleton(graph);
        ctx.Services.AddSingleton(launcher);
        ctx.Services.AddSingleton(engine);

        return (ctx, state, launcher);
    }

    [Fact]
    public void Sidebar_NewThread_Search_Select_And_Delete_Work()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        Assert.Single(state.Sessions);

        cut.Find("button.gs-new-chat-btn").Click();
        Assert.Equal(2, state.Sessions.Count);

        state.Sessions[0].Title = "Alpha Thread";
        state.Sessions[1].Title = "Beta Thread";

        var searchInput = cut.Find("input.gs-session-search-input");
        searchInput.Input("Beta");
        Assert.Equal("Beta", state.SessionSearchQuery);

        cut.Find("button.gs-search-clear-btn").Click();
        Assert.Equal("", state.SessionSearchQuery);

        var deleteButtons = cut.FindAll("button.gs-session-delete");
        Assert.True(deleteButtons.Count >= 2);
        deleteButtons[0].Click();
        Assert.Single(state.Sessions);
    }

    [Fact]
    public void Header_And_Composer_ModelSelect_Export_InspectorToggle_And_SlashCommands_Work()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        cut.Find("select.gs-header-model-select").Change(ModelServerProvider.LocalOllama.ToString());
        Assert.Equal(ModelServerProvider.LocalOllama, state.Provider);

        cut.Find("select.gs-composer-model-select").Change(ModelServerProvider.RemoteGemini.ToString());
        Assert.Equal(ModelServerProvider.RemoteGemini, state.Provider);

        var mdBtn = cut.FindAll("button.gs-btn-export").First(b => b.TextContent.Contains("MD", StringComparison.OrdinalIgnoreCase));
        mdBtn.Click();
        Assert.Contains(".md", cut.Markup, StringComparison.OrdinalIgnoreCase);

        var bannerClose = cut.Find("button.gs-banner-close");
        bannerClose.Click();

        // Empty send input validation: Send button should not execute an agent turn
        var sendBtn = cut.Find("button.gs-btn-send");
        sendBtn.Click();
        Assert.Equal(AgentExecutionStatus.Idle, state.ExecutionStatus);

        // Typing '/' opens slash command menu
        var textarea = cut.Find("textarea.gs-textarea");
        textarea.Input("/");
        var slashMenu = cut.Find("#slashMenuPopup");
        Assert.NotNull(slashMenu);
    }

    [Fact]
    public void RightDrawer_AllFourTabs_Doc_Console_Graph_Telemetry_Render_And_Switch()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        var tabs = cut.FindAll("button.gs-aux-tab");
        Assert.Equal(4, tabs.Count);

        tabs[0].Click();
        Assert.Equal("doc", state.ActiveRightTab);

        tabs[1].Click();
        Assert.Equal("console", state.ActiveRightTab);
        Assert.Contains("git status", cut.Markup);

        tabs[2].Click();
        Assert.Equal("graph", state.ActiveRightTab);

        tabs[3].Click();
        Assert.Equal("telemetry", state.ActiveRightTab);
        Assert.Contains("Local Inference Runtimes", cut.Markup);
    }

    [Fact]
    public void TelemetryTab_SleepMemory_Checkbox_Checkpoint_Refresh_And_Trigger_Work()
    {
        bool sleepEndpointEnabled = false;
        var (ctx, state, launcher) = CreateStudioTestContext(req =>
        {
            string path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/v1/sleep/status", StringComparison.OrdinalIgnoreCase))
            {
                string json = sleepEndpointEnabled
                    ? """{"enabled":true,"state":"Idle","completed_cycles":2,"total_dreams_consolidated":8,"pending_dreams":1}"""
                    : """{"enabled":false,"state":"Disabled","completed_cycles":0,"total_dreams_consolidated":0,"pending_dreams":0}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
            if (path.EndsWith("/v1/sleep/trigger", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"triggered":true,"state":"Idle","expert_slot_index":0,"total_dreams":1,"accepted_dreams":1,"acceptance_rate":1.0,"initial_loss":0.5,"final_loss":0.2,"synaptic_pruning_succeeded":true}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var bunit = ctx;
        state.ActiveRightTab = "telemetry";
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        // Initially SleepEnabled is false, so Trigger Sleep is disabled
        var triggerBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Trigger Sleep"));
        Assert.True(triggerBtn.HasAttribute("disabled"));

        // Toggle --sleep-memory checkbox
        var sleepCheckbox = cut.FindAll("input[type='checkbox']")
            .First(i => i.ParentElement?.TextContent.Contains("--sleep-memory") == true);
        sleepCheckbox.Change(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        Assert.True(launcher.InferenceEnableSleepMemory);

        // --sleep-checkpoint input should now be visible
        var ckptInput = cut.Find("input[placeholder*='--sleep-checkpoint']");
        ckptInput.Change(@"C:\temp\custom_sleep.gmoe");
        Assert.Equal(@"C:\temp\custom_sleep.gmoe", launcher.InferenceSleepCheckpointPath);

        // Enable sleep on server and click Refresh in Sleep Memory card
        sleepEndpointEnabled = true;
        var refreshBtn = cut.FindAll("button").First(b =>
            b.TextContent.Trim() == "Refresh" &&
            b.ParentElement?.TextContent.Contains("Trigger Sleep") == true);
        refreshBtn.Click();

        cut.WaitForAssertion(() => Assert.True(launcher.SleepEnabled));
        Assert.Equal(2, launcher.SleepCompletedCycles);

        // Trigger Sleep should now be enabled and callable
        triggerBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Trigger Sleep"));
        Assert.False(triggerBtn.HasAttribute("disabled"));
        triggerBtn.Click();
    }

    [Fact]
    public void TelemetryTab_DistributedCoordinatorAndWorkerRoles_And_NodeValidation_Work()
    {
        var (ctx, state, launcher) = CreateStudioTestContext();
        using var bunit = ctx;
        state.ActiveRightTab = "telemetry";
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        Assert.Equal("coordinator", launcher.DistributedRole);
        Assert.Contains("Worker Stage Nodes", cut.Markup);

        // Empty node input validation
        int initialCount = launcher.GetConfiguredNodes().Count;
        var addNodeBtn = cut.FindAll("button").First(b => b.TextContent.Contains("+ Node", StringComparison.OrdinalIgnoreCase));
        addNodeBtn.Click();
        Assert.Equal(initialCount, launcher.GetConfiguredNodes().Count);

        // Valid node addition & removal
        var nodeInput = cut.Find("input[placeholder*='Add host:port']");
        nodeInput.Change("127.0.0.1:5099");
        addNodeBtn.Click();
        Assert.Equal(initialCount + 1, launcher.GetConfiguredNodes().Count);
        // Switch role to worker and back
        var roleSelect = cut.FindAll("select.gs-select").First(s => s.InnerHtml.Contains("Coordinator (--role coordinator"));
        roleSelect.Change("worker");
        Assert.Equal("worker", launcher.DistributedRole);
        Assert.DoesNotContain("Worker Stage Nodes", cut.Markup);

        roleSelect = cut.FindAll("select.gs-select").First(s => s.InnerHtml.Contains("Coordinator (--role coordinator"));
        roleSelect.Change("coordinator");
        Assert.Equal("coordinator", launcher.DistributedRole);
        Assert.Equal(initialCount + 1, launcher.GetConfiguredNodes().Count);

        cut.FindAll("button.gs-runtime-node-remove").Last().Click();
        Assert.Equal(initialCount, launcher.GetConfiguredNodes().Count);
    }

    [Fact]
    public void SettingsModal_AllFourTabs_Fields_EndpointValidation_And_Close_Work()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        cut.Find("button.gs-settings-trigger-btn").Click();
        Assert.Contains("Settings", cut.Markup);

        // 1. Models tab
        var navButtons = cut.FindAll("button.gs-modal-nav-item");
        Assert.Equal(4, navButtons.Count);

        var endpointInput = cut.Find("input[placeholder='http://127.0.0.1:5055/v1']");
        endpointInput.Change("http://127.0.0.1:9999/v1");
        Assert.Equal("http://127.0.0.1:9999/v1", state.EndpointUrl);

        var modelInput = cut.Find("input[placeholder='e.g. qwen3-4b']");
        modelInput.Change("qwen3-8b");
        Assert.Equal("qwen3-8b", state.SelectedModel);

        // Input validation: Test Connection with invalid/empty URL
        endpointInput.Change("not-a-valid-url");
        var testConnBtn = cut.Find("button.gs-btn-test-conn");
        testConnBtn.Click();
        Assert.Contains("Invalid", cut.Markup, StringComparison.OrdinalIgnoreCase);

        // 2. MCP tab
        cut.FindAll("button.gs-modal-nav-item")[1].Click();
        Assert.Contains("Rescan", cut.Markup);

        // 3. Engine tab
        cut.FindAll("button.gs-modal-nav-item")[2].Click();
        var agentToggle = cut.FindAll("input[type='checkbox']").Last();
        bool beforeAgent = state.IsAgentMode;
        agentToggle.Change(!beforeAgent);
        Assert.Equal(!beforeAgent, state.IsAgentMode);

        // 4. About tab
        cut.FindAll("button.gs-modal-nav-item")[3].Click();
        Assert.Contains("Glacier", cut.Markup);

        // Close modal
        cut.Find("button.gs-modal-close-btn").Click();
        Assert.Empty(cut.FindAll("div.gs-modal-card"));
    }
}
