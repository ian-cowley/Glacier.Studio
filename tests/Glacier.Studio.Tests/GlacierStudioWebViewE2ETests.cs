namespace Glacier.Studio.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

[Trait("Category", "E2E")]
public class GlacierStudioWebViewE2ETests
{
    private static string? FindBuiltStudioExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string? repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        string releaseExe = Path.Combine(repoRoot, "src", "Glacier.Studio", "bin", "Release", "net10.0", "Glacier.Studio.exe");
        if (File.Exists(releaseExe)) return releaseExe;
        string debugExe = Path.Combine(repoRoot, "src", "Glacier.Studio", "bin", "Debug", "net10.0", "Glacier.Studio.exe");
        return File.Exists(debugExe) ? debugExe : null;
    }

    [Fact]
    public async Task DesktopApp_Playwright_WebView2_WalksTabsModalsAndControls()
    {
        if (Environment.GetEnvironmentVariable("GLACIER_RUN_E2E") != "1")
        {
            return;
        }

        string? exePath = FindBuiltStudioExe();
        Assert.False(string.IsNullOrEmpty(exePath), "Glacier.Studio.exe not found. Build Release or Debug first.");

        const int cdpPort = 9229;
        string userDataDir = Path.Combine(Path.GetTempPath(), "glacier_wv2_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDir);

        var psi = new ProcessStartInfo
        {
            FileName = exePath!,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false
        };
        psi.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = $"--remote-debugging-port={cdpPort}";
        psi.Environment["WEBVIEW2_USER_DATA_FOLDER"] = userDataDir;

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);

        try
        {
            using var probe = new HttpClient();
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    var resp = await probe.GetAsync($"http://127.0.0.1:{cdpPort}/json/version");
                    if (resp.IsSuccessStatusCode) break;
                }
                catch { }
                await Task.Delay(250);
            }

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{cdpPort}");
            var page = browser.Contexts[0].Pages[0];
            await page.WaitForSelectorAsync("button.gs-new-chat-btn");

            // 1. Sidebar: New Thread + Search + Clear
            await page.ClickAsync("button.gs-new-chat-btn");
            await page.FillAsync("input.gs-session-search-input", "New");
            await page.ClickAsync("button.gs-search-clear-btn");

            // 2. Settings Modal: All 4 tabs + endpoint validation + close
            await page.ClickAsync("button.gs-settings-trigger-btn");
            await page.WaitForSelectorAsync("div.gs-modal-card");
            var modalTabs = page.Locator("button.gs-modal-nav-item");
            int tabCount = await modalTabs.CountAsync();
            Assert.Equal(4, tabCount);
            await page.FillAsync("input[placeholder='http://127.0.0.1:5055/v1']", "invalid-url");
            await page.ClickAsync("button.gs-btn-test-conn");
            for (int t = 1; t < tabCount; t++)
            {
                await modalTabs.Nth(t).ClickAsync();
            }
            await page.ClickAsync("button.gs-modal-close-btn");

            // 3. Right Drawer: All 4 tabs + Sleep Memory checkbox
            var auxTabs = page.Locator("button.gs-aux-tab");
            Assert.Equal(4, await auxTabs.CountAsync());
            for (int t = 0; t < 4; t++)
            {
                await auxTabs.Nth(t).ClickAsync();
            }
            await page.Locator("label:has-text('--sleep-memory') input[type='checkbox']").CheckAsync();
            await page.WaitForSelectorAsync("input[placeholder*='--sleep-checkpoint']");
        }
        finally
        {
            try
            {
                if (!proc!.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { }
            try
            {
                if (Directory.Exists(userDataDir)) Directory.Delete(userDataDir, recursive: true);
            }
            catch { }
        }
    }
}
