namespace Glacier.Studio.Tests;

using System;
using System.IO;
using System.Linq;
using Glacier.Showcase.Services.GlacierStudio;
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
}
