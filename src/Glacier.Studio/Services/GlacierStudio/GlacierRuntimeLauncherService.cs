using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Glacier.Showcase.Services.GlacierStudio;

public enum RuntimeHealthState
{
    Offline,
    Starting,
    Online,
    MissingProject
}

public sealed record DiscoveredGgufModel(string DisplayName, string FullPath, double SizeGb);

public sealed record ClusterStageStatusItem(
    int StageIndex,
    string Role,
    string Endpoint,
    int StartLayer,
    int EndLayer,
    int LayerCount,
    string DeviceName);

public sealed class GlacierRuntimeLauncherService : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly GlacierStudioState _studioState;
    private readonly GlacierConsoleService _consoleService;
    private readonly HttpClient _probeClient;

    private Process? _inferenceProcess;
    private Process? _distributedProcess;

    // 1. Glacier.Inference (Single-Node Bare-Metal GPU Server)
    public int InferencePort { get; set; } = 5055;
    public string InferenceDevice { get; set; } = "nvidia-rtx-4060";
    public string InferenceEngine { get; set; } = "baremetal";
    public int InferenceContextSize { get; set; } = 8192;
    public string SelectedGgufPath { get; set; } = "";
    public RuntimeHealthState InferenceState { get; private set; } = RuntimeHealthState.Offline;
    public string InferenceActiveModel { get; private set; } = "";
    public long InferenceLatencyMs { get; private set; }
    public string InferenceStatusDetail { get; private set; } = "Unchecked";

    // 2. Glacier.Inference.Distributed (Multi-Node Cluster Coordinator / Worker)
    public string DistributedRole { get; set; } = "coordinator"; // "coordinator" or "worker"
    public string DistributedStages { get; set; } = "127.0.0.1:5056"; // --stages / --nodes
    public string DistributedLayers { get; set; } = "auto"; // --layers / --alloc ("auto" or "8,14,6")
    public int DistributedHostGpu { get; set; } = -1; // --host-gpu / --adapter (-1 = auto fastest VRAM)
    public string DistributedHttpHost { get; set; } = "0.0.0.0"; // --host
    public int DistributedHttpPort { get; set; } = 8080; // --http-port
    public int DistributedWorkerPort { get; set; } = 5056; // --port (worker TCP port)
    public string DistributedCacheDir { get; set; } = "./models/cache"; // --cache
    public int DistributedMaxTokens { get; set; } = -1; // --tokens (-1 = unlimited)
    public string NewNodeEndpointInput { get; set; } = "";

    public RuntimeHealthState DistributedState { get; private set; } = RuntimeHealthState.Offline;
    public string DistributedActiveModel { get; private set; } = "";
    public long DistributedLatencyMs { get; private set; }
    public string DistributedStatusDetail { get; private set; } = "Unchecked";
    public IReadOnlyList<ClusterStageStatusItem> DistributedClusterStages => _distributedClusterStages;
    private readonly List<ClusterStageStatusItem> _distributedClusterStages = new();

    public IReadOnlyList<DiscoveredGgufModel> AvailableModels => _availableModels;
    private readonly List<DiscoveredGgufModel> _availableModels = new();

    public bool IsInferenceProjectPresent => File.Exists(GetInferenceCsprojPath());
    public bool IsDistributedProjectPresent => File.Exists(GetDistributedCsprojPath());

    public event Action? OnRuntimeStateChanged;

    public IReadOnlyList<string> GetConfiguredNodes()
    {
        if (string.IsNullOrWhiteSpace(DistributedStages))
            return Array.Empty<string>();

        return DistributedStages
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    public void AddDistributedNode(string? endpoint = null)
    {
        string target = (endpoint ?? NewNodeEndpointInput)?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(target)) return;
        if (!target.Contains(':'))
        {
            target += ":5056";
        }

        var nodes = GetConfiguredNodes().ToList();
        if (!nodes.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(target);
            DistributedStages = string.Join(",", nodes);
        }

        if (endpoint == null)
        {
            NewNodeEndpointInput = "";
        }
        NotifyChanged();
    }

    public void RemoveDistributedNode(string endpoint)
    {
        var nodes = GetConfiguredNodes()
            .Where(n => !string.Equals(n, endpoint, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DistributedStages = string.Join(",", nodes);
        NotifyChanged();
    }

    public void ApplyDistributedNodesPreset(string preset)
    {
        DistributedStages = preset switch
        {
            "local-1" => "127.0.0.1:5056",
            "local-2" => "127.0.0.1:5056,127.0.0.1:5057",
            "lan-3" => "192.168.1.108:5055,192.168.1.101:5056,192.168.1.200:5057",
            "host-only" => "",
            _ => DistributedStages
        };
        NotifyChanged();
    }

    public string BuildDistributedCliArgs()
    {
        string cacheArg = !string.IsNullOrWhiteSpace(DistributedCacheDir)
            ? $" --cache \"{DistributedCacheDir.Trim()}\""
            : "";

        if (DistributedRole == "worker")
        {
            return $"--role worker --port {DistributedWorkerPort}{cacheArg}";
        }

        string modelName = !string.IsNullOrWhiteSpace(SelectedGgufPath) ? SelectedGgufPath : "<model.gguf>";
        string stagesArg = !string.IsNullOrWhiteSpace(DistributedStages)
            ? $" --stages \"{DistributedStages.Trim()}\""
            : "";
        string layersArg = !string.IsNullOrWhiteSpace(DistributedLayers)
            ? $" --layers \"{DistributedLayers.Trim()}\""
            : " --layers auto";
        string hostGpuArg = $" --host-gpu {DistributedHostGpu}";
        string hostBindArg = !string.IsNullOrWhiteSpace(DistributedHttpHost)
            ? $" --host {DistributedHttpHost.Trim()}"
            : " --host 0.0.0.0";
        string tokensArg = DistributedMaxTokens > 0
            ? $" --tokens {DistributedMaxTokens}"
            : "";

        return $"serve --role coordinator --model \"{modelName}\"{stagesArg}{layersArg}{hostGpuArg} --http-port {DistributedHttpPort}{hostBindArg}{cacheArg}{tokensArg}";
    }

    public GlacierRuntimeLauncherService(
        GlacierStudioState studioState,
        GlacierConsoleService consoleService,
        string? workspaceRoot = null)
    {
        _studioState = studioState;
        _consoleService = consoleService;
        _workspaceRoot = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : GlacierWorkspaceResolver.ResolveWorkspaceRoot();

        _probeClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(1500)
        };

        DiscoverLocalGgufModels();
        _ = ProbeAllRuntimesAsync();
    }

    public string GetInferenceCsprojPath() =>
        Path.Combine(_workspaceRoot, "Glacier.Inference", "src", "Glacier.Inference.Cli", "Glacier.Inference.Cli.csproj");

    public string GetInferenceExePath()
    {
        string debugExe = Path.Combine(_workspaceRoot, "Glacier.Inference", "src", "Glacier.Inference.Cli", "bin", "Debug", "net10.0", "glacier.exe");
        if (File.Exists(debugExe)) return debugExe;

        string releaseExe = Path.Combine(_workspaceRoot, "Glacier.Inference", "src", "Glacier.Inference.Cli", "bin", "Release", "net10.0", "glacier.exe");
        return File.Exists(releaseExe) ? releaseExe : debugExe;
    }

    public string GetDistributedCsprojPath() =>
        Path.Combine(_workspaceRoot, "Glacier.Inference.Distributed", "src", "Glacier.Inference.Distributed.Cli", "Glacier.Inference.Distributed.Cli.csproj");

    public string GetDistributedExePath()
    {
        string cliDir = Path.Combine(_workspaceRoot, "Glacier.Inference.Distributed", "src", "Glacier.Inference.Distributed.Cli", "bin");
        string[] candidates =
        [
            Path.Combine(cliDir, "Debug", "net10.0", "glacier-distributed.exe"),
            Path.Combine(cliDir, "Debug", "net10.0", "Glacier.Inference.Distributed.Cli.exe"),
            Path.Combine(cliDir, "Release", "net10.0", "glacier-distributed.exe"),
            Path.Combine(cliDir, "Release", "net10.0", "Glacier.Inference.Distributed.Cli.exe")
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return candidates[0];
    }

    public void DiscoverLocalGgufModels()
    {
        _availableModels.Clear();
        var searchDirs = new List<string>
        {
            @"D:\lmstudio\models",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models"),
            Path.Combine(_workspaceRoot, "Glacier.Inference", "models"),
            Path.Combine(_workspaceRoot, "models")
        };

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories))
                {
                    if (!seenPaths.Add(file)) continue;
                    var info = new FileInfo(file);
                    // Skip tiny test fixture files (< 50 MB)
                    if (info.Length < 50L * 1024 * 1024) continue;

                    double sizeGb = info.Length / (1024.0 * 1024.0 * 1024.0);
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    _availableModels.Add(new DiscoveredGgufModel($"{fileName} ({sizeGb:F1} GB)", file, sizeGb));
                }
            }
            catch
            {
                // Ignore inaccessible directories
            }
        }

        // Order so Qwen3-4B-Instruct (fast GPU SASS) comes first, followed by Qwen2.5-Coder-7B, then smaller models that fit in VRAM
        _availableModels.Sort((a, b) =>
        {
            static int GetModelPriority(DiscoveredGgufModel m)
            {
                if (m.DisplayName.Contains("Qwen3-4B-Instruct", StringComparison.OrdinalIgnoreCase)) return 0;
                if (m.DisplayName.Contains("Qwen3-4B", StringComparison.OrdinalIgnoreCase)) return 1;
                if (m.DisplayName.Contains("Qwen2.5-7B-Instruct", StringComparison.OrdinalIgnoreCase) && m.SizeGb <= 6.0) return 2;
                if (m.DisplayName.Contains("qwen2.5-coder-7b", StringComparison.OrdinalIgnoreCase)) return 3;
                if (m.SizeGb <= 6.0) return 4;
                if (m.SizeGb <= 8.0) return 5;
                return 6;
            }

            int pA = GetModelPriority(a);
            int pB = GetModelPriority(b);
            if (pA != pB) return pA.CompareTo(pB);
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        if (string.IsNullOrEmpty(SelectedGgufPath) && _availableModels.Count > 0)
        {
            SelectedGgufPath = _availableModels[0].FullPath;
        }
    }

    public async Task ProbeAllRuntimesAsync()
    {
        await Task.WhenAll(
            ProbeInferenceServerAsync(),
            ProbeDistributedServerAsync());
        NotifyChanged();
    }

    public async Task<bool> ProbeInferenceServerAsync()
    {
        if (!IsInferenceProjectPresent)
        {
            InferenceState = RuntimeHealthState.MissingProject;
            InferenceStatusDetail = "Project not found in workspace";
            return false;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _probeClient.GetAsync($"http://127.0.0.1:{InferencePort}/v1/models");
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                string modelId = TryParseFirstModelId(json) ?? "active";
                InferenceState = RuntimeHealthState.Online;
                InferenceActiveModel = modelId;
                InferenceLatencyMs = sw.ElapsedMilliseconds;
                InferenceStatusDetail = $"Online · {InferenceLatencyMs} ms · :{InferencePort}";
                return true;
            }
        }
        catch
        {
            // Offline or unreachable
        }

        if (InferenceState != RuntimeHealthState.Starting)
        {
            InferenceState = RuntimeHealthState.Offline;
            InferenceActiveModel = "";
            InferenceStatusDetail = $"Offline (:{InferencePort})";
        }
        return false;
    }

    public async Task<bool> ProbeDistributedServerAsync()
    {
        if (!IsDistributedProjectPresent)
        {
            DistributedState = RuntimeHealthState.MissingProject;
            DistributedStatusDetail = "Project not found in workspace";
            _distributedClusterStages.Clear();
            return false;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _probeClient.GetAsync($"http://127.0.0.1:{DistributedHttpPort}/v1/models");
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                string modelId = TryParseFirstModelId(json) ?? "cluster-model";
                DistributedState = RuntimeHealthState.Online;
                DistributedActiveModel = modelId;
                DistributedLatencyMs = sw.ElapsedMilliseconds;

                await RefreshClusterStagesStatusAsync();
                int stageCount = _distributedClusterStages.Count;
                DistributedStatusDetail = stageCount > 0
                    ? $"Online · {stageCount} stage(s) · {DistributedLatencyMs} ms · :{DistributedHttpPort}"
                    : $"Online · {DistributedLatencyMs} ms · :{DistributedHttpPort}";
                return true;
            }
        }
        catch
        {
            // Offline or unreachable
        }

        if (DistributedState != RuntimeHealthState.Starting)
        {
            DistributedState = RuntimeHealthState.Offline;
            DistributedActiveModel = "";
            _distributedClusterStages.Clear();
            DistributedStatusDetail = $"Offline (:{DistributedHttpPort})";
        }
        return false;
    }

    private async Task RefreshClusterStagesStatusAsync()
    {
        try
        {
            using var resp = await _probeClient.GetAsync($"http://127.0.0.1:{DistributedHttpPort}/v1/cluster/status");
            if (!resp.IsSuccessStatusCode) return;

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("stages", out var stagesArr) || stagesArr.ValueKind != JsonValueKind.Array)
                return;

            _distributedClusterStages.Clear();
            foreach (var item in stagesArr.EnumerateArray())
            {
                int idx = item.TryGetProperty("stage_index", out var pIdx) ? pIdx.GetInt32() : 0;
                string role = item.TryGetProperty("role", out var pRole) ? pRole.GetString() ?? "" : "";
                string ep = item.TryGetProperty("endpoint", out var pEp) ? pEp.GetString() ?? "" : "";
                int startL = item.TryGetProperty("start_layer", out var pStart) ? pStart.GetInt32() : 0;
                int endL = item.TryGetProperty("end_layer", out var pEnd) ? pEnd.GetInt32() : 0;
                int countL = item.TryGetProperty("layer_count", out var pCount) ? pCount.GetInt32() : 0;
                string dev = item.TryGetProperty("device_name", out var pDev) ? pDev.GetString() ?? "" : "";
                _distributedClusterStages.Add(new ClusterStageStatusItem(idx, role, ep, startL, endL, countL, dev));
            }
        }
        catch
        {
            // Ignore status parsing errors
        }
    }

    public async Task<bool> StartInferenceServerAsync()
    {
        if (await ProbeInferenceServerAsync())
        {
            ActivateInferenceInStudio();
            NotifyChanged();
            return true;
        }

        if (string.IsNullOrWhiteSpace(SelectedGgufPath) || !File.Exists(SelectedGgufPath))
        {
            InferenceStatusDetail = "Select a valid .gguf model file first";
            NotifyChanged();
            return false;
        }

        InferenceState = RuntimeHealthState.Starting;
        InferenceStatusDetail = "Building / starting server...";
        NotifyChanged();

        try
        {
            string exePath = GetInferenceExePath();
            if (!File.Exists(exePath))
            {
                _consoleService.AddLog("SYSTEM", "Compiling Glacier.Inference.Cli...");
                await _consoleService.ExecuteCommandAsync($"dotnet build \"{GetInferenceCsprojPath()}\" -c Debug", "SYSTEM");
                exePath = GetInferenceExePath();
            }

            if (!File.Exists(exePath))
            {
                InferenceState = RuntimeHealthState.Offline;
                InferenceStatusDetail = "Build failed: glacier.exe not found";
                NotifyChanged();
                return false;
            }

            string args = $"serve \"{SelectedGgufPath}\" --port {InferencePort} --device {InferenceDevice} --engine {InferenceEngine} --ctx {InferenceContextSize}";
            _consoleService.AddLog("SYSTEM", $"Starting Glacier.Inference: glacier.exe {args}");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _inferenceProcess = Process.Start(psi);
            if (_inferenceProcess != null)
            {
                HookProcessLogs(_inferenceProcess, "INFERENCE");
            }

            // Poll up to 15 seconds for server readiness
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                if (await ProbeInferenceServerAsync())
                {
                    ActivateInferenceInStudio();
                    _consoleService.AddLog("SYSTEM", $"Glacier.Inference is online at http://127.0.0.1:{InferencePort}/v1 (Model: {InferenceActiveModel})");
                    NotifyChanged();
                    return true;
                }
            }

            InferenceState = RuntimeHealthState.Offline;
            InferenceStatusDetail = "Startup timed out (check Terminal logs)";
            NotifyChanged();
            return false;
        }
        catch (Exception ex)
        {
            InferenceState = RuntimeHealthState.Offline;
            InferenceStatusDetail = $"Start error: {ex.Message}";
            _consoleService.AddLog("SYSTEM", $"Failed to start Glacier.Inference: {ex.Message}", isError: true);
            NotifyChanged();
            return false;
        }
    }

    public async Task StopInferenceServerAsync()
    {
        try
        {
            if (_inferenceProcess is { HasExited: false })
            {
                _inferenceProcess.Kill(entireProcessTree: true);
                _inferenceProcess.Dispose();
                _inferenceProcess = null;
            }
            else
            {
                foreach (var proc in Process.GetProcessesByName("glacier"))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
            _consoleService.AddLog("SYSTEM", "Stopped Glacier.Inference server.");
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("SYSTEM", $"Stop warning: {ex.Message}", isError: true);
        }

        await Task.Delay(300);
        InferenceState = RuntimeHealthState.Offline;
        await ProbeInferenceServerAsync();
        NotifyChanged();
    }

    public async Task<bool> StartDistributedServerAsync()
    {
        if (DistributedRole == "coordinator" && await ProbeDistributedServerAsync())
        {
            ActivateDistributedInStudio();
            NotifyChanged();
            return true;
        }

        if (DistributedRole == "coordinator" && (string.IsNullOrWhiteSpace(SelectedGgufPath) || !File.Exists(SelectedGgufPath)))
        {
            DistributedStatusDetail = "Select a valid .gguf model file first";
            NotifyChanged();
            return false;
        }

        DistributedState = RuntimeHealthState.Starting;
        DistributedStatusDetail = $"Starting {DistributedRole}...";
        NotifyChanged();

        try
        {
            string exePath = GetDistributedExePath();
            if (!File.Exists(exePath))
            {
                _consoleService.AddLog("SYSTEM", "Compiling Glacier.Inference.Distributed.Cli...");
                await _consoleService.ExecuteCommandAsync($"dotnet build \"{GetDistributedCsprojPath()}\" -c Debug", "SYSTEM");
                exePath = GetDistributedExePath();
            }

            if (!File.Exists(exePath))
            {
                DistributedState = RuntimeHealthState.Offline;
                DistributedStatusDetail = "Build failed: distributed CLI exe not found";
                NotifyChanged();
                return false;
            }

            string args = BuildDistributedCliArgs();

            _consoleService.AddLog("SYSTEM", $"Starting Glacier.Inference.Distributed ({DistributedRole}): {Path.GetFileName(exePath)} {args}");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _distributedProcess = Process.Start(psi);
            if (_distributedProcess != null)
            {
                HookProcessLogs(_distributedProcess, "CLUSTER");
            }

            if (DistributedRole == "worker")
            {
                await Task.Delay(800);
                if (_distributedProcess is { HasExited: false })
                {
                    DistributedState = RuntimeHealthState.Online;
                    DistributedStatusDetail = $"Worker listening on TCP :{DistributedWorkerPort}";
                    NotifyChanged();
                    return true;
                }
            }

            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                if (await ProbeDistributedServerAsync())
                {
                    ActivateDistributedInStudio();
                    _consoleService.AddLog("SYSTEM", $"Glacier.Inference.Distributed is online at http://127.0.0.1:{DistributedHttpPort}/v1");
                    NotifyChanged();
                    return true;
                }
            }

            DistributedState = RuntimeHealthState.Offline;
            DistributedStatusDetail = "Startup timed out (ensure worker nodes are reachable or check Terminal)";
            NotifyChanged();
            return false;
        }
        catch (Exception ex)
        {
            DistributedState = RuntimeHealthState.Offline;
            DistributedStatusDetail = $"Start error: {ex.Message}";
            _consoleService.AddLog("SYSTEM", $"Failed to start Glacier.Inference.Distributed: {ex.Message}", isError: true);
            NotifyChanged();
            return false;
        }
    }

    public async Task StopDistributedServerAsync()
    {
        try
        {
            if (_distributedProcess is { HasExited: false })
            {
                _distributedProcess.Kill(entireProcessTree: true);
                _distributedProcess.Dispose();
                _distributedProcess = null;
            }
            else
            {
                foreach (var name in new[] { "glacier-distributed", "Glacier.Inference.Distributed.Cli" })
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            _consoleService.AddLog("SYSTEM", "Stopped Glacier.Inference.Distributed runtime.");
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("SYSTEM", $"Stop warning: {ex.Message}", isError: true);
        }

        await Task.Delay(300);
        DistributedState = RuntimeHealthState.Offline;
        await ProbeDistributedServerAsync();
        NotifyChanged();
    }

    public void ActivateInferenceInStudio()
    {
        _studioState.Provider = ModelServerProvider.LocalGlacierInference;
        _studioState.EndpointUrl = $"http://127.0.0.1:{InferencePort}/v1";
        if (!string.IsNullOrEmpty(InferenceActiveModel))
        {
            _studioState.SelectedModel = InferenceActiveModel;
        }
        else if (!string.IsNullOrEmpty(SelectedGgufPath))
        {
            _studioState.SelectedModel = Path.GetFileNameWithoutExtension(SelectedGgufPath);
        }
        _studioState.ActiveSession.ModelSnapshot = _studioState.GetModelDisplayName();
        _studioState.NotifyStateChanged();
    }

    public void ActivateDistributedInStudio()
    {
        _studioState.Provider = ModelServerProvider.LocalGlacierDistributed;
        _studioState.EndpointUrl = $"http://127.0.0.1:{DistributedHttpPort}/v1";
        if (!string.IsNullOrEmpty(DistributedActiveModel))
        {
            _studioState.SelectedModel = DistributedActiveModel;
        }
        else if (!string.IsNullOrEmpty(SelectedGgufPath))
        {
            _studioState.SelectedModel = Path.GetFileNameWithoutExtension(SelectedGgufPath);
        }
        _studioState.ActiveSession.ModelSnapshot = _studioState.GetModelDisplayName();
        _studioState.NotifyStateChanged();
    }

    private void HookProcessLogs(Process proc, string tag)
    {
        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _consoleService.AddLog(tag, e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _consoleService.AddLog(tag, e.Data, isError: true);
            }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
    }

    private static string? TryParseFirstModelId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (first.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private void NotifyChanged()
    {
        OnRuntimeStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _probeClient.Dispose();
    }
}
