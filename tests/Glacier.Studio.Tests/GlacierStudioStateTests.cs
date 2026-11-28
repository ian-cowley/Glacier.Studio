namespace Glacier.Studio.Tests;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Glacier.Studio.Serialization;
using Glacier.Studio.Services;
using Xunit;

[Trait("Category", "Unit")]
public class GlacierStudioStateTests
{
    private static GlacierStudioState CreateIsolatedState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "glacier_test_state_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return new GlacierStudioState(tempDir);
    }

    [Fact]
    public void StudioState_Initialization_HasDefaultActiveSessionAndIdleStatus()
    {
        var state = CreateIsolatedState();

        Assert.NotNull(state.ActiveSession);
        Assert.Single(state.Sessions);
        Assert.Equal(AgentExecutionStatus.Idle, state.ExecutionStatus);
        Assert.Equal("Ready", state.StatusText);
        Assert.False(state.IsExecutingTurn);
    }

    [Fact]
    public void StudioState_CreateAndSwitchSession_UpdatesActiveSession()
    {
        var state = CreateIsolatedState();
        var initialSession = state.ActiveSession;

        state.CreateNewSession();

        Assert.Equal(2, state.Sessions.Count);
        Assert.NotEqual(initialSession.Id, state.ActiveSession.Id);

        // Switch back to initial session
        state.SwitchSession(initialSession.Id);
        Assert.Equal(initialSession.Id, state.ActiveSession.Id);
    }

    [Fact]
    public void StudioState_DeleteSession_RemovesSessionAndMaintainsValidActive()
    {
        var state = CreateIsolatedState();
        state.CreateNewSession();
        var sessionToDelete = state.ActiveSession;

        Assert.Equal(2, state.Sessions.Count);

        state.DeleteSession(sessionToDelete.Id);

        Assert.Single(state.Sessions);
        Assert.NotEqual(sessionToDelete.Id, state.ActiveSession.Id);
    }

    [Fact]
    public void StudioState_NotifyStateChanged_FiresEvent()
    {
        var state = CreateIsolatedState();
        int eventFiredCount = 0;

        state.OnStateChanged += () => eventFiredCount++;

        state.StatusText = "Working...";
        state.NotifyStateChanged();

        state.ExecutionStatus = AgentExecutionStatus.Thinking;
        state.NotifyStateChanged();

        Assert.Equal(2, eventFiredCount);
    }

    [Fact]
    public void RuntimeLauncher_ResolveSleepBaseUrl_StripsV1SuffixOrFallsBackToPort()
    {
        Assert.Equal("http://127.0.0.1:8080", GlacierRuntimeLauncherService.ResolveSleepBaseUrl("http://127.0.0.1:8080/v1/", 5055));
        Assert.Equal("http://127.0.0.1:5055", GlacierRuntimeLauncherService.ResolveSleepBaseUrl("", 5055));
    }

    [Fact]
    public void RuntimeLauncher_ApplySleepStatusJson_UpdatesTelemetryFields()
    {
        var state = CreateIsolatedState();
        var console = new GlacierConsoleService();
        using var launcher = new GlacierRuntimeLauncherService(
            state,
            console,
            workspaceRoot: Path.GetTempPath(),
            startBackgroundProber: false,
            autoDiscover: false,
            autoProbe: false);

        launcher.ApplySleepStatusJson("""
            {"enabled":true,"state":"Idle","completed_cycles":3,"total_dreams_consolidated":12,"pending_dreams":2}
            """);

        Assert.True(launcher.SleepEnabled);
        Assert.Equal("Idle", launcher.SleepState);
        Assert.Equal(3, launcher.SleepCompletedCycles);
        Assert.Equal(12, launcher.SleepTotalDreamsConsolidated);
        Assert.Equal(2, launcher.SleepPendingDreams);
        Assert.Contains("Cycles: 3", launcher.SleepStatusDetail);
    }

    [Fact]
    public void RuntimeLauncher_BuildInferenceServeArguments_AppendsSleepFlagsWhenEnabled()
    {
        string baseArgs = GlacierRuntimeLauncherService.BuildInferenceServeArguments(
            "model.gguf", 5055, "auto", "baremetal", 8192);
        Assert.DoesNotContain("--sleep-memory", baseArgs);

        string sleepArgs = GlacierRuntimeLauncherService.BuildInferenceServeArguments(
            "model.gguf", 5055, "auto", "baremetal", 8192,
            splitArg: " --split auto",
            enableSleepMemory: true,
            sleepCheckpointPath: @"C:\models\sleep.gmoe");
        Assert.Contains("--split auto --sleep-memory --sleep-checkpoint \"C:\\models\\sleep.gmoe\"", sleepArgs);
    }

    [Fact]
    public void MarkdownRendererAndDocPreview_SanitizeDangerousUrls_AndRejectPathTraversal()
    {
        string md = "[xss](javascript:alert(1)) ![bad](data:text/html,<svg>) [ok](https://example.com)";
        string rendered = GlacierMarkdownRenderer.Render(md);
        Assert.DoesNotContain("javascript:", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://example.com\"", rendered);

        string tempRoot = Path.Combine(Path.GetTempPath(), "glacier_doc_sec_" + Guid.NewGuid().ToString("N"));
        string outsideFile = Path.Combine(Path.GetTempPath(), $"glacier_outside_{Guid.NewGuid():N}.md");
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(outsideFile, "# Secret Outside Workspace");
        try
        {
            var preview = new GlacierDocTreePreviewService(tempRoot);
            string relEscape = Path.GetRelativePath(tempRoot, outsideFile);
            preview.LoadFile(relEscape);
            Assert.Equal("Access Denied", preview.ActiveDocument.Title);
            Assert.DoesNotContain("Secret Outside Workspace", preview.ActiveDocument.RawContent);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
            try { File.Delete(outsideFile); } catch { }
        }
    }

    [Fact]
    public void RuntimeLauncher_GetDistributedCsprojPath_TargetsGlacierCluster()
    {
        var state = CreateIsolatedState();
        var console = new GlacierConsoleService();
        using var launcher = new GlacierRuntimeLauncherService(
            state,
            console,
            workspaceRoot: Path.GetTempPath(),
            startBackgroundProber: false,
            autoDiscover: false,
            autoProbe: false);

        string distPath = launcher.GetDistributedCsprojPath();
        Assert.Equal(launcher.GetClusterCsprojPath(), distPath);
        Assert.EndsWith(Path.Combine("Glacier.Cluster", "src", "Glacier.Cluster", "Glacier.Cluster.csproj"), distPath);
    }

    [Fact]
    public async Task RuntimeLauncher_ProbeDistributedServerAsync_ProbesHttpEvenWhenStandaloneCliAbsent()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_cluster_probe_").FullName;
        try
        {
            using var handler = new StubClusterHttpHandler((req, _) =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"data":[{"id":"qwen-cluster-14b"}]}""", Encoding.UTF8, "application/json")
                    });
                }
                if (path.EndsWith("/v1/cluster/status", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"stages":[{"stage_index":0,"role":"coordinator","endpoint":"127.0.0.1:8080","start_layer":0,"end_layer":19,"layer_count":20,"device_name":"RTX 4060"}]}""", Encoding.UTF8, "application/json")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var http = new HttpClient(handler);

            var state = CreateIsolatedState();
            var console = new GlacierConsoleService();
            using var launcher = new GlacierRuntimeLauncherService(
                state,
                console,
                workspaceRoot: tempWs,
                probeClient: http,
                startBackgroundProber: false,
                autoDiscover: false,
                autoProbe: false);

            Assert.False(launcher.IsDistributedProjectPresent);
            bool online = await launcher.ProbeDistributedServerAsync();

            Assert.True(online);
            Assert.Equal(RuntimeHealthState.Online, launcher.DistributedState);
            Assert.Equal("qwen-cluster-14b", launcher.DistributedActiveModel);
            Assert.Single(launcher.DistributedClusterStages);
            Assert.Equal("RTX 4060", launcher.DistributedClusterStages[0].DeviceName);
        }
        finally
        {
            if (Directory.Exists(tempWs)) Directory.Delete(tempWs, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeLauncher_StartDistributedServerAsync_WorkerWithoutCli_FailsCleanlyOffline()
    {
        string tempWs = Directory.CreateTempSubdirectory("glacier_cluster_worker_").FullName;
        try
        {
            var state = CreateIsolatedState();
            var console = new GlacierConsoleService();
            using var launcher = new GlacierRuntimeLauncherService(
                state,
                console,
                workspaceRoot: tempWs,
                startBackgroundProber: false,
                autoDiscover: false,
                autoProbe: false);

            launcher.DistributedRole = "worker";
            bool started = await launcher.StartDistributedServerAsync();

            Assert.False(started);
            Assert.Equal(RuntimeHealthState.Offline, launcher.DistributedState);
            Assert.Contains("Glacier.Cluster", launcher.DistributedStatusDetail);
        }
        finally
        {
            if (Directory.Exists(tempWs)) Directory.Delete(tempWs, recursive: true);
        }
    }

    [Fact]
    public async Task StudioState_SgJsonAndEncryptedSecrets_RoundtripAndNeverStorePlaintext()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "glacier_studio_sec_" + Guid.NewGuid().ToString("N"));
        string keyFile = Path.Combine(tempRoot, "local.key");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var state = new GlacierStudioState(tempRoot);
            state.SetUiZoomPercent(120);
            var session = state.CreateNewSession("SG-JSON Session");
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Hello SG-JSON" });
            await state.SaveSessionToDiskAsync(session);

            string exported = state.ExportActiveSessionAsJson();
            var roundtrippedExport = System.Text.Json.JsonSerializer.Deserialize(exported, StudioJsonContext.Default.StudioSession);
            Assert.NotNull(roundtrippedExport);
            Assert.Equal("SG-JSON Session", roundtrippedExport.Title);

            var reloaded = new GlacierStudioState(tempRoot);
            Assert.Equal(120, reloaded.UiZoomPercent);
            Assert.Contains(reloaded.Sessions, s => s.Id == session.Id && s.Title == "SG-JSON Session");

            const string dummySecret = "not-a-real-secret-unit-test-value";
            state.SaveProviderApiKey(ModelServerProvider.RemoteOpenAI, dummySecret, keyFile);
            string secretsDiskText = File.ReadAllText(Path.Combine(tempRoot, ".glacier", "studio", "secrets.enc.json"));
            Assert.DoesNotContain(dummySecret, secretsDiskText, StringComparison.Ordinal);
            Assert.Equal(dummySecret, reloaded.LoadProviderApiKey(ModelServerProvider.RemoteOpenAI, keyFile));

            string aesCipher = StudioSecretProtector.EncryptAesGcm(dummySecret, keyFile);
            Assert.Equal(dummySecret, StudioSecretProtector.DecryptAesGcm(aesCipher, keyFile));

            byte[] raw = Convert.FromBase64String(aesCipher);
            raw[^1] ^= 0xFF;
            Assert.Equal("", StudioSecretProtector.DecryptAesGcm(Convert.ToBase64String(raw), keyFile));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ConsoleServiceAndLauncher_CrossPlatformShellAndExePaths()
    {
        var (psArgs, isPs) = GlacierConsoleService.BuildShellInvocation("pwsh.exe", "echo hi");
        Assert.True(isPs);
        Assert.Contains("-EncodedCommand", psArgs);

        var (shArgs, isShPs) = GlacierConsoleService.BuildShellInvocation("/bin/bash", "echo hi");
        Assert.False(isShPs);
        Assert.Equal(new[] { "-c", "echo hi" }, shArgs);

        var state = CreateIsolatedState();
        var console = new GlacierConsoleService();
        using var launcher = new GlacierRuntimeLauncherService(
            state,
            console,
            workspaceRoot: Path.GetTempPath(),
            startBackgroundProber: false,
            autoDiscover: false,
            autoProbe: false);

        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith(".exe", launcher.GetInferenceExePath(), StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".exe", launcher.GetDistributedExePath(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain(".exe", launcher.GetInferenceExePath(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".exe", launcher.GetDistributedExePath(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WorkspaceGraph_IngestFileContent_UsesCSharpLexerAndIgnoresCommentsAndStrings()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "glacier_studio_ast_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var graphSvc = new GlacierWorkspaceGraphService(
                tempRoot,
                new WorkspaceRagOptions { EnableNpuEmbeddings = false });

            string sampleCs = """
                namespace Demo.Ast;

                // class FakeCommentClass { public void FakeCommentMethod() {} }
                /*
                   interface IFakeBlockInterface {}
                */
                public interface IRealService
                {
                    void Run();
                }

                public sealed class RealEngine : IRealService
                {
                    private const string Snippet = "public class FakeStringClass {}";
                    public void Run() { }
                }
                """;

            string csPath = Path.Combine(tempRoot, "RealEngine.cs");
            var discovered = graphSvc.IngestFileContent(csPath, sampleCs);

            Assert.Contains(discovered, d => d.Contains("interface IRealService", StringComparison.Ordinal));
            Assert.Contains(discovered, d => d.Contains("class RealEngine", StringComparison.Ordinal));
            Assert.Contains(discovered, d => d.Contains("method Run()", StringComparison.Ordinal));

            Assert.DoesNotContain(discovered, d => d.Contains("FakeCommentClass", StringComparison.Ordinal));
            Assert.DoesNotContain(discovered, d => d.Contains("FakeCommentMethod", StringComparison.Ordinal));
            Assert.DoesNotContain(discovered, d => d.Contains("IFakeBlockInterface", StringComparison.Ordinal));
            Assert.DoesNotContain(discovered, d => d.Contains("FakeStringClass", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class StubClusterHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
