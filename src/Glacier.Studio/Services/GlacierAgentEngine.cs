using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Glacier.Showcase.Services.GlacierStudio;

public class GlacierAgentEngine
{
    private readonly HttpClient _httpClient;
    private readonly GlacierStudioState _state;
    private readonly GlacierConsoleService _consoleService;
    private readonly GlacierDocTreePreviewService _docPreviewService;
    private readonly GlacierMcpManager _mcpManager;
    private readonly GlacierWorkspaceGraphService _graphService;
    private GlacierRuntimeLauncherService? _launcherService;
    private CancellationTokenSource? _currentCts;

    private readonly string _workspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();

    public GlacierWorkspaceGraphService GraphService => _graphService;
    public GlacierRuntimeLauncherService? LauncherService
    {
        get => _launcherService;
        set => _launcherService = value;
    }

    public GlacierAgentEngine(
        HttpClient httpClient,
        GlacierStudioState state,
        GlacierConsoleService consoleService,
        GlacierDocTreePreviewService docPreviewService,
        GlacierMcpManager mcpManager,
        GlacierWorkspaceGraphService? graphService = null,
        GlacierRuntimeLauncherService? launcherService = null)
    {
        _httpClient = httpClient;
        _state = state;
        _consoleService = consoleService;
        _docPreviewService = docPreviewService;
        _mcpManager = mcpManager;
        _graphService = graphService ?? new GlacierWorkspaceGraphService(_workspaceRoot);
        _launcherService = launcherService;
    }

    public void CancelCurrentExecution()
    {
        _currentCts?.Cancel();
        _state.ExecutionStatus = AgentExecutionStatus.Cancelled;
        _state.StatusText = "Cancelled by user";
        _state.NotifyStateChanged();
    }

