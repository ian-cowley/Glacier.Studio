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

public enum CircuitBreakerState
{
    Closed,
    Open,
    HalfOpen
}

public enum AgentTurnTier
{
    LocalFastTool,
    RemoteDeepReasoning
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

public sealed record NodeTelemetryRecord(
    string Endpoint,
    string Model,
    long LatencyMs,
    bool Success,
    DateTime Timestamp,
    string? ErrorMessage = null);

public sealed class GlacierRuntimeLauncherService : IDisposable
{
    // Hardware-aware VRAM budget constraint (RTX 4060 Laptop dedicated usable VRAM limit)
    public const double MaxStandaloneVramBudgetGb = 4.8;
    public double StandaloneVramBudgetGb { get; set; } = MaxStandaloneVramBudgetGb;

    private readonly string _workspaceRoot;
    private readonly GlacierStudioState _studioState;
    private readonly GlacierConsoleService _consoleService;
    private readonly HttpClient _probeClient;
    private readonly bool _ownsProbeClient;

    private Process? _inferenceProcess;
    private Process? _distributedProcess;

    private readonly CancellationTokenSource _healthProbeCts = new();
    private Task? _healthProbeTask;

    // 1. Glacier.Inference (Single-Node Bare-Metal GPU Server)
    public int InferencePort { get; set; } = 5055;
    public string InferenceDevice { get; set; } = "nvidia-rtx-4060";
    public string InferenceEngine { get; set; } = "baremetal";
    public int InferenceContextSize { get; set; } = 8192;
    public string SelectedGgufPath { get; set; } = "";
    public string SelectedLocalGgufPath { get; set; } = "";
    public string SelectedClusterGgufPath { get; set; } = "";
    public RuntimeHealthState InferenceState { get; private set; } = RuntimeHealthState.Offline;
    public string InferenceActiveModel { get; private set; } = "";
    public long InferenceLatencyMs { get; private set; }
    public string InferenceStatusDetail { get; private set; } = "Unchecked";

    // 2. Glacier.Inference.Distributed (Multi-Node Cluster Coordinator / Worker)
    public string DistributedRole { get; set; } = "coordinator"; // "coordinator" or "worker"
    public string DistributedStages { get; set; } = ""; // Local-first dual-GPU (RTX 4060 + Radeon 890M)
    public string DistributedLayers { get; set; } = "20:28"; // 20 on Host RTX 4060 dGPU, 28 on Host Radeon 890M iGPU
    public int DistributedHostGpu { get; set; } = -1; // --host-gpu / --adapter (-1 = auto fastest VRAM)
    public string DistributedHttpHost { get; set; } = "0.0.0.0"; // --host
    public int DistributedHttpPort { get; set; } = 8080; // --http-port
    public int DistributedWorkerPort { get; set; } = 5056; // --port (worker TCP port)
    public string DistributedCacheDir { get; set; } = @"D:\GlacierModels\cache"; // --cache
    public int DistributedMaxTokens { get; set; } = -1; // --tokens (-1 = unlimited)
    public string NewNodeEndpointInput { get; set; } = "";

    public RuntimeHealthState DistributedState { get; private set; } = RuntimeHealthState.Offline;
    public string DistributedActiveModel { get; private set; } = "";
    public long DistributedLatencyMs { get; private set; }
    public string DistributedStatusDetail { get; private set; } = "Unchecked";
    public IReadOnlyList<ClusterStageStatusItem> DistributedClusterStages => _distributedClusterStages;
    private readonly List<ClusterStageStatusItem> _distributedClusterStages = new();

    // 3. Remote Beast Cluster Node (RTX 3060 12GB - Glacier-Architect-Coder-14B-Q4_K_M)
    public string BeastEndpointUrl { get; set; } = "http://192.168.1.108:8080/v1";
    public string BeastDefaultModel { get; set; } = "Glacier-Architect-Coder-14B-Q4_K_M";
    public RuntimeHealthState BeastClusterState { get; private set; } = RuntimeHealthState.Offline;
    public string BeastClusterActiveModel { get; private set; } = "";
    public long BeastClusterLatencyMs { get; private set; }
    public string BeastClusterStatusDetail { get; private set; } = "Unchecked";

