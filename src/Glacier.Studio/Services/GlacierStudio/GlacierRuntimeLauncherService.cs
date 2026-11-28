using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Inference.Hardware;

namespace Glacier.Studio.Services;

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
    /// <summary>Default standalone VRAM budget fallback (GB) when no discrete GPU is detected.</summary>
    public const double MaxStandaloneVramBudgetGb = 4.8;
    public double StandaloneVramBudgetGb { get; set; } = DetectStandaloneVramBudgetGb();

    public static double DetectStandaloneVramBudgetGb(IReadOnlyList<DeviceInfo>? devices = null)
    {
        devices ??= DeviceManager.GetDevices();
        double maxDiscrete = 0.0;
        foreach (DeviceInfo dev in devices)
        {
            if (dev.Vendor != GpuVendor.Cpu && !dev.IsNpu && dev.DedicatedVramGb >= 2.0 && dev.DedicatedVramGb > maxDiscrete)
            {
                maxDiscrete = dev.DedicatedVramGb;
            }
        }

        if (maxDiscrete <= 0.0)
            return MaxStandaloneVramBudgetGb;

        return Math.Clamp(Math.Round(maxDiscrete * 0.75, 1), 2.0, 64.0);
    }

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
    public string InferenceDevice { get; set; } = "auto";
    public string InferenceEngine { get; set; } = "baremetal";
    public int InferenceContextSize { get; set; } = 8192;
    public bool InferenceEnableSleepMemory { get; set; }
    public string InferenceSleepCheckpointPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".glacier", "sleep.gmoe");
    public string SelectedGgufPath { get; set; } = "";
    public string SelectedLocalGgufPath { get; set; } = "";
    public string SelectedClusterGgufPath { get; set; } = "";
    public RuntimeHealthState InferenceState
    {
        get { lock (_runtimeStateLock) return _inferenceState; }
        private set { lock (_runtimeStateLock) _inferenceState = value; }
    }
    public string InferenceActiveModel
    {
        get { lock (_runtimeStateLock) return _inferenceActiveModel; }
        private set { lock (_runtimeStateLock) _inferenceActiveModel = value; }
    }
    public long InferenceLatencyMs
    {
        get { lock (_runtimeStateLock) return _inferenceLatencyMs; }
        private set { lock (_runtimeStateLock) _inferenceLatencyMs = value; }
    }
    public string InferenceStatusDetail
    {
        get { lock (_runtimeStateLock) return _inferenceStatusDetail; }
        private set { lock (_runtimeStateLock) _inferenceStatusDetail = value; }
    }

    // 1b. Sleep Memory (MoE Consolidation Daemon Telemetry)
    public bool SleepEnabled { get; private set; }
    public string SleepState { get; private set; } = "Unchecked";
    public long SleepCompletedCycles { get; private set; }
    public long SleepTotalDreamsConsolidated { get; private set; }
    public int SleepPendingDreams { get; private set; }
    public string SleepStatusDetail { get; private set; } = "Unchecked";
    public bool IsTriggeringSleep { get; private set; }

    // 2. Glacier.Cluster / Glacier.Serve (Multi-Node Cluster Coordinator / Worker)
    public string DistributedRole { get; set; } = "coordinator"; // "coordinator" or "worker"
    public string DistributedStages { get; set; } = ""; // Local-first dual-GPU (RTX 4060 + Radeon 890M)
    public string DistributedLayers { get; set; } = "20:28"; // 20 on Host RTX 4060 dGPU, 28 on Host Radeon 890M iGPU
    public int DistributedHostGpu { get; set; } = -1; // --host-gpu / --adapter (-1 = auto fastest VRAM)
    public string DistributedHttpHost { get; set; } = "0.0.0.0"; // --host
    public int DistributedHttpPort { get; set; } = 8080; // --http-port
    public int DistributedWorkerPort { get; set; } = 5056; // --port (worker TCP port)
    public string DistributedCacheDir { get; set; } = Path.Combine(GlacierStudioState.ResolveDefaultModelsRootDir(), "cache"); // --cache
    public int DistributedMaxTokens { get; set; } = -1; // --tokens (-1 = unlimited)
    public string NewNodeEndpointInput { get; set; } = "";

    public RuntimeHealthState DistributedState
    {
        get { lock (_runtimeStateLock) return _distributedState; }
        private set { lock (_runtimeStateLock) _distributedState = value; }
    }
    public string DistributedActiveModel
    {
        get { lock (_runtimeStateLock) return _distributedActiveModel; }
        private set { lock (_runtimeStateLock) _distributedActiveModel = value; }
    }
    public long DistributedLatencyMs
    {
        get { lock (_runtimeStateLock) return _distributedLatencyMs; }
        private set { lock (_runtimeStateLock) _distributedLatencyMs = value; }
    }
    public string DistributedStatusDetail
    {
        get { lock (_runtimeStateLock) return _distributedStatusDetail; }
        private set { lock (_runtimeStateLock) _distributedStatusDetail = value; }
    }
    public IReadOnlyList<ClusterStageStatusItem> DistributedClusterStages
    {
        get
        {
            lock (_runtimeStateLock)
            {
                return _distributedClusterStages.ToArray();
            }
        }
    }
    private readonly List<ClusterStageStatusItem> _distributedClusterStages = new();

    // 3. Remote Beast Cluster Node (RTX 3060 12GB - Glacier-Architect-Coder-14B-Q4_K_M)
    public string BeastEndpointUrl { get; set; } = GlacierStudioState.ResolveDefaultClusterEndpointUrl();
    public string BeastDefaultModel { get; set; } = "Glacier-Architect-Coder-14B-Q4_K_M";
    public RuntimeHealthState BeastClusterState
    {
        get { lock (_runtimeStateLock) return _beastClusterState; }
        private set { lock (_runtimeStateLock) _beastClusterState = value; }
    }
    public string BeastClusterActiveModel
    {
        get { lock (_runtimeStateLock) return _beastClusterActiveModel; }
        private set { lock (_runtimeStateLock) _beastClusterActiveModel = value; }
    }
    public long BeastClusterLatencyMs
    {
        get { lock (_runtimeStateLock) return _beastClusterLatencyMs; }
        private set { lock (_runtimeStateLock) _beastClusterLatencyMs = value; }
    }
    public string BeastClusterStatusDetail
    {
        get { lock (_runtimeStateLock) return _beastClusterStatusDetail; }
        private set { lock (_runtimeStateLock) _beastClusterStatusDetail = value; }
    }

    // 3b. Local NPU Sidecar Slot (AMD XDNA 2 / FastFlowLM / Lemonade)
    public const double DefaultNpuSharedCapGb = 15.5;
    private readonly object _runtimeStateLock = new();
    private RuntimeHealthState _inferenceState = RuntimeHealthState.Offline;
    private string _inferenceActiveModel = "";
    private long _inferenceLatencyMs;
    private string _inferenceStatusDetail = "Unchecked";
    private RuntimeHealthState _distributedState = RuntimeHealthState.Offline;
    private string _distributedActiveModel = "";
    private long _distributedLatencyMs;
    private string _distributedStatusDetail = "Unchecked";
    private RuntimeHealthState _beastClusterState = RuntimeHealthState.Offline;
    private string _beastClusterActiveModel = "";
    private long _beastClusterLatencyMs;
    private string _beastClusterStatusDetail = "Unchecked";
    private readonly object _npuStateLock = new();
    private int _activeLocalGpuRequests;
    private volatile bool _isGpuBusyOverride;
    private int _disposed;

    public RuntimeHealthState NpuSidecarState { get; private set; } = RuntimeHealthState.Offline;
    public string NpuSidecarEndpointUrl { get; private set; } = "";
    public string NpuSidecarCustomEndpointUrl { get; set; } = "";
    public NpuSidecarRuntime NpuSidecarRuntime { get; private set; } = NpuSidecarRuntime.Unknown;
    public string NpuSidecarRuntimeName { get; private set; } = "Unchecked";
    public IReadOnlyList<string> NpuSidecarModels { get; private set; } = Array.Empty<string>();
    public string NpuSidecarActiveModel { get; private set; } = "";
    public long NpuSidecarLatencyMs { get; private set; }
    public string NpuSidecarStatusDetail { get; private set; } = "Unchecked";
    public string NpuDeviceId { get; private set; } = "";
    public string NpuDeviceName { get; private set; } = "";
    public bool IsNpuHardwarePresent { get; private set; }
    public double NpuSidecarSharedCapGb { get; private set; } = DefaultNpuSharedCapGb;
    public bool AllowNpuOffloadOnBusyGpu { get; set; }
    public bool PreferNpuForLocalFastTool { get; set; }

    public int ActiveLocalGpuRequests => Volatile.Read(ref _activeLocalGpuRequests);
    public bool IsGpuBusy
    {
        get => _isGpuBusyOverride || Volatile.Read(ref _activeLocalGpuRequests) > 0;
        set => _isGpuBusyOverride = value;
    }

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

        RefreshNpuHardware();

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
            "lan-3" => Environment.GetEnvironmentVariable("GLACIER_CLUSTER_STAGES") ?? "127.0.0.1:5055,127.0.0.1:5056,127.0.0.1:5057",
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
        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string cliBinDir = Path.Combine(_workspaceRoot, "Glacier.Inference", "src", "Glacier.Inference.Cli", "bin");
        string[] candidates =
        [
            Path.Combine(cliBinDir, "Debug", "net10.0", $"glacier-ai{ext}"),
            Path.Combine(cliBinDir, "Release", "net10.0", $"glacier-ai{ext}"),
            Path.Combine(cliBinDir, "Debug", "net10.0", $"glacier{ext}"),
            Path.Combine(cliBinDir, "Release", "net10.0", $"glacier{ext}")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public string GetClusterCsprojPath() =>
        Path.Combine(_workspaceRoot, "Glacier.Cluster", "src", "Glacier.Cluster", "Glacier.Cluster.csproj");

    public string GetDistributedCsprojPath() => GetClusterCsprojPath();

    public string GetDistributedExePath()
    {
        string? envExe = Environment.GetEnvironmentVariable("GLACIER_CLUSTER_CLI_EXE");
        if (!string.IsNullOrWhiteSpace(envExe) && File.Exists(envExe))
            return envExe;

        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string cliDir = Path.Combine(_workspaceRoot, "Glacier.Cluster", "src", "Glacier.Cluster.Cli", "bin");
        string[] candidates =
        [
            Path.Combine(cliDir, "Debug", "net10.0", $"glacier-cluster{ext}"),
            Path.Combine(cliDir, "Debug", "net10.0", $"Glacier.Cluster.Cli{ext}"),
            Path.Combine(cliDir, "Release", "net10.0", $"glacier-cluster{ext}"),
            Path.Combine(cliDir, "Release", "net10.0", $"Glacier.Cluster.Cli{ext}")
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
            GlacierStudioState.ResolveDefaultModelsRootDir(),
            Environment.GetEnvironmentVariable("LMSTUDIO_MODELS_DIR") ?? "",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models"),
            Path.Combine(_workspaceRoot, "Glacier.Inference", "models"),
            Path.Combine(_workspaceRoot, "models")
        }.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
    /// Evaluates hardware-aware model pairing for single-node local execution within <paramref name="maxVramGb"/>.
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
            ProbeBeastServerAsync(),
            ProbeNpuSidecarAsync(),
            ProbeSleepStatusAsync());
        NotifyChanged();
    }

    public void RefreshNpuHardware(IReadOnlyList<DeviceInfo>? devicesOverride = null)
    {
        IReadOnlyList<DeviceInfo> npus = devicesOverride ?? SafeGetNpuDevices();
        lock (_npuStateLock)
        {
            if (npus.Count > 0)
            {
                var primary = npus[0];
                IsNpuHardwarePresent = true;
                NpuDeviceId = primary.Id;
                NpuDeviceName = primary.Name;
                NpuSidecarSharedCapGb = primary.SharedVramGb > 0
                    ? Math.Round(primary.SharedVramGb, 1)
                    : DefaultNpuSharedCapGb;
            }
            else
            {
                IsNpuHardwarePresent = false;
                NpuDeviceId = "";
                NpuDeviceName = "";
                NpuSidecarSharedCapGb = DefaultNpuSharedCapGb;
            }
        }
    }

    private static IReadOnlyList<DeviceInfo> SafeGetNpuDevices()
    {
        try
        {
            return DeviceManager.GetNpuDevices();
        }
        catch
        {
            return Array.Empty<DeviceInfo>();
        }
    }

    public Task<bool> ProbeNpuSidecarAsync(CancellationToken ct = default)
        => ProbeNpuSidecarAsync(candidates: null, ct);

    public async Task<bool> ProbeNpuSidecarAsync(
        IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? candidates,
        CancellationToken ct = default)
    {
        try
        {
            var effectiveCandidates = candidates
                ?? (!string.IsNullOrWhiteSpace(NpuSidecarCustomEndpointUrl)
                    ? NpuSidecarDiscovery.GetDefaultCandidateEndpoints(NpuSidecarCustomEndpointUrl)
                    : null);

            NpuSidecarEndpoint? endpoint = await NpuSidecarDiscovery.ProbeAsync(
                httpClient: _probeClient,
                candidates: effectiveCandidates,
                perEndpointTimeout: TimeSpan.FromMilliseconds(350),
                cancellationToken: ct).ConfigureAwait(false);

            lock (_npuStateLock)
            {
                if (endpoint != null)
                {
                    NpuSidecarState = RuntimeHealthState.Online;
                    NpuSidecarEndpointUrl = endpoint.BaseUrl;
                    NpuSidecarRuntime = endpoint.Runtime;
                    NpuSidecarRuntimeName = FormatNpuRuntimeName(endpoint.Runtime);
                    NpuSidecarModels = endpoint.Models.ToArray();
                    NpuSidecarActiveModel = endpoint.PrimaryModel ?? "npu-model";
                    NpuSidecarLatencyMs = (long)Math.Round(endpoint.LatencyMs);
                    string hwLabel = !string.IsNullOrEmpty(NpuDeviceId)
                        ? $"{NpuDeviceId} · {NpuSidecarSharedCapGb:F1} GB cap"
                        : $"{NpuSidecarSharedCapGb:F1} GB cap";
                    NpuSidecarStatusDetail = $"Online · {NpuSidecarRuntimeName} · {NpuSidecarLatencyMs} ms · {hwLabel}";
                    return true;
                }

                SetNpuOfflineLocked();
                return false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_npuStateLock)
            {
                SetNpuOfflineLocked();
            }
            return false;
        }
        catch
        {
            lock (_npuStateLock)
            {
                SetNpuOfflineLocked();
            }
            return false;
        }
    }

    private void SetNpuOfflineLocked()
    {
        NpuSidecarState = RuntimeHealthState.Offline;
        NpuSidecarEndpointUrl = "";
        NpuSidecarRuntime = NpuSidecarRuntime.Unknown;
        NpuSidecarRuntimeName = "Offline";
        NpuSidecarModels = Array.Empty<string>();
        NpuSidecarActiveModel = "";
        NpuSidecarLatencyMs = 0;
        NpuSidecarStatusDetail = IsNpuHardwarePresent
            ? $"Offline ({NpuDeviceId} · {NpuSidecarSharedCapGb:F1} GB cap)"
            : "Offline (No sidecar detected)";
    }

    private static string FormatNpuRuntimeName(NpuSidecarRuntime runtime) => runtime switch
    {
        NpuSidecarRuntime.FastFlowLM => "FastFlowLM",
        NpuSidecarRuntime.LemonadeServer => "Lemonade",
        NpuSidecarRuntime.CustomOpenAi => "CustomOpenAI",
        _ => "Unknown"
    };

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
                long latency = sw.ElapsedMilliseconds;
                lock (_runtimeStateLock)
                {
                    _inferenceState = RuntimeHealthState.Online;
                    _inferenceActiveModel = modelId;
                    _inferenceLatencyMs = latency;
                    _inferenceStatusDetail = $"Online · {latency} ms · :{InferencePort}";
                }
                return true;
            }
        }
        catch
        {
            // Offline or unreachable
        }

        lock (_runtimeStateLock)
        {
            if (_inferenceState != RuntimeHealthState.Starting)
            {
                _inferenceState = RuntimeHealthState.Offline;
                _inferenceActiveModel = "";
                _inferenceStatusDetail = $"Offline (:{InferencePort})";
            }
        }
        return false;
    }

    public async Task<bool> ProbeDistributedServerAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _probeClient.GetAsync($"http://127.0.0.1:{DistributedHttpPort}/v1/models");
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync();
                string modelId = TryParseFirstModelId(json) ?? "cluster-model";
                long latency = sw.ElapsedMilliseconds;

                await RefreshClusterStagesStatusAsync();
                lock (_runtimeStateLock)
                {
                    _distributedState = RuntimeHealthState.Online;
                    _distributedActiveModel = modelId;
                    _distributedLatencyMs = latency;
                    int stageCount = _distributedClusterStages.Count;
                    _distributedStatusDetail = stageCount > 0
                        ? $"Online · {stageCount} stage(s) · {latency} ms · :{DistributedHttpPort}"
                        : $"Online · {latency} ms · :{DistributedHttpPort}";
                }
                return true;
            }
        }
        catch
        {
            // Offline or unreachable
        }

        lock (_runtimeStateLock)
        {
            if (_distributedState != RuntimeHealthState.Starting)
            {
                if (!IsDistributedProjectPresent && !IsInferenceProjectPresent)
                {
                    _distributedState = RuntimeHealthState.MissingProject;
                    _distributedStatusDetail = "Project not found in workspace";
                }
                else
                {
                    _distributedState = RuntimeHealthState.Offline;
                    _distributedStatusDetail = $"Offline (:{DistributedHttpPort})";
                }
                _distributedActiveModel = "";
                _distributedClusterStages.Clear();
            }
        }
        return false;
    }

    public Task<bool> ProbeBeastServerAsync() => ProbeRemoteNodeAsync(CancellationToken.None);

    private string GetBeastAuthorityLabel() =>
        Uri.TryCreate(BeastEndpointUrl, UriKind.Absolute, out var uri) ? uri.Authority : BeastEndpointUrl;

    private string GetBeastHostOnly() =>
        Uri.TryCreate(BeastEndpointUrl, UriKind.Absolute, out var uri) ? uri.Host : "127.0.0.1";

    public async Task<bool> ProbeRemoteNodeAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string endpointLabel = GetBeastAuthorityLabel();
        try
        {
            string probeUrl = BeastEndpointUrl.TrimEnd('/') + "/models";
            using var resp = await _probeClient.GetAsync(probeUrl, ct);
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                string modelId = TryParseFirstModelId(json) ?? BeastDefaultModel;
                long latency = sw.ElapsedMilliseconds;
                lock (_runtimeStateLock)
                {
                    _beastClusterActiveModel = modelId;
                    _beastClusterLatencyMs = latency;
                    _beastClusterStatusDetail = $"Online · {latency} ms · {endpointLabel}";
                }
                RecordRemoteSuccess(latency);
                return true;
            }
            else
            {
                sw.Stop();
                RecordRemoteFailure($"HTTP {(int)resp.StatusCode}");
                BeastClusterStatusDetail = $"HTTP {(int)resp.StatusCode} ({endpointLabel})";
                return false;
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordRemoteFailure(ex.Message);
            BeastClusterStatusDetail = $"Offline ({endpointLabel})";
            return false;
        }
    }

    private void StartHealthProbeLoop()
    {
        CancellationToken ct = _healthProbeCts.Token;
        _healthProbeTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    await timer.WaitForNextTickAsync(ct);
                    await Task.WhenAll(
                        ProbeRemoteNodeAsync(ct),
                        ProbeNpuSidecarAsync(ct));
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
        bool transitioned = false;
        CircuitBreakerState current;
        lock (_runtimeStateLock)
        {
            if (_remoteNodeState == CircuitBreakerState.Open &&
                DateTime.UtcNow - _circuitBreakerTripTime >= CircuitBreakerCooldown)
            {
                _remoteNodeState = CircuitBreakerState.HalfOpen;
                transitioned = true;
            }
            current = _remoteNodeState;
        }
        if (transitioned)
        {
            NotifyChanged();
        }
        return current;
    }

    public void RecordRemoteSuccess(long latencyMs = 0)
    {
        Interlocked.Exchange(ref _remoteConsecutiveFailures, 0);
        bool closed = false;
        string modelForLog;
        lock (_runtimeStateLock)
        {
            _beastClusterLatencyMs = latencyMs;
            _beastClusterState = RuntimeHealthState.Online;
            modelForLog = !string.IsNullOrEmpty(_beastClusterActiveModel) ? _beastClusterActiveModel : BeastDefaultModel;
            if (_remoteNodeState != CircuitBreakerState.Closed)
            {
                _remoteNodeState = CircuitBreakerState.Closed;
                closed = true;
            }
        }

        if (closed)
        {
            _consoleService.AddLog("CIRCUIT", "[CIRCUIT BREAKER]: Beast cluster node recovered. State transitioned to CLOSED.");
            NotifyChanged();
        }

        lock (_telemetryHistory)
        {
            _telemetryHistory.Add(new NodeTelemetryRecord(
                BeastEndpointUrl,
                modelForLog,
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
        string modelForLog;
        bool trippedHalfOpen = false;
        bool trippedClosed = false;
        lock (_runtimeStateLock)
        {
            _beastClusterState = RuntimeHealthState.Offline;
            modelForLog = !string.IsNullOrEmpty(_beastClusterActiveModel) ? _beastClusterActiveModel : BeastDefaultModel;
            if (_remoteNodeState == CircuitBreakerState.HalfOpen)
            {
                _remoteNodeState = CircuitBreakerState.Open;
                _circuitBreakerTripTime = DateTime.UtcNow;
                trippedHalfOpen = true;
            }
            else if (_remoteNodeState == CircuitBreakerState.Closed && consecutiveFailures >= 3)
            {
                _remoteNodeState = CircuitBreakerState.Open;
                _circuitBreakerTripTime = DateTime.UtcNow;
                trippedClosed = true;
            }
        }

        lock (_telemetryHistory)
        {
            _telemetryHistory.Add(new NodeTelemetryRecord(
                BeastEndpointUrl,
                modelForLog,
                0,
                Success: false,
                Timestamp: DateTime.UtcNow,
                ErrorMessage: reason));
            if (_telemetryHistory.Count > 100)
            {
                _telemetryHistory.RemoveAt(0);
            }
        }

        if (trippedHalfOpen)
        {
            _consoleService.AddLog("CIRCUIT", $"[CIRCUIT BREAKER]: Probe failed in HalfOpen state ({reason}). Tripped back to OPEN.", isError: true);
            NotifyChanged();
        }
        else if (trippedClosed)
        {
            _consoleService.AddLog("CIRCUIT", $"[CIRCUIT BREAKER]: 3 consecutive failures reached on Beast node ({reason}). Tripped to OPEN. Automatic local fallback active.", isError: true);
            NotifyChanged();
        }
    }

    public void ResetCircuitBreaker()
    {
        lock (_runtimeStateLock)
        {
            _remoteNodeState = CircuitBreakerState.Closed;
            _circuitBreakerTripTime = DateTime.MinValue;
        }
        Interlocked.Exchange(ref _remoteConsecutiveFailures, 0);
        NotifyChanged();
    }

    public void SetInferenceStateForTesting(RuntimeHealthState state, string modelId = "")
    {
        lock (_runtimeStateLock)
        {
            _inferenceState = state;
            _inferenceActiveModel = modelId;
        }
    }

    public void SetBeastClusterStateForTesting(RuntimeHealthState state, string modelId = "qwen2.5-coder-14b")
    {
        lock (_runtimeStateLock)
        {
            _beastClusterState = state;
            _beastClusterActiveModel = modelId;
        }
    }

    public void SetCircuitStateForTesting(CircuitBreakerState state)
    {
        lock (_runtimeStateLock)
        {
            _remoteNodeState = state;
            if (state == CircuitBreakerState.Open)
            {
                _circuitBreakerTripTime = DateTime.UtcNow;
            }
        }
    }

    public void BeginLocalGpuTurn() => Interlocked.Increment(ref _activeLocalGpuRequests);

    public void EndLocalGpuTurn()
    {
        while (true)
        {
            int cur = Volatile.Read(ref _activeLocalGpuRequests);
            if (cur <= 0) break;
            if (Interlocked.CompareExchange(ref _activeLocalGpuRequests, cur - 1, cur) == cur) break;
        }
    }

    public void SetGpuBusyForTesting(bool isBusy) => _isGpuBusyOverride = isBusy;

    public bool IsLocalGpuEndpoint(string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl)) return false;
        if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        bool isLoopback = string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        return isLoopback && (uri.Port == InferencePort || uri.Port == DistributedHttpPort);
    }

    public void SetNpuSidecarStateForTesting(
        RuntimeHealthState state,
        string endpointUrl = "http://127.0.0.1:52625",
        string modelId = "qwen3.5-9b",
        NpuSidecarRuntime runtime = NpuSidecarRuntime.FastFlowLM,
        IReadOnlyList<string>? models = null)
    {
        lock (_npuStateLock)
        {
            if (state == RuntimeHealthState.Online)
            {
                NpuSidecarState = RuntimeHealthState.Online;
                NpuSidecarEndpointUrl = string.IsNullOrWhiteSpace(endpointUrl)
                    ? ""
                    : endpointUrl.Trim().TrimEnd('/');
                if (NpuSidecarEndpointUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                {
                    NpuSidecarEndpointUrl = NpuSidecarEndpointUrl[..^3].TrimEnd('/');
                }
                NpuSidecarRuntime = runtime;
                NpuSidecarRuntimeName = FormatNpuRuntimeName(runtime);
                NpuSidecarModels = models?.ToArray() ?? (string.IsNullOrWhiteSpace(modelId) ? Array.Empty<string>() : new[] { modelId });
                NpuSidecarActiveModel = !string.IsNullOrWhiteSpace(modelId)
                    ? modelId
                    : (NpuSidecarModels.Count > 0 ? NpuSidecarModels[0] : "qwen3.5-9b");
                NpuSidecarLatencyMs = 1;
                NpuSidecarStatusDetail = $"Online · {NpuSidecarRuntimeName} · {NpuSidecarSharedCapGb:F1} GB cap";
            }
            else
            {
                SetNpuOfflineLocked();
                NpuSidecarState = state;
            }
        }
    }

    public static string NormalizeOpenAiV1Endpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "";

        string trimmed = baseUrl.Trim();
        int suffixIdx = trimmed.IndexOfAny(['?', '#']);
        string pathPart = (suffixIdx >= 0 ? trimmed[..suffixIdx] : trimmed).TrimEnd('/');
        string suffix = suffixIdx >= 0 ? trimmed[suffixIdx..] : string.Empty;

        if (string.IsNullOrWhiteSpace(pathPart))
            return "";

        if (pathPart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return pathPart + suffix;

        return $"{pathPart}/v1{suffix}";
    }

    public bool TryGetOnlineNpuConfig(out string endpointUrl, out string activeModel)
    {
        lock (_npuStateLock)
        {
            if (NpuSidecarState == RuntimeHealthState.Online &&
                !string.IsNullOrWhiteSpace(NpuSidecarEndpointUrl))
            {
                endpointUrl = NormalizeOpenAiV1Endpoint(NpuSidecarEndpointUrl);
                activeModel = !string.IsNullOrWhiteSpace(NpuSidecarActiveModel)
                    ? NpuSidecarActiveModel
                    : (NpuSidecarModels.Count > 0 ? NpuSidecarModels[0] : "");
                return true;
            }

            endpointUrl = "";
            activeModel = "";
            return false;
        }
    }

    public bool ShouldUseNpuForLocalFastTool()
    {
        lock (_npuStateLock)
        {
            if (!AllowNpuOffloadOnBusyGpu) return false;
            if (NpuSidecarState != RuntimeHealthState.Online || string.IsNullOrWhiteSpace(NpuSidecarEndpointUrl))
                return false;

            return PreferNpuForLocalFastTool || IsGpuBusy || InferenceState != RuntimeHealthState.Online;
        }
    }

    public string ResolveEndpointForTurn(AgentTurnTier turnTier)
    {
        if (turnTier == AgentTurnTier.LocalFastTool)
        {
            string? npuEndpoint = null;
            lock (_npuStateLock)
            {
                if (AllowNpuOffloadOnBusyGpu
                    && NpuSidecarState == RuntimeHealthState.Online
                    && !string.IsNullOrWhiteSpace(NpuSidecarEndpointUrl)
                    && (PreferNpuForLocalFastTool || IsGpuBusy || InferenceState != RuntimeHealthState.Online))
                {
                    npuEndpoint = NpuSidecarEndpointUrl;
                }
            }

            if (!string.IsNullOrWhiteSpace(npuEndpoint))
            {
                return NormalizeOpenAiV1Endpoint(npuEndpoint);
            }

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
            string? npuModel = null;
            lock (_npuStateLock)
            {
                if (AllowNpuOffloadOnBusyGpu
                    && NpuSidecarState == RuntimeHealthState.Online
                    && !string.IsNullOrWhiteSpace(NpuSidecarEndpointUrl)
                    && (PreferNpuForLocalFastTool || IsGpuBusy || InferenceState != RuntimeHealthState.Online))
                {
                    npuModel = !string.IsNullOrWhiteSpace(NpuSidecarActiveModel)
                        ? NpuSidecarActiveModel
                        : (NpuSidecarModels.Count > 0 ? NpuSidecarModels[0] : "qwen3.5-9b");
                }
            }

            if (!string.IsNullOrWhiteSpace(npuModel))
            {
                return npuModel;
            }

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

            lock (_runtimeStateLock)
            {
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
                $"[GUARD]: Selected model '{Path.GetFileName(SelectedGgufPath)}' ({sizeGb:F1} GB) exceeds dedicated VRAM budget ({StandaloneVramBudgetGb:F1} GB). Auto-pairing with fitting local model to prevent host paging thrashing...", isError: true);

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
                InferenceStatusDetail = $"Build failed: {Path.GetFileName(exePath)} not found";
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"[GlacierRuntimeLauncherService] Could not inspect model size: {ex.Message}");
            }

            string args = BuildInferenceServeArguments(
                SelectedGgufPath,
                InferencePort,
                InferenceDevice,
                InferenceEngine,
                InferenceContextSize,
                splitArg,
                InferenceEnableSleepMemory,
                InferenceSleepCheckpointPath);
            _consoleService.AddLog("SYSTEM", $"Starting Glacier.Inference: {Path.GetFileName(exePath)} {args}");

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
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        Trace.TraceWarning($"[GlacierRuntimeLauncherService] Failed to terminate glacier process {proc.Id}: {ex.Message}");
                    }
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
            string args;
            if (File.Exists(exePath))
            {
                args = BuildDistributedCliArgs();
                _consoleService.AddLog("SYSTEM", $"Starting Glacier.Cluster ({DistributedRole}): {Path.GetFileName(exePath)} {args}");
            }
            else if (DistributedRole == "coordinator" && GetConfiguredNodes().Count == 0 && IsInferenceProjectPresent)
            {
                exePath = GetInferenceExePath();
                if (!File.Exists(exePath))
                {
                    _consoleService.AddLog("SYSTEM", "Compiling Glacier.Inference.Cli for Host-Only cluster coordinator...");
                    await _consoleService.ExecuteCommandAsync($"dotnet build \"{GetInferenceCsprojPath()}\" -c Debug", "SYSTEM");
                    exePath = GetInferenceExePath();
                }

                if (!File.Exists(exePath))
                {
                    DistributedState = RuntimeHealthState.Offline;
                    DistributedStatusDetail = $"Build failed: {Path.GetFileName(exePath)} not found";
                    NotifyChanged();
                    return false;
                }

                int ctxLen = DistributedMaxTokens > 0 ? DistributedMaxTokens : InferenceContextSize;
                args = BuildInferenceServeArguments(
                    SelectedGgufPath,
                    DistributedHttpPort,
                    InferenceDevice,
                    InferenceEngine,
                    ctxLen,
                    " --split auto",
                    InferenceEnableSleepMemory,
                    InferenceSleepCheckpointPath);
                if (!string.IsNullOrWhiteSpace(DistributedHttpHost))
                {
                    args += $" --host {DistributedHttpHost.Trim()}";
                }
                _consoleService.AddLog("SYSTEM", $"Starting Glacier.Cluster Host-Only Coordinator via Glacier.Inference.Cli: {Path.GetFileName(exePath)} {args}");
            }
            else
            {
                DistributedState = RuntimeHealthState.Offline;
                DistributedStatusDetail = "Glacier.Cluster library wired; multi-node worker CLI not yet standalone (use Host Only or start cluster HTTP endpoint)";
                _consoleService.AddLog("CLUSTER", DistributedStatusDetail, isError: true);
                NotifyChanged();
                return false;
            }

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
                    _consoleService.AddLog("SYSTEM", $"Glacier.Cluster runtime is online at http://127.0.0.1:{DistributedHttpPort}/v1");
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
            _consoleService.AddLog("SYSTEM", $"Failed to start Glacier.Cluster runtime: {ex.Message}", isError: true);
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
                foreach (var name in new[] { "glacier-cluster", "Glacier.Cluster.Cli" })
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            proc.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                        {
                            Trace.TraceWarning($"[GlacierRuntimeLauncherService] Failed to terminate cluster process {proc.Id}: {ex.Message}");
                        }
                    }
                }
            }
            _consoleService.AddLog("SYSTEM", "Stopped Glacier.Cluster runtime.");
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

    public void ActivateNpuSidecarInStudio()
    {
        string endpoint;
        string model;
        lock (_npuStateLock)
        {
            endpoint = !string.IsNullOrWhiteSpace(NpuSidecarEndpointUrl)
                ? NormalizeOpenAiV1Endpoint(NpuSidecarEndpointUrl)
                : (!string.IsNullOrWhiteSpace(NpuSidecarCustomEndpointUrl)
                    ? NormalizeOpenAiV1Endpoint(NpuSidecarCustomEndpointUrl)
                    : "http://127.0.0.1:52625/v1");
            model = !string.IsNullOrWhiteSpace(NpuSidecarActiveModel)
                ? NpuSidecarActiveModel
                : (NpuSidecarModels.Count > 0 ? NpuSidecarModels[0] : "qwen3.5-9b");
        }

        _studioState.Provider = ModelServerProvider.LocalNpuSidecar;
        _studioState.EndpointUrl = endpoint;
        _studioState.SelectedModel = model;
        _studioState.ActiveSession.ModelSnapshot = _studioState.GetModelDisplayName();
        _studioState.NotifyStateChanged();
    }

    public async Task<bool> StartBeastRemoteServerAsync()
    {
        string targetHost = GetBeastHostOnly();
        string endpointLabel = GetBeastAuthorityLabel();
        string scriptPath = Path.Combine(_workspaceRoot, "scripts", "Invoke-FleetCommand.ps1");
        if (!File.Exists(scriptPath))
        {
            BeastClusterState = RuntimeHealthState.Offline;
            BeastClusterStatusDetail = $"Fleet launcher script missing ({endpointLabel})";
            _consoleService.AddLog("CLUSTER", $"Remote fleet launcher script not found at \"{scriptPath}\". Configure GLACIER_CLUSTER_URL or start the remote node directly.", isError: true);
            NotifyChanged();
            return false;
        }

        BeastClusterState = RuntimeHealthState.Starting;
        BeastClusterStatusDetail = $"Starting on {targetHost}...";
        NotifyChanged();

        try
        {
            string remoteClusterDir = Environment.GetEnvironmentVariable("GLACIER_REMOTE_CLUSTER_DIR") ?? "%USERPROFILE%\\.glacier\\cluster";
            string remoteModelsDir = Environment.GetEnvironmentVariable("GLACIER_REMOTE_MODELS_DIR") ?? "%USERPROFILE%\\.glacier\\models";
            string remoteClusterBin = Environment.GetEnvironmentVariable("GLACIER_REMOTE_CLUSTER_BIN") ?? "glacier-ai.exe";
            _consoleService.AddLog("CLUSTER", $"Launching {BeastDefaultModel} standalone server on cluster node ({targetHost})...");
            string remoteCmd = $"Stop-Process -Name *glacier* -Force -ErrorAction SilentlyContinue; ([wmiclass]'Win32_Process').Create('cmd.exe /c cd /d {remoteClusterDir} && start /b {remoteClusterBin} serve --model {remoteModelsDir}\\{BeastDefaultModel}.gguf --port 8080 --host 0.0.0.0 > beast_serve.log 2>&1')";

            var psi = new ProcessStartInfo
            {
                FileName = GlacierConsoleService.ResolveShellExecutable(),
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -Target \"{targetHost}\" -Command \"{remoteCmd}\"",
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();

            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                if (await ProbeRemoteNodeAsync())
                {
                    _consoleService.AddLog("CLUSTER", $"Cluster Node is ONLINE at {BeastEndpointUrl}");
                    ActivateBeastInStudio();
                    NotifyChanged();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("CLUSTER", $"Failed to launch remote cluster server: {ex.Message}", isError: true);
        }

        BeastClusterState = RuntimeHealthState.Offline;
        BeastClusterStatusDetail = $"Startup timed out ({endpointLabel})";
        NotifyChanged();
        return false;
    }

    public async Task StopBeastRemoteServerAsync()
    {
        string targetHost = GetBeastHostOnly();
        string endpointLabel = GetBeastAuthorityLabel();
        string scriptPath = Path.Combine(_workspaceRoot, "scripts", "Invoke-FleetCommand.ps1");
        if (!File.Exists(scriptPath))
        {
            BeastClusterState = RuntimeHealthState.Offline;
            BeastClusterStatusDetail = $"Offline ({endpointLabel})";
            NotifyChanged();
            return;
        }

        try
        {
            _consoleService.AddLog("CLUSTER", $"Stopping Glacier processes on cluster node ({targetHost})...");
            string remoteCmd = "Stop-Process -Name *glacier* -Force -ErrorAction SilentlyContinue";

            var psi = new ProcessStartInfo
            {
                FileName = GlacierConsoleService.ResolveShellExecutable(),
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -Target \"{targetHost}\" -Command \"{remoteCmd}\"",
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();
            BeastClusterState = RuntimeHealthState.Offline;
            BeastClusterStatusDetail = $"Stopped ({endpointLabel})";
            _consoleService.AddLog("CLUSTER", "Cluster Node stopped.");
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("CLUSTER", $"Failed to stop cluster server: {ex.Message}", isError: true);
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
        catch (JsonException ex)
        {
            Trace.TraceWarning($"[GlacierRuntimeLauncherService] Invalid models payload JSON: {ex.Message}");
        }
        return null;
    }

    public static string BuildInferenceServeArguments(
        string ggufPath,
        int port,
        string device,
        string engine,
        int contextSize,
        string splitArg = "",
        bool enableSleepMemory = false,
        string? sleepCheckpointPath = null)
    {
        string args = $"serve \"{ggufPath}\" --port {port} --device {device} --engine {engine} --ctx {contextSize}{splitArg}";
        if (enableSleepMemory)
        {
            args += " --sleep-memory";
            if (!string.IsNullOrWhiteSpace(sleepCheckpointPath))
            {
                args += $" --sleep-checkpoint \"{sleepCheckpointPath.Trim()}\"";
            }
        }
        return args;
    }

    public static string ResolveSleepBaseUrl(string? endpointUrl, int inferencePort)
    {
        if (!string.IsNullOrWhiteSpace(endpointUrl))
        {
            string trimmed = endpointUrl.Trim().TrimEnd('/');
            if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^3];
            }
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }
        return $"http://127.0.0.1:{inferencePort}";
    }

    public void ApplySleepStatusJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        SleepEnabled = root.TryGetProperty("enabled", out var pEnabled) && pEnabled.ValueKind == JsonValueKind.True;
        SleepState = root.TryGetProperty("state", out var pState) ? pState.GetString() ?? "Unknown" : "Unknown";
        SleepCompletedCycles = root.TryGetProperty("completed_cycles", out var pCycles) && pCycles.TryGetInt64(out long c) ? c : 0;
        SleepTotalDreamsConsolidated = root.TryGetProperty("total_dreams_consolidated", out var pDreams) && pDreams.TryGetInt64(out long d) ? d : 0;
        SleepPendingDreams = root.TryGetProperty("pending_dreams", out var pPending) && pPending.TryGetInt32(out int p) ? p : 0;

        SleepStatusDetail = SleepEnabled
            ? $"{SleepState} · Cycles: {SleepCompletedCycles} · Consolidated: {SleepTotalDreamsConsolidated} · Pending: {SleepPendingDreams}"
            : $"Disabled ({SleepState})";
    }

    public async Task<bool> ProbeSleepStatusAsync(CancellationToken ct = default)
    {
        string baseUrl = ResolveSleepBaseUrl(_studioState.EndpointUrl, InferencePort);
        try
        {
            using var resp = await _probeClient.GetAsync($"{baseUrl}/v1/sleep/status", ct);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                ApplySleepStatusJson(json);
                NotifyChanged();
                return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            Trace.TraceWarning($"[GlacierRuntimeLauncherService] Sleep endpoint unavailable: {ex.Message}");
        }

        SleepEnabled = false;
        SleepState = "Unavailable";
        SleepStatusDetail = "Sleep endpoint unavailable";
        NotifyChanged();
        return false;
    }

    public async Task<bool> TriggerSleepConsolidationAsync(CancellationToken ct = default)
    {
        if (IsTriggeringSleep) return false;

        IsTriggeringSleep = true;
        SleepStatusDetail = "Triggering sleep cycle...";
        NotifyChanged();

        string baseUrl = ResolveSleepBaseUrl(_studioState.EndpointUrl, InferencePort);
        try
        {
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var resp = await _probeClient.PostAsync($"{baseUrl}/v1/sleep/trigger", content, ct);
            string json = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool triggered = root.TryGetProperty("triggered", out var pTrig) && pTrig.ValueKind == JsonValueKind.True;
                int slot = root.TryGetProperty("expert_slot_index", out var pSlot) && pSlot.TryGetInt32(out int s) ? s : -1;
                int accepted = root.TryGetProperty("accepted_dreams", out var pAcc) && pAcc.TryGetInt32(out int a) ? a : 0;
                int total = root.TryGetProperty("total_dreams", out var pTot) && pTot.TryGetInt32(out int t) ? t : 0;
                _consoleService.AddLog("SLEEP", triggered
                    ? $"Sleep cycle completed: slot #{slot}, consolidated {accepted}/{total} dream(s)."
                    : $"Sleep trigger returned triggered=false ({json}).");
                await ProbeSleepStatusAsync(ct);
                return triggered;
            }

            _consoleService.AddLog("SLEEP", $"Sleep trigger failed ({(int)resp.StatusCode}): {json}", isError: true);
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("SLEEP", $"Sleep trigger error: {ex.Message}", isError: true);
        }
        finally
        {
            IsTriggeringSleep = false;
            NotifyChanged();
        }

        return false;
    }

    private void NotifyChanged()
    {
        OnRuntimeStateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _healthProbeCts.Cancel(); } catch (ObjectDisposedException) { }

        var probeTask = _healthProbeTask;
        if (probeTask != null)
        {
            _ = probeTask.ContinueWith(_ =>
            {
                try { _healthProbeCts.Dispose(); } catch (ObjectDisposedException) { }
                if (_ownsProbeClient)
                {
                    try { _probeClient.Dispose(); } catch (ObjectDisposedException) { }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            try { _healthProbeCts.Dispose(); } catch (ObjectDisposedException) { }
            if (_ownsProbeClient)
            {
                try { _probeClient.Dispose(); } catch (ObjectDisposedException) { }
            }
        }
    }
}
