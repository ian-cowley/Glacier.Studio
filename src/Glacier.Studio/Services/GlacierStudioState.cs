using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Studio.Serialization;
using Glacier.Studio.Services;

namespace Glacier.Showcase.Services.GlacierStudio;

public class GlacierStudioState
{
    private readonly List<StudioSession> _sessions = new();
    private readonly List<string> _projects = new();
    private string _activeSessionId = "";
    private readonly string _workspaceRoot;
    private readonly string _sessionsDir;
    private readonly string _exportsDir;
    private readonly string _prefsPath;
    private readonly string _secretsPath;

    public IReadOnlyList<StudioSession> Sessions => _sessions;
    public IReadOnlyList<string> Projects => _projects;
    public string ActiveWorkspaceRoot { get; private set; }
    public int UiZoomPercent { get; private set; } = 100;

    public string SessionSearchQuery { get; set; } = "";

    public IEnumerable<StudioSession> FilteredSessions
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SessionSearchQuery))
                return _sessions;

            string query = SessionSearchQuery.Trim();
            return _sessions.Where(s =>
                s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Messages.Any(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public StudioSession ActiveSession
    {
        get
        {
            var session = _sessions.FirstOrDefault(s => s.Id == _activeSessionId);
            if (session == null)
            {
                session = CreateNewSession("Initial Chat");
            }
            return session;
        }
    }

    // Model selection state
    public ModelServerProvider Provider { get; set; } = ModelServerProvider.LocalGlacierInference;
    public string EndpointUrl { get; set; } = "http://127.0.0.1:5055/v1";
    public string SelectedModel { get; set; } = "Glacier-Grandmaster-7B-Q4_K_M";
    public string ApiKey { get; set; } = ""; // Loaded only into memory, never saved to disk
    public float Temperature { get; set; } = 0.2f;
    public float RepetitionPenalty { get; set; } = 1.15f;
    public int MaxTokens { get; set; } = 4096;
    public bool IsAgentMode { get; set; } = true;

    // Agent status
    public AgentExecutionStatus ExecutionStatus { get; set; } = AgentExecutionStatus.Idle;
    public string StatusText { get; set; } = "Ready";
    public DateTime? ActiveTurnStartUtc { get; set; }
    public bool IsExecutingTurn => ExecutionStatus is AgentExecutionStatus.Thinking
        or AgentExecutionStatus.ExecutingTool
        or AgentExecutionStatus.StreamingResponse;

    // Telemetry
    public double LastTokensPerSecond { get; set; } = 0.0;
    public TimeSpan LastTurnDuration { get; set; } = TimeSpan.Zero;
    public int ActiveContextTokens { get; set; } = 0;

    // Active tab on right auxiliary pane: "doc", "console", "graph", "telemetry"
    public string ActiveRightTab { get; set; } = "doc";

    public event Action? OnStateChanged;

    public GlacierStudioState(string? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot ?? GlacierWorkspaceResolver.ResolveWorkspaceRoot();
        ActiveWorkspaceRoot = _workspaceRoot;
        _projects.Add(_workspaceRoot);
        _sessionsDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "sessions");
        _exportsDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "exports");
        _prefsPath = Path.Combine(_workspaceRoot, ".glacier", "studio", "studio-prefs.json");
        _secretsPath = Path.Combine(_workspaceRoot, ".glacier", "studio", "secrets.enc.json");

        // Try reading environment variable for Gemini or OpenAI if available
        var geminiEnv = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrEmpty(geminiEnv))
        {
            ApiKey = geminiEnv;
        }

        // Initialize with default session
        var defaultSession = new StudioSession
        {
            Title = "Workspace Session",
            ModelSnapshot = GetModelDisplayName(),
            WorkspaceRoot = ActiveWorkspaceRoot
        };

        defaultSession.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "### Glacier Studio\n\nAutonomous engineering environment powered by the **Glacier .NET 10 Ecosystem**.\n\n- **Decision Engine (`Glacier.Clavier`)**: Sub-millisecond intent routing, dynamic skill gating, and step supervision.\n- **Workspace Graph (`Glacier.Graph`)**: Live AST symbol & project dependency graph in the **Graph** panel.\n- **Document AST (`Glacier.DocTree`)**: Zero-copy markdown and source outline navigation in the **Document** panel.\n- **Integrated Terminal**: Shared PowerShell execution environment in the **Terminal** panel."
        });

        _sessions.Add(defaultSession);
        _activeSessionId = defaultSession.Id;

        // Load existing persisted sessions & studio preferences from disk
        LoadSessionsFromDisk();
        LoadPreferencesFromDisk();
    }

    public bool SwitchProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path.Trim().Trim('"', '\''));
        }
        catch
        {
            return false;
        }

        if (!Directory.Exists(normalized)) return false;

        if (!_projects.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _projects.Insert(0, normalized);
        }

        ActiveWorkspaceRoot = normalized;
        SavePreferencesToDisk();
        NotifyStateChanged();
        return true;
    }

    public bool AddProject(string path) => SwitchProject(path);

    public bool RemoveProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        int idx = _projects.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || _projects.Count <= 1) return false;

        string removed = _projects[idx];
        _projects.RemoveAt(idx);
        if (string.Equals(ActiveWorkspaceRoot, removed, StringComparison.OrdinalIgnoreCase))
        {
            ActiveWorkspaceRoot = _projects[0];
        }
        SavePreferencesToDisk();
        NotifyStateChanged();
        return true;
    }

    public void SetUiZoomPercent(int percent)
    {
        int clamped = Math.Clamp(percent, 75, 175);
        if (UiZoomPercent == clamped) return;
        UiZoomPercent = clamped;
        SavePreferencesToDisk();
        NotifyStateChanged();
    }

    public void ZoomIn() => SetUiZoomPercent(UiZoomPercent + 10);
    public void ZoomOut() => SetUiZoomPercent(UiZoomPercent - 10);
    public void ResetZoom() => SetUiZoomPercent(100);

    private void LoadPreferencesFromDisk()
    {
        try
        {
            if (!File.Exists(_prefsPath)) return;
            string json = File.ReadAllText(_prefsPath, Encoding.UTF8);
            var payload = JsonSerializer.Deserialize(json, StudioJsonContext.Default.StudioPreferencesPayload);
            if (payload == null) return;

            UiZoomPercent = Math.Clamp(payload.UiZoomPercent == 0 ? 100 : payload.UiZoomPercent, 75, 175);
            if (payload.Projects != null)
            {
                foreach (string p in payload.Projects)
                {
                    if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p) &&
                        !_projects.Any(existing => string.Equals(existing, p, StringComparison.OrdinalIgnoreCase)))
                    {
                        _projects.Add(p);
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(payload.ActiveWorkspaceRoot) && Directory.Exists(payload.ActiveWorkspaceRoot))
            {
                ActiveWorkspaceRoot = payload.ActiveWorkspaceRoot;
                if (!_projects.Any(existing => string.Equals(existing, ActiveWorkspaceRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    _projects.Insert(0, ActiveWorkspaceRoot);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[GlacierStudioState] Failed to load preferences from '{_prefsPath}': {ex.Message}");
        }
    }

    private void SavePreferencesToDisk()
    {
        try
        {
            var payload = new StudioPreferencesPayload
            {
                UiZoomPercent = UiZoomPercent,
                ActiveWorkspaceRoot = ActiveWorkspaceRoot,
                Projects = _projects.ToList()
            };
            string json = JsonSerializer.Serialize(payload, StudioJsonContext.Indented.StudioPreferencesPayload);
            AtomicWriteAllText(_prefsPath, json);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[GlacierStudioState] Failed to save preferences to '{_prefsPath}': {ex.Message}");
        }
    }

    public void LoadSessionsFromDisk()
    {
        try
        {
            if (Directory.Exists(_sessionsDir))
            {
                var files = Directory.GetFiles(_sessionsDir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        string json = File.ReadAllText(file, Encoding.UTF8);
                        var session = JsonSerializer.Deserialize(json, StudioJsonContext.Default.StudioSession);
                        if (session != null && !string.IsNullOrEmpty(session.Id))
                        {
                            if (!_sessions.Any(s => s.Id == session.Id))
                            {
                                _sessions.Add(session);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GlacierStudioState] Failed to deserialize session '{file}': {ex.Message}");
                    }
                }

                // Keep default session first, then sort remaining by newest updated
                if (_sessions.Count > 1)
                {
                    var head = _sessions[0];
                    var tail = _sessions.Skip(1).OrderByDescending(s => s.UpdatedAt).ToList();
                    _sessions.Clear();
                    _sessions.Add(head);
                    _sessions.AddRange(tail);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlacierStudioState] Failed to load sessions from disk: {ex.Message}");
        }
    }

    public void SaveSessionToDisk(StudioSession session)
    {
        try
        {
            string filePath = Path.Combine(_sessionsDir, $"{session.Id}.json");
            string json = JsonSerializer.Serialize(session, StudioJsonContext.Indented.StudioSession);
            AtomicWriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlacierStudioState] Failed to save session '{session.Id}': {ex.Message}");
        }
    }

    public async Task SaveSessionToDiskAsync(StudioSession session, CancellationToken ct = default)
    {
        try
        {
            string filePath = Path.Combine(_sessionsDir, $"{session.Id}.json");
            string json = JsonSerializer.Serialize(session, StudioJsonContext.Indented.StudioSession);
            await AtomicWriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlacierStudioState] Failed to save session async '{session.Id}': {ex.Message}");
        }
    }

    public void SaveProviderApiKey(ModelServerProvider provider, string apiKey, string? keyFilePath = null)
    {
        var payload = LoadSecretsPayload();
        string providerKey = provider.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            payload.EncryptedProviderKeys.Remove(providerKey);
        }
        else
        {
            string encrypted = StudioSecretProtector.Encrypt(apiKey, keyFilePath);
            if (!string.IsNullOrEmpty(encrypted))
            {
                payload.EncryptedProviderKeys[providerKey] = encrypted;
            }
        }

        string json = JsonSerializer.Serialize(payload, StudioJsonContext.Indented.StudioEncryptedSecretsPayload);
        AtomicWriteAllText(_secretsPath, json);
    }

    public string LoadProviderApiKey(ModelServerProvider provider, string? keyFilePath = null)
    {
        var payload = LoadSecretsPayload();
        if (!payload.EncryptedProviderKeys.TryGetValue(provider.ToString(), out string? encrypted) ||
            string.IsNullOrWhiteSpace(encrypted))
        {
            return "";
        }

        return StudioSecretProtector.Decrypt(encrypted, keyFilePath);
    }

    private StudioEncryptedSecretsPayload LoadSecretsPayload()
    {
        try
        {
            if (!File.Exists(_secretsPath))
                return new StudioEncryptedSecretsPayload();
            string json = File.ReadAllText(_secretsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize(json, StudioJsonContext.Default.StudioEncryptedSecretsPayload)
                ?? new StudioEncryptedSecretsPayload();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[GlacierStudioState] Failed to load encrypted secrets from '{_secretsPath}': {ex.Message}");
            return new StudioEncryptedSecretsPayload();
        }
    }

    private static void AtomicWriteAllText(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content, Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { Trace.TraceWarning($"[GlacierStudioState] Temp cleanup failed for '{tempPath}': {ex.Message}"); }
            }
            throw;
        }
    }

    private static async Task AtomicWriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { Trace.TraceWarning($"[GlacierStudioState] Temp cleanup failed for '{tempPath}': {ex.Message}"); }
            }
            throw;
        }
    }

    public void SaveActiveSession()
    {
        var session = ActiveSession;
        session.UpdatedAt = DateTime.Now;
        SaveSessionToDisk(session);
    }

    public StudioSession CreateNewSession(string title = "New Conversation")
    {
        var session = new StudioSession
        {
            Title = title,
            ModelSnapshot = GetModelDisplayName(),
            WorkspaceRoot = ActiveWorkspaceRoot
        };

        _sessions.Insert(0, session);
        _activeSessionId = session.Id;
        SaveSessionToDisk(session);
        NotifyStateChanged();
        return session;
    }

    public void SwitchSession(string sessionId)
    {
        var target = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (target != null)
        {
            _activeSessionId = target.Id;
            if (!string.IsNullOrWhiteSpace(target.WorkspaceRoot) && Directory.Exists(target.WorkspaceRoot))
            {
                ActiveWorkspaceRoot = target.WorkspaceRoot;
                if (!_projects.Any(p => string.Equals(p, target.WorkspaceRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    _projects.Insert(0, target.WorkspaceRoot);
                }
                SavePreferencesToDisk();
            }
            NotifyStateChanged();
        }
    }

    public void DeleteSession(string sessionId)
    {
        var target = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (target != null)
        {
            _sessions.Remove(target);
            try
            {
                string filePath = Path.Combine(_sessionsDir, $"{sessionId}.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[GlacierStudioState] Failed to delete session file '{sessionId}': {ex.Message}");
            }

            if (_activeSessionId == sessionId)
            {
                _activeSessionId = _sessions.FirstOrDefault()?.Id ?? CreateNewSession().Id;
            }
            NotifyStateChanged();
        }
    }

    public string ExportActiveSessionAsJson()
    {
        return JsonSerializer.Serialize(ActiveSession, StudioJsonContext.Indented.StudioSession);
    }

    public string ExportActiveSessionAsMarkdown()
    {
        var session = ActiveSession;
        var sb = new StringBuilder();
        sb.AppendLine($"# {session.Title}");
        sb.AppendLine();
        sb.AppendLine($"> **Model**: {session.ModelSnapshot}  ");
        sb.AppendLine($"> **Created**: {session.CreatedAt:yyyy-MM-dd HH:mm:ss}  ");
        sb.AppendLine($"> **Updated**: {session.UpdatedAt:yyyy-MM-dd HH:mm:ss}  ");
        sb.AppendLine($"> **Messages**: {session.Messages.Count}  ");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in session.Messages)
        {
            string sender = msg.Role switch
            {
                "assistant" => "🤖 Assistant (Glacier Agent)",
                "user" => "👤 User",
                "system" => "⚙️ System",
                "tool" => $"🛠️ Tool ({msg.ToolName ?? "Unknown"})",
                _ => msg.Role
            };

            sb.AppendLine($"### {sender} — *{msg.Timestamp:HH:mm:ss}*");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(msg.ThinkingContent))
            {
                sb.AppendLine("<details>");
                sb.AppendLine("<summary>🧠 Thought Process</summary>");
                sb.AppendLine();
                sb.AppendLine(msg.ThinkingContent);
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }

            if (msg.ToolCalls.Count > 0)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    sb.AppendLine($"**Tool Invocation**: `{tc.ToolName}` ({tc.Duration.TotalMilliseconds:F0} ms)");
                    sb.AppendLine("```json");
                    sb.AppendLine(tc.ArgumentsJson);
                    sb.AppendLine("```");
                    if (!string.IsNullOrEmpty(tc.ResultOutput))
                    {
                        sb.AppendLine("**Result**:");
                        sb.AppendLine("```");
                        sb.AppendLine(tc.ResultOutput);
                        sb.AppendLine("```");
                    }
                    sb.AppendLine();
                }
            }

            if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.AppendLine(msg.Content);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportActiveSessionToDisk(string format)
    {
        Directory.CreateDirectory(_exportsDir);
        string safeTitle = string.Join("_", ActiveSession.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "Session";
        if (safeTitle.Length > 40) safeTitle = safeTitle.Substring(0, 40);

        string ext = format.Equals("json", StringComparison.OrdinalIgnoreCase) ? "json" : "md";
        string fileName = $"{safeTitle}_{ActiveSession.Id}.{ext}";
        string filePath = Path.Combine(_exportsDir, fileName);

        string content = ext == "json" ? ExportActiveSessionAsJson() : ExportActiveSessionAsMarkdown();
        File.WriteAllText(filePath, content, Encoding.UTF8);
        return filePath;
    }

    public IReadOnlyList<string> GetModelPresetsForProvider(ModelServerProvider provider) => provider switch
    {
        ModelServerProvider.LocalOllama =>
        [
            "llama3.2",
            "llama3.1:8b",
            "qwen2.5-coder:7b",
            "deepseek-r1:8b",
            "mistral",
            "phi3:mini"
        ],
        ModelServerProvider.LocalGlacierInference =>
        [
            "Glacier-Grandmaster-7B-Q4_K_M",
            "Glacier-Grandmaster-4B-Q4_K_M",
            "Qwen3-4B-Instruct-2507-Q4_K_M",
            "Qwen2.5-7B-Instruct-1M-Q4_K_M",
            "DeepSeek-R1-Distill-Qwen-7B-Q4_K_M",
            "Glacier-Architect-Coder-14B-Grandmaster-Q4_K_M",
            "Glacier-Architect-Coder-14B-Q4_K_M",
            "DeepSeek-R1-Distill-Qwen-14B-Q4_K_M"
        ],
        ModelServerProvider.LocalGlacierDistributed =>
        [
            "Glacier-Architect-Coder-14B-Grandmaster-Q4_K_M",
            "Glacier-Architect-Coder-14B-Q4_K_M",
            "DeepSeek-R1-Distill-Qwen-14B-Q4_K_M",
            "Qwen3-4B-Instruct-2507-Q4_K_M",
            "Qwen2.5-7B-Instruct-1M-Q4_K_M"
        ],
        ModelServerProvider.LocalNpuSidecar =>
        [
            "qwen3.5-9b",
            "qwen3-4b",
            "lfm2.5-1.2b",
            "gemma-3-4b",
            "llama3.2:3b"
        ],
        ModelServerProvider.ClusterBeastCoder =>
        [
            "qwen2.5-coder-14b-instruct-q3_k_m",
            "qwen2.5-coder-7b-instruct",
            "DeepSeek-R1-Distill-Qwen-14B-Q4_K_M"
        ],
        ModelServerProvider.LocalLMStudio =>
        [
            "local-model",
            "qwen2.5-coder-7b-instruct",
            "deepseek-r1-distill-qwen-14b",
            "meta-llama-3.1-8b-instruct"
        ],
        ModelServerProvider.RemoteGemini =>
        [
            "gemini-2.5-flash",
            "gemini-2.5-pro",
            "gemini-2.0-flash-exp",
            "gemini-1.5-pro"
        ],
        ModelServerProvider.RemoteOpenAI =>
        [
            "gpt-4o",
            "gpt-4o-mini",
            "o1-preview",
            "o1-mini",
            "gpt-3.5-turbo"
        ],
        _ => ["custom-model"]
    };

    public void SetProvider(ModelServerProvider provider)
    {
        Provider = provider;
        switch (provider)
        {
            case ModelServerProvider.LocalOllama:
                EndpointUrl = "http://127.0.0.1:11434/v1";
                SelectedModel = "llama3.2";
                break;
            case ModelServerProvider.LocalGlacierInference:
                EndpointUrl = "http://127.0.0.1:5055/v1";
                SelectedModel = "Glacier-Grandmaster-7B-Q4_K_M";
                break;
            case ModelServerProvider.LocalGlacierDistributed:
                EndpointUrl = "http://127.0.0.1:8080/v1";
                SelectedModel = "Glacier-Architect-Coder-14B-Grandmaster-Q4_K_M";
                break;
            case ModelServerProvider.LocalNpuSidecar:
                EndpointUrl = "http://127.0.0.1:52625/v1";
                SelectedModel = "qwen3.5-9b";
                break;
            case ModelServerProvider.ClusterBeastCoder:
                EndpointUrl = ResolveDefaultClusterEndpointUrl();
                SelectedModel = "qwen2.5-coder-14b-instruct-q3_k_m";
                break;
            case ModelServerProvider.LocalLMStudio:
                EndpointUrl = "http://127.0.0.1:1234/v1";
                SelectedModel = "local-model";
                break;
            case ModelServerProvider.RemoteGemini:
                EndpointUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
                SelectedModel = "gemini-2.5-flash";
                if (string.IsNullOrEmpty(ApiKey))
                {
                    ApiKey = LoadProviderApiKey(provider);
                    if (string.IsNullOrEmpty(ApiKey))
                        ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
                }
                break;
            case ModelServerProvider.RemoteOpenAI:
                EndpointUrl = "https://api.openai.com/v1";
                SelectedModel = "gpt-4o";
                if (string.IsNullOrEmpty(ApiKey))
                {
                    ApiKey = LoadProviderApiKey(provider);
                    if (string.IsNullOrEmpty(ApiKey))
                        ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
                }
                break;
        }

        ActiveSession.ModelSnapshot = GetModelDisplayName();
        NotifyStateChanged();
    }

    public string GetModelDisplayName() => Provider switch
    {
        ModelServerProvider.LocalOllama => $"Ollama · {SelectedModel}",
        ModelServerProvider.LocalGlacierInference => $"Glacier.Inference · {SelectedModel}",
        ModelServerProvider.LocalGlacierDistributed => $"Glacier.Distributed · {SelectedModel}",
        ModelServerProvider.LocalNpuSidecar => $"NPU Sidecar · {SelectedModel}",
        ModelServerProvider.ClusterBeastCoder => $"Beast Cluster · {SelectedModel}",
        ModelServerProvider.LocalLMStudio => $"LM Studio · {SelectedModel}",
        ModelServerProvider.LocalCustom => $"Local · {SelectedModel}",
        ModelServerProvider.RemoteGemini => $"Gemini · {SelectedModel}",
        ModelServerProvider.RemoteOpenAI => $"OpenAI · {SelectedModel}",
        ModelServerProvider.RemoteCustom => $"Cloud · {SelectedModel}",
        _ => SelectedModel
    };

    public static string ResolveDefaultClusterEndpointUrl() =>
        Environment.GetEnvironmentVariable("GLACIER_CLUSTER_URL") ?? "http://127.0.0.1:8080/v1";

    public static string ResolveDefaultModelsRootDir() =>
        Environment.GetEnvironmentVariable("GLACIER_MODELS_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".glacier", "models");

    public void NotifyStateChanged()
    {
        OnStateChanged?.Invoke();
    }
}