    // 4. Circuit Breaker & Real-time Telemetry
    private CircuitBreakerState _remoteNodeState = CircuitBreakerState.Closed;
    public CircuitBreakerState RemoteNodeState => GetCurrentCircuitState();
    private int _remoteConsecutiveFailures = 0;
    public int RemoteConsecutiveFailures => Volatile.Read(ref _remoteConsecutiveFailures);
    private int _remoteErrorCount = 0;
    public int RemoteErrorCount => Volatile.Read(ref _remoteErrorCount);
    public long RemoteLatencyMs => BeastClusterLatencyMs;
    public DateTime LastCircuitBreakerTripTime => _circuitBreakerTripTime;
    private DateTime _circuitBreakerTripTime = DateTime.MinValue;
    public TimeSpan CircuitBreakerCooldown { get; set; } = TimeSpan.FromSeconds(15);
    public bool IsCircuitBreakerOpen => RemoteNodeState == CircuitBreakerState.Open;

    public string ActiveEndpoint => RemoteNodeState == CircuitBreakerState.Open
        ? $"http://127.0.0.1:{InferencePort}/v1"
        : BeastEndpointUrl;

    private readonly List<NodeTelemetryRecord> _telemetryHistory = new();
    public IReadOnlyList<NodeTelemetryRecord> TelemetryHistory
    {
        get
        {
            lock (_telemetryHistory)
            {
                return _telemetryHistory.ToArray();
            }
        }
    }

    public IReadOnlyList<DiscoveredGgufModel> AvailableModels => _availableModels;
    private readonly List<DiscoveredGgufModel> _availableModels = new();

    public bool IsInferenceProjectPresent => File.Exists(GetInferenceCsprojPath());
    public bool IsDistributedProjectPresent => File.Exists(GetDistributedCsprojPath());

    public event Action? OnRuntimeStateChanged;

    public GlacierRuntimeLauncherService(
        GlacierStudioState studioState,
        GlacierConsoleService consoleService,
        string? workspaceRoot = null,
        HttpClient? probeClient = null,
        bool startBackgroundProber = true,
        bool autoDiscover = true,
        bool autoProbe = true)
    {
        _studioState = studioState;
        _consoleService = consoleService;
        _workspaceRoot = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : GlacierWorkspaceResolver.ResolveWorkspaceRoot();

        if (probeClient != null)
        {
            _probeClient = probeClient;
            _ownsProbeClient = false;
        }
        else
        {
            _probeClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(1500)
            };
            _ownsProbeClient = true;
        }

        if (autoDiscover)
        {
            DiscoverLocalGgufModels();
        }

        if (autoProbe)
        {
            _ = ProbeAllRuntimesAsync();
        }

        if (startBackgroundProber)
        {
            StartHealthProbeLoop();
        }
    }

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

    public void AddDiscoveredModel(string displayName, string fullPath, double sizeGb)
    {
        _availableModels.Add(new DiscoveredGgufModel(displayName, fullPath, sizeGb));
        SortAvailableModels();
    }

