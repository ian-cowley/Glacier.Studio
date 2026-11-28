using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Glacier.Inference.Config;
using Glacier.Inference.Hardware;
using Glacier.Studio.Services;
using Glacier.Studio.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glacier.Studio.Tests;

public sealed class NpuRuntimeAndRagTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public ConcurrentQueue<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : "";
            Requests.Enqueue((request.Method, request.RequestUri?.ToString() ?? "", body));
            return await _handler(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static DeviceInfo CreateSyntheticXdna2Device() => new()
    {
        Id = "amd-xdna2",
        Index = 2,
        Name = "AMD XDNA 2 NPU (Ryzen AI 50 TOPS)",
        Vendor = GpuVendor.Amd,
        DedicatedVramBytes = 0,
        SharedVramBytes = 16_642_998_272UL, // ~15.500 GB
        IsDisplayDevice = false,
        IsNpu = true,
        SupportedEngines = [InferenceEngineType.NpuSidecar],
        RecommendedEngine = InferenceEngineType.NpuSidecar,
        SafetyNotes = "AMD XDNA 2 NPU sidecar runtime"
    };

    private static GlacierRuntimeLauncherService CreateLauncher(HttpClient probeClient, out GlacierStudioState state)
    {
        state = new GlacierStudioState();
        var console = new GlacierConsoleService();
        return new GlacierRuntimeLauncherService(
            state,
            console,
            workspaceRoot: Path.GetTempPath(),
            probeClient: probeClient,
            startBackgroundProber: false,
            autoDiscover: false,
            autoProbe: false);
    }

    // -------------------------------------------------------------------------
    // Feature #14: NPU Sidecar Runtime Slot & GPU-Busy Intent Routing Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProbeNpuSidecarAsync_DiscoversFastFlowLmAndLemonade_PopulatesModelsAndLatency()
    {
        using var flmHandler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.ToString() == "http://127.0.0.1:52625/v1/models")
            {
                return Task.FromResult(JsonResponse("""{"data":[{"id":"qwen3.5-9b"},{"id":"embeddinggemma"}]}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var flmClient = new HttpClient(flmHandler);
        using var flmLauncher = CreateLauncher(flmClient, out _);

        bool foundFlm = await flmLauncher.ProbeNpuSidecarAsync(
        [
            ("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM),
            ("http://127.0.0.1:8000", NpuSidecarRuntime.LemonadeServer)
        ]);

        Assert.True(foundFlm);
        Assert.Equal(RuntimeHealthState.Online, flmLauncher.NpuSidecarState);
        Assert.Equal(NpuSidecarRuntime.FastFlowLM, flmLauncher.NpuSidecarRuntime);
        Assert.Equal("FastFlowLM", flmLauncher.NpuSidecarRuntimeName);
        Assert.Equal("http://127.0.0.1:52625", flmLauncher.NpuSidecarEndpointUrl);
        Assert.Equal(new[] { "qwen3.5-9b", "embeddinggemma" }, flmLauncher.NpuSidecarModels);
        Assert.Equal("qwen3.5-9b", flmLauncher.NpuSidecarActiveModel);
        Assert.True(flmLauncher.NpuSidecarLatencyMs >= 0);

        using var lemonadeHandler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.ToString() == "http://127.0.0.1:8000/v1/models")
            {
                return Task.FromResult(JsonResponse("""{"data":[{"id":"Qwen3-4B-Hybrid"}]}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var lemonadeClient = new HttpClient(lemonadeHandler);
        using var lemonadeLauncher = CreateLauncher(lemonadeClient, out _);

        bool foundLemonade = await lemonadeLauncher.ProbeNpuSidecarAsync(
        [
            ("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM),
            ("http://127.0.0.1:8000", NpuSidecarRuntime.LemonadeServer)
        ]);

        Assert.True(foundLemonade);
        Assert.Equal(RuntimeHealthState.Online, lemonadeLauncher.NpuSidecarState);
        Assert.Equal(NpuSidecarRuntime.LemonadeServer, lemonadeLauncher.NpuSidecarRuntime);
        Assert.Equal("Lemonade", lemonadeLauncher.NpuSidecarRuntimeName);
        Assert.Equal("http://127.0.0.1:8000", lemonadeLauncher.NpuSidecarEndpointUrl);
        Assert.Equal("Qwen3-4B-Hybrid", lemonadeLauncher.NpuSidecarActiveModel);
    }

    [Fact]
    public void RefreshNpuHardware_PopulatesAmdXdna2SharedCap_AndHandlesAbsentHardware()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        using var launcher = CreateLauncher(client, out _);

        launcher.RefreshNpuHardware([CreateSyntheticXdna2Device()]);
        Assert.True(launcher.IsNpuHardwarePresent);
        Assert.Equal("amd-xdna2", launcher.NpuDeviceId);
        Assert.Contains("XDNA 2", launcher.NpuDeviceName);
        Assert.Equal(15.5, launcher.NpuSidecarSharedCapGb, precision: 1);

        launcher.RefreshNpuHardware(Array.Empty<DeviceInfo>());
        Assert.False(launcher.IsNpuHardwarePresent);
        Assert.Equal("", launcher.NpuDeviceId);
        Assert.Equal(15.5, launcher.NpuSidecarSharedCapGb, precision: 1);
    }

    [Fact]
    public async Task ProbeNpuSidecarAsync_WhenUnreachable_FallsBackCleanlyToOffline()
    {
        using var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));
        using var client = new HttpClient(handler);
        using var launcher = CreateLauncher(client, out _);

        launcher.AllowNpuOffloadOnBusyGpu = true;
        launcher.SetGpuBusyForTesting(true);

        bool ok = await launcher.ProbeNpuSidecarAsync(
        [
            ("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM),
            ("http://127.0.0.1:8000", NpuSidecarRuntime.LemonadeServer)
        ]);

        Assert.False(ok);
        Assert.Equal(RuntimeHealthState.Offline, launcher.NpuSidecarState);
        Assert.Equal("", launcher.NpuSidecarEndpointUrl);
        Assert.Empty(launcher.NpuSidecarModels);
        Assert.Equal($"http://127.0.0.1:{launcher.InferencePort}/v1", launcher.ResolveEndpointForTurn(AgentTurnTier.LocalFastTool));
    }

    [Fact]
    public void ResolveTurnRouting_OffloadsLocalFastToolOnlyWhenEnabledAndGpuBusyOrOffline_AndNormalizesV1()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        using var launcher = CreateLauncher(client, out _);

        launcher.SetInferenceStateForTesting(RuntimeHealthState.Online, "Glacier-Grandmaster-7B-Q4_K_M");
        launcher.SetBeastClusterStateForTesting(RuntimeHealthState.Online, "qwen2.5-coder-14b");
        launcher.SetNpuSidecarStateForTesting(
            RuntimeHealthState.Online,
            endpointUrl: "http://127.0.0.1:52625",
            modelId: "qwen3.5-9b");

        // Disabled by default -> stays on GPU even if busy
        launcher.AllowNpuOffloadOnBusyGpu = false;
        launcher.BeginLocalGpuTurn();
        Assert.True(launcher.IsGpuBusy);
        Assert.Equal("http://127.0.0.1:5055/v1", launcher.ResolveEndpointForTurn(AgentTurnTier.LocalFastTool));

        // Enabled + GPU busy -> routes LocalFastTool to NPU with /v1 normalized
        launcher.AllowNpuOffloadOnBusyGpu = true;
        Assert.Equal("http://127.0.0.1:52625/v1", launcher.ResolveEndpointForTurn(AgentTurnTier.LocalFastTool));
        Assert.Equal("qwen3.5-9b", launcher.ResolveModelForTurn(AgentTurnTier.LocalFastTool));

        // RemoteDeepReasoning stays on Beast even while GPU is busy
        Assert.Equal(launcher.BeastEndpointUrl, launcher.ResolveEndpointForTurn(AgentTurnTier.RemoteDeepReasoning));

        // Ending GPU turn reverts LocalFastTool back to idle GPU
        launcher.EndLocalGpuTurn();
        launcher.EndLocalGpuTurn(); // Extra decrement must clamp at 0
        Assert.Equal(0, launcher.ActiveLocalGpuRequests);
        Assert.False(launcher.IsGpuBusy);
        Assert.Equal("http://127.0.0.1:5055/v1", launcher.ResolveEndpointForTurn(AgentTurnTier.LocalFastTool));

        // If GPU goes offline while offload enabled -> NPU picks up LocalFastTool
        launcher.SetInferenceStateForTesting(RuntimeHealthState.Offline);
        Assert.Equal("http://127.0.0.1:52625/v1", launcher.ResolveEndpointForTurn(AgentTurnTier.LocalFastTool));
    }

    [Fact]
    public async Task RuntimeLauncher_ConcurrentProbeAndRouting_AndDoubleDispose_AreThreadSafe()
    {
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/v1/models", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(JsonResponse("""{"data":[{"id":"qwen3.5-9b"}]}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var client = new HttpClient(handler);
        var launcher = CreateLauncher(client, out _);
        launcher.AllowNpuOffloadOnBusyGpu = true;
        launcher.SetInferenceStateForTesting(RuntimeHealthState.Online, "Glacier-Grandmaster-7B-Q4_K_M");

        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            if (i % 2 == 0)
            {
                await launcher.ProbeNpuSidecarAsync([("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]);
            }
            launcher.BeginLocalGpuTurn();
            string ep = launcher.ResolveEndpointForTurn(AgentTurnTier.LocalFastTool);
            string mdl = launcher.ResolveModelForTurn(AgentTurnTier.LocalFastTool);
            Assert.False(string.IsNullOrWhiteSpace(ep));
            Assert.False(string.IsNullOrWhiteSpace(mdl));
            launcher.EndLocalGpuTurn();
        });

        await Task.WhenAll(tasks);
        Assert.Equal(0, launcher.ActiveLocalGpuRequests);

        launcher.Dispose();
        launcher.Dispose(); // Must be idempotent
    }

    [Fact]
    public async Task GlacierIntentRouter_And_AgentEngine_DispatchToNpuWithLeanPrompt()
    {
        var inquiry = GlacierIntentRouter.Analyze("hello, what time is it?", Path.GetTempPath());
        Assert.True(GlacierIntentRouter.IsLightweightToolIntent(inquiry));

        var modIntent = new AnalyzedDeveloperIntent
        {
            Archetype = DeveloperIntentArchetype.CodeModification,
            Target = "Foo.cs",
            Confidence = 0.95f,
            LatencyMs = 0.5,
            NeedsToolExecution = true,
            NeedsWorkspaceMutation = true,
            ComplexityScore = 7.5f,
            AllowedTools = ["read_file", "write_file"],
            StrategicPlan = "plan",
            NudgeDirective = "nudge",
            SystemInstructionHint = "hint"
        };
        Assert.False(GlacierIntentRouter.IsLightweightToolIntent(modIntent));

        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"NPU OK\"}}]}\n\ndata: [DONE]\n\n";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(resp);
        });
        using var httpClient = new HttpClient(handler);

        string tempWs = Directory.CreateTempSubdirectory("glacier_studio_agent_test_").FullName;
        try
        {
            var state = new GlacierStudioState { IsAgentMode = true };
            var console = new GlacierConsoleService();
            var docPreview = new GlacierDocTreePreviewService();
            var mcp = new GlacierMcpManager(tempWs);
            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions { EnableNpuEmbeddings = false });
            using var launcher = new GlacierRuntimeLauncherService(
                state,
                console,
                tempWs,
                probeClient: httpClient,
                startBackgroundProber: false,
                autoDiscover: false,
                autoProbe: false);

            launcher.SetInferenceStateForTesting(RuntimeHealthState.Online, "Glacier-Grandmaster-7B-Q4_K_M");
            launcher.SetNpuSidecarStateForTesting(RuntimeHealthState.Online, "http://127.0.0.1:52625", "qwen3.5-9b");
            launcher.AllowNpuOffloadOnBusyGpu = true;
            launcher.SetGpuBusyForTesting(true);

            // 1. Direct LocalGlacierInference + GPU busy + lightweight prompt -> offloads to NPU
            state.SetProvider(ModelServerProvider.LocalGlacierInference);
            var engine = new GlacierAgentEngine(httpClient, state, console, docPreview, mcp, graph, launcher);
            await engine.ProcessUserMessageAsync("hello");

            Assert.True(handler.Requests.TryDequeue(out var req1));
            Assert.Equal("http://127.0.0.1:52625/v1/chat/completions", req1.Url);
            Assert.DoesNotContain("Available Projects in Workspace:", req1.Body);

            // 2. Explicit LocalNpuSidecar provider -> uses lean local prompt and NPU endpoint
            launcher.SetGpuBusyForTesting(false);
            launcher.ActivateNpuSidecarInStudio();
            Assert.Equal(ModelServerProvider.LocalNpuSidecar, state.Provider);
            Assert.Equal("http://127.0.0.1:52625/v1", state.EndpointUrl);

            await engine.ProcessUserMessageAsync("hi again");
            Assert.True(handler.Requests.TryDequeue(out var req2));
            Assert.Equal("http://127.0.0.1:52625/v1/chat/completions", req2.Url);
            Assert.Contains("\"model\":\"qwen3.5-9b\"", req2.Body);
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GlacierStudioMain_RendersNpuSidecarCardAndProviderOption()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.ToString() == "http://127.0.0.1:52625/v1/models")
            {
                return Task.FromResult(JsonResponse("""{"data":[{"id":"qwen3.5-9b"},{"id":"embeddinggemma"}]}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        string tempWs = Directory.CreateTempSubdirectory("glacier_studio_bunit_").FullName;

        try
        {
            var state = new GlacierStudioState { ActiveRightTab = "telemetry" };
            var console = new GlacierConsoleService();
            var docPreview = new GlacierDocTreePreviewService();
            var mcp = new GlacierMcpManager(tempWs);
            var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions { EnableNpuEmbeddings = false });
            var launcher = new GlacierRuntimeLauncherService(
                state,
                console,
                tempWs,
                probeClient: httpClient,
                startBackgroundProber: false,
                autoDiscover: false,
                autoProbe: false);

            launcher.RefreshNpuHardware([CreateSyntheticXdna2Device()]);
            launcher.SetNpuSidecarStateForTesting(
                RuntimeHealthState.Online,
                endpointUrl: "http://127.0.0.1:52625",
                modelId: "qwen3.5-9b",
                runtime: NpuSidecarRuntime.FastFlowLM,
                models: ["qwen3.5-9b", "embeddinggemma"]);

            var engine = new GlacierAgentEngine(httpClient, state, console, docPreview, mcp, graph, launcher);

            ctx.Services.AddSingleton(state);
            ctx.Services.AddSingleton(engine);
            ctx.Services.AddSingleton(console);
            ctx.Services.AddSingleton(docPreview);
            ctx.Services.AddSingleton(mcp);
            ctx.Services.AddSingleton(graph);
            ctx.Services.AddSingleton(launcher);
            ctx.Services.AddSingleton<IGlacierFolderPicker>(new GlacierFolderPickerService());
            ctx.Services.AddHttpClient();

            var cut = ctx.RenderComponent<GlacierStudioMain>();
            cut.WaitForAssertion(() =>
            {
                string markup = cut.Markup;
                Assert.Contains("LocalNpuSidecar", markup);
                Assert.Contains("NPU Sidecar (FastFlowLM / Lemonade)", markup);
                Assert.Contains("amd-xdna2", markup);
                Assert.Contains("15.5 GB NPU Cap", markup);
                Assert.Contains("qwen3.5-9b", markup);
                Assert.Contains("embeddinggemma", markup);
                Assert.Contains("Offload LocalFastTool turns to NPU", markup);

                var headerSelect = cut.Find("select.gs-header-model-select");
                Assert.Contains("value=\"LocalNpuSidecar\"", headerSelect.InnerHtml);

                var composerSelect = cut.Find("select.gs-composer-model-select");
                Assert.Contains("value=\"LocalNpuSidecar\"", composerSelect.InnerHtml);
            }, timeout: TimeSpan.FromSeconds(5));

            cut.Find("button.gs-composer-util-btn").Click();
            cut.WaitForAssertion(() =>
            {
                var settingsProviderSelect = cut.Find("div.gs-modal-card select.gs-select");
                Assert.Contains("value=\"LocalNpuSidecar\"", settingsProviderSelect.InnerHtml);
            }, timeout: TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Feature #15: Semantic Workspace RAG & Dimension-Isolation Tests
    // -------------------------------------------------------------------------

    private static float[] MakeTopicVector(int dims, int topicIndex)
    {
        var vec = new float[dims];
        int bucketSize = dims / 4;
        int start = (topicIndex % 4) * bucketSize;
        for (int i = 0; i < bucketSize; i++)
        {
            vec[start + i] = 1.0f;
        }
        return vec;
    }

    private static string BuildEmbeddingsJson(float[] vector) =>
        JsonSerializer.Serialize(new { data = new[] { new { embedding = vector } } });

    [Fact]
    public async Task WorkspaceGraph_NpuAutoDiscovery_AndSemanticSearch_FindsConceptualMatch()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_rag_npu_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempWs, "JwtTokenValidator.cs"),
                "namespace Demo.Security; public class JwtTokenValidator { public bool VerifySignature(string bearer) => true; }");
            await File.WriteAllTextAsync(Path.Combine(tempWs, "CudaKernelLauncher.cs"),
                "namespace Demo.Gpu; public class CudaKernelLauncher { public void DispatchWarpGrid() {} }");
            await File.WriteAllTextAsync(Path.Combine(tempWs, "WavAudioEncoder.cs"),
                "namespace Demo.Audio; public class WavAudioEncoder { public byte[] EncodePcm16(float[] samples) => []; }");

            using var handler = new StubHttpMessageHandler(async (req, ct) =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.EndsWith("/v1/models", StringComparison.Ordinal))
                {
                    return JsonResponse("""{"data":[{"id":"qwen3-4b"},{"id":"embeddinggemma"}]}""");
                }
                if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
                {
                    string reqBody = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
                    int topic = reqBody.Contains("JwtTokenValidator", StringComparison.OrdinalIgnoreCase) ||
                                reqBody.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                                reqBody.Contains("credential", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : reqBody.Contains("Cuda", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    return JsonResponse(BuildEmbeddingsJson(MakeTopicVector(768, topic)));
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var httpClient = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = httpClient,
                NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                ProbeCacheTtl = TimeSpan.Zero
            });

            bool initialized = await graph.InitializeEmbeddingsAsync();
            Assert.True(initialized);
            Assert.True(graph.IsUsingNpuEmbeddings);
            Assert.Equal("embeddinggemma", graph.ActiveEmbeddingModelId);
            Assert.Equal(768, graph.ActiveEmbeddingDimensions);

            await graph.IndexWorkspaceSemanticAsync();
            Assert.True(graph.SemanticChunkCount >= 3);

            // Conceptual query with zero substring overlap with "JwtTokenValidator"
            var hits = await graph.SearchSemanticAsync("authentication credential check", topK: 3);
            Assert.NotEmpty(hits);
            Assert.Equal("JwtTokenValidator.cs", hits[0].RelativePath);

            string summary = await graph.SearchWorkspaceAsync("authentication credential check");
            Assert.Contains("JwtTokenValidator.cs", summary);
            Assert.Contains("[SEMANTIC MATCH", summary);
            Assert.Contains("NPU", summary);
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceGraph_WhenNpuOffline_FallsBackCleanlyToFastHash384()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_rag_offline_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempWs, "AlphaService.cs"),
                "namespace Demo; public class AlphaService { public void ExecutePipeline() {} }");

            using var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Sidecar offline"));
            using var httpClient = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = httpClient,
                NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                ProbeCacheTtl = TimeSpan.Zero
            });

            bool initialized = await graph.InitializeEmbeddingsAsync();
            Assert.False(initialized);
            Assert.False(graph.IsUsingNpuEmbeddings);
            Assert.Equal(384, graph.ActiveEmbeddingDimensions);
            Assert.Contains("Hash Fallback", graph.EmbeddingModeLabel);

            string result = await graph.SearchWorkspaceAsync("ExecutePipeline");
            Assert.Contains("AlphaService.cs", result);
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceGraph_MidSessionNpuDrop_FallsBack768To384WithoutDimensionMismatch()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_rag_drop_").FullName;
        try
        {
            string fileA = Path.Combine(tempWs, "OrderProcessor.cs");
            string fileB = Path.Combine(tempWs, "InventoryTracker.cs");
            await File.WriteAllTextAsync(fileA, "public class OrderProcessor { public void ProcessInvoice() {} }");
            await File.WriteAllTextAsync(fileB, "public class InventoryTracker { public void CountStock() {} }");

            bool failEmbeddings = false;
            using var handler = new StubHttpMessageHandler((req, _) =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.EndsWith("/v1/models", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse("""{"data":[{"id":"embeddinggemma"}]}"""));
                }
                if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
                {
                    if (failEmbeddings)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                    }
                    return Task.FromResult(JsonResponse(BuildEmbeddingsJson(MakeTopicVector(768, 0))));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var httpClient = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = httpClient,
                NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                ProbeCacheTtl = TimeSpan.Zero
            });

            Assert.True(await graph.InitializeEmbeddingsAsync());
            await graph.IndexWorkspaceSemanticAsync();
            Assert.Equal(768, graph.ActiveEmbeddingDimensions);

            // Drop NPU mid-session
            failEmbeddings = true;
            var fallbackHits = await graph.SearchSemanticAsync("ProcessInvoice", topK: 2);
            Assert.NotEmpty(fallbackHits);
            Assert.False(graph.IsUsingNpuEmbeddings);
            Assert.Equal(384, graph.ActiveEmbeddingDimensions);

            // Subsequent ingestion while offline must succeed via hash fallback with zero exceptions
            int chunks = await graph.IngestFileContentAsync(
                Path.Combine(tempWs, "PaymentGateway.cs"),
                "public class PaymentGateway { public void ChargeCard() {} }");
            Assert.True(chunks > 0);
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceGraph_OfflineToOnlineAndModelSwitch_384To768To1024_RebuildsCleanly()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_rag_switch_").FullName;
        try
        {
            bool online = false;
            int currentDims = 768;
            string currentModel = "embeddinggemma";

            using var handler = new StubHttpMessageHandler((req, _) =>
            {
                if (!online) throw new HttpRequestException("Offline");
                string url = req.RequestUri?.ToString() ?? "";
                if (url.EndsWith("/v1/models", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse($$"""{"data":[{"id":"{{currentModel}}"}]}"""));
                }
                if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse(BuildEmbeddingsJson(MakeTopicVector(currentDims, 1))));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var httpClient = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = httpClient,
                NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                ProbeCacheTtl = TimeSpan.Zero
            });

            // 1. Offline ingest (384d)
            Assert.False(await graph.InitializeEmbeddingsAsync());
            await graph.IngestFileContentAsync(Path.Combine(tempWs, "MetricCollector.cs"),
                "public class MetricCollector { public void RecordTelemetry() {} }");
            Assert.Equal(384, graph.ActiveEmbeddingDimensions);

            // 2. Bring online at 768d
            online = true;
            currentDims = 768;
            currentModel = "embeddinggemma";
            Assert.True(await graph.InitializeEmbeddingsAsync(forceRefresh: true));
            Assert.Equal(768, graph.ActiveEmbeddingDimensions);
            var hits768 = await graph.SearchSemanticAsync("RecordTelemetry", topK: 2);
            Assert.Single(hits768);

            // 3. Switch NPU embedding model to 1024d
            currentDims = 1024;
            currentModel = "bge-large-embed";
            Assert.True(await graph.InitializeEmbeddingsAsync(forceRefresh: true));
            Assert.Equal(1024, graph.ActiveEmbeddingDimensions);
            Assert.Equal("bge-large-embed", graph.ActiveEmbeddingModelId);
            var hits1024 = await graph.SearchSemanticAsync("RecordTelemetry", topK: 2);
            Assert.Single(hits1024);
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceGraph_SetWorkspaceRoot_ClearsPreviousWorkspaceChunks()
    {
        string wsA = Directory.CreateTempSubdirectory("glacier_ws_a_").FullName;
        string wsB = Directory.CreateTempSubdirectory("glacier_ws_b_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(wsA, "AlphaOnly.cs"), "public class AlphaOnly {}");
            await File.WriteAllTextAsync(Path.Combine(wsB, "BetaOnly.cs"), "public class BetaOnly {}");

            using var graph = new GlacierWorkspaceGraphService(wsA, new WorkspaceRagOptions { EnableNpuEmbeddings = false });
            await graph.IndexWorkspaceSemanticAsync();
            Assert.True(graph.SemanticChunkCount >= 1);

            graph.SetWorkspaceRoot(wsB);
            Assert.Equal(0, graph.SemanticChunkCount);

            await graph.IndexWorkspaceSemanticAsync();
            var hits = await graph.SearchSemanticAsync("BetaOnly", topK: 5);
            Assert.Contains(hits, h => h.RelativePath == "BetaOnly.cs");
            Assert.DoesNotContain(hits, h => h.RelativePath == "AlphaOnly.cs");
        }
        finally
        {
            try { Directory.Delete(wsA, recursive: true); } catch { }
            try { Directory.Delete(wsB, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorkspaceGraph_DiRegistration_MatchesProgramCsWithoutNetworkHangs()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("GlacierAgent");
        services.AddSingleton<GlacierWorkspaceGraphService>();

        using var provider = services.BuildServiceProvider();
        using var resolved = provider.GetRequiredService<GlacierWorkspaceGraphService>();

        Assert.NotNull(resolved);
        Assert.NotNull(resolved.Store);
    }

    [Fact]
    public async Task WorkspaceGraph_ConcurrentIngestSearchAndNeighborhoodSummary_IsThreadSafe()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_rag_conc_").FullName;
        try
        {
            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions { EnableNpuEmbeddings = false });

            var workers = Enumerable.Range(0, 20).Select(async i =>
            {
                string path = Path.Combine(tempWs, $"Worker{i}.cs");
                await graph.IngestFileContentAsync(path, $"namespace Demo; public class Worker{i} {{ public void Run{i}() {{}} }}");
                var hits = await graph.SearchSemanticAsync($"Worker{i}", topK: 3);
                Assert.NotNull(hits);
                string summary = graph.QueryNeighborhoodSummary($"Worker{i}", maxEdges: 10);
                Assert.False(string.IsNullOrEmpty(summary));
            });

            await Task.WhenAll(workers);
            Assert.Equal(20, graph.SemanticChunkCount);
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceGraph_StaleChunkSaturation_StillReturnsValidLiveFile()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_rag_b8_").FullName;
        try
        {
            using var handler = new StubHttpMessageHandler(async (req, ct) =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.EndsWith("/v1/models", StringComparison.Ordinal))
                {
                    return JsonResponse("""{"data":[{"id":"embeddinggemma"}]}""");
                }
                if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
                {
                    string body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
                    int topic = body.Contains("JwtSignatureClaims", StringComparison.OrdinalIgnoreCase) ||
                                body.Contains("JwtBackupChecker", StringComparison.OrdinalIgnoreCase) ||
                                body.Contains("credential verification", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 2;
                    return JsonResponse(BuildEmbeddingsJson(MakeTopicVector(768, topic)));
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var httpClient = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = httpClient,
                NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                ProbeCacheTtl = TimeSpan.Zero
            });

            Assert.True(await graph.InitializeEmbeddingsAsync());

            await graph.IngestFileContentAsync(
                Path.Combine(tempWs, "ValidTarget.cs"),
                "namespace Demo; public class JwtBackupChecker { public bool VerifyToken() => true; }");

            // Saturate append-only vector index with >=26 stale edits to StaleCandidate.cs
            string stalePath = Path.Combine(tempWs, "StaleCandidate.cs");
            for (int i = 0; i < 26; i++)
            {
                await graph.IngestFileContentAsync(
                    stalePath,
                    $"namespace Demo; public class JwtSignatureClaimsV{i} {{ public bool CheckClaims() => true; }}");
            }

            // Overwrite StaleCandidate.cs with unrelated content
            await graph.IngestFileContentAsync(
                stalePath,
                "namespace Demo; public class CompletelyUnrelatedMath { public int Add(int a, int b) => a + b; }");

            var hits = await graph.SearchSemanticAsync("credential verification", topK: 3);
            Assert.NotEmpty(hits);
            Assert.Contains(hits, h => h.RelativePath == "ValidTarget.cs");
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceGraph_SelectEmbeddingModelId_PrefersBgeM3OverChatModel()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_rag_bge_").FullName;
        try
        {
            using var handler = new StubHttpMessageHandler((req, _) =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.EndsWith("/v1/models", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse("""{"data":[{"id":"qwen3.5-9b"},{"id":"bge-m3-npu"}]}"""));
                }
                if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse(BuildEmbeddingsJson(MakeTopicVector(1024, 0))));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var httpClient = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = httpClient,
                NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                ProbeCacheTtl = TimeSpan.Zero
            });

            Assert.True(await graph.InitializeEmbeddingsAsync());
            Assert.Equal("bge-m3-npu", graph.ActiveEmbeddingModelId);
            Assert.Equal(1024, graph.ActiveEmbeddingDimensions);
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RuntimeLauncher_TryGetOnlineNpuConfig_AndBackgroundProberDispose_AreSafe()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

        var launcher = new GlacierRuntimeLauncherService(
            new GlacierStudioState(),
            new GlacierConsoleService(),
            workspaceRoot: Path.GetTempPath(),
            probeClient: client,
            startBackgroundProber: true,
            autoDiscover: false,
            autoProbe: false);

        Assert.False(launcher.TryGetOnlineNpuConfig(out _, out _));

        launcher.SetNpuSidecarStateForTesting(
            RuntimeHealthState.Online,
            endpointUrl: "http://127.0.0.1:52625",
            modelId: "qwen3.5-9b");

        Assert.True(launcher.TryGetOnlineNpuConfig(out string ep, out string mdl));
        Assert.Equal("http://127.0.0.1:52625/v1", ep);
        Assert.Equal("qwen3.5-9b", mdl);

        launcher.Dispose();
        launcher.Dispose();
    }
}
