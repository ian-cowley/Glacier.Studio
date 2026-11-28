namespace Glacier.Studio.Tests;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Inference.Hardware;
using Glacier.Studio.Services;
using Xunit;

public sealed class Tier5M7Phase2HardeningTests : IDisposable
{
    private sealed class StubStudioHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubStudioHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request, cancellationToken);
    }

    public Tier5M7Phase2HardeningTests() => NpuSidecarDiscovery.InvalidateCache();
    public void Dispose() => NpuSidecarDiscovery.InvalidateCache();

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task WorkspaceGraphService_TransientNpu500ThenRecovery_AutoRecoversWithoutManualForceRefresh()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_t5_recover_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempWs, "AuthService.cs"),
                "public class AuthService { public bool ValidateJwtToken(string jwt) => true; }");

            int modelsStatus = 200;
            int embeddingsStatus = 200;

            using var handler = new StubStudioHandler((req, _) =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                {
                    return modelsStatus == 200
                        ? Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"bge-m3-npu"}]}"""))
                        : Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, """{"error":"down"}"""));
                }
                if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    if (embeddingsStatus != 200)
                        return Task.FromResult(Json(HttpStatusCode.InternalServerError, """{"error":"boom"}"""));
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0]}]}"""));
                }
                return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
            });
            using var http = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = http,
                NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                ProbeCacheTtl = TimeSpan.Zero
            });

            Assert.True(await graph.InitializeEmbeddingsAsync(forceRefresh: false));
            Assert.True(graph.IsUsingNpuEmbeddings);

            // Inject transient HTTP 500 on embeddings -> demotes cleanly to Hash fallback
            Volatile.Write(ref embeddingsStatus, 500);
            var fallbackHits = await graph.SearchSemanticAsync("ValidateJwtToken");
            Assert.NotNull(fallbackHits);
            Assert.False(graph.IsUsingNpuEmbeddings);

            // Restore sidecar -> calling InitializeEmbeddingsAsync() WITHOUT forceRefresh: true MUST recover
            Volatile.Write(ref embeddingsStatus, 200);
            bool recovered = await graph.InitializeEmbeddingsAsync(forceRefresh: false);
            Assert.True(recovered);
            Assert.True(graph.IsUsingNpuEmbeddings);
            Assert.Equal("bge-m3-npu", graph.ActiveEmbeddingModelId);
        }
        finally
        {
            if (Directory.Exists(tempWs)) Directory.Delete(tempWs, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceGraphService_SidecarRestartDifferentDims_ReindexesWithoutCorruptingVectorStore()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_t5_dimswap_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempWs, "SwapDoc.cs"),
                "public class SwapDoc { public void Run() {} }");

            int dims = 768;
            using var handler = new StubStudioHandler((req, _) =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"embed-swap"}]}"""));
                if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    int d = Volatile.Read(ref dims);
                    var vec = new float[d];
                    vec[0] = 1.0f;
                    return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"data\":[{{\"index\":0,\"embedding\":{JsonSerializer.Serialize(vec)}}}]}}"));
                }
                return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
            });
            using var http = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = http,
                NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                ProbeCacheTtl = TimeSpan.Zero
            });

            Assert.True(await graph.InitializeEmbeddingsAsync(forceRefresh: true));
            var hits768 = await graph.SearchSemanticAsync("SwapDoc");
            Assert.NotEmpty(hits768);
            Assert.Equal(768, graph.ActiveEmbeddingDimensions);

            // Sidecar restarts with 128d embeddings
            Volatile.Write(ref dims, 128);
            Assert.True(await graph.InitializeEmbeddingsAsync(forceRefresh: true));
            Assert.Equal(128, graph.ActiveEmbeddingDimensions);

            var hits128 = await graph.SearchSemanticAsync("SwapDoc");
            Assert.NotEmpty(hits128);
        }
        finally
        {
            if (Directory.Exists(tempWs)) Directory.Delete(tempWs, recursive: true);
        }
    }

    [Fact]
    public void GlacierAgentEngine_EndpointWithoutV1AndWithQueryString_NormalizesToV1ChatCompletions()
    {
        Assert.Equal("http://127.0.0.1:52625/v1/chat/completions",
            GlacierAgentEngine.NormalizeChatCompletionsUrl("http://127.0.0.1:52625"));
        Assert.Equal("http://127.0.0.1:52625/v1/chat/completions?v=1",
            GlacierAgentEngine.NormalizeChatCompletionsUrl("http://127.0.0.1:52625?v=1"));
        Assert.Equal("http://127.0.0.1:52625/v1/chat/completions?v=1",
            GlacierAgentEngine.NormalizeChatCompletionsUrl("http://127.0.0.1:52625/v1?v=1"));
        Assert.Equal("http://127.0.0.1:52625/v1/chat/completions",
            GlacierAgentEngine.NormalizeChatCompletionsUrl("http://127.0.0.1:52625/v1/chat/completions"));
    }

    [Fact]
    public async Task RuntimeLauncherService_ConcurrentProbeStormAndDispose_ThreadSafeWithZeroUnhandledExceptions()
    {
        using var handler = new StubStudioHandler(async (req, ct) =>
        {
            await Task.Delay(5, ct);
            string url = req.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return Json(HttpStatusCode.OK, """{"data":[{"id":"qwen3-npu"}]}""");
            return Json(HttpStatusCode.NotFound, "{}");
        });
        using var http = new HttpClient(handler);
        var state = new GlacierStudioState();
        var console = new GlacierConsoleService();
        var launcher = new GlacierRuntimeLauncherService(
            state,
            console,
            workspaceRoot: Path.GetTempPath(),
            probeClient: http,
            startBackgroundProber: true,
            autoDiscover: false,
            autoProbe: false);

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            for (int j = 0; j < 10; j++)
            {
                await launcher.ProbeNpuSidecarAsync([("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]);
                _ = launcher.ResolveEndpointForTurn(i % 2 == 0 ? AgentTurnTier.LocalFastTool : AgentTurnTier.RemoteDeepReasoning);
                launcher.SetInferenceStateForTesting(j % 2 == 0 ? RuntimeHealthState.Online : RuntimeHealthState.Offline, "m");
                launcher.SetBeastClusterStateForTesting(j % 2 == 0 ? RuntimeHealthState.Online : RuntimeHealthState.Offline, "b");
                _ = launcher.DistributedClusterStages;
                _ = launcher.BeastClusterActiveModel;
                _ = launcher.BeastClusterLatencyMs;
            }
        })).ToArray();

        await Task.Delay(15);
        launcher.Dispose();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task WorkspaceGraphService_ConcurrentInitializeAndSearch_RacedAgainstDispose_CompletesPromptlyWithoutHang()
    {
        for (int round = 0; round < 5; round++)
        {
            string tempWs = Directory.CreateTempSubdirectory("glacier_t5_disposerace_").FullName;
            try
            {
                await File.WriteAllTextAsync(Path.Combine(tempWs, "RaceDoc.cs"),
                    "public class RaceDoc { public void Go() {} }");

                using var handler = new StubStudioHandler(async (req, ct) =>
                {
                    await Task.Delay(5, ct);
                    string path = req.RequestUri?.AbsolutePath ?? "";
                    if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                        return Json(HttpStatusCode.OK, """{"data":[{"id":"bge-m3-npu"}]}""");
                    if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                        return Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0,0.0,0.0,0.0]}]}""");
                    return Json(HttpStatusCode.NotFound, "{}");
                });
                using var http = new HttpClient(handler);

                var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
                {
                    HttpClient = http,
                    NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)],
                    ProbeCacheTtl = TimeSpan.Zero
                });

                var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
                {
                    try
                    {
                        if (i % 2 == 0)
                            await graph.InitializeEmbeddingsAsync(forceRefresh: true);
                        else
                            await graph.SearchSemanticAsync("RaceDoc");
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
                    {
                        // Expected during concurrent disposal
                    }
                })).ToArray();

                await Task.Delay(2);
                graph.Dispose();

                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                if (Directory.Exists(tempWs)) Directory.Delete(tempWs, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WorkspaceGraphService_QueryStringEndpointWithoutExplicitModel_DiscoversServedModel()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_t5_qs_model_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempWs, "QueryDoc.cs"),
                "public class QueryDoc { public void Ping() {} }");

            var capturedUris = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var handler = new StubStudioHandler((req, _) =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                capturedUris.Enqueue(url);
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"embed-q"}]}"""));
                if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0,0.0,0.0,0.0]}]}"""));
                return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
            });
            using var http = new HttpClient(handler);

            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions
            {
                HttpClient = http,
                EmbeddingEndpoint = "http://127.0.0.1:52625/v1?api-version=1",
                EmbeddingModelId = null,
                ProbeCacheTtl = TimeSpan.Zero
            });

            bool ok = await graph.InitializeEmbeddingsAsync(forceRefresh: true);
            Assert.True(ok);
            Assert.True(graph.IsUsingNpuEmbeddings);
            Assert.Equal("embed-q", graph.ActiveEmbeddingModelId);
            Assert.Contains("http://127.0.0.1:52625/v1/models?api-version=1", capturedUris);
            Assert.Contains("http://127.0.0.1:52625/v1/embeddings?api-version=1", capturedUris);
        }
        finally
        {
            if (Directory.Exists(tempWs)) Directory.Delete(tempWs, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:52625/v1?api-version=1", "http://127.0.0.1:52625/v1?api-version=1")]
    [InlineData("http://127.0.0.1:52625?api-version=1", "http://127.0.0.1:52625/v1?api-version=1")]
    [InlineData("http://127.0.0.1:52625/v1/?api-version=1#frag", "http://127.0.0.1:52625/v1?api-version=1#frag")]
    [InlineData("http://127.0.0.1:52625/", "http://127.0.0.1:52625/v1")]
    public void RuntimeLauncherService_NormalizeOpenAiV1Endpoint_PreservesQueryAndFragment(string input, string expected)
    {
        Assert.Equal(expected, GlacierRuntimeLauncherService.NormalizeOpenAiV1Endpoint(input));
    }

    [Fact]
    public async Task GlacierAgentEngine_NonObjectJsonAndMalformedUsage_FailsGracefullyWithoutInvalidOperationCrash()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_t5_agentjson_").FullName;
        try
        {
            string[] oddJsonPayloads =
            [
                "[1,2,3]",
                """{"choices":[{"message":{"content":"ok-non-int-usage"}}],"usage":{"prompt_tokens":"ten","completion_tokens":null,"total_tokens":{}}}"""
            ];

            var sseBody =
                "data: [1,2,3]\n\n" +
                "data: \"scalar\"\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok-sse\"}}]}\n\n" +
                "data: [DONE]\n\n";

            int idx = 0;
            using var handler = new StubStudioHandler((_, _) =>
            {
                int cur = Interlocked.Increment(ref idx) - 1;
                if (cur % 3 == 2)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
                    });
                }
                string body = oddJsonPayloads[cur % oddJsonPayloads.Length];
                return Task.FromResult(Json(HttpStatusCode.OK, body));
            });
            using var http = new HttpClient(handler);
            var state = new GlacierStudioState
            {
                Provider = ModelServerProvider.LocalGlacierInference,
                EndpointUrl = "http://127.0.0.1:52625/v1",
                SelectedModel = "qwen3-4b"
            };
            using var graph = new GlacierWorkspaceGraphService(tempWs, new WorkspaceRagOptions { EnableNpuEmbeddings = false });
            var engine = new GlacierAgentEngine(
                http,
                state,
                new GlacierConsoleService(),
                new GlacierDocTreePreviewService(),
                new GlacierMcpManager(),
                graphService: graph);

            await engine.ProcessUserMessageAsync("hello array root");
            Assert.NotEqual(AgentExecutionStatus.Error, state.ExecutionStatus);

            await engine.ProcessUserMessageAsync("hello odd usage");
            Assert.NotEqual(AgentExecutionStatus.Error, state.ExecutionStatus);
            Assert.Contains(state.ActiveSession.Messages, m => m.Role == "assistant" && m.Content.Contains("ok-non-int-usage", StringComparison.Ordinal));

            await engine.ProcessUserMessageAsync("hello odd sse");
            Assert.NotEqual(AgentExecutionStatus.Error, state.ExecutionStatus);
            Assert.Contains(state.ActiveSession.Messages, m => m.Role == "assistant" && m.Content.Contains("ok-sse", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempWs)) Directory.Delete(tempWs, recursive: true);
        }
    }
}
