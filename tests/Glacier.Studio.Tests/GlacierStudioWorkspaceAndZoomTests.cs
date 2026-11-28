namespace Glacier.Studio.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Glacier.Studio.Services;
using Glacier.Studio.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GlacierStudioWorkspaceAndZoomTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _projectA;
    private readonly string _projectB;

    public GlacierStudioWorkspaceAndZoomTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "glacier_studio_wz_" + Guid.NewGuid().ToString("N"));
        _projectA = Path.Combine(_rootDir, "ProjectAlpha");
        _projectB = Path.Combine(_rootDir, "ProjectBeta");
        Directory.CreateDirectory(_projectA);
        Directory.CreateDirectory(_projectB);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDir))
                Directory.Delete(_rootDir, recursive: true);
        }
        catch { }
    }

    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoOpHttpHandler());
    }

    private TestContext CreateTestContext(GlacierStudioState state, IGlacierFolderPicker? folderPicker = null)
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = new StubHttpClientFactory();
        var consoleSvc = new GlacierConsoleService(state.ActiveWorkspaceRoot);
        var graphSvc = new GlacierWorkspaceGraphService(state.ActiveWorkspaceRoot);
        var docSvc = new GlacierDocTreePreviewService();
        var mcpMgr = new GlacierMcpManager(state.ActiveWorkspaceRoot);
        var launcher = new GlacierRuntimeLauncherService(state, consoleSvc);
        var agent = new GlacierAgentEngine(factory.CreateClient("stub"), state, consoleSvc, docSvc, mcpMgr, graphSvc, launcher);

        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(consoleSvc);
        ctx.Services.AddSingleton(graphSvc);
        ctx.Services.AddSingleton(docSvc);
        ctx.Services.AddSingleton(mcpMgr);
        ctx.Services.AddSingleton(launcher);
        ctx.Services.AddSingleton(agent);
        ctx.Services.AddSingleton<IGlacierFolderPicker>(folderPicker ?? new GlacierFolderPickerService());
        ctx.Services.AddSingleton<IHttpClientFactory>(factory);
        return ctx;
    }

    [Fact]
    public void SwitchProject_ValidatesDirectoryAndPersistsPreferences()
    {
        var state = new GlacierStudioState(_projectA);

        Assert.False(state.SwitchProject(Path.Combine(_rootDir, "DoesNotExist")));
        Assert.True(state.SwitchProject(_projectB));
        Assert.Equal(_projectB, state.ActiveWorkspaceRoot);
        Assert.Equal(2, state.Projects.Count);

        // Reload state from same root -> preferences persist
        var reloaded = new GlacierStudioState(_projectA);
        Assert.Equal(_projectB, reloaded.ActiveWorkspaceRoot);
        Assert.Contains(_projectB, reloaded.Projects);

        // Cannot remove the last remaining project
        Assert.True(reloaded.RemoveProject(_projectB));
        Assert.False(reloaded.RemoveProject(_projectA));
    }

    [Fact]
    public void Session_TracksAndRestoresWorkspaceRoot()
    {
        var state = new GlacierStudioState(_projectA);
        var sessionA = state.ActiveSession;
        Assert.Equal(_projectA, sessionA.WorkspaceRoot);

        Assert.True(state.SwitchProject(_projectB));
        var sessionB = state.CreateNewSession("Beta Thread");
        Assert.Equal(_projectB, sessionB.WorkspaceRoot);

        state.SwitchSession(sessionA.Id);
        Assert.Equal(_projectA, state.ActiveWorkspaceRoot);
    }

    [Fact]
    public void UiZoom_ClampsStepsAndPersists()
    {
        var state = new GlacierStudioState(_projectA);
        Assert.Equal(100, state.UiZoomPercent);

        state.ZoomIn();
        Assert.Equal(110, state.UiZoomPercent);
        state.SetUiZoomPercent(500);
        Assert.Equal(175, state.UiZoomPercent);
        state.SetUiZoomPercent(10);
        Assert.Equal(75, state.UiZoomPercent);
        state.ResetZoom();
        Assert.Equal(100, state.UiZoomPercent);

        state.ZoomIn();
        var reloaded = new GlacierStudioState(_projectA);
        Assert.Equal(110, reloaded.UiZoomPercent);
    }

    [Fact]
    public void GraphService_SetWorkspaceRoot_UpdatesRoot()
    {
        var graph = new GlacierWorkspaceGraphService(_projectA);
        Assert.Equal(_projectA, graph.WorkspaceRoot);

        graph.SetWorkspaceRoot(_projectB);
        Assert.Equal(_projectB, graph.WorkspaceRoot);
    }

    [Fact]
    public void Ui_ProjectsSwitcherAndZoomControls_UpdateStateAndDom()
    {
        var state = new GlacierStudioState(_projectA);
        using var ctx = CreateTestContext(state);
        var cut = ctx.RenderComponent<GlacierStudioMain>();

        // Open project path input and switch to ProjectBeta
        cut.Find(".gs-project-add-btn").Click();
        cut.Find(".gs-project-path-input").Input(_projectB);
        cut.Find(".gs-project-confirm-btn").Click();

        Assert.Equal(_projectB, state.ActiveWorkspaceRoot);
        Assert.Contains("ProjectBeta", cut.Find(".gs-workspace-pill").TextContent);

        // Zoom in, zoom out, reset via sidebar zoom controls
        cut.Find("button[title='Zoom In (+10%)']").Click();
        Assert.Equal("110", cut.Find(".gs-container").GetAttribute("data-ui-zoom"));
        Assert.Equal("110%", cut.Find(".gs-zoom-value").TextContent.Trim());

        cut.Find("button[title='Zoom Out (-10%)']").Click();
        Assert.Equal("100", cut.Find(".gs-container").GetAttribute("data-ui-zoom"));
    }

    [Fact]
    public void Ui_NativeFolderPicker_SwitchesProjectDirectlyOnClick()
    {
        var state = new GlacierStudioState(_projectA);
        var picker = new GlacierFolderPickerService();
        picker.SetNativePicker(_ => _projectB);

        using var ctx = CreateTestContext(state, picker);
        var cut = ctx.RenderComponent<GlacierStudioMain>();

        cut.Find(".gs-project-add-btn").Click();

        Assert.Equal(_projectB, state.ActiveWorkspaceRoot);
        Assert.Contains("ProjectBeta", cut.Find(".gs-workspace-pill").TextContent);
        Assert.Empty(cut.FindAll(".gs-project-path-input"));
    }
}
