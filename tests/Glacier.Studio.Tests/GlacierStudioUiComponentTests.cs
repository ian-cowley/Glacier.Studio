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
            if (path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase))
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
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private (TestContext Ctx, GlacierStudioState State, GlacierRuntimeLauncherService Launcher) CreateStudioTestContext(
        Func<HttpRequestMessage, HttpResponseMessage>? httpHandler = null)
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var handler = new StubHttpMessageHandler(httpHandler);
        var httpClient = new HttpClient(handler, disposeHandler: false);
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

        ctx.Services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
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

    [Fact]
    public void Sidebar_SwitchSession_SearchFiltersDom_And_DeleteLastSessionKeepsOneActive()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        state.Sessions[0].Title = "Alpha Unique Thread";
        state.Sessions[0].Messages.Add(new ChatMessage { Role = "user", Content = "Alpha prompt" });
        cut.Find("button.gs-new-chat-btn").Click();
        state.Sessions.First(s => s.Title != "Alpha Unique Thread").Title = "Zebra Unique Thread";
        cut.Find("input.gs-session-search-input").Input("Unique");

        Assert.Contains("Alpha Unique Thread", cut.Markup);
        Assert.Contains("Zebra Unique Thread", cut.Markup);

        // Search hides non-matching session
        cut.Find("input.gs-session-search-input").Input("Zebra");
        Assert.DoesNotContain("Alpha Unique Thread", cut.Markup);
        Assert.Contains("Zebra Unique Thread", cut.Markup);

        // Clear restores both
        cut.Find("button.gs-search-clear-btn").Click();
        Assert.Contains("Alpha Unique Thread", cut.Markup);

        // Clicking first session switches active session
        cut.FindAll("div.gs-session-item")[0].Click();
        Assert.Equal(state.Sessions[0].Id, state.ActiveSession.Id);

        // Deleting down to last session hides delete button & keeps 1 valid active session
        cut.FindAll("button.gs-session-delete")[0].Click();
        Assert.Single(state.Sessions);
        Assert.Empty(cut.FindAll("button.gs-session-delete"));
        Assert.NotNull(state.ActiveSession);
    }

    [Theory]
    [InlineData(ModelServerProvider.LocalGlacierDistributed)]
    [InlineData(ModelServerProvider.ClusterBeastCoder)]
    [InlineData(ModelServerProvider.LocalGlacierInference)]
    [InlineData(ModelServerProvider.LocalOllama)]
    [InlineData(ModelServerProvider.LocalLMStudio)]
    [InlineData(ModelServerProvider.RemoteGemini)]
    [InlineData(ModelServerProvider.RemoteOpenAI)]
    [InlineData(ModelServerProvider.LocalCustom)]
    public void Header_And_Settings_ProviderSelect_SupportsAllProviders(ModelServerProvider provider)
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        cut.Find("select.gs-header-model-select").Change(provider.ToString());
        Assert.Equal(provider, state.Provider);
    }

    [Fact]
    public void Header_JsonExport_InspectorToggle_ComposerUtils_And_MessageTurnControls_Work()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;

        // Seed a full turn with plan, thinking, and tool call
        state.ActiveSession.Messages.Add(new ChatMessage { Role = "user", Content = "Inspect repo" });
        state.ActiveSession.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "Done inspecting.",
            ExecutionPlan = "1. Read files",
            ThinkingContent = "Checking symbols...",
            ToolCalls = new()
            {
                new AgentToolCallRecord { ToolName = "read_file", ArgumentsJson = "{}", ResultOutput = "ok" }
            }
        });

        var cut = bunit.RenderComponent<GlacierStudioMain>();

        // Export JSON + close banner
        var jsonBtn = cut.FindAll("button.gs-btn-export").First(b => b.TextContent.Contains("JSON", StringComparison.OrdinalIgnoreCase));
        jsonBtn.Click();
        Assert.Contains(".json", cut.Markup, StringComparison.OrdinalIgnoreCase);
        cut.Find("button.gs-banner-close").Click();

        // Toggle inspector panel off and back on
        var toggleInspectorBtn = cut.FindAll("button.gs-btn-export").First(b =>
            b.GetAttribute("title")?.Contains("Inspector Panel", StringComparison.OrdinalIgnoreCase) == true);
        toggleInspectorBtn.Click();
        Assert.Empty(cut.FindAll("button.gs-aux-tab"));
        toggleInspectorBtn.Click();
        Assert.Equal(4, cut.FindAll("button.gs-aux-tab").Count);

        // Expand/collapse plan, thinking, and tool call headers
        cut.Find("div.gs-plan-header").Click();
        cut.Find("div.gs-thinking-header").Click();
        cut.Find("div.gs-tool-call-header").Click();

        // Message copy & reuse buttons (re-query after each click)
        Assert.True(cut.FindAll("button.gs-msg-footer-copy-btn").Count >= 2);
        cut.FindAll("button.gs-msg-footer-copy-btn")[0].Click();
        cut.FindAll("button.gs-msg-footer-copy-btn")[1].Click();

        // Slash command select closes popup
        cut.Find("textarea.gs-textarea").Input("/");
        var slashItems = cut.FindAll("div.gs-slash-item");
        Assert.NotEmpty(slashItems);
        slashItems[0].Click();
        Assert.Empty(cut.FindAll("#slashMenuPopup"));

        // Composer Team and Clear thread buttons
        cut.FindAll("button.gs-composer-util-btn").First(b => b.TextContent.Contains("Team", StringComparison.OrdinalIgnoreCase)).Click();
        Assert.Contains("/team", cut.Find("textarea.gs-textarea").GetAttribute("value") ?? cut.Markup);

        cut.FindAll("button.gs-composer-util-btn").First(b => b.TextContent.Contains("Clear thread", StringComparison.OrdinalIgnoreCase)).Click();
        Assert.Single(state.ActiveSession.Messages);
        Assert.Contains("cleared", state.ActiveSession.Messages[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocTab_Outline_FormattedRawToggle_And_FilePicker_Work()
    {
        var mdPath = Path.Combine(_tempDir, "README.md");
        File.WriteAllText(mdPath, "# Glacier Studio\n\n## Architecture\nDetails here.\n");

        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        state.ActiveRightTab = "doc";
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        cut.FindAll("button.gs-toggle-btn").First(b => b.TextContent.Contains("Outline", StringComparison.OrdinalIgnoreCase)).Click();
        cut.FindAll("button.gs-toggle-btn").First(b => b.TextContent.Contains("Outline", StringComparison.OrdinalIgnoreCase)).Click();

        cut.FindAll("button.gs-toggle-btn").First(b => b.TextContent.Trim() == "Raw").Click();
        cut.FindAll("button.gs-toggle-btn").First(b => b.TextContent.Trim() == "Preview").Click();

        var fileSelect = cut.FindAll("select.gs-select").First(s => s.InnerHtml.Contains("Open file..."));
        if (fileSelect.InnerHtml.Contains("README.md"))
        {
            fileSelect.Change(mdPath);
        }
    }

    [Fact]
    public void ConsoleTab_InputValidation_Run_And_Clear_Work()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        var console = bunit.Services.GetRequiredService<GlacierConsoleService>();
        console.AddLog("SYSTEM", "Seeded log line");
        state.ActiveRightTab = "console";
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        Assert.Contains("Seeded log line", cut.Markup);

        // Empty command validation does not throw
        cut.Find("button.gs-console-btn").Click();

        // Clear chip clears previous logs
        cut.FindAll("span.gs-quick-cmd").First(s => s.TextContent.Trim() == "clear").Click();
        Assert.DoesNotContain("Seeded log line", cut.Markup);
    }

    [Fact]
    public void GraphTab_SearchInput_And_AllQuickFilterChips_Work()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        state.ActiveRightTab = "graph";
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        cut.Find("input[placeholder*='Filter project or symbol']").Input("Glacier.Tune");

        int chipCount = cut.FindAll("span.gs-quick-cmd").Count;
        Assert.Equal(5, chipCount);
        for (int i = 0; i < chipCount; i++)
        {
            cut.FindAll("span.gs-quick-cmd")[i].Click();
        }
    }

    [Theory]
    [InlineData("2048")]
    [InlineData("4096")]
    [InlineData("8192")]
    [InlineData("16384")]
    [InlineData("32768")]
    public void TelemetryTab_InferenceParameters_BindAllContextSizes_Port_Device_And_Engine(string ctxSize)
    {
        var (ctx, state, launcher) = CreateStudioTestContext();
        using var bunit = ctx;
        state.ActiveRightTab = "telemetry";
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        cut.FindAll("div.gs-runtime-param-grid select.gs-select")[0].Change("cpu");
        Assert.Equal("cpu", launcher.InferenceDevice);

        cut.FindAll("div.gs-runtime-param-grid select.gs-select")[1].Change("vulkan");
        Assert.Equal("vulkan", launcher.InferenceEngine);

        cut.FindAll("div.gs-runtime-param-grid select.gs-select")[2].Change(ctxSize);
        Assert.Equal(int.Parse(ctxSize), launcher.InferenceContextSize);

        cut.Find("div.gs-runtime-param-grid input[type='number']").Change("5060");
        Assert.Equal(5060, launcher.InferencePort);
    }

    [Fact]
    public void TelemetryTab_DistributedPresets_Fields_CheckStatus_And_HardwareRefresh_Work()
    {
        var (ctx, state, launcher) = CreateStudioTestContext();
        using var bunit = ctx;
        state.ActiveRightTab = "telemetry";
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        // Check Status & Hardware Refresh buttons
        cut.FindAll("button.gs-btn-export").First(b => b.TextContent.Contains("Check Status")).Click();
        cut.FindAll("button.gs-btn-export").First(b => b.TextContent.Trim() == "Refresh").Click();

        // Preset chips: Local :5056, 2x Local, 3-Stage, Host Only, auto
        int chipCount = cut.FindAll("button.gs-runtime-preset-chip").Count;
        Assert.True(chipCount >= 5);
        for (int i = 0; i < chipCount; i++)
        {
            cut.FindAll("button.gs-runtime-preset-chip")[i].Click();
        }
        Assert.Equal("auto", launcher.DistributedLayers);

        // Coordinator inputs
        cut.Find("input[placeholder*='Comma-separated']").Change("127.0.0.1:5056,127.0.0.1:5057");
        Assert.Equal("127.0.0.1:5056,127.0.0.1:5057", launcher.DistributedStages);

        cut.Find("input[placeholder*='auto or e.g.']").Change("12,16");
        Assert.Equal("12,16", launcher.DistributedLayers);

        // Coordinator Host GPU, HTTP Bind, HTTP Port, Slice Cache, Max Tokens
        var hostBindSelect = cut.FindAll("select.gs-select").First(s => s.InnerHtml.Contains("127.0.0.1 (Localhost only)"));
        hostBindSelect.Change("127.0.0.1");
        Assert.Equal("127.0.0.1", launcher.DistributedHttpHost);

        var httpPortInput = cut.FindAll("div.gs-runtime-param-field")
            .First(f => f.TextContent.Contains("--http-port"))
            .QuerySelector("input[type='number']")!;
        httpPortInput.Change("8099");
        Assert.Equal(8099, launcher.DistributedHttpPort);

        var cacheInput = cut.FindAll("div.gs-runtime-param-field")
            .First(f => f.TextContent.Contains("--cache"))
            .QuerySelector("input")!;
        cacheInput.Change(@"C:\cache\dist");
        Assert.Equal(@"C:\cache\dist", launcher.DistributedCacheDir);

        var maxTokensInput = cut.FindAll("div.gs-runtime-param-field")
            .First(f => f.TextContent.Contains("--tokens"))
            .QuerySelector("input[type='number']")!;
        maxTokensInput.Change("4096");
        Assert.Equal(4096, launcher.DistributedMaxTokens);

        // Worker role inputs (--port & --cache)
        var roleSelect = cut.FindAll("select.gs-select").First(s => s.InnerHtml.Contains("Coordinator (--role coordinator"));
        roleSelect.Change("worker");
        Assert.Equal("worker", launcher.DistributedRole);

        var workerPortInput = cut.FindAll("div.gs-runtime-param-field")
            .First(f => f.TextContent.Contains("Worker TCP Port"))
            .QuerySelector("input[type='number']")!;
        workerPortInput.Change("5099");
        Assert.Equal(5099, launcher.DistributedWorkerPort);

        var workerCacheInput = cut.FindAll("div.gs-runtime-param-field")
            .First(f => f.TextContent.Contains("Slice Cache"))
            .QuerySelector("input")!;
        workerCacheInput.Change(@"C:\cache\worker");
        Assert.Equal(@"C:\cache\worker", launcher.DistributedCacheDir);
        Assert.Contains("--port 5099", launcher.BuildDistributedCliArgs());
    }

    [Fact]
    public void SettingsModal_RemoteApiKeyVisibility_ModelPreset_TestConnection200_BackdropAndStopPropagation_Work()
    {
        var (ctx, state, _) = CreateStudioTestContext();
        using var bunit = ctx;
        var cut = bunit.RenderComponent<GlacierStudioMain>();

        cut.Find("button.gs-settings-trigger-btn").Click();

        // API Key password input appears for RemoteGemini
        var providerSelect = cut.FindAll("div.gs-modal-form-grid select.gs-select")[0];
        providerSelect.Change(ModelServerProvider.RemoteGemini.ToString());
        var apiKeyInput = cut.Find("input[type='password']");
        apiKeyInput.Change("test-session-value");
        Assert.Equal("test-session-value", state.ApiKey);

        // Switching back to LocalOllama hides API Key input
        providerSelect = cut.FindAll("div.gs-modal-form-grid select.gs-select")[0];
        providerSelect.Change(ModelServerProvider.LocalOllama.ToString());
        Assert.Empty(cut.FindAll("input[type='password']"));

        // Model preset selection updates SelectedModel
        var presetSelect = cut.FindAll("div.gs-modal-form-grid select.gs-select")[1];
        var presets = state.GetModelPresetsForProvider(ModelServerProvider.LocalOllama);
        if (presets.Count > 0)
        {
            presetSelect.Change(presets[0]);
            Assert.Equal(presets[0], state.SelectedModel);
        }

        // Valid Test Connection -> 200 OK -> success span
        cut.Find("input[placeholder='http://127.0.0.1:5055/v1']").Change("http://127.0.0.1:5055/v1");
        cut.Find("button.gs-btn-test-conn").Click();
        cut.WaitForAssertion(() => Assert.Contains("✓", cut.Markup));

        // Backdrop click closes modal
        cut.Find("div.gs-modal-backdrop").Click();
        Assert.Empty(cut.FindAll("div.gs-modal-card"));
    }
}