    public void DiscoverLocalGgufModels()
    {
        _availableModels.Clear();
        var searchDirs = new List<string>
        {
            @"D:\GlacierModels",
            @"D:\lmstudio\models",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models"),
            Path.Combine(_workspaceRoot, "Glacier.Inference", "models"),
            Path.Combine(_workspaceRoot, "models")
        };

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan workspace root top-level directory for project models (e.g. Glacier-Architect-Coder-14B-Grandmaster)
        if (Directory.Exists(_workspaceRoot))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_workspaceRoot, "*.gguf", SearchOption.TopDirectoryOnly))
                {
                    if (!seenPaths.Add(file)) continue;
                    var info = new FileInfo(file);
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

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories))
                {
                    if (!seenPaths.Add(file)) continue;
                    var info = new FileInfo(file);
                    if (info.Length < 50L * 1024 * 1024) continue;
                    if (file.Contains(@"\cache\", StringComparison.OrdinalIgnoreCase) || file.Contains("/cache/", StringComparison.OrdinalIgnoreCase)) continue;

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

        SortAvailableModels();

        // Separate local fitting selection (<= 4.8 GB) from cluster/distributed selection (> 4.8 GB)
        var bestLocal = GetBestFittingLocalModelInfo(StandaloneVramBudgetGb);
        if (bestLocal != null)
        {
            SelectedLocalGgufPath = bestLocal.FullPath;
            if (_studioState.Provider == ModelServerProvider.LocalGlacierInference)
            {
                _studioState.SelectedModel = Path.GetFileNameWithoutExtension(bestLocal.FullPath);
            }
        }

        var clusterCandidate = _availableModels.FirstOrDefault(m => m.SizeGb > StandaloneVramBudgetGb);
        if (clusterCandidate != null)
        {
            SelectedClusterGgufPath = clusterCandidate.FullPath;
        }

        string grandmasterPath = Path.Combine(_workspaceRoot, "Glacier-Architect-Coder-14B-Grandmaster-Q4_K_M.gguf");
        if (File.Exists(grandmasterPath))
        {
            SelectedClusterGgufPath = grandmasterPath;
        }

        // Single-node default: pair with best fitting local model (<= 4.8 GB)
        SelectedGgufPath = !string.IsNullOrEmpty(SelectedLocalGgufPath)
            ? SelectedLocalGgufPath
            : (_availableModels.Count > 0 ? _availableModels[0].FullPath : "");
    }

    private void SortAvailableModels()
    {
        _availableModels.Sort((a, b) =>
        {
            int pA = GetModelGlobalPriority(a);
            int pB = GetModelGlobalPriority(b);
            if (pA != pB) return pA.CompareTo(pB);
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int GetModelGlobalPriority(DiscoveredGgufModel m)
    {
        // Local standalone fitting models first (<= MaxStandaloneVramBudgetGb)
        if (m.DisplayName.Contains("Glacier-Grandmaster-7B", StringComparison.OrdinalIgnoreCase) && m.SizeGb <= MaxStandaloneVramBudgetGb) return -2;
        if (m.DisplayName.Contains("Glacier-Grandmaster-4B", StringComparison.OrdinalIgnoreCase) && m.SizeGb <= MaxStandaloneVramBudgetGb) return -1;
        if (m.DisplayName.Contains("Qwen3-4B-Instruct", StringComparison.OrdinalIgnoreCase) && m.SizeGb <= MaxStandaloneVramBudgetGb) return 0;
        if (m.DisplayName.Contains("Qwen3-4B", StringComparison.OrdinalIgnoreCase) && m.SizeGb <= MaxStandaloneVramBudgetGb) return 1;
        if (m.DisplayName.Contains("Qwen2.5-7B-Instruct", StringComparison.OrdinalIgnoreCase) && m.SizeGb <= MaxStandaloneVramBudgetGb) return 2;
        if (m.DisplayName.Contains("qwen2.5-coder-7b", StringComparison.OrdinalIgnoreCase) && m.SizeGb <= MaxStandaloneVramBudgetGb) return 3;
        if (m.SizeGb <= MaxStandaloneVramBudgetGb) return 4;

        // Cluster & Distributed models (> 4.8 GB)
        if (m.DisplayName.Contains("Glacier-Architect-Coder-14B-Grandmaster", StringComparison.OrdinalIgnoreCase)) return 5;
        if (m.DisplayName.Contains("Glacier-Architect", StringComparison.OrdinalIgnoreCase)) return 6;
        if (m.DisplayName.Contains("DeepSeek-R1-Distill-Qwen-14B", StringComparison.OrdinalIgnoreCase)) return 7;
        if (m.DisplayName.Contains("DeepSeek-R1", StringComparison.OrdinalIgnoreCase) && m.DisplayName.Contains("14B", StringComparison.OrdinalIgnoreCase)) return 7;
        if (m.SizeGb <= 8.0) return 8;
        return 9;
    }

    /// <summary>
    /// Evaluates hardware-aware model pairing for single-node local execution (RTX 4060 Laptop dedicated VRAM limit <= 4.8 GB).
    /// Returns the full path to the best-fitting local model.
    /// </summary>
    public string GetBestFittingLocalModel(double maxVramGb = MaxStandaloneVramBudgetGb)
    {
        var model = GetBestFittingLocalModelInfo(maxVramGb);
        return model?.FullPath ?? "";
    }

    /// <summary>
    /// Returns the metadata record of the best-fitting local model adhering to the dedicated VRAM budget limit.
    /// </summary>
    public DiscoveredGgufModel? GetBestFittingLocalModelInfo(double maxVramGb = MaxStandaloneVramBudgetGb)
    {
        return _availableModels
            .Where(m => m.SizeGb <= maxVramGb)
            .OrderBy(m => GetLocalStandalonePriority(m))
            .ThenBy(m => m.SizeGb)
            .FirstOrDefault();
    }

    public static int GetLocalStandalonePriority(DiscoveredGgufModel m)
    {
        if (m.DisplayName.Contains("Glacier-Grandmaster-7B", StringComparison.OrdinalIgnoreCase)) return -2;
        if (m.DisplayName.Contains("Glacier-Grandmaster-4B", StringComparison.OrdinalIgnoreCase)) return -1;
        if (m.DisplayName.Contains("qwen2.5-coder-7b", StringComparison.OrdinalIgnoreCase)) return 0;
        if (m.DisplayName.Contains("Qwen3-4B-Instruct", StringComparison.OrdinalIgnoreCase)) return 1;
        if (m.DisplayName.Contains("Qwen3-4B", StringComparison.OrdinalIgnoreCase)) return 2;
        if (m.DisplayName.Contains("Qwen2.5-7B-Instruct", StringComparison.OrdinalIgnoreCase)) return 3;
        if (m.DisplayName.Contains("DeepSeek-R1-Distill-Qwen-7B", StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    public async Task ProbeAllRuntimesAsync()
    {
        await Task.WhenAll(
            ProbeInferenceServerAsync(),
            ProbeDistributedServerAsync(),
            ProbeBeastServerAsync());
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

    public Task<bool> ProbeBeastServerAsync() => ProbeRemoteNodeAsync(CancellationToken.None);

    public async Task<bool> ProbeRemoteNodeAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string probeUrl = BeastEndpointUrl.TrimEnd('/') + "/models";
            using var resp = await _probeClient.GetAsync(probeUrl, ct);
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                string modelId = TryParseFirstModelId(json) ?? BeastDefaultModel;
                BeastClusterActiveModel = modelId;
                BeastClusterLatencyMs = sw.ElapsedMilliseconds;
                BeastClusterStatusDetail = $"Online · {BeastClusterLatencyMs} ms · 192.168.1.108:8080";
                RecordRemoteSuccess(BeastClusterLatencyMs);
                return true;
            }
            else
            {
                sw.Stop();
                RecordRemoteFailure($"HTTP {(int)resp.StatusCode}");
                BeastClusterStatusDetail = $"HTTP {(int)resp.StatusCode} (192.168.1.108:8080)";
                return false;
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordRemoteFailure(ex.Message);
            BeastClusterStatusDetail = "Offline (192.168.1.108:8080)";
            return false;
        }
    }

    private void StartHealthProbeLoop()
    {
        _healthProbeTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!_healthProbeCts.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(_healthProbeCts.Token);
                    await ProbeRemoteNodeAsync(_healthProbeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore background probe loop failures
                }
            }
        });
    }

    public CircuitBreakerState GetCurrentCircuitState()
    {
        if (_remoteNodeState == CircuitBreakerState.Open)
        {
            if (DateTime.UtcNow - _circuitBreakerTripTime >= CircuitBreakerCooldown)
            {
                _remoteNodeState = CircuitBreakerState.HalfOpen;
                NotifyChanged();
            }
        }
        return _remoteNodeState;
    }

    public void RecordRemoteSuccess(long latencyMs = 0)
    {
        Interlocked.Exchange(ref _remoteConsecutiveFailures, 0);
        BeastClusterLatencyMs = latencyMs;
        BeastClusterState = RuntimeHealthState.Online;

        if (_remoteNodeState != CircuitBreakerState.Closed)
        {
            _remoteNodeState = CircuitBreakerState.Closed;
            _consoleService.AddLog("CIRCUIT", "[CIRCUIT BREAKER]: Beast cluster node recovered. State transitioned to CLOSED.");
            NotifyChanged();
        }

        lock (_telemetryHistory)
        {
            _telemetryHistory.Add(new NodeTelemetryRecord(
                BeastEndpointUrl,
                !string.IsNullOrEmpty(BeastClusterActiveModel) ? BeastClusterActiveModel : BeastDefaultModel,
                latencyMs,
                Success: true,
                Timestamp: DateTime.UtcNow));
            if (_telemetryHistory.Count > 100)
            {
                _telemetryHistory.RemoveAt(0);
            }
        }
    }

    public void RecordRemoteFailure(string? reason = null)
    {
        Interlocked.Increment(ref _remoteErrorCount);
        int consecutiveFailures = Interlocked.Increment(ref _remoteConsecutiveFailures);
        BeastClusterState = RuntimeHealthState.Offline;

        lock (_telemetryHistory)
        {
            _telemetryHistory.Add(new NodeTelemetryRecord(
                BeastEndpointUrl,
                !string.IsNullOrEmpty(BeastClusterActiveModel) ? BeastClusterActiveModel : BeastDefaultModel,
                0,
                Success: false,
                Timestamp: DateTime.UtcNow,
                ErrorMessage: reason));
            if (_telemetryHistory.Count > 100)
            {
                _telemetryHistory.RemoveAt(0);
            }
        }

        if (_remoteNodeState == CircuitBreakerState.HalfOpen)
        {
            _remoteNodeState = CircuitBreakerState.Open;
            _circuitBreakerTripTime = DateTime.UtcNow;
            _consoleService.AddLog("CIRCUIT", $"[CIRCUIT BREAKER]: Probe failed in HalfOpen state ({reason}). Tripped back to OPEN.", isError: true);
            NotifyChanged();
        }
        else if (_remoteNodeState == CircuitBreakerState.Closed && consecutiveFailures >= 3)
        {
            _remoteNodeState = CircuitBreakerState.Open;
            _circuitBreakerTripTime = DateTime.UtcNow;
            _consoleService.AddLog("CIRCUIT", $"[CIRCUIT BREAKER]: 3 consecutive failures reached on Beast node ({reason}). Tripped to OPEN. Automatic local fallback active.", isError: true);
            NotifyChanged();
        }
    }

    public void ResetCircuitBreaker()
    {
        _remoteNodeState = CircuitBreakerState.Closed;
        Interlocked.Exchange(ref _remoteConsecutiveFailures, 0);
        _circuitBreakerTripTime = DateTime.MinValue;
        NotifyChanged();
    }

    public void SetBeastClusterStateForTesting(RuntimeHealthState state, string modelId = "qwen2.5-coder-14b")
    {
        BeastClusterState = state;
        BeastClusterActiveModel = modelId;
    }

    public void SetCircuitStateForTesting(CircuitBreakerState state)
    {
        _remoteNodeState = state;
        if (state == CircuitBreakerState.Open)
        {
            _circuitBreakerTripTime = DateTime.UtcNow;
        }
    }

    public string ResolveEndpointForTurn(AgentTurnTier turnTier)
    {
        if (turnTier == AgentTurnTier.LocalFastTool)
        {
            return $"http://127.0.0.1:{InferencePort}/v1";
        }

        // RemoteDeepReasoning turn
        var state = GetCurrentCircuitState();
        if (state == CircuitBreakerState.Open)
        {
            // Circuit breaker open -> automatic fallback to local standalone
            return $"http://127.0.0.1:{InferencePort}/v1";
        }

        if (BeastClusterState == RuntimeHealthState.Online)
        {
            return BeastEndpointUrl;
        }

        // If offline and circuit closed, fallback to local standalone
        return $"http://127.0.0.1:{InferencePort}/v1";
    }

    public string ResolveModelForTurn(AgentTurnTier turnTier)
    {
        if (turnTier == AgentTurnTier.LocalFastTool)
        {
            if (!string.IsNullOrEmpty(InferenceActiveModel)) return InferenceActiveModel;
            var best = GetBestFittingLocalModelInfo(StandaloneVramBudgetGb);
            if (best != null) return Path.GetFileNameWithoutExtension(best.FullPath);
            return "Glacier-Grandmaster-7B-Q4_K_M";
        }

        // RemoteDeepReasoning turn
        var state = GetCurrentCircuitState();
        if (state == CircuitBreakerState.Open || BeastClusterState != RuntimeHealthState.Online)
        {
            // Fallback to local model
            if (!string.IsNullOrEmpty(InferenceActiveModel)) return InferenceActiveModel;
            var best = GetBestFittingLocalModelInfo(StandaloneVramBudgetGb);
            if (best != null) return Path.GetFileNameWithoutExtension(best.FullPath);
            return "Glacier-Grandmaster-7B-Q4_K_M";
        }

        return !string.IsNullOrEmpty(BeastClusterActiveModel) ? BeastClusterActiveModel : BeastDefaultModel;
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

        // Hardware-Aware Model Selection VRAM Guard
        var fileInfo = new FileInfo(SelectedGgufPath);
        double sizeGb = fileInfo.Length / (1024.0 * 1024.0 * 1024.0);
        if (sizeGb > StandaloneVramBudgetGb)
        {
            _consoleService.AddLog("VRAM_GUARD",
                $"[GUARD]: Selected model '{Path.GetFileName(SelectedGgufPath)}' ({sizeGb:F1} GB) exceeds RTX 4060 dedicated VRAM limit ({StandaloneVramBudgetGb} GB). Auto-pairing with fitting local model to prevent WDDM paging thrashing...", isError: true);

            var bestFitting = GetBestFittingLocalModelInfo(StandaloneVramBudgetGb);
            if (bestFitting != null)
            {
                SelectedGgufPath = bestFitting.FullPath;
                _consoleService.AddLog("VRAM_GUARD", $"[GUARD]: Successfully auto-paired with '{bestFitting.DisplayName}' ({bestFitting.SizeGb:F1} GB).");
            }
            else
            {
                InferenceState = RuntimeHealthState.Offline;
                InferenceStatusDetail = $"Selected model ({sizeGb:F1} GB) exceeds {StandaloneVramBudgetGb}GB budget and no local 4B/7B model found.";
                NotifyChanged();
                return false;
            }
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

            string splitArg = "";
            try
            {
                if (File.Exists(SelectedGgufPath) && new FileInfo(SelectedGgufPath).Length > 7L * 1024 * 1024 * 1024)
                {
                    splitArg = " --split auto";
                }
            }
            catch { }

            string args = $"serve \"{SelectedGgufPath}\" --port {InferencePort} --device {InferenceDevice} --engine {InferenceEngine} --ctx {InferenceContextSize}{splitArg}";
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

    public void ActivateBeastInStudio()
    {
        _studioState.Provider = ModelServerProvider.ClusterBeastCoder;
        _studioState.EndpointUrl = BeastEndpointUrl;
        _studioState.SelectedModel = !string.IsNullOrEmpty(BeastClusterActiveModel) ? BeastClusterActiveModel : BeastDefaultModel;
        _studioState.ActiveSession.ModelSnapshot = _studioState.GetModelDisplayName();
        _studioState.NotifyStateChanged();
    }

    public async Task<bool> StartBeastRemoteServerAsync()
    {
        BeastClusterState = RuntimeHealthState.Starting;
        BeastClusterStatusDetail = "Starting on Beast (192.168.1.108)...";
        NotifyChanged();

        try
        {
            _consoleService.AddLog("CLUSTER", "Launching Glacier-Architect-Coder-14B-Q4_K_M standalone server on Beast Desktop (192.168.1.108)...");
            string scriptPath = Path.Combine(_workspaceRoot, "scripts", "Invoke-FleetCommand.ps1");
            string remoteCmd = "Stop-Process -Name *glacier* -Force -ErrorAction SilentlyContinue; ([wmiclass]'Win32_Process').Create('cmd.exe /c cd /d D:\\GlacierPipelineCluster && start /b glacier-distributed.exe serve --model D:\\GlacierModels\\Glacier-Architect-Coder-14B-Q4_K_M.gguf --http-port 8080 --host 0.0.0.0 --cache D:\\GlacierModels\\cache > beast_serve.log 2>&1')";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -Target \"192.168.1.108\" -Command \"{remoteCmd}\"",
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();

            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                if (await ProbeRemoteNodeAsync())
                {
                    _consoleService.AddLog("CLUSTER", $"Beast Cluster Node is ONLINE at {BeastEndpointUrl}");
                    ActivateBeastInStudio();
                    NotifyChanged();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("CLUSTER", $"Failed to launch Beast server: {ex.Message}", isError: true);
        }

        BeastClusterState = RuntimeHealthState.Offline;
        BeastClusterStatusDetail = "Startup timed out on Beast";
        NotifyChanged();
        return false;
    }

    public async Task StopBeastRemoteServerAsync()
    {
        try
        {
            _consoleService.AddLog("CLUSTER", "Stopping Glacier processes on Beast Desktop (192.168.1.108)...");
            string scriptPath = Path.Combine(_workspaceRoot, "scripts", "Invoke-FleetCommand.ps1");
            string remoteCmd = "Stop-Process -Name *glacier* -Force -ErrorAction SilentlyContinue";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -Target \"192.168.1.108\" -Command \"{remoteCmd}\"",
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();
            BeastClusterState = RuntimeHealthState.Offline;
            BeastClusterStatusDetail = "Stopped (192.168.1.108:8080)";
            _consoleService.AddLog("CLUSTER", "Beast Cluster Node stopped.");
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("CLUSTER", $"Failed to stop Beast server: {ex.Message}", isError: true);
        }
        NotifyChanged();
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
        _healthProbeCts.Cancel();
        _healthProbeCts.Dispose();
        if (_ownsProbeClient)
        {
            _probeClient.Dispose();
        }
    }
}
