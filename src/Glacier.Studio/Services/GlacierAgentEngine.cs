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
    private CancellationTokenSource? _currentCts;

    private readonly string _workspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();

    public GlacierWorkspaceGraphService GraphService => _graphService;

    public GlacierAgentEngine(
        HttpClient httpClient,
        GlacierStudioState state,
        GlacierConsoleService consoleService,
        GlacierDocTreePreviewService docPreviewService,
        GlacierMcpManager mcpManager,
        GlacierWorkspaceGraphService? graphService = null)
    {
        _httpClient = httpClient;
        _state = state;
        _consoleService = consoleService;
        _docPreviewService = docPreviewService;
        _mcpManager = mcpManager;
        _graphService = graphService ?? new GlacierWorkspaceGraphService(_workspaceRoot);
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

            case "/plan":
            case "/goal":
                await RunAgentLoopAsync($"Please formulate and execute a step-by-step plan for: {args}", ct);
                break;

            default:
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
            ? (isLocalModel ? 3 : 5)
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
                    ? "Streaming Executive Synthesis..."
                    : $"Streaming Turn {currentTurn}...";
                _state.NotifyStateChanged();

                LlmStreamResult? streamResult = null;
                try
                {
                    streamResult = await StreamModelEndpointAsync(
                        messagesPayload,
                        toolsPayload,
                        onContentDelta: delta =>
                        {
                            currentAssistantMsg.Content += delta;
                            _state.NotifyStateChanged();
                        },
                        onReasoningDelta: delta =>
                        {
                            currentAssistantMsg.ReasoningContent = (currentAssistantMsg.ReasoningContent ?? "") + delta;
                            _state.NotifyStateChanged();
                        },
                        ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _consoleService.AddLog("SYSTEM", $"Model API error: {ex.Message}", isError: true);
                    _state.ExecutionStatus = AgentExecutionStatus.Error;
                    _state.StatusText = $"Endpoint Error: {ex.Message}";
                    currentAssistantMsg.IsError = true;
                    currentAssistantMsg.Content = $"### ⚠️ Model Endpoint Error\n\nCould not communicate with model endpoint at `{_state.EndpointUrl}`: `{ex.Message}`\n\nPlease ensure the model server is running and reachable, or switch to a different provider in the left pane.";
                    break;
                }

                if (streamResult == null) break;

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

                    _state.ExecutionStatus = AgentExecutionStatus.Thinking;
                    _state.NotifyStateChanged();
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

            _state.ExecutionStatus = AgentExecutionStatus.Completed;
            _state.StatusText = _state.LastTokensPerSecond > 0 
                ? $"Completed in {elapsedSeconds:F2}s ({_state.LastTokensPerSecond:F1} tok/s)"
                : $"Completed in {elapsedSeconds:F2}s";
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
                    if (!Directory.Exists(targetDir)) return (false, $"Error: Directory '{dirPath}' not found.");

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

                default:
                    return (false, $"Error: Unknown tool '{toolName}'.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Tool Execution Exception: {ex.Message}");
        }
    }

    private async Task<LlmStreamResult> StreamModelEndpointAsync(
        List<JsonObject> messages,
        JsonArray tools,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct)
    {
        string endpoint = _state.EndpointUrl.TrimEnd('/');
        string url = endpoint.EndsWith("/chat/completions") ? endpoint : $"{endpoint}/chat/completions";

        var payload = new JsonObject
        {
            ["model"] = _state.SelectedModel,
            ["messages"] = new JsonArray(messages.ToArray()),
            ["temperature"] = _state.Temperature,
            ["max_tokens"] = _state.MaxTokens,
            ["stream"] = true
        };

        if (_state.IsAgentMode && tools.Count > 0)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrEmpty(_state.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _state.ApiKey);
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

        // System prompt instructing the Stage 4 Super Agent
        var sbSystem = new StringBuilder();
        sbSystem.AppendLine("You are the Glacier Antigravity Super Agent, an autonomous AI assistant and pair programmer embedded in the Glacier .NET 10 Ecosystem.");
        sbSystem.AppendLine($"Workspace Directory: {_workspaceRoot}");
        sbSystem.AppendLine("Operating System: Windows (PowerShell)");
        sbSystem.AppendLine("Zero Secrets Policy: Strictly enforced. Never embed or emit credentials, API keys, or secrets.");

        if (_state.IsAgentMode)
        {
            string targetEntity = intent?.Target ?? "Workspace";

            // Only inject Knowledge Graph ontology when a specific project/file/symbol is targeted
            // so general questions and CLI requests stay lean and fast to prefill on local GPUs.
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
                sbSystem.AppendLine("3. Deliver a comprehensive, structured Markdown response grounded strictly in the observed tool outputs and Knowledge Graph facts.");
            }
            else
            {
                sbSystem.AppendLine();
                sbSystem.AppendLine("## AUTONOMOUS DECISION & TOOL-USE GUIDELINES:");
                sbSystem.AppendLine("1. Decide autonomously how best to fulfill the user's request:");
                sbSystem.AppendLine("   - If the user sends a greeting, conversational message (e.g. 'test', 'hello'), or general knowledge/coding question that does not require live disk or system state, respond directly and concisely without calling tools.");
                sbSystem.AppendLine("   - If the request requires live system state (e.g. current date/time via `Get-Date`, environment info, git status, running builds/tests) or inspecting/modifying workspace files, invoke the appropriate tool (`run_command`, `read_file`, `list_directory`, `search_workspace`, etc.) first.");
                sbSystem.AppendLine("   - If a request cannot be fulfilled with your knowledge or available tools, clearly state what you can and cannot do.");
                sbSystem.AppendLine("2. Never fabricate file contents, command outputs, or system state.");

                var allowedTools = intent?.AllowedTools;
                bool hasTools = allowedTools == null || allowedTools.Count > 0;
                if (hasTools)
                {
                    sbSystem.AppendLine();
                    sbSystem.AppendLine("## AVAILABLE TOOLS:");
                    var allToolDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["run_command"] = "- `run_command`: Executes a PowerShell / CLI command on the local machine (e.g. `Get-Date`, `dotnet test`, `git status`). Args: `{\"command\": \"<command>\"}`.",
                        ["read_file"] = "- `read_file`: Reads file content from the workspace. Args: `{\"path\": \"<file_path>\"}`.",
                        ["write_file"] = "- `write_file`: Creates or updates a file in the workspace. Args: `{\"path\": \"<file_path>\", \"content\": \"<text>\"}`.",
                        ["list_directory"] = "- `list_directory`: Lists directory contents. Args: `{\"path\": \"<dir_path>\"}` (use \"\" for workspace root).",
                        ["search_workspace"] = "- `search_workspace`: Searches workspace files for symbols or text. Args: `{\"query\": \"<symbol_or_text>\", \"scope\": \"<optional_dir>\"}`.",
                        ["query_knowledge_graph"] = "- `query_knowledge_graph`: Queries in-process Glacier.Graph ontology for symbol/project relationships. Args: `{\"entity\": \"<project_or_type_name>\"}`.",
                        ["inspect_document"] = "- `inspect_document`: Inspects AST structure of Markdown or code file. Args: `{\"path\": \"<file_path>\"}`.",
                        ["call_mcp_tool"] = "- `call_mcp_tool`: Calls an MCP tool. Args: `{\"server\": \"<server>\", \"tool\": \"<tool>\", \"arguments\": {}}`."
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

        // Stage 4 Rolling Context Compaction: keep recent tool outputs within local KV-cache budget, compact older large tool outputs
        var toolMessages = session.Messages.Where(m => m.Role == "tool").ToList();
        int totalToolMessages = toolMessages.Count;
        int currentToolIdx = 0;
        bool isLocalModel = _state.Provider is ModelServerProvider.LocalGlacierInference
            or ModelServerProvider.LocalGlacierDistributed
            or ModelServerProvider.LocalOllama
            or ModelServerProvider.LocalLMStudio;
        int recentMaxChars = isLocalModel ? 3600 : 6000;

        for (int i = 0; i < session.Messages.Count; i++)
        {
            var msg = session.Messages[i];

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
                sb.AppendLine($"**Tool Output (`{tm.ToolName ?? "observation"}`)**:");
                sb.AppendLine("```text");
                sb.AppendLine(snippet);
                sb.AppendLine("```");
                sb.AppendLine();
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
        var directCallMatches = Regex.Matches(working, @"\b(run_command|read_file|write_file|list_directory|search_workspace|query_knowledge_graph|inspect_document|call_mcp_tool)\s*\(\s*(?:(?:path|command|file|query|entity)\s*=\s*)?[""']([^""']*)[""']\s*\)", RegexOptions.IgnoreCase);
        foreach (Match match in directCallMatches)
        {
            string fn = match.Groups[1].Value.Trim();
            string argVal = match.Groups[2].Value.Trim();
            string argKey = fn switch
            {
                "run_command" => "command",
                "search_workspace" => "query",
                "query_knowledge_graph" => "entity",
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
            "search_workspace" => "query",
            "query_knowledge_graph" => "entity",
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
                    ["description"] = "Searches workspace files for symbols, class names, or text snippets and indexes matches into Knowledge Graph.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol or text to search for." },
                            ["scope"] = new JsonObject { ["type"] = "string", ["description"] = "Optional subdirectory scope." }
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

        return tools;
    }
}
