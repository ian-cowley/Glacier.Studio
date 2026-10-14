using System;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Glacier.Showcase.Services.GlacierStudio;

namespace Glacier.Studio;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Ensure wwwroot exists in the application base directory
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string targetWwwroot = Path.Combine(baseDir, "wwwroot");

        if (!Directory.Exists(targetWwwroot))
        {
            Directory.CreateDirectory(targetWwwroot);
        }
        string targetIndex = Path.Combine(targetWwwroot, "index.html");

        // Always sync wwwroot from source location during development if present
        string sourceWwwroot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot"));
        if (Directory.Exists(sourceWwwroot))
        {
            foreach (var file in Directory.GetFiles(sourceWwwroot, "*.*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceWwwroot, file);
                string dest = Path.Combine(targetWwwroot, relative);
                string? destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                File.Copy(file, dest, overwrite: true);
            }
        }
        else if (!File.Exists(targetIndex))
            {
                // Fallback default index.html
                File.WriteAllText(targetIndex, @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>Glacier Agent Studio</title>
    <base href=""/"" />
    <link href=""app.css"" rel=""stylesheet"" />
</head>
<body style=""background-color: #080c14; margin: 0; padding: 0; overflow: hidden;"">
    <div id=""app"">Loading Glacier Studio...</div>
    <script src=""_framework/blazor.webview.js""></script>
</body>
</html>");
            }
        // 2. Build Photino Blazor Native Desktop App
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

        // Core HTTP Services (configured for long-running SSE streams)
        appBuilder.Services.AddHttpClient("GlacierAgent", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        // Glacier Studio Production Services
        appBuilder.Services.AddSingleton<GlacierConsoleService>();
        appBuilder.Services.AddSingleton<GlacierMcpManager>();
        appBuilder.Services.AddSingleton<GlacierStudioState>();
        appBuilder.Services.AddSingleton<GlacierDocTreePreviewService>();
        appBuilder.Services.AddSingleton<GlacierWorkspaceGraphService>();
        appBuilder.Services.AddSingleton<GlacierRuntimeLauncherService>();
        appBuilder.Services.AddSingleton<GlacierAgentEngine>(sp =>
            new GlacierAgentEngine(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("GlacierAgent"),
                sp.GetRequiredService<GlacierStudioState>(),
                sp.GetRequiredService<GlacierConsoleService>(),
                sp.GetRequiredService<GlacierDocTreePreviewService>(),
                sp.GetRequiredService<GlacierMcpManager>(),
                sp.GetRequiredService<GlacierWorkspaceGraphService>()));

        // Root Component
        appBuilder.RootComponents.Add<App>("#app");

        var app = appBuilder.Build();

        // Native OS Desktop Window Configuration (Antigravity 2 Style)
        app.MainWindow
            .SetTitle("Glacier Agent Studio — Antigravity 2")
            .SetUseOsDefaultSize(false)
            .SetSize(1480, 940)
            .SetMinSize(1080, 720)
            .Center();

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
            {
                File.WriteAllText(Path.Combine(baseDir, "studio_unhandled.log"), error.ExceptionObject?.ToString());
            };

            app.Run();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(baseDir, "studio_crash.log"), ex.ToString());
            throw;
        }
    }
}