    public async Task ProcessUserMessageAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        _currentCts?.Dispose();
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        // 1. Add user message to active session
        var session = _state.ActiveSession;
        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Timestamp = DateTime.Now
        });
        session.UpdatedAt = DateTime.Now;

        // Auto-title session if still default
        if (session.Title == "New Conversation" || session.Title == "Initial Chat")
        {
            session.Title = prompt.Length > 28 ? prompt[..25] + "..." : prompt;
        }

        _state.NotifyStateChanged();

        // 2. Check for slash commands
        if (prompt.StartsWith("/"))
        {
            await HandleSlashCommandAsync(prompt, ct);
            return;
        }

        // 3. Run Autonomous Agent Loop
        await RunAgentLoopAsync(prompt, ct);
    }

    private async Task HandleSlashCommandAsync(string prompt, CancellationToken ct)
    {
        var parts = prompt.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();
        string args = parts.Length > 1 ? parts[1] : "";

        var session = _state.ActiveSession;

        switch (cmd)
        {
            case "/help":
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = @"### ⚡ Glacier Super Agent Studio Slash Commands
- `/team <objective>`: Run autonomous heterogeneous agent team across Host (DeepSeek-R1) + Beast (Qwen-Coder).
- `/terminal <command>`: Directly run shell command in the interactive console.
- `/inspect <filepath>`: Load and preview any workspace file in the Glacier.DocTree viewer.
- `/graph [entity]`: Query the in-process `Glacier.Graph` Knowledge Graph ontology.
- `/mcp`: List all connected MCP servers and available tools.
- `/clear`: Clear message history and working memory in the current session.
- `/plan <objective>`: Formulate a multi-step structured engineering execution plan.
- `/goal <objective>`: Run autonomous goal-directed ReAct agent task.",
                    Timestamp = DateTime.Now
                });
                break;

            case "/clear":
                session.Messages.Clear();
                session.WorkingMemory = new StudioWorkingMemory();
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Conversation history and session working memory cleared. Ready for your instructions.",
                    Timestamp = DateTime.Now
                });
                break;

            case "/graph":
                string graphTarget = string.IsNullOrWhiteSpace(args) ? "Workspace" : args.Trim();
                string graphSummary = _graphService.QueryNeighborhoodSummary(graphTarget, maxEdges: 35);
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = graphSummary,
                    Timestamp = DateTime.Now
                });
                break;

            case "/terminal":
                if (!string.IsNullOrWhiteSpace(args))
                {
                    _state.ActiveRightTab = "console";
                    _state.NotifyStateChanged();
                    var (exitCode, output) = await _consoleService.ExecuteCommandAsync(args, "USER", ct);
                    session.Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"```powershell\n$ {args}\n# Exit code: {exitCode}\n{output}\n```",
                        Timestamp = DateTime.Now
                    });
                }
                break;

            case "/inspect":
                if (!string.IsNullOrWhiteSpace(args))
                {
                    _docPreviewService.LoadFile(args);
                    _state.ActiveRightTab = "doc";
                    session.Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"Loaded **{args}** into Document Preview pane. Inspected with `Glacier.DocTree` AST.",
                        Timestamp = DateTime.Now
                    });
                }
                break;

            case "/mcp":
                var sb = new StringBuilder("### 🛠️ Connected MCP Servers & Tools\n\n");
                foreach (var s in _mcpManager.Servers)
                {
                    sb.AppendLine($"- **{s.Name}** (`{s.StatusMessage}`):");
                    foreach (var t in s.AvailableTools)
                    {
                        sb.AppendLine($"  - `{t}`");
                    }
                }
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = sb.ToString(),
                    Timestamp = DateTime.Now
                });
                _state.ActiveRightTab = "mcp";
                break;

            case "/btw":
                await RunAgentLoopAsync($"[FAST DIRECT ANSWER]: {args}", ct);
                break;

            case "/compact":
                await CompactSessionHistoryAsync(ct);
                break;

            case "/runtimes":
            case "/telemetry":
                _state.ActiveRightTab = "telemetry";
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Opened **Hardware & Inference Runtimes** panel in the right auxiliary drawer.",
                    Timestamp = DateTime.Now
                });
                break;

            case "/browser":
                await RunAgentLoopAsync($"Please search the web or inspect online references for: {args}", ct);
                break;

            case "/schedule":
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = $"⏱ **Automation / Schedule Registered**: Scheduled task *\"{args}\"* in session memory.",
                    Timestamp = DateTime.Now
                });
                break;

            case "/grill-me":
                await RunAgentLoopAsync($"Please interview me thoroughly and ask probing questions to align on technical requirements and design decisions for: {args}", ct);
                break;

            case "/plan":
            case "/goal":
                await RunAgentLoopAsync($"Please formulate and execute a step-by-step plan for: {args}", ct);
                break;

            case "/team":
            case "/teamwork":
            case "/cluster":
                await RunHeterogeneousTeamWorkflowAsync(args, ct);
                break;

            default:
                if (cmd.StartsWith("/mcp:"))
                {
                    var segs = cmd.Substring(5).Split(':', 2);
                    if (segs.Length == 2)
                    {
                        string sName = segs[0];
                        string tName = segs[1];
                        string callArgs = string.IsNullOrWhiteSpace(args) ? "{}" : args;
                        string mcpOut = await _mcpManager.ExecuteMcpToolAsync(sName, tName, callArgs, ct);
                        session.Messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = $"### 🛠️ MCP Result: `{sName}:{tName}`\n```json\n{mcpOut}\n```",
                            Timestamp = DateTime.Now
                        });
                        break;
                    }
                }
                await RunAgentLoopAsync(prompt, ct);
                break;
        }

        _state.NotifyStateChanged();
    }

    private async Task RunAgentLoopAsync(string initialPrompt, CancellationToken ct)
    {
        _state.ActiveTurnStartUtc = DateTime.UtcNow;
        _state.ExecutionStatus = AgentExecutionStatus.Thinking;
        _state.StatusText = _state.IsAgentMode ? "Architect: Formulating Strategic Plan..." : "Thinking...";
        _state.NotifyStateChanged();

        var session = _state.ActiveSession;
        var stopwatch = Stopwatch.StartNew();

        int maxTurns = _state.IsAgentMode ? 10 : 1;
        int currentTurn = 0;
        int totalCompletionTokens = 0;
        ChatMessage? currentAssistantMsg = null;

        // Stage 1: Architect Cognitive Expansion Step via System-1 Intent Router
        AnalyzedDeveloperIntent? analyzedIntent = null;
        string? strategicPlan = null;
        if (_state.IsAgentMode)
        {
            analyzedIntent = GlacierIntentRouter.Analyze(initialPrompt, _workspaceRoot);
            strategicPlan = analyzedIntent.StrategicPlan;
            _consoleService.AddLog("ARCHITECT", $"Strategic Plan formulated: [{analyzedIntent.Archetype}] Target: '{analyzedIntent.Target}' ({analyzedIntent.Confidence * 100f:F1}% conf, {analyzedIntent.LatencyMs:F2}ms)");
        }

        int turnToolCallsExecuted = 0;
        bool isLocalModel = _state.Provider is ModelServerProvider.LocalGlacierInference
            or ModelServerProvider.LocalGlacierDistributed
            or ModelServerProvider.LocalOllama
            or ModelServerProvider.LocalLMStudio;

        int maxToolTurnsBeforeSynthesis = (analyzedIntent?.Archetype is DeveloperIntentArchetype.ArchitectureReview
            or DeveloperIntentArchetype.CodeSearch
            or DeveloperIntentArchetype.DocumentationAndSpecs)
            ? 6
            : maxTurns - 1;

        try
        {
            while (currentTurn < maxTurns && !ct.IsCancellationRequested)
            {
                currentTurn++;
                bool isSynthesisTurn = _state.IsAgentMode &&
                    (currentTurn >= maxTurns ||
                     (turnToolCallsExecuted >= maxToolTurnsBeforeSynthesis && currentTurn > maxToolTurnsBeforeSynthesis));

                _state.StatusText = isSynthesisTurn
                    ? "Phase 4: Synthesizing Ground-Truth Review..."
                    : $"Agent Cycle {currentTurn}/{maxTurns}...";
                _state.NotifyStateChanged();

                // Prepare OpenAI-compatible chat messages payload
                var messagesPayload = BuildMessagesPayload(session, strategicPlan, analyzedIntent, isSynthesisTurn);
                var toolsPayload = isSynthesisTurn ? new JsonArray() : GetToolsSchema(analyzedIntent);

                // Dual-tier routing resolution:
                // Only activate dual-tier cluster routing when in distributed/cluster mode.
                // When in LocalGlacierInference, LocalOllama, RemoteGemini, etc., respect the user's chosen local provider directly.
                string targetEndpoint;
                string targetModel;
                AgentTurnTier turnTier = AgentTurnTier.LocalFastTool;

                if (_state.Provider is ModelServerProvider.LocalGlacierDistributed or ModelServerProvider.ClusterBeastCoder)
                {
                    turnTier = (currentTurn == 1 || isSynthesisTurn)
                        ? AgentTurnTier.RemoteDeepReasoning
                        : AgentTurnTier.LocalFastTool;

                    targetEndpoint = _launcherService != null
                        ? _launcherService.ResolveEndpointForTurn(turnTier)
                        : _state.EndpointUrl;

                    targetModel = _launcherService != null
                        ? _launcherService.ResolveModelForTurn(turnTier)
                        : _state.SelectedModel;
                }
                else
                {
                    targetEndpoint = _state.EndpointUrl;
                    targetModel = _state.SelectedModel;
                }

                // Create a distinct assistant message for this turn
                currentAssistantMsg = new ChatMessage
                {
                    Role = "assistant",
                    Timestamp = DateTime.Now,
                    ExecutionPlan = currentTurn == 1 ? strategicPlan : null
                };
                session.Messages.Add(currentAssistantMsg);

                _state.ExecutionStatus = AgentExecutionStatus.StreamingResponse;
                _state.StatusText = isSynthesisTurn
                    ? $"Streaming Executive Synthesis ({targetModel})..."
                    : $"Streaming Turn {currentTurn} ({targetModel})...";
                _state.NotifyStateChanged();

                LlmStreamResult? streamResult = null;
                try
                {
                    streamResult = await StreamModelEndpointCoreAsync(
                        targetEndpoint,
                        targetModel,
                        _state.ApiKey,
                        messagesPayload,
                        toolsPayload,
                        onContentDelta: delta =>
                        {
                            currentAssistantMsg.Content += delta;
                            if (currentAssistantMsg.Content.Contains("<|im_end|>") || currentAssistantMsg.Content.Contains("<|endoftext|>"))
                            {
                                currentAssistantMsg.Content = currentAssistantMsg.Content
                                    .Replace("<|im_end|>", "")
                                    .Replace("<|endoftext|>", "")
                                    .Replace("<|im_start|>", "");
                            }
                            _state.NotifyStateChanged();
                        },
                        onReasoningDelta: delta =>
                        {
                            currentAssistantMsg.ReasoningContent = (currentAssistantMsg.ReasoningContent ?? "") + delta;
                            _state.NotifyStateChanged();
                        },
                        ct);

                    if (turnTier == AgentTurnTier.RemoteDeepReasoning && (targetEndpoint == _launcherService?.BeastEndpointUrl || targetEndpoint.Contains("192.168.1.108")))
                    {
                        _launcherService?.RecordRemoteSuccess();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (targetEndpoint == _launcherService?.BeastEndpointUrl || turnTier == AgentTurnTier.RemoteDeepReasoning)
                {
                    // Failover from Beast remote cluster to local standalone!
                    _launcherService?.RecordRemoteFailure(ex.Message);

                    string fallbackEndpoint = $"http://127.0.0.1:{_launcherService?.InferencePort ?? 5055}/v1";
                    string fallbackModel = _launcherService?.ResolveModelForTurn(AgentTurnTier.LocalFastTool) ?? _state.SelectedModel;

                    _consoleService.AddLog("HYBRID",
                        $"[FALLBACK]: Remote Beast cluster error ({ex.Message}). Automatically failing over turn {currentTurn} to local standalone runtime ({fallbackEndpoint}).",
                        isError: true);

                    currentAssistantMsg.Content = $"> ⚠️ *Beast cluster node unavailable ({ex.Message}). Seamlessly falling back to local standalone model (`{fallbackModel}`).*\n\n";
                    _state.NotifyStateChanged();

                    try
                    {
                        streamResult = await StreamModelEndpointCoreAsync(
                            fallbackEndpoint,
                            fallbackModel,
                            _state.ApiKey,
                            messagesPayload,
                            toolsPayload,
                            onContentDelta: delta =>
                            {
                                currentAssistantMsg.Content += delta;
                                if (currentAssistantMsg.Content.Contains("<|im_end|>") || currentAssistantMsg.Content.Contains("<|endoftext|>"))
                                {
                                    currentAssistantMsg.Content = currentAssistantMsg.Content
                                        .Replace("<|im_end|>", "")
                                        .Replace("<|endoftext|>", "")
                                        .Replace("<|im_start|>", "");
                                }
                                _state.NotifyStateChanged();
                            },
                            onReasoningDelta: delta =>
                            {
                                currentAssistantMsg.ReasoningContent = (currentAssistantMsg.ReasoningContent ?? "") + delta;
                                _state.NotifyStateChanged();
                            },
                            ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception fallbackEx)
                    {
                        _consoleService.AddLog("SYSTEM", $"Local fallback failed: {fallbackEx.Message}", isError: true);
                        _state.ExecutionStatus = AgentExecutionStatus.Error;
                        _state.StatusText = $"Endpoint Error: {fallbackEx.Message}";
                        currentAssistantMsg.IsError = true;
                        currentAssistantMsg.Content += $"### ⚠️ Model Endpoint Error\n\nCould not communicate with local fallback endpoint at `{fallbackEndpoint}`: `{fallbackEx.Message}`";
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _consoleService.AddLog("SYSTEM", $"Model API error: {ex.Message}", isError: true);
                    _state.ExecutionStatus = AgentExecutionStatus.Error;
                    _state.StatusText = $"Endpoint Error: {ex.Message}";
                    currentAssistantMsg.IsError = true;
                    currentAssistantMsg.Content = $"### ⚠️ Model Endpoint Error\n\nCould not communicate with model endpoint at `{targetEndpoint}`: `{ex.Message}`\n\nPlease ensure the model server is running and reachable, or switch to a different provider in the left pane.";
                    break;
                }

                if (streamResult == null) break;

                if (!string.IsNullOrWhiteSpace(currentAssistantMsg.Content))
                {
                    currentAssistantMsg.Content = currentAssistantMsg.Content
                        .Replace("<|im_end|>", "")
                        .Replace("<|endoftext|>", "")
                        .Replace("<|im_start|>", "")
                        .TrimEnd();
                }

                totalCompletionTokens += streamResult.CompletionTokens;
                if (streamResult.TotalTokens > 0)
                {
                    session.TotalTokensUsed += streamResult.TotalTokens;
                    _state.ActiveContextTokens = streamResult.TotalTokens;
                }

                // Fallback text-based tool call extraction (for open-source and local models without wire-level tool calling)
                if (streamResult.ToolCalls.Count == 0 && !string.IsNullOrWhiteSpace(currentAssistantMsg.Content))
                {
                    var (extractedCalls, cleanedContent) = ExtractToolCallsFromContent(currentAssistantMsg.Content);
                    if (extractedCalls.Count > 0)
                    {
                        currentAssistantMsg.Content = cleanedContent;
                        if (!isSynthesisTurn)
                        {
                            streamResult.ToolCalls.AddRange(extractedCalls);
                        }
                        _state.NotifyStateChanged();
                    }
                }

                // Also check ReasoningContent (thinking block) if Content didn't contain tool calls
                if (streamResult.ToolCalls.Count == 0 && !string.IsNullOrWhiteSpace(currentAssistantMsg.ReasoningContent))
                {
                    var (extractedCalls, _) = ExtractToolCallsFromContent(currentAssistantMsg.ReasoningContent);
                    if (extractedCalls.Count > 0 && !isSynthesisTurn)
                    {
                        streamResult.ToolCalls.AddRange(extractedCalls);
                    }
                }

                // Clavier Autonomous Auto-Bootstrapping:
                // If this is Turn 1 and the intent requires tool execution (e.g. WebResearchAndNews),
                // but the local model returned 0 tool calls or stalled with minimal text,
                // autonomously trigger Phase 1 of the Strategic Plan so the user is never left hanging.
                if (streamResult.ToolCalls.Count == 0 && currentTurn == 1 && !isSynthesisTurn && _state.IsAgentMode)
                {
                    if (analyzedIntent?.Archetype == DeveloperIntentArchetype.WebResearchAndNews)
                    {
                        var urlMatch = Regex.Match(initialPrompt, @"https?://[^\s""'>]+|news\.google\.com", RegexOptions.IgnoreCase);
                        if (urlMatch.Success)
                        {
                            string targetUrl = urlMatch.Value;
                            if (!targetUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                targetUrl = "https://" + targetUrl;

                            _consoleService.AddLog("CLAVIER", $"Auto-executing Phase 1 Web Retrieval for: {targetUrl}");
                            streamResult.ToolCalls.Add(new ToolCallItem
                            {
                                Id = $"call_{Guid.NewGuid():N}"[..12],
                                Name = "fetch_web_url",
                                Arguments = JsonSerializer.Serialize(new { url = targetUrl })
                            });
                        }
                        else
                        {
                            string cleanQuery = Regex.Replace(initialPrompt, @"^(?:please\s+|can\s+you\s+)?(?:search\s+for|find|get\s+news\s+on|browse|look\s+up|read\s+news\s+from|summarize)\s+", "", RegexOptions.IgnoreCase).Trim();
                            if (string.IsNullOrWhiteSpace(cleanQuery)) cleanQuery = "latest world news";

                            _consoleService.AddLog("CLAVIER", $"Auto-executing Phase 1 Web Search for: {cleanQuery}");
                            streamResult.ToolCalls.Add(new ToolCallItem
                            {
                                Id = $"call_{Guid.NewGuid():N}"[..12],
                                Name = "search_web",
                                Arguments = JsonSerializer.Serialize(new { query = cleanQuery })
                            });
                        }

                        // Clear any tiny degenerate text like "provide" or timid disclaimers so the response cleanly transitions to tool execution
                        if (currentAssistantMsg.Content.Trim().Length < 50 || currentAssistantMsg.Content.Contains("unable to browse", StringComparison.OrdinalIgnoreCase))
                        {
                            currentAssistantMsg.Content = "";
                        }
                        _state.NotifyStateChanged();
                    }
                }

                if (isSynthesisTurn)
                {
                    streamResult.ToolCalls.Clear();
                    if (string.IsNullOrWhiteSpace(currentAssistantMsg.Content) || currentAssistantMsg.Content.Trim().Length < 120)
                    {
                        _consoleService.AddLog("CLAVIER", "Appending structured ground-truth synthesis from Working Memory & Knowledge Graph.");
                        string synthesized = SynthesizeGroundTruthReport(session, analyzedIntent);
                        currentAssistantMsg.Content = string.IsNullOrWhiteSpace(currentAssistantMsg.Content)
                            ? synthesized
                            : $"{currentAssistantMsg.Content.Trim()}\n\n{synthesized}";
                        _state.NotifyStateChanged();
                    }
                    break;
                }

                // If tool calls were returned, execute each tool
                if (streamResult.ToolCalls.Count > 0 && _state.IsAgentMode)
                {
                    _state.ExecutionStatus = AgentExecutionStatus.ExecutingTools;

                    foreach (var toolCall in streamResult.ToolCalls)
                    {
                        if (ct.IsCancellationRequested) break;

                        var toolRecord = new AgentToolCallRecord
                        {
                            Id = toolCall.Id,
                            ToolName = toolCall.Name,
                            ArgumentsJson = toolCall.Arguments
                        };
                        currentAssistantMsg.ToolCalls.Add(toolRecord);

                        _state.StatusText = $"Executing {toolCall.Name}...";
                        _state.NotifyStateChanged();

                        var toolSw = Stopwatch.StartNew();
                        var (isToolSuccess, toolResult) = await ExecuteToolAsync(toolCall.Name, toolCall.Arguments, ct);
                        toolSw.Stop();
                        turnToolCallsExecuted++;

                        // Stage 4 Clavier Post-Tool Supervision (Error Recovery & Graph Path Resolution)
                        string? recoveryHint = GlacierIntentRouter.SuperviseToolExecution(
                            toolCall.Name,
                            toolCall.Arguments,
                            toolResult,
                            _graphService);

                        if (!string.IsNullOrEmpty(recoveryHint))
                        {
                            _consoleService.AddLog("CLAVIER", recoveryHint);
                            toolResult = $"{toolResult}\n\n{recoveryHint}";
                        }

                        toolRecord.IsSuccess = isToolSuccess;
                        toolRecord.Duration = toolSw.Elapsed;
                        toolRecord.ResultOutput = toolResult;

                        // Add tool message to session history for next turn
                        session.Messages.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            Content = toolResult,
                            IsError = !isToolSuccess,
                            Timestamp = DateTime.Now
                        });
                    }

                    if (!ct.IsCancellationRequested)
                    {
                        _state.ExecutionStatus = AgentExecutionStatus.Thinking;
                        _state.NotifyStateChanged();
                    }
                }
                else
                {
                    int sessionToolCount = session.Messages.Count(m => m.Role == "tool");

                    // If local model returned an empty completion after tool execution (e.g. context window limit reached),
                    // synthesize a ground-truth report from Working Memory, Knowledge Graph, and executed tool outputs.
                    if (currentAssistantMsg != null && string.IsNullOrWhiteSpace(currentAssistantMsg.Content))
                    {
                        if (sessionToolCount > 0)
                        {
                            _consoleService.AddLog("CLAVIER", "Local model returned empty completion after tool execution; synthesizing ground-truth report from Working Memory & Knowledge Graph.");
                            currentAssistantMsg.Content = SynthesizeGroundTruthReport(session, analyzedIntent);
                            _state.NotifyStateChanged();
                            break;
                        }
                        else if (string.IsNullOrWhiteSpace(currentAssistantMsg.ReasoningContent))
                        {
                            currentAssistantMsg.Content = $"*(Model `{_state.SelectedModel}` returned an empty response. Try clearing the thread with `/clear` or check the Terminal tab for inference server logs.)*";
                            _state.NotifyStateChanged();
                            break;
                        }
                    }

                    // Stage 4 Clavier In-Flight Step Supervision: evaluate whether the turn stalled or asked a timid question
                    if (_state.IsAgentMode && currentTurn < maxTurns && currentAssistantMsg != null && !string.IsNullOrWhiteSpace(currentAssistantMsg.Content))
                    {
                        var supervision = GlacierIntentRouter.SuperviseTurnCompletion(
                            currentAssistantMsg.Content,
                            currentTurn,
                            maxTurns,
                            sessionToolCount,
                            analyzedIntent,
                            session.WorkingMemory,
                            _graphService);

                        if (supervision.ShouldContinueAutonomously && !string.IsNullOrWhiteSpace(supervision.DirectiveMessage))
                        {
                            _consoleService.AddLog(
                                "CLAVIER",
                                $"In-Flight Step Supervisor ({supervision.Confidence * 100f:F1}% conf, {supervision.LatencyMs:F2}ms): {supervision.DirectiveMessage}");

                            session.Messages.Add(new ChatMessage
                            {
                                Role = "user",
                                Content = supervision.DirectiveMessage,
                                Timestamp = DateTime.Now
                            });
                            continue;
                        }
                    }

                    // No more tool calls and no pending phase transition, ReAct cycle finishes
                    break;
                }
            }

            stopwatch.Stop();
            _state.LastTurnDuration = stopwatch.Elapsed;
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

            if (totalCompletionTokens > 0 && elapsedSeconds > 0)
            {
                _state.LastTokensPerSecond = totalCompletionTokens / elapsedSeconds;
            }
            else
            {
                _state.LastTokensPerSecond = 0.0;
            }

            if (currentAssistantMsg != null)
            {
                currentAssistantMsg.DurationSeconds = elapsedSeconds;
                currentAssistantMsg.TokensPerSecond = _state.LastTokensPerSecond;
            }

            if (ct.IsCancellationRequested || _state.ExecutionStatus == AgentExecutionStatus.Cancelled)
            {
                _state.ExecutionStatus = AgentExecutionStatus.Cancelled;
                _state.StatusText = "Cancelled by user";
                if (currentAssistantMsg != null && !currentAssistantMsg.Content.Contains("Execution was cancelled"))
                {
                    currentAssistantMsg.DurationSeconds = elapsedSeconds;
                    currentAssistantMsg.Content += "\n\n*(Execution was cancelled)*";
                }
            }
            else if (_state.ExecutionStatus != AgentExecutionStatus.Error)
            {
                _state.ExecutionStatus = AgentExecutionStatus.Completed;
                _state.StatusText = _state.LastTokensPerSecond > 0 
                    ? $"Completed in {elapsedSeconds:F2}s ({_state.LastTokensPerSecond:F1} tok/s)"
                    : $"Completed in {elapsedSeconds:F2}s";
            }
        }
        catch (OperationCanceledException)
        {
            _state.ExecutionStatus = AgentExecutionStatus.Cancelled;
            _state.StatusText = "Cancelled by user";
            if (currentAssistantMsg != null)
            {
                currentAssistantMsg.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
                currentAssistantMsg.Content += "\n\n*(Execution was cancelled)*";
            }
        }
        catch (Exception ex)
        {
            _state.ExecutionStatus = AgentExecutionStatus.Error;
            _state.StatusText = $"Error: {ex.Message}";
            if (currentAssistantMsg != null)
            {
                currentAssistantMsg.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
                currentAssistantMsg.Content += $"\n\n**Agent Execution Error:** `{ex.Message}`";
            }
        }
        finally
        {
            _state.ActiveTurnStartUtc = null;
            _state.NotifyStateChanged();
        }
    }

    private async Task RunHeterogeneousTeamWorkflowAsync(string objective, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            objective = "Design and implement a high-throughput lock-free concurrency primitive with benchmark suite.";
        }

        var session = _state.ActiveSession;
        var sw = Stopwatch.StartNew();

        _state.ActiveTurnStartUtc = DateTime.UtcNow;
        _state.ExecutionStatus = AgentExecutionStatus.Thinking;
        _state.StatusText = "Agent Team Active: Spawning Host + Beast Subagents...";
        _state.NotifyStateChanged();

        var teamMsg = new ChatMessage
        {
            Role = "assistant",
            Content = $"### 🚀 Heterogeneous Agent Team Initialized\n\n" +
                      $"- 🧠 **Architect Subagent**: Host DeepSeek-R1-14B (Dual-GPU RTX 4060 + 890M :8080)\n" +
                      $"- ⚡ **Coder Subagent**: Beast Qwen2.5-Coder-14B (RTX 3060 192.168.1.108:8080)\n" +
                      $"- **Objective**: *\"{objective}\"*\n\n" +
                      $"*Phase 1: Concurrently dispatching tasks to both cluster nodes in parallel...*",
            Timestamp = DateTime.Now
        };
        session.Messages.Add(teamMsg);
        _state.NotifyStateChanged();

        string hostUrl = "http://127.0.0.1:8080/v1";
        string hostModel = "DeepSeek-R1-Distill-Qwen-14B-Q4_K_M";
        string beastUrl = "http://192.168.1.108:8080/v1";
        string beastModel = "qwen2.5-coder-14b-instruct-q3_k_m";

        _consoleService.AddLog("TEAM", $"Starting heterogeneous cluster subagents for objective: {objective}");

        try
        {
            // PHASE 1: Concurrent Parallel Execution
            // Subagent A (Host): Formal Architecture, Invariants, Memory Barrier Semantics
            // Subagent B (Beast): BenchmarkDotNet Suite & Multi-Thread Stress Harness
            var architectPrompt = new List<JsonObject>
            {
                new()
                {
                    ["role"] = "system",
                    ["content"] = "You are the Lead Systems Architect & Concurrency Specialist in an autonomous agent team. Your role is to deeply analyze the objective and define the formal data structures, cache-line false sharing padding ([StructLayout(LayoutKind.Explicit)]), memory barrier semantics (Volatile, Interlocked, MemoryBarrier), and API interface contract."
                },
                new()
                {
                    ["role"] = "user",
                    ["content"] = $"Objective: {objective}\n\nPlease provide the formal architectural blueprint, memory layouts, invariants, and interface contracts."
                }
            };

            var harnessPrompt = new List<JsonObject>
            {
                new()
                {
                    ["role"] = "system",
                    ["content"] = "You are the Performance & Benchmark Engineer in an autonomous agent team. Your role is to construct a production BenchmarkDotNet harness and multi-threaded stress verification suite for the given objective."
                },
                new()
                {
                    ["role"] = "user",
                    ["content"] = $"Objective: {objective}\n\nPlease write the complete BenchmarkDotNet harness and multi-threaded stress test runner."
                }
            };

            _state.StatusText = "Agent Team: Host (Architect) & Beast (Benchmark) running concurrently in parallel...";
            _state.NotifyStateChanged();

            var taskArchitect = SafeQueryNodeAsync(
                hostUrl, hostModel, _state.EndpointUrl, _state.SelectedModel, architectPrompt, "Host-Architect", ct);

            var taskHarness = SafeQueryNodeAsync(
                beastUrl, beastModel, _state.EndpointUrl, _state.SelectedModel, harnessPrompt, "Beast-Harness", ct);

            await Task.WhenAll(taskArchitect, taskHarness);

            var architectResult = await taskArchitect;
            var harnessResult = await taskHarness;

            _consoleService.AddLog("TEAM", $"Phase 1 complete. Architect: {architectResult.CompletionTokens} tokens, Harness: {harnessResult.CompletionTokens} tokens.");

            // Update intermediate status in chat
            teamMsg.Content += $"\n\n✓ **Phase 1 Concurrent Execution Succeeded**:\n" +
                               $"- Architect generated **{architectResult.CompletionTokens} tokens**.\n" +
                               $"- Test Lead generated **{harnessResult.CompletionTokens} tokens**.\n\n" +
                               $"*Phase 2: Handing architectural blueprint to Beast Desktop for full C# implementation...*";
            _state.StatusText = "Agent Team: Beast Desktop generating production C# implementation...";
            _state.NotifyStateChanged();

            // PHASE 2: Machine-to-Machine Handoff to Beast
            string cleanBlueprint = CleanThinkingTags(architectResult.Content);
            string cleanHarness = CleanThinkingTags(harnessResult.Content);

            var implPrompt = new List<JsonObject>
            {
                new()
                {
                    ["role"] = "system",
                    ["content"] = "You are the Lead Implementation Engineer in an autonomous agent team. Implement the complete, production-grade, zero-allocation C# solution adhering strictly to the Architect's specification and compatible with the test harness. Always specify target file paths clearly using headers or comments (e.g. // File: src/Glacier.RingBuffer/SpscQueue.cs)."
                },
                new()
                {
                    ["role"] = "user",
                    ["content"] = $"Objective: {objective}\n\n" +
                                  $"=== ARCHITECTURAL BLUEPRINT (from Host DeepSeek-R1) ===\n{cleanBlueprint}\n\n" +
                                  $"=== BENCHMARK HARNESS ===\n{cleanHarness}\n\n" +
                                  $"Now write the complete, optimized C# implementation code. For every file, indicate the target file path clearly."
                }
            };

            var implResult = await SafeQueryNodeAsync(
                beastUrl, beastModel, _state.EndpointUrl, _state.SelectedModel, implPrompt, "Beast-Implementation", ct);

            // Phase 3: Automatically parse and persist any generated code artifacts directly to disk!
            var savedFiles = new List<string>();
            savedFiles.AddRange(PersistExtractedCodeArtifacts(implResult.Content));
            savedFiles.AddRange(PersistExtractedCodeArtifacts(harnessResult.Content));
            savedFiles = savedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            string filesSection = "";
            if (savedFiles.Count > 0)
            {
                var sbFiles = new StringBuilder("\n\n### 📁 Files Written to Workspace\n");
                foreach (var f in savedFiles)
                {
                    sbFiles.AppendLine($"- ✓ `{f}`");
                }
                filesSection = sbFiles.ToString();
            }

            sw.Stop();
            double totalSec = sw.Elapsed.TotalSeconds;
            int totalTokens = architectResult.CompletionTokens + harnessResult.CompletionTokens + implResult.CompletionTokens;
            double combinedTps = totalSec > 0 ? totalTokens / totalSec : 0;

            _state.LastTurnDuration = sw.Elapsed;
            _state.LastTokensPerSecond = combinedTps;
            teamMsg.DurationSeconds = totalSec;
            teamMsg.TokensPerSecond = combinedTps;

            // Render Final Unified Report
            teamMsg.Content = 
$@"## 🚀 Heterogeneous Agent Team Synthesis

> **Team Execution Telemetry**:
> - 🧠 **Lead Architect**: Host DeepSeek-R1-14B (Dual-GPU RTX 4060 + 890M)
> - ⚡ **Implementation & Benchmark Lead**: Beast Desktop Qwen2.5-Coder-14B (RTX 3060 12GB)
> - ⏱️ **Total Wall-Clock Time**: {totalSec:F2}s · **Total Tokens**: {totalTokens} (~{combinedTps:F1} aggregate tok/s)
> - 🔄 **Transit**: Zero-copy LAN RPC machine-to-machine handoff (zero user clipboard required)
{filesSection}

---

### 🧠 Phase 1: Formal Architecture & Invariants (Host DeepSeek-R1)
<details open>
<summary>Architectural Blueprint &amp; Concurrency Model</summary>

{architectResult.Content}
</details>

---

### ⚡ Phase 2: Production Implementation (Beast Qwen2.5-Coder)
{implResult.Content}

---

### 📊 Phase 3: Benchmark & Stress Harness (Beast Qwen2.5-Coder)
<details>
<summary>BenchmarkDotNet &amp; Fuzz Harness</summary>

{harnessResult.Content}
</details>";

            _state.ExecutionStatus = AgentExecutionStatus.Completed;
            _state.StatusText = $"Agent Team Complete in {totalSec:F2}s ({combinedTps:F1} tok/s across cluster)";
        }
        catch (Exception ex)
        {
            _state.ExecutionStatus = AgentExecutionStatus.Error;
            _state.StatusText = $"Team Execution Error: {ex.Message}";
            teamMsg.Content += $"\n\n⚠️ **Agent Team Error**: `{ex.Message}`";
            _consoleService.AddLog("TEAM", $"Agent team failed: {ex.Message}", isError: true);
        }
        finally
        {
            _state.ActiveTurnStartUtc = null;
            _state.SaveActiveSession();
            _state.NotifyStateChanged();
        }
    }

    private async Task<LlmStreamResult> SafeQueryNodeAsync(
        string nodeUrl,
        string nodeModel,
        string fallbackUrl,
        string fallbackModel,
        List<JsonObject> messages,
        string nodeName,
        CancellationToken ct)
    {
        try
        {
            return await StreamModelEndpointCoreAsync(
                nodeUrl, nodeModel, null, messages, new JsonArray(), null, null, ct);
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("TEAM", $"Node '{nodeName}' ({nodeUrl}) error: {ex.Message}. Falling back to default studio endpoint ({fallbackUrl}).", isError: true);
            return await StreamModelEndpointCoreAsync(
                fallbackUrl, fallbackModel, _state.ApiKey, messages, new JsonArray(), null, null, ct);
        }
    }

    private static string CleanThinkingTags(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string text = raw;
        int start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        int end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            text = (text[..start] + text[(end + 8)..]).Trim();
        }
        else if (end >= 0 && start < 0)
        {
            text = text[(end + 8)..].Trim();
        }
        text = Regex.Replace(text, @"<｜[^>]+｜>", "").Trim();
        return text.Trim();
    }

    private List<string> PersistExtractedCodeArtifacts(string? text)
    {
        var writtenFiles = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return writtenFiles;

        // Pattern 1: Explicit <write_file path="...">content</write_file>
        var xmlMatches = Regex.Matches(text, @"<write_file\s+path=[""']([^""']+)[""']\s*>(.*?)</write_file>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in xmlMatches)
        {
            string p = m.Groups[1].Value.Trim();
            string content = m.Groups[2].Value.Trim();
            if (TrySaveArtifact(p, content, out string savedRel))
            {
                if (!writtenFiles.Contains(savedRel)) writtenFiles.Add(savedRel);
            }
        }

        // Pattern 2: Markdown headers with file paths right before code blocks
        // e.g. "create the `SpscQueue.cs` file in `src/Glacier.RingBuffer/`"
        var splitPathMatches = Regex.Matches(text, @"create (?:the )?[`""']([a-zA-Z0-9_\-]+\.[a-zA-Z0-9]+)[`""'] (?:file )?in [`""']([a-zA-Z0-9_\-\./\\]+)[`""'][^\n\r]*[\r\n]+[^\n\r`]*```(?:csharp|cs|dotnet)?\s*[\r\n]+(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in splitPathMatches)
        {
            string fileName = m.Groups[1].Value.Trim();
            string dirName = m.Groups[2].Value.Trim();
            string fullRel = Path.Combine(dirName, fileName).Replace('\\', '/');
            string content = m.Groups[3].Value.Trim();
            if (TrySaveArtifact(fullRel, content, out string savedRel))
            {
                if (!writtenFiles.Contains(savedRel)) writtenFiles.Add(savedRel);
            }
        }

        // Pattern 3: Direct file paths in text before code blocks (e.g. "in `tests/Glacier.RingBuffer.Tests/Benchmark.cs`")
        var codeBlockMatches = Regex.Matches(text, @"(?:in|at|file|path|to)\s*[`""']([a-zA-Z0-9_\-\./\\]+\.[a-zA-Z0-9]+)[`""'][^\n\r]*[\r\n]+[^\n\r`]*```(?:csharp|cs|dotnet)?\s*[\r\n]+(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in codeBlockMatches)
        {
            string p = m.Groups[1].Value.Trim();
            string content = m.Groups[2].Value.Trim();
            if (TrySaveArtifact(p, content, out string savedRel))
            {
                if (!writtenFiles.Contains(savedRel)) writtenFiles.Add(savedRel);
            }
        }

        // Pattern 4: In-code comment header "// File: <path>"
        var commentMatches = Regex.Matches(text, @"```(?:csharp|cs|dotnet)?\s*[\r\n]+(?://|#)\s*(?:File|Path|filepath):\s*[`""']?([a-zA-Z0-9_\-\./\\]+\.[a-zA-Z0-9]+)[`""']?\s*[\r\n]+(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in commentMatches)
        {
            string p = m.Groups[1].Value.Trim();
            string content = m.Groups[2].Value.Trim();
            if (TrySaveArtifact(p, content, out string savedRel))
            {
                if (!writtenFiles.Contains(savedRel)) writtenFiles.Add(savedRel);
            }
        }

        return writtenFiles;
    }

    private bool TrySaveArtifact(string rawPath, string content, out string savedRelPath)
    {
        savedRelPath = "";
        try
        {
            rawPath = rawPath.Trim().Trim('`', '"', '\'');
            if (string.IsNullOrWhiteSpace(rawPath) || string.IsNullOrWhiteSpace(content)) return false;

            if (!TryResolveWorkspacePath(rawPath, allowEmptyAsRoot: false, out string fullPath, out _))
            {
                return false;
            }

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, content, Encoding.UTF8);
            _docPreviewService.LoadFile(fullPath);
            _graphService.IngestFileContent(fullPath, content);
            savedRelPath = _graphService.NormalizeRelativePath(fullPath);
            _state.ActiveSession.WorkingMemory.RecordFact($"Created file '{savedRelPath}' ({content.Length} chars)");
            _consoleService.AddLog("TEAM", $"Auto-persisted artifact to disk: {savedRelPath} ({content.Length} chars)");
            return true;
        }
        catch (Exception ex)
        {
            _consoleService.AddLog("TEAM", $"Failed to auto-persist artifact '{rawPath}': {ex.Message}", isError: true);
            return false;
        }
    }

    private async Task CompactSessionHistoryAsync(CancellationToken ct)
    {
        var session = _state.ActiveSession;
        if (session.Messages.Count <= 2)
        {
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "Conversation is already compact (2 or fewer messages).",
                Timestamp = DateTime.Now
            });
            return;
        }

        _state.ExecutionStatus = AgentExecutionStatus.Thinking;
        _state.StatusText = "Compacting conversation history...";
        _state.NotifyStateChanged();

        try
        {
            var textToSummarize = new StringBuilder();
            foreach (var m in session.Messages)
            {
                if (m.Role == "user") textToSummarize.AppendLine($"User: {m.Content}");
                else if (m.Role == "assistant")
                {
                    var (clean, _) = ExtractThinkingFromText(m.Content ?? "");
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        textToSummarize.AppendLine($"Assistant: {clean}");
                    }
                }
            }

            var promptMsgs = new List<JsonObject>
            {
                new()
                {
                    ["role"] = "system",
                    ["content"] = "You are an expert conversation summarizer. Distill the following conversation into a concise, high-density structured markdown summary covering key architectural decisions, goals, code changes, and open items."
                },
                new()
                {
                    ["role"] = "user",
                    ["content"] = textToSummarize.ToString()
                }
            };

            var summaryRes = await SafeQueryNodeAsync(_state.EndpointUrl, _state.SelectedModel, _state.EndpointUrl, _state.SelectedModel, promptMsgs, "Compactor", ct);
            string summary = summaryRes.Content;

            session.Messages.Clear();
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"### 🗜️ Conversation History Compacted\n*Previous message context was compressed to free up the context window.*\n\n**Structured Session Summary:**\n\n{summary}",
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"Error compacting conversation: {ex.Message}",
                Timestamp = DateTime.Now
            });
        }
        finally
        {
            _state.ExecutionStatus = AgentExecutionStatus.Idle;
            _state.StatusText = "Ready";
            _state.NotifyStateChanged();
        }
    }

    private static (string CleanText, string Thought) ExtractThinkingFromText(string text)
    {
        if (string.IsNullOrEmpty(text)) return ("", "");
        text = Regex.Replace(text, @"<｜[^>]+｜>", "").Trim();

        int start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        int end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            string thought = text.Substring(start + 7, end - (start + 7)).Trim();
            string clean = (text.Substring(0, start) + text.Substring(end + 8)).Trim();
            return (clean, thought);
        }
        if (end >= 0 && start < 0)
        {
            string thought = text.Substring(0, end).Trim();
            string clean = text.Substring(end + 8).Trim();
            return (clean, thought);
        }
        return (text.Trim(), "");
    }

    private bool TryResolveWorkspacePath(string rawPath, bool allowEmptyAsRoot, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            if (allowEmptyAsRoot)
            {
                fullPath = Path.GetFullPath(_workspaceRoot);
                return true;
            }
            error = "Error: 'path' argument is required.";
            return false;
        }

        try
        {
            string normalizedWorkspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_workspaceRoot));
            string candidate = Path.IsPathRooted(rawPath)
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(Path.Combine(normalizedWorkspace, rawPath));

            string normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
            bool isWithinWorkspace =
                normalizedCandidate.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) ||
                normalizedCandidate.StartsWith(normalizedWorkspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            if (!isWithinWorkspace)
            {
                error = $"Error: Path '{rawPath}' resolves outside the workspace boundary ('{_workspaceRoot}').";
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Error: Invalid path '{rawPath}': {ex.Message}";
            return false;
        }
    }

    private async Task<(bool IsSuccess, string Output)> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        _consoleService.AddLog("AGENT", $"Tool Call: {toolName}({argumentsJson})");
        var memory = _state.ActiveSession.WorkingMemory;

        try
        {
            var root = ToolArgumentSanitizer.ParseAndSanitize(argumentsJson);

            switch (toolName.ToLowerInvariant())
            {
                case "run_command":
                    string cmd = root.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(cmd) && root.TryGetProperty("raw", out var rawCmdProp))
                    {
                        cmd = rawCmdProp.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(cmd)) return (false, "Error: 'command' argument is required.");
                    
                    var (exitCode, output) = await _consoleService.ExecuteCommandAsync(cmd, "AGENT", ct);
                    memory.AddTrace($"Ran command '{cmd}' (Exit: {exitCode})");
                    return (exitCode == 0, $"Exit Code: {exitCode}\nOutput:\n{output}");

                case "read_file":
                    string path = root.TryGetProperty("path", out var pProp) ? pProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(path) && root.TryGetProperty("raw", out var rawPathProp))
                    {
                        path = rawPathProp.GetString() ?? "";
                    }
                    if (!TryResolveWorkspacePath(path, allowEmptyAsRoot: false, out string fullPath, out string readErr))
                    {
                        return (false, readErr);
                    }
                    if (!File.Exists(fullPath))
                    {
                        // Attempt Knowledge Graph symbol/filename resolution within workspace
                        string? resolvedCandidate = _graphService.TryResolveFilePath(path);
                        if (!string.IsNullOrEmpty(resolvedCandidate) &&
                            TryResolveWorkspacePath(resolvedCandidate, allowEmptyAsRoot: false, out string safeResolved, out _) &&
                            File.Exists(safeResolved))
                        {
                            fullPath = safeResolved;
                        }
                        else
                        {
                            return (false, $"Error: File '{path}' does not exist.");
                        }
                    }
                    
                    string content = await File.ReadAllTextAsync(fullPath, ct);
                    _docPreviewService.LoadFile(fullPath);

                    // Stage 4: Ingest AST symbols into Knowledge Graph & Session Working Memory
                    var discoveredSymbols = _graphService.IngestFileContent(fullPath, content);
                    string relReadPath = _graphService.NormalizeRelativePath(fullPath);
                    memory.RecordFileInspection(relReadPath, content.Length, discoveredSymbols);

                    // If reading a PROJECT_SOURCE.md bundle, skip the redundant Table of Contents + CI YAML + duplicate README
                    // so the active context window receives Overview + Directory Tree + actual Core Library C# Source Code (src/)
                    if (Path.GetFileName(fullPath).Equals("PROJECT_SOURCE.md", StringComparison.OrdinalIgnoreCase) && content.Length > 3000)
                    {
                        int cutStartIdx = content.IndexOf("## Table of Contents", StringComparison.OrdinalIgnoreCase);
                        if (cutStartIdx < 0)
                        {
                            cutStartIdx = content.IndexOf("# Project Configuration & Metadata", StringComparison.OrdinalIgnoreCase);
                        }
                        int coreSrcIdx = content.IndexOf("# Core Library Source Code", StringComparison.OrdinalIgnoreCase);
                        if (coreSrcIdx < 0)
                        {
                            coreSrcIdx = content.IndexOf("## `src/", StringComparison.OrdinalIgnoreCase);
                        }
                        if (cutStartIdx > 0 && coreSrcIdx > cutStartIdx)
                        {
                            content = content[..cutStartIdx].TrimEnd() + "\n\n" + content[coreSrcIdx..];
                        }
                    }

                    return (true, content.Length > 8000 ? content[..8000] + "\n...(truncated)" : content);

                case "write_file":
                    string wPath = root.TryGetProperty("path", out var wpProp) ? wpProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(wPath) && root.TryGetProperty("raw", out var rawWPathProp))
                    {
                        wPath = rawWPathProp.GetString() ?? "";
                    }
                    if (!TryResolveWorkspacePath(wPath, allowEmptyAsRoot: false, out string fullWPath, out string writeErr))
                    {
                        return (false, writeErr);
                    }
                    string wContent = root.TryGetProperty("content", out var wcProp) ? wcProp.GetString() ?? "" : "";
                    
                    string? dir = Path.GetDirectoryName(fullWPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    await File.WriteAllTextAsync(fullWPath, wContent, ct);
                    _docPreviewService.LoadFile(fullWPath);

                    // Stage 4: Update Knowledge Graph & Working Memory with modified file
                    var writtenSymbols = _graphService.IngestFileContent(fullWPath, wContent);
                    string relWritePath = _graphService.NormalizeRelativePath(fullWPath);
                    memory.RecordFileInspection(relWritePath, wContent.Length, writtenSymbols);
                    memory.RecordFact($"Wrote {wContent.Length} chars to '{relWritePath}'");

                    _consoleService.AddLog("AGENT", $"File created/updated: {wPath} ({wContent.Length} chars)");
                    return (true, $"Successfully wrote {wContent.Length} characters to '{wPath}'.");

                case "delete_file":
                    string dPath = root.TryGetProperty("path", out var dpDelProp) ? dpDelProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(dPath) && root.TryGetProperty("raw", out var rawDPathProp))
                    {
                        dPath = rawDPathProp.GetString() ?? "";
                    }
                    if (!TryResolveWorkspacePath(dPath, allowEmptyAsRoot: false, out string fullDPath, out string delErr))
                    {
                        return (false, delErr);
                    }
                    if (!File.Exists(fullDPath))
                    {
                        return (false, $"Error: File '{dPath}' does not exist.");
                    }
                    File.Delete(fullDPath);
                    string relDelPath = _graphService.NormalizeRelativePath(fullDPath);
                    memory.RecordFact($"Deleted file '{relDelPath}'");
                    _consoleService.AddLog("AGENT", $"File deleted: {relDelPath}");
                    return (true, $"Successfully deleted '{relDelPath}'.");

                case "list_directory":
                    string dirPath = root.TryGetProperty("path", out var dpProp) ? dpProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(dirPath) && root.TryGetProperty("raw", out var rawDirProp))
                    {
                        dirPath = rawDirProp.GetString() ?? "";
                    }
                    if (!TryResolveWorkspacePath(dirPath, allowEmptyAsRoot: true, out string targetDir, out string dirErr))
                    {
                        return (false, dirErr);
                    }
                    if (!Directory.Exists(targetDir))
                    {
                        // Fuzzy recovery for common LLM path mistakes (e.g. "src/Glacier.Gpu" instead of "Glacier.Gpu/src/Glacier.Gpu")
                        string leaf = Path.GetFileName(dirPath.TrimEnd('/', '\\'));
                        string candSub = Path.Combine(_workspaceRoot, leaf, "src", leaf);
                        string candRoot = Path.Combine(_workspaceRoot, leaf);
                        if (Directory.Exists(candSub))
                        {
                            targetDir = candSub;
                        }
                        else if (Directory.Exists(candRoot))
                        {
                            targetDir = candRoot;
                        }
                        else
                        {
                            var matchingDir = Directory.GetDirectories(_workspaceRoot, leaf, SearchOption.TopDirectoryOnly).FirstOrDefault();
                            if (matchingDir != null)
                            {
                                targetDir = matchingDir;
                            }
                            else
                            {
                                return (false, $"Error: Directory '{dirPath}' not found.");
                            }
                        }
                    }

                    var entries = Directory.GetFileSystemEntries(targetDir);
                    var subDirs = new List<string>();
                    var fileNames = new List<string>();
                    var formattedEntries = new List<string>();
                    var sb = new StringBuilder();

                    foreach (var f in entries)
                    {
                        string name = Path.GetFileName(f);
                        bool isDir = Directory.Exists(f);
                        if (isDir) subDirs.Add(name);
                        else fileNames.Add(name);

                        string formatted = name + (isDir ? "/" : "");
                        formattedEntries.Add(formatted);
                        sb.AppendLine(formatted);
                    }

                    // Stage 4: Populate Knowledge Graph & Working Memory
                    string relDir = _graphService.NormalizeRelativePath(targetDir);
                    _graphService.IngestDirectory(relDir, subDirs, fileNames);
                    memory.RecordDirectoryListing(relDir, formattedEntries);

                    return (true, sb.ToString());

                case "search_workspace":
                    string searchQuery = root.TryGetProperty("query", out var sqProp) ? sqProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(searchQuery) && root.TryGetProperty("path", out var spProp))
                        searchQuery = spProp.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(searchQuery) && root.TryGetProperty("raw", out var srProp))
                        searchQuery = srProp.GetString() ?? "";

                    string? scopePath = root.TryGetProperty("scope", out var scProp) ? scProp.GetString() : null;
                    string searchOutput = _graphService.SearchWorkspace(searchQuery, scopePath);
                    memory.AddTrace($"Searched workspace for '{searchQuery}'");
                    return (true, searchOutput);

                case "query_knowledge_graph":
                    string kgEntity = root.TryGetProperty("entity", out var kgProp) ? kgProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(kgEntity) && root.TryGetProperty("query", out var kgqProp))
                        kgEntity = kgqProp.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(kgEntity) && root.TryGetProperty("path", out var kgpProp))
                        kgEntity = kgpProp.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(kgEntity) && root.TryGetProperty("raw", out var kgrProp))
                        kgEntity = kgrProp.GetString() ?? "";

                    if (string.IsNullOrWhiteSpace(kgEntity)) kgEntity = "Workspace";
                    string kgResult = _graphService.QueryNeighborhoodSummary(kgEntity, maxEdges: 30);
                    memory.AddTrace($"Queried Knowledge Graph for '{kgEntity}'");
                    return (true, kgResult);

                case "inspect_document":
                    string docFile = root.TryGetProperty("path", out var dfProp) ? dfProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(docFile) && root.TryGetProperty("raw", out var rawDocProp))
                    {
                        docFile = rawDocProp.GetString() ?? "";
                    }
                    if (!TryResolveWorkspacePath(docFile, allowEmptyAsRoot: false, out string fullDocPath, out string docErr))
                    {
                        return (false, docErr);
                    }
                    if (!File.Exists(fullDocPath)) return (false, $"Error: File '{docFile}' does not exist.");

                    _docPreviewService.LoadFile(fullDocPath);
                    _state.ActiveRightTab = "doc";
                    memory.AddTrace($"Inspected document AST for '{docFile}'");
                    return (true, $"Document '{docFile}' loaded into DocTree Preview. Word Count: {_docPreviewService.ActiveDocument.WordCount}, Outline items: {_docPreviewService.ActiveDocument.Outline.Count}.");

                case "call_mcp_tool":
                    string server = root.TryGetProperty("server", out var sProp) ? sProp.GetString() ?? "" : "SharpTools";
                    string mcpTool = root.TryGetProperty("tool", out var tProp) ? tProp.GetString() ?? "" : "query";
                    string mcpArgs = root.TryGetProperty("arguments", out var aProp) ? aProp.ToString() : "{}";
                    string mcpResult = await _mcpManager.ExecuteMcpToolAsync(server, mcpTool, mcpArgs, ct);
                    bool mcpSuccess = !mcpResult.Contains("\"error\": true", StringComparison.OrdinalIgnoreCase) &&
                                      !mcpResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
                    memory.AddTrace($"Called MCP {server}.{mcpTool} (Success: {mcpSuccess})");
                    return (mcpSuccess, mcpResult);

                case "fetch_web_url":
                    string webUrl = root.TryGetProperty("url", out var uProp) ? uProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(webUrl) && root.TryGetProperty("path", out var pPropUrl))
                    {
                        webUrl = pPropUrl.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webUrl) && root.TryGetProperty("link", out var lProp))
                    {
                        webUrl = lProp.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webUrl) && root.TryGetProperty("target", out var tPropUrl))
                    {
                        webUrl = tPropUrl.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webUrl) && root.TryGetProperty("raw", out var rawUProp))
                    {
                        webUrl = rawUProp.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webUrl)) return (false, "Error: 'url' parameter is required.");
                    var webContent = await FetchWebUrlContentAsync(webUrl, ct);
                    memory.AddTrace($"Fetched web URL '{webUrl}' ({webContent.Length} chars)");
                    return (true, webContent);

                case "search_web":
                    string webQuery = root.TryGetProperty("query", out var wqProp) ? wqProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(webQuery) && root.TryGetProperty("q", out var qProp))
                    {
                        webQuery = qProp.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webQuery) && root.TryGetProperty("search", out var sPropSearch))
                    {
                        webQuery = sPropSearch.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webQuery) && root.TryGetProperty("topic", out var topProp))
                    {
                        webQuery = topProp.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webQuery) && root.TryGetProperty("path", out var pPropSearch))
                    {
                        webQuery = pPropSearch.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webQuery) && root.TryGetProperty("raw", out var rawWqProp))
                    {
                        webQuery = rawWqProp.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(webQuery)) return (false, "Error: 'query' parameter is required.");
                    var searchResult = await SearchWebAsync(webQuery, ct);
                    memory.AddTrace($"Searched web for '{webQuery}'");
                    return (true, searchResult);

                default:
                    return (false, $"Error: Unknown tool '{toolName}'.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Tool Execution Exception: {ex.Message}");
        }
    }

    private static HttpRequestMessage CreateBrowserLikeHttpRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        req.Headers.Add("Sec-Ch-Ua", "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
        req.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
        req.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        req.Headers.Add("Sec-Fetch-Dest", "document");
        req.Headers.Add("Sec-Fetch-Mode", "navigate");
        req.Headers.Add("Sec-Fetch-Site", "none");
        req.Headers.Add("Sec-Fetch-User", "?1");
        req.Headers.Add("Upgrade-Insecure-Requests", "1");
        req.Headers.Add("Cookie", "CONSENT=YES+; SOCS=CAESHAgBEhJnd3NfMjAyMzA4MTAtMF9SQzIaAmVuIAEaBgiA_LymBg; 1P_JAR=2024-04-01-00;");
        return req;
    }

    private async Task<string> FetchWebUrlContentAsync(string url, CancellationToken ct)
    {
        try
        {
            string targetUrl = url.Trim();

            // Intelligent rewrite: If user requested news.google.com without specific RSS path,
            // route directly to Google News RSS feed to bypass JavaScript client render & consent wall
            if (targetUrl.Contains("news.google.com", StringComparison.OrdinalIgnoreCase) &&
                !targetUrl.Contains("/rss", StringComparison.OrdinalIgnoreCase))
            {
                targetUrl = "https://news.google.com/rss?hl=en-US&gl=US&ceid=US:en";
            }

            var req = CreateBrowserLikeHttpRequest(targetUrl);
            var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return $"HTTP Error {(int)resp.StatusCode}: {resp.ReasonPhrase}";
            }

            string raw = await resp.Content.ReadAsStringAsync(ct);

            // If a consent barrier was returned anyway (e.g. EU/UK IP), fall back to Google News RSS feed
            if ((raw.Contains("Before you continue to Google", StringComparison.OrdinalIgnoreCase) ||
                 raw.Contains("consent.google.com", StringComparison.OrdinalIgnoreCase)) &&
                !targetUrl.Contains("/rss", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackReq = CreateBrowserLikeHttpRequest("https://news.google.com/rss?hl=en-US&gl=US&ceid=US:en");
                var fallbackResp = await _httpClient.SendAsync(fallbackReq, ct);
                if (fallbackResp.IsSuccessStatusCode)
                {
                    string rssXml = await fallbackResp.Content.ReadAsStringAsync(ct);
                    return ParseRssFeed(rssXml, maxItems: 12);
                }
            }

            // If RSS / XML feed (e.g. Google News RSS, blog feeds)
            if (raw.Contains("<rss", StringComparison.OrdinalIgnoreCase) || 
                raw.Contains("<feed", StringComparison.OrdinalIgnoreCase) || 
                targetUrl.EndsWith(".rss", StringComparison.OrdinalIgnoreCase) || 
                targetUrl.Contains("/rss", StringComparison.OrdinalIgnoreCase))
            {
                return ParseRssFeed(raw, maxItems: 12);
            }

            return CleanHtmlToMarkdown(raw, maxChars: 6000);
        }
        catch (Exception ex)
        {
            return $"Error fetching URL '{url}': {ex.Message}";
        }
    }

    private async Task<string> SearchWebAsync(string query, CancellationToken ct)
    {
        try
        {
            // Google News RSS Search with spoofed browser headers
            string newsUrl = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl=en-US&gl=US&ceid=US:en";
            var req = CreateBrowserLikeHttpRequest(newsUrl);
            var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                string xml = await resp.Content.ReadAsStringAsync(ct);
                string parsedNews = ParseRssFeed(xml, maxItems: 10);
                if (!string.IsNullOrWhiteSpace(parsedNews) && !parsedNews.Contains("No items found"))
                {
                    return $"### 🌐 Google News Results for: \"{query}\"\n\n{parsedNews}";
                }
            }

            // Fallback: DuckDuckGo HTML Instant Search
            string ddgUrl = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            var ddgReq = CreateBrowserLikeHttpRequest(ddgUrl);
            var ddgResp = await _httpClient.SendAsync(ddgReq, ct);
            if (ddgResp.IsSuccessStatusCode)
            {
                string html = await ddgResp.Content.ReadAsStringAsync(ct);
                return CleanHtmlToMarkdown(html, maxChars: 5000);
            }

            return $"No search results found for: \"{query}\"";
        }
        catch (Exception ex)
        {
            return $"Web search exception: {ex.Message}";
        }
    }

    private static string ParseRssFeed(string xml, int maxItems)
    {
        var sb = new StringBuilder();
        var itemMatches = Regex.Matches(xml, @"<item>(.*?)</item>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        int count = 0;
        foreach (Match m in itemMatches)
        {
            if (count++ >= maxItems) break;
            string itemXml = m.Groups[1].Value;

            string title = Regex.Match(itemXml, @"<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;
            string link = Regex.Match(itemXml, @"<link>(.*?)</link>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;
            string pubDate = Regex.Match(itemXml, @"<pubDate>(.*?)</pubDate>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;
            string source = Regex.Match(itemXml, @"<source[^>]*>(.*?)</source>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;

            // Strip CDATA
            title = Regex.Replace(title, @"<!\[CDATA\[(.*?)\]\]>", "$1", RegexOptions.Singleline).Trim();
            link = Regex.Replace(link, @"<!\[CDATA\[(.*?)\]\]>", "$1", RegexOptions.Singleline).Trim();
            source = Regex.Replace(source, @"<!\[CDATA\[(.*?)\]\]>", "$1", RegexOptions.Singleline).Trim();

            sb.AppendLine($"{count}. **{title}**");
            if (!string.IsNullOrEmpty(source) || !string.IsNullOrEmpty(pubDate))
            {
                sb.AppendLine($"   *Source: {source} | Date: {pubDate}*");
            }
            if (!string.IsNullOrEmpty(link))
            {
                sb.AppendLine($"   [Read Article]({link})");
            }
            sb.AppendLine();
        }

        if (sb.Length == 0)
        {
            return "No items found in feed.";
        }
        return sb.ToString().Trim();
    }

    private static string CleanHtmlToMarkdown(string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // Remove script, style, head, nav, footer, noscript tags
        html = Regex.Replace(html, @"<(script|style|head|nav|footer|noscript)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Convert common block elements to newlines
        html = Regex.Replace(html, @"<(?:h[1-6]|p|div|br|li)[^>]*>", "\n", RegexOptions.IgnoreCase);

        // Strip remaining HTML tags
        html = Regex.Replace(html, @"<[^>]+>", " ", RegexOptions.Singleline);

        // Decode HTML entities
        html = System.Net.WebUtility.HtmlDecode(html);

        // Collapse whitespace and multiple newlines
        html = Regex.Replace(html, @"[ \t]+", " ");
        html = Regex.Replace(html, @"\n\s*\n+", "\n\n");

        html = html.Trim();
        if (html.Length > maxChars)
        {
            html = html[..maxChars] + "\n...(truncated)";
        }
        return html;
    }

    private Task<LlmStreamResult> StreamModelEndpointAsync(
        List<JsonObject> messages,
        JsonArray tools,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct)
    {
        return StreamModelEndpointCoreAsync(
            _state.EndpointUrl,
            _state.SelectedModel,
            _state.ApiKey,
            messages,
            tools,
            onContentDelta,
            onReasoningDelta,
            ct);
    }

    private async Task<LlmStreamResult> StreamModelEndpointCoreAsync(
        string endpointUrl,
        string modelName,
        string? apiKey,
        List<JsonObject> messages,
        JsonArray tools,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct)
    {
        string endpoint = (endpointUrl ?? _state.EndpointUrl).TrimEnd('/');
        string url = endpoint.EndsWith("/chat/completions") ? endpoint : $"{endpoint}/chat/completions";

        var payload = new JsonObject
        {
            ["model"] = modelName ?? _state.SelectedModel,
            ["messages"] = new JsonArray(messages.Select(m => (JsonObject)m.DeepClone()).ToArray()),
            ["temperature"] = _state.Temperature,
            ["repetition_penalty"] = _state.RepetitionPenalty,
            ["max_tokens"] = _state.MaxTokens,
            ["stream"] = true
        };

        if (_state.IsAgentMode && tools.Count > 0)
        {
            payload["tools"] = (JsonArray)tools.DeepClone();
            payload["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        string effectiveKey = !string.IsNullOrEmpty(apiKey) ? apiKey : _state.ApiKey;
        if (!string.IsNullOrEmpty(effectiveKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effectiveKey);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            string err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Model API error {(int)response.StatusCode}: {err}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            string responseJson = await response.Content.ReadAsStringAsync(ct);
            return ParseNonStreamingResponse(responseJson, onContentDelta, onReasoningDelta);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var toolAccumulators = new Dictionary<int, StreamingToolCallAccumulator>();
        var fullContent = new StringBuilder();
        var fullReasoning = new StringBuilder();
        int tokenCount = 0;
        int completionTokens = 0;
        int promptTokens = 0;
        int totalTokens = 0;
        bool inThinkBlock = false;
        string pendingTagBuffer = "";

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith(":")) continue;

            string lineTrimmed = line.Trim();
            if (!lineTrimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            string data = lineTrimmed.Substring(5).Trim();
            if (data == "[DONE]") break;
            if (string.IsNullOrEmpty(data)) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
                {
                    if (usageElem.TryGetProperty("completion_tokens", out var ctProp) && ctProp.TryGetInt32(out int ctVal))
                        completionTokens = ctVal;
                    if (usageElem.TryGetProperty("prompt_tokens", out var ptProp) && ptProp.TryGetInt32(out int ptVal))
                        promptTokens = ptVal;
                    if (usageElem.TryGetProperty("total_tokens", out var ttProp) && ttProp.TryGetInt32(out int ttVal))
                        totalTokens = ttVal;
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var firstChoice = choices[0];
                if (!firstChoice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // 1. Check reasoning_content delta (DeepSeek-R1 / Qwen2.5)
                if (delta.TryGetProperty("reasoning_content", out var rcProp) && rcProp.ValueKind == JsonValueKind.String)
                {
                    string rToken = rcProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(rToken))
                    {
                        tokenCount++;
                        fullReasoning.Append(rToken);
                        onReasoningDelta?.Invoke(rToken);
                    }
                }

                // 2. Check content delta (with cross-chunk <think> / </think> boundary handling)
                if (delta.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                {
                    string cToken = cProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(cToken))
                    {
                        tokenCount++;
                        ProcessStreamContentChunk(
                            cToken,
                            ref inThinkBlock,
                            ref pendingTagBuffer,
                            fullContent,
                            fullReasoning,
                            onContentDelta,
                            onReasoningDelta);
                    }
                }

                // 3. Tool call streaming deltas
                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        int index = tc.TryGetProperty("index", out var idxProp) && idxProp.TryGetInt32(out int idx) ? idx : 0;
                        if (!toolAccumulators.TryGetValue(index, out var acc))
                        {
                            acc = new StreamingToolCallAccumulator { Index = index };
                            toolAccumulators[index] = acc;
                        }

                        if (tc.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        {
                            string idVal = idProp.GetString() ?? "";
                            if (!string.IsNullOrEmpty(idVal)) acc.Id = idVal;
                        }

                        if (tc.TryGetProperty("function", out var fnProp) && fnProp.ValueKind == JsonValueKind.Object)
                        {
                            if (fnProp.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                            {
                                string nVal = nProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(nVal)) acc.Name += nVal;
                            }
                            if (fnProp.TryGetProperty("arguments", out var aProp) && aProp.ValueKind == JsonValueKind.String)
                            {
                                string aVal = aProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(aVal)) acc.ArgumentsBuilder.Append(aVal);
                            }
                        }
                    }
                }
            }
        }

        // Flush any remaining partial tag buffer at end of stream
        if (!string.IsNullOrEmpty(pendingTagBuffer))
        {
            if (inThinkBlock)
            {
                fullReasoning.Append(pendingTagBuffer);
                onReasoningDelta?.Invoke(pendingTagBuffer);
            }
            else
            {
                fullContent.Append(pendingTagBuffer);
                onContentDelta?.Invoke(pendingTagBuffer);
            }
        }

        var finalToolCalls = toolAccumulators.Values
            .OrderBy(a => a.Index)
            .Select(a => new ToolCallItem
            {
                Id = string.IsNullOrEmpty(a.Id) ? $"call_{Guid.NewGuid():N}"[..12] : a.Id,
                Name = a.Name,
                Arguments = a.ArgumentsBuilder.ToString()
            })
            .Where(tc => !string.IsNullOrEmpty(tc.Name))
            .ToList();

        return new LlmStreamResult
        {
            Content = fullContent.ToString(),
            Reasoning = fullReasoning.ToString(),
            ToolCalls = finalToolCalls,
            TotalTokens = totalTokens > 0 ? totalTokens : tokenCount,
            CompletionTokens = completionTokens > 0 ? completionTokens : tokenCount,
            PromptTokens = promptTokens
        };
    }

    private static void ProcessStreamContentChunk(
        string chunk,
        ref bool inThinkBlock,
        ref string pendingTagBuffer,
        StringBuilder fullContent,
        StringBuilder fullReasoning,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta)
    {
        string buffer = pendingTagBuffer + chunk;
        pendingTagBuffer = "";

        while (!string.IsNullOrEmpty(buffer))
        {
            if (!inThinkBlock)
            {
                int idx = buffer.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string before = buffer[..idx];
                    if (!string.IsNullOrEmpty(before))
                    {
                        fullContent.Append(before);
                        onContentDelta?.Invoke(before);
                    }
                    inThinkBlock = true;
                    buffer = buffer[(idx + 7)..];
                    continue;
                }

                int partialLen = GetTrailingPartialPrefixLength(buffer, "<think>");
                if (partialLen > 0)
                {
                    pendingTagBuffer = buffer[^partialLen..];
                    buffer = buffer[..^partialLen];
                }

                if (!string.IsNullOrEmpty(buffer))
                {
                    fullContent.Append(buffer);
                    onContentDelta?.Invoke(buffer);
                    buffer = "";
                }
            }
            else
            {
                int endIdx = buffer.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (endIdx >= 0)
                {
                    string thinkPart = buffer[..endIdx];
                    if (!string.IsNullOrEmpty(thinkPart))
                    {
                        fullReasoning.Append(thinkPart);
                        onReasoningDelta?.Invoke(thinkPart);
                    }
                    inThinkBlock = false;
                    buffer = buffer[(endIdx + 8)..];
                    continue;
                }

                int partialLen = GetTrailingPartialPrefixLength(buffer, "</think>");
                if (partialLen > 0)
                {
                    pendingTagBuffer = buffer[^partialLen..];
                    buffer = buffer[..^partialLen];
                }

                if (!string.IsNullOrEmpty(buffer))
                {
                    fullReasoning.Append(buffer);
                    onReasoningDelta?.Invoke(buffer);
                    buffer = "";
                }
            }
        }
    }

    private static int GetTrailingPartialPrefixLength(string text, string tag)
    {
        int maxLen = Math.Min(text.Length, tag.Length - 1);
        for (int len = maxLen; len >= 1; len--)
        {
            if (text.AsSpan(text.Length - len).Equals(tag.AsSpan(0, len), StringComparison.OrdinalIgnoreCase))
            {
                return len;
            }
        }
        return 0;
    }

    private static LlmStreamResult ParseNonStreamingResponse(
        string json,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta)
    {
        var result = new LlmStreamResult();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
        {
            if (usageElem.TryGetProperty("completion_tokens", out var ctProp))
                result.CompletionTokens = ctProp.GetInt32();
            if (usageElem.TryGetProperty("prompt_tokens", out var ptProp))
                result.PromptTokens = ptProp.GetInt32();
            if (usageElem.TryGetProperty("total_tokens", out var ttProp))
                result.TotalTokens = ttProp.GetInt32();
        }

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("reasoning_content", out var rProp) && rProp.ValueKind == JsonValueKind.String)
                {
                    result.Reasoning = rProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(result.Reasoning))
                    {
                        onReasoningDelta?.Invoke(result.Reasoning);
                    }
                }

                if (msg.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                {
                    string raw = contentProp.GetString() ?? "";
                    
                    int thinkStart = raw.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                    int thinkEnd = raw.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                    if (thinkStart >= 0 && thinkEnd > thinkStart)
                    {
                        string thinkPart = raw.Substring(thinkStart + 7, thinkEnd - (thinkStart + 7)).Trim();
                        result.Reasoning = (result.Reasoning + "\n" + thinkPart).Trim();
                        raw = (raw[..thinkStart] + raw[(thinkEnd + 8)..]).Trim();
                        onReasoningDelta?.Invoke(thinkPart);
                    }

                    result.Content = raw;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        onContentDelta?.Invoke(raw);
                    }
                }

                if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        string id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : $"call_{Guid.NewGuid():N}"[..12];
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            string fnName = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            string fnArgs = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";

                            if (!string.IsNullOrEmpty(fnName))
                            {
                                result.ToolCalls.Add(new ToolCallItem
                                {
                                    Id = id,
                                    Name = fnName,
                                    Arguments = fnArgs
                                });
                            }
                        }
                    }
                }
            }
        }

        if (result.CompletionTokens == 0)
        {
            result.CompletionTokens = result.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return result;
    }

    private Task<string?> GenerateStrategicPlanAsync(string prompt, CancellationToken ct)
    {
        var intent = GlacierIntentRouter.Analyze(prompt, _workspaceRoot);
        _consoleService.AddLog("ARCHITECT", $"Strategic Mission Plan formulated: [{intent.Archetype}] on '{intent.Target}' ({intent.Confidence * 100f:F1}% conf, {intent.LatencyMs:F2}ms)");
        return Task.FromResult<string?>(intent.StrategicPlan);
    }

    private List<JsonObject> BuildMessagesPayload(
        StudioSession session,
        string? strategicPlan = null,
        AnalyzedDeveloperIntent? intent = null,
        bool isSynthesisTurn = false)
    {
        var list = new List<JsonObject>();

        bool isLocalModel = _state.Provider is ModelServerProvider.LocalGlacierInference
            or ModelServerProvider.LocalGlacierDistributed
            or ModelServerProvider.LocalOllama
            or ModelServerProvider.LocalLMStudio;

        // System prompt instructing the Stage 4 Super Agent
        var sbSystem = new StringBuilder();
        sbSystem.AppendLine("You are the Glacier Antigravity Super Agent, an autonomous AI assistant and pair programmer embedded in the Glacier .NET 10 Ecosystem.");
        sbSystem.AppendLine($"Workspace Directory: {_workspaceRoot}");
        sbSystem.AppendLine("Operating System: Windows (PowerShell)");
        sbSystem.AppendLine("Zero Secrets Policy: Strictly enforced. Never embed or emit credentials, API keys, or secrets.");

        if (_state.IsAgentMode)
        {
            string targetEntity = intent?.Target ?? "Workspace";

            // Only inject full Knowledge Graph ontology when using cloud models
            // For local models, keep prompt ultra-lean (< 300 tokens) for sub-second GPU prefill.
            if (!isLocalModel)
            {
                if (targetEntity != "Workspace" || intent?.Archetype is DeveloperIntentArchetype.ArchitectureReview or DeveloperIntentArchetype.CodeSearch)
                {
                    if (Directory.Exists(_workspaceRoot))
                    {
                        var topDirs = Directory.GetDirectories(_workspaceRoot)
                            .Select(Path.GetFileName)
                            .Where(d => !string.IsNullOrEmpty(d) && !d.StartsWith(".") && d != "bin" && d != "obj")
                            .Take(16);
                        sbSystem.AppendLine($"Available Projects in Workspace: {string.Join(", ", topDirs)}");
                    }

                    string kgSummary = _graphService.QueryNeighborhoodSummary(targetEntity, maxEdges: 14);
                    if (!string.IsNullOrWhiteSpace(kgSummary))
                    {
                        sbSystem.AppendLine();
                        sbSystem.AppendLine(kgSummary);
                    }
                }
            }
            else
            {
                if (targetEntity != "Workspace")
                {
                    string candSub = Path.Combine(_workspaceRoot, targetEntity, "src", targetEntity);
                    string candRoot = Path.Combine(_workspaceRoot, targetEntity);
                    if (Directory.Exists(candSub))
                    {
                        sbSystem.AppendLine($"Target Scope: `{targetEntity}` (Project Directory: `{targetEntity}/src/{targetEntity}`)");
                    }
                    else if (Directory.Exists(candRoot))
                    {
                        sbSystem.AppendLine($"Target Scope: `{targetEntity}` (Project Directory: `{targetEntity}`)");
                    }
                    else
                    {
                        sbSystem.AppendLine($"Target Scope: `{targetEntity}`");
                    }
                }
            }

            // Inject Session Working Memory if tools have been executed in this session
            string memorySummary = session.WorkingMemory.FormatPromptSummary();
            if (!string.IsNullOrWhiteSpace(memorySummary))
            {
                sbSystem.AppendLine();
                sbSystem.AppendLine(memorySummary);
            }

            if (isSynthesisTurn)
            {
                sbSystem.AppendLine();
                sbSystem.AppendLine("## PHASE 4: EXECUTIVE SYNTHESIS (ALL TOOLS COMPLETED — DO NOT CALL ANY MORE TOOLS):");
                sbSystem.AppendLine("1. All required directory, file, and Knowledge Graph inspections have now been executed.");
                sbSystem.AppendLine("2. Do NOT output any `<tool_call>` or `tool_call` blocks.");
                sbSystem.AppendLine("3. Deliver a comprehensive, structured Markdown response grounded strictly in observed tool outputs and facts.");
            }
            else
            {
                sbSystem.AppendLine();
                sbSystem.AppendLine("## AUTONOMOUS DECISION & TOOL-USE GUIDELINES:");
                sbSystem.AppendLine("1. Decide autonomously how best to fulfill the user's request:");
                sbSystem.AppendLine("   - If the request does not require live disk or system state, respond directly and concisely without calling tools.");
                sbSystem.AppendLine("   - If the request requires inspecting/modifying workspace files or running tests/builds, invoke the appropriate tool first.");
                sbSystem.AppendLine("2. Never fabricate file contents, command outputs, or system state.");

                var allowedTools = intent?.AllowedTools;
                bool hasTools = allowedTools == null || allowedTools.Count > 0;
                if (hasTools)
                {
                    sbSystem.AppendLine();
                    sbSystem.AppendLine("## AVAILABLE TOOLS:");

                    if (isLocalModel)
                    {
                        // Ultra-lean tool signatures for sub-second local GPU prefill
                        sbSystem.AppendLine("- `run_command`: Run PowerShell/CLI. Args: `{\"command\": \"...\"}`");
                        sbSystem.AppendLine("- `read_file`: Read workspace file. Args: `{\"path\": \"...\"}`");
                        sbSystem.AppendLine("- `write_file`: Write workspace file. Args: `{\"path\": \"...\", \"content\": \"...\"}`");
                        sbSystem.AppendLine("- `list_directory`: List directory contents. Args: `{\"path\": \"...\"}`");
                        sbSystem.AppendLine("- `search_workspace`: Search workspace files. Args: `{\"query\": \"...\"}`");
                        sbSystem.AppendLine("- `query_knowledge_graph`: Query in-process AST/ontology. Args: `{\"entity\": \"...\"}`");
                    }
                    else
                    {
                        var allToolDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["run_command"] = "- `run_command`: Executes a PowerShell / CLI command on the local machine (e.g. `Get-Date`, `dotnet test`, `git status`). Args: `{\"command\": \"<command>\"}`.",
                            ["read_file"] = "- `read_file`: Reads file content from the workspace. Args: `{\"path\": \"<file_path>\"}`.",
                            ["write_file"] = "- `write_file`: Creates or updates a file in the workspace. Args: `{\"path\": \"<file_path>\", \"content\": \"<text>\"}`.",
                            ["delete_file"] = "- `delete_file`: Deletes a file within the workspace. Args: `{\"path\": \"<file_path>\"}`.",
                            ["list_directory"] = "- `list_directory`: Lists directory contents. Args: `{\"path\": \"<dir_path>\"}` (use \"\" for workspace root).",
                            ["search_workspace"] = "- `search_workspace`: Searches workspace files for symbols or text. Args: `{\"query\": \"<symbol_or_text>\", \"scope\": \"<optional_dir>\"}`.",
                            ["query_knowledge_graph"] = "- `query_knowledge_graph`: Queries in-process Glacier.Graph ontology for symbol/project relationships. Args: `{\"entity\": \"<project_or_type_name>\"}`.",
                            ["inspect_document"] = "- `inspect_document`: Inspects AST structure of Markdown or code file. Args: `{\"path\": \"<file_path>\"}`.",
                            ["call_mcp_tool"] = "- `call_mcp_tool`: Calls an MCP tool. Args: `{\"server\": \"<server>\", \"tool\": \"<tool>\", \"arguments\": {}}`.",
                            ["fetch_web_url"] = "- `fetch_web_url`: Fetches web page or RSS feed content via HTTP, strips HTML, and returns readable markdown text. Args: `{\"url\": \"<http_url>\"}`.",
                            ["search_web"] = "- `search_web`: Searches live web, Google News, and online articles. Args: `{\"query\": \"<keywords_or_news_topic>\"}`."
                        };

                        if (allowedTools != null && allowedTools.Count > 0)
                        {
                            foreach (var toolName in allowedTools)
                            {
                                if (allToolDescriptions.TryGetValue(toolName, out var desc))
                                    sbSystem.AppendLine(desc);
                            }
                        }
                        else
                        {
                            foreach (var desc in allToolDescriptions.Values)
                                sbSystem.AppendLine(desc);
                        }
                    }

                    sbSystem.AppendLine();
                    sbSystem.AppendLine("## HOW TO CALL A TOOL:");
                    sbSystem.AppendLine("When you need to run a tool, output ONLY a tool call block:");
                    sbSystem.AppendLine("<tool_call>");
                    sbSystem.AppendLine("{\"name\": \"<tool_name>\", \"arguments\": {\"<arg_name>\": \"<value>\"}}");
                    sbSystem.AppendLine("</tool_call>");
                    sbSystem.AppendLine("If no tool is needed, reply directly to the user in plain Markdown.");
                }
            }
        }

        list.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = sbSystem.ToString().Trim()
        });

        // Stage 4 Rolling Context Compaction: keep recent tool outputs within local KV-cache budget
        int recentMaxChars = isLocalModel ? 1000 : 6000;

        // For local models, keep conversation history bounded (last 4 messages) to guarantee < 1.5s prefill
        var rawMessages = session.Messages;
        var activeMessages = (isLocalModel && rawMessages.Count > 4)
            ? rawMessages.Skip(rawMessages.Count - 4).ToList()
            : rawMessages.ToList();

        var toolMessages = activeMessages.Where(m => m.Role == "tool").ToList();
        int totalToolMessages = toolMessages.Count;
        int currentToolIdx = 0;

        for (int i = 0; i < activeMessages.Count; i++)
        {
            var msg = activeMessages[i];

            // Skip static welcome banner, endpoint error cards, and cancelled empty turns so they never poison the model context
            if (msg.Role == "assistant")
            {
                if (i == 0 && msg.Content != null && msg.Content.StartsWith("### Glacier Studio", StringComparison.Ordinal))
                    continue;
                if (msg.IsError)
                    continue;
                if (msg.ToolCalls.Count == 0 &&
                    (string.IsNullOrWhiteSpace(msg.Content) ||
                     msg.Content.Trim() == "*(Execution was cancelled)*"))
                    continue;
            }

            if (msg.Role == "tool")
            {
                currentToolIdx++;
                bool isRecentTool = (totalToolMessages - currentToolIdx) < 2;
                string rawToolContent = msg.Content ?? "";
                if (!isRecentTool && rawToolContent.Length > 1000)
                {
                    rawToolContent = rawToolContent[..500] +
                        $"\n... [Compacted {rawToolContent.Length - 500} chars from older turn; AST symbols & facts preserved in Session Working Memory & Knowledge Graph]";
                }
                else if (isRecentTool && rawToolContent.Length > recentMaxChars)
                {
                    rawToolContent = rawToolContent[..recentMaxChars] +
                        $"\n... [Compacted {rawToolContent.Length - recentMaxChars} chars to fit active context window; AST symbols & facts indexed in Knowledge Graph]";
                }

                list.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = msg.ToolCallId ?? "call_0",
                    ["content"] = string.IsNullOrEmpty(msg.ToolName)
                        ? rawToolContent
                        : $"[Tool Result for {msg.ToolName}]:\n{rawToolContent}"
                });
            }
            else if (msg.Role == "assistant")
            {
                string assistantContent = (msg.Content ?? "").Replace("\n\n*(Execution was cancelled)*", "").Trim();

                if (msg.ToolCalls.Count > 0)
                {
                    if (isSynthesisTurn)
                    {
                        var summaryLines = string.Join(", ", msg.ToolCalls.Select(tc => $"{tc.ToolName}({tc.ArgumentsJson})"));
                        if (string.IsNullOrWhiteSpace(assistantContent))
                        {
                            assistantContent = $"[Executed: {summaryLines}]";
                        }
                    }
                    else
                    {
                        var tcBlocks = string.Join("\n", msg.ToolCalls.Select(tc =>
                            $"<tool_call>\n{{\"name\": \"{tc.ToolName}\", \"arguments\": {tc.ArgumentsJson}}}\n</tool_call>"));

                        if (string.IsNullOrWhiteSpace(assistantContent))
                        {
                            assistantContent = tcBlocks;
                        }
                        else if (!assistantContent.Contains("<tool_call>") && !assistantContent.Contains("```tool_call"))
                        {
                            assistantContent = (assistantContent.Trim() + "\n\n" + tcBlocks).Trim();
                        }
                    }
                }

                var assistantObj = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.IsNullOrEmpty(assistantContent) ? null : assistantContent
                };

                if (msg.ToolCalls.Count > 0 && !isSynthesisTurn)
                {
                    var toolCallsArray = new JsonArray();
                    foreach (var tc in msg.ToolCalls)
                    {
                        toolCallsArray.Add(new JsonObject
                        {
                            ["id"] = string.IsNullOrEmpty(tc.Id) ? $"call_{Guid.NewGuid():N}"[..12] : tc.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tc.ToolName,
                                ["arguments"] = tc.ArgumentsJson
                            }
                        });
                    }
                    assistantObj["tool_calls"] = toolCallsArray;
                }

                list.Add(assistantObj);
            }
            else
            {
                // If a previous user message was immediately followed by an error or cancelled empty turn,
                // skip that orphaned user message so we don't create consecutive "User: ... User: ..." turns
                bool isLastMessage = (i == session.Messages.Count - 1);
                if (!isLastMessage && msg.Role == "user")
                {
                    var nextMsg = session.Messages[i + 1];
                    if (nextMsg.Role == "assistant" &&
                        (nextMsg.IsError ||
                         (nextMsg.ToolCalls.Count == 0 &&
                          (string.IsNullOrWhiteSpace(nextMsg.Content) || nextMsg.Content.Trim() == "*(Execution was cancelled)*"))))
                    {
                        continue;
                    }
                }

                string text = msg.Content ?? "";
                list.Add(new JsonObject
                {
                    ["role"] = msg.Role,
                    ["content"] = text
                });
            }
        }

        if (isSynthesisTurn)
        {
            list.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"All tool executions are now complete. Do NOT call any more tools. Provide your clear, complete response to the user based on the tool outputs above."
            });
        }

        return list;
    }

    private string SynthesizeGroundTruthReport(StudioSession session, AnalyzedDeveloperIntent? intent)
    {
        string target = intent?.Target ?? "Workspace";
        var sb = new StringBuilder();

        // 1. Include highlights from the most recent tool observations in this turn
        var recentTools = session.Messages.Where(m => m.Role == "tool" && !string.IsNullOrWhiteSpace(m.Content)).TakeLast(2).ToList();
        if (recentTools.Count > 0)
        {
            foreach (var tm in recentTools)
            {
                string snippet = tm.Content.Trim();
                if (snippet.Length > 1800)
                {
                    snippet = snippet[..1800] + "\n... (see tool telemetry card above for full output)";
                }
                if (tm.ToolName is "fetch_web_url" or "search_web")
                {
                    sb.AppendLine("### 📰 Web & News Synthesis");
                    sb.AppendLine(snippet);
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine($"**Tool Output (`{tm.ToolName ?? "observation"}`)**:");
                    sb.AppendLine("```text");
                    sb.AppendLine(snippet);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        // 2. Include Working Memory summary if files/directories were inspected
        string memSummary = session.WorkingMemory.FormatPromptSummary();
        if (!string.IsNullOrWhiteSpace(memSummary))
        {
            sb.AppendLine(memSummary.Trim());
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static (List<ToolCallItem> ToolCalls, string CleanedContent) ExtractToolCallsFromContent(string content)
    {
        var toolCalls = new List<ToolCallItem>();
        if (string.IsNullOrWhiteSpace(content)) return (toolCalls, content);

        string working = content;

        // 1. Match <tool_call>...</tool_call> blocks
        var xmlMatches = Regex.Matches(working, @"<tool_call>\s*(.*?)\s*</tool_call>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in xmlMatches)
        {
            string raw = match.Groups[1].Value.Trim();
            var tc = TryParseToolCallJson(raw);
            if (tc != null)
            {
                toolCalls.Add(tc);
                working = working.Replace(match.Value, "");
            }
        }

        // 2. Match ```tool_call ... ``` or ```tool ... ``` blocks
        var codeBlockMatches = Regex.Matches(working, @"```(?:tool_call|tool|action|json)?\s*(\{\s*""(?:name|tool|function|action)""\s*:.*?\}?)\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in codeBlockMatches)
        {
            string raw = match.Groups[1].Value.Trim();
            var tc = TryParseToolCallJson(raw);
            if (tc != null)
            {
                toolCalls.Add(tc);
                working = working.Replace(match.Value, "");
            }
        }

        // 3. Match classic ReAct "Action: <tool>\nAction Input: <json or string>"
        var reactMatches = Regex.Matches(working, @"(?:Action|Tool):\s*([a-zA-Z0-9_\-\.]+)\s*(?:\r?\n)+(?:Action Input|Arguments|Parameters):\s*(\{[^\r\n]*\}|""[^""\r\n]*""|'[^'\r\n]*'|[^\r\n]+)", RegexOptions.IgnoreCase);
        foreach (Match match in reactMatches)
        {
            string toolName = match.Groups[1].Value.Trim();
            string rawArgs = match.Groups[2].Value.Trim();
            string argsJson = NormalizeArgumentsJson(toolName, rawArgs);
            toolCalls.Add(new ToolCallItem
            {
                Id = $"call_{Guid.NewGuid():N}"[..12],
                Name = toolName,
                Arguments = argsJson
            });
            working = working.Replace(match.Value, "");
        }

        // 4. Match direct function call syntax: list_directory("Glacier.Gpu") or read_file("path")
        var directCallMatches = Regex.Matches(working, @"\b(run_command|read_file|write_file|delete_file|list_directory|search_workspace|query_knowledge_graph|inspect_document|call_mcp_tool|fetch_web_url|search_web)\s*\(\s*(?:(?:path|command|file|query|entity|url)\s*=\s*)?[""']([^""']*)[""']\s*\)", RegexOptions.IgnoreCase);
        foreach (Match match in directCallMatches)
        {
            string fn = match.Groups[1].Value.Trim();
            string argVal = match.Groups[2].Value.Trim();
            string argKey = fn switch
            {
                "run_command" => "command",
                "search_workspace" or "search_web" => "query",
                "query_knowledge_graph" => "entity",
                "fetch_web_url" => "url",
                _ => "path"
            };
            toolCalls.Add(new ToolCallItem
            {
                Id = $"call_{Guid.NewGuid():N}"[..12],
                Name = fn,
                Arguments = JsonSerializer.Serialize(new Dictionary<string, string> { [argKey] = argVal })
            });
            working = working.Replace(match.Value, "");
        }

        return (toolCalls, working.Trim());
    }

    private static ToolCallItem? TryParseToolCallJson(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            string toolName = "";

            if (root.TryGetProperty("name", out var nProp)) toolName = nProp.GetString() ?? "";
            else if (root.TryGetProperty("tool", out var tProp)) toolName = tProp.GetString() ?? "";
            else if (root.TryGetProperty("function", out var fProp))
            {
                if (fProp.ValueKind == JsonValueKind.Object && fProp.TryGetProperty("name", out var fnName))
                    toolName = fnName.GetString() ?? "";
                else if (fProp.ValueKind == JsonValueKind.String)
                    toolName = fProp.GetString() ?? "";
            }
            else if (root.TryGetProperty("action", out var aProp)) toolName = aProp.GetString() ?? "";

            if (string.IsNullOrWhiteSpace(toolName)) return null;

            string argsJson = "{}";
            if (root.TryGetProperty("arguments", out var argsProp))
            {
                if (argsProp.ValueKind == JsonValueKind.Object)
                    argsJson = argsProp.GetRawText();
                else if (argsProp.ValueKind == JsonValueKind.String)
                {
                    string str = argsProp.GetString() ?? "{}";
                    argsJson = str.StartsWith("{") ? str : JsonSerializer.Serialize(new { raw = str });
                }
            }
            else if (root.TryGetProperty("parameters", out var paramProp))
            {
                if (paramProp.ValueKind == JsonValueKind.Object)
                    argsJson = paramProp.GetRawText();
                else if (paramProp.ValueKind == JsonValueKind.String)
                {
                    string str = paramProp.GetString() ?? "{}";
                    argsJson = str.StartsWith("{") ? str : JsonSerializer.Serialize(new { raw = str });
                }
            }
            else
            {
                // Fallback: Check if arguments were placed as top-level fields (e.g. {"name": "list_directory", "path": "Glacier.Gpu"})
                var dict = new Dictionary<string, object?>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("name") || prop.NameEquals("tool") || prop.NameEquals("action") || prop.NameEquals("function"))
                        continue;
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.GetRawText()
                    };
                }
                if (dict.Count > 0)
                {
                    argsJson = JsonSerializer.Serialize(dict);
                }
            }

            return new ToolCallItem
            {
                Id = $"call_{Guid.NewGuid():N}"[..12],
                Name = toolName,
                Arguments = argsJson
            };
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeArgumentsJson(string toolName, string rawArgs)
    {
        rawArgs = rawArgs.Trim();
        if (rawArgs.StartsWith("{") && rawArgs.EndsWith("}"))
        {
            return rawArgs;
        }
        if ((rawArgs.StartsWith("\"") && rawArgs.EndsWith("\"")) || (rawArgs.StartsWith("'") && rawArgs.EndsWith("'")))
        {
            rawArgs = rawArgs[1..^1];
        }

        string key = toolName.ToLowerInvariant() switch
        {
            "run_command" => "command",
            "search_workspace" or "search_web" => "query",
            "query_knowledge_graph" => "entity",
            "fetch_web_url" => "url",
            _ => "path"
        };
        return JsonSerializer.Serialize(new Dictionary<string, string> { [key] = rawArgs });
    }

    private static JsonArray GetToolsSchema(AnalyzedDeveloperIntent? intent = null)
    {
        var tools = new JsonArray();
        var allowed = intent?.AllowedTools;

        // If Dynamic Skill Gating explicitly specifies 0 tools (e.g. GeneralInquiry), return empty array
        if (allowed != null && allowed.Count == 0)
        {
            return tools;
        }

        bool IsAllowed(string name) =>
            allowed == null || allowed.Contains(name, StringComparer.OrdinalIgnoreCase);

        // 1. run_command
        if (IsAllowed("run_command"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "run_command",
                    ["description"] = "Executes a shell or terminal command in the workspace and streams output to console.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "Command line string to execute." }
                        },
                        ["required"] = new JsonArray { "command" }
                    }
                }
            });
        }

        // 2. read_file
        if (IsAllowed("read_file"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["description"] = "Reads file content from workspace, loads it into Document Preview, and indexes AST into Knowledge Graph.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative or absolute file path." }
                        },
                        ["required"] = new JsonArray { "path" }
                    }
                }
            });
        }

        // 3. write_file
        if (IsAllowed("write_file"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "write_file",
                    ["description"] = "Writes or overwrites file content in workspace and updates Knowledge Graph.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Target file path." },
                            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Text content to write." }
                        },
                        ["required"] = new JsonArray { "path", "content" }
                    }
                }
            });
        }

        // 3b. delete_file
        if (IsAllowed("delete_file") || IsAllowed("write_file"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "delete_file",
                    ["description"] = "Deletes a file within the workspace boundary and updates Knowledge Graph.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path of file to delete." }
                        },
                        ["required"] = new JsonArray { "path" }
                    }
                }
            });
        }

        // 4. list_directory
        if (IsAllowed("list_directory"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "list_directory",
                    ["description"] = "Lists files and subdirectories in workspace and records topology in Knowledge Graph.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Directory path (leave empty for root)." }
                        }
                    }
                }
            });
        }

        // 5. search_workspace
        if (IsAllowed("search_workspace"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "search_workspace",
                    ["description"] = "Searches workspace source files for symbols, type names, or concise keywords (e.g. 'GpuBuffer', 'PersistentRingBuffer', 'IGpuEngine', 'SASS'). Prefer concise identifiers rather than long natural language sentences.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol, class name, or concise keyword to search for." },
                            ["scope"] = new JsonObject { ["type"] = "string", ["description"] = "Optional subdirectory scope (e.g. 'Glacier.Gpu/src')." }
                        },
                        ["required"] = new JsonArray { "query" }
                    }
                }
            });
        }

        // 6. query_knowledge_graph
        if (IsAllowed("query_knowledge_graph"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "query_knowledge_graph",
                    ["description"] = "Queries the in-process Glacier.Graph Knowledge Graph for project dependencies, declared types, and relationships.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["entity"] = new JsonObject { ["type"] = "string", ["description"] = "Project, file, or symbol name to query." }
                        },
                        ["required"] = new JsonArray { "entity" }
                    }
                }
            });
        }

        // 7. inspect_document
        if (IsAllowed("inspect_document"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "inspect_document",
                    ["description"] = "Parses Markdown or source file using Glacier.DocTree and displays outline in preview pane.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "File path to inspect." }
                        },
                        ["required"] = new JsonArray { "path" }
                    }
                }
            });
        }

        // 8. call_mcp_tool
        if (IsAllowed("call_mcp_tool"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "call_mcp_tool",
                    ["description"] = "Calls an MCP tool on an ecosystem server (e.g. SharpTools, GlacierVectorEngine, GlacierGraphEngine).",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["server"] = new JsonObject { ["type"] = "string", ["description"] = "MCP server name." },
                            ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "Tool name." },
                            ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Tool arguments." }
                        },
                        ["required"] = new JsonArray { "server", "tool" }
                    }
                }
            });
        }

        // 9. fetch_web_url
        if (IsAllowed("fetch_web_url"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "fetch_web_url",
                    ["description"] = "Fetches web page or RSS feed content via HTTP, strips HTML, and returns readable markdown text.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["url"] = new JsonObject { ["type"] = "string", ["description"] = "Full HTTP/HTTPS URL to fetch." }
                        },
                        ["required"] = new JsonArray { "url" }
                    }
                }
            });
        }

        // 10. search_web
        if (IsAllowed("search_web"))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "search_web",
                    ["description"] = "Searches the live web and Google News for current events, topics, or documentation.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search keywords or news topic." }
                        },
                        ["required"] = new JsonArray { "query" }
                    }
                }
            });
        }

        return tools;
    }
}
