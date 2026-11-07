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
}
