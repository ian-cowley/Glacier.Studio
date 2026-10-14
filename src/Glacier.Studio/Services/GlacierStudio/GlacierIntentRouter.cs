using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Glacier.Clavier.Engine;
using Glacier.Clavier.Model;

namespace Glacier.Showcase.Services.GlacierStudio;

public enum DeveloperIntentArchetype
{
    CodeModification = 0,
    CodeSearch = 1,
    FeatureCreation = 2,
    TestingAndVerification = 3,
    ArchitectureReview = 4,
    BuildAndDiagnostics = 5,
    SecurityAudit = 6,
    PerformanceProfiling = 7,
    DatabaseAndData = 8,
    DevOpsAndGit = 9,
    DocumentationAndSpecs = 10,
    GeneralInquiry = 11
}

public sealed record AnalyzedDeveloperIntent
{
    public required DeveloperIntentArchetype Archetype { get; init; }
    public required string Target { get; init; }
    public required float Confidence { get; init; }
    public required double LatencyMs { get; init; }
    public required bool NeedsToolExecution { get; init; }
    public required bool NeedsWorkspaceMutation { get; init; }
    public required float ComplexityScore { get; init; }
    public required IReadOnlyList<string> AllowedTools { get; init; }
    public required string StrategicPlan { get; init; }
    public required string NudgeDirective { get; init; }
    public required string SystemInstructionHint { get; init; }
}

public sealed record StepSupervisionDecision
{
    public required bool ShouldContinueAutonomously { get; init; }
    public required string DirectiveMessage { get; init; }
    public required float Confidence { get; init; }
    public required double LatencyMs { get; init; }
}

/// <summary>
/// Stage 4 System-1 Policy Router &amp; In-Flight Step Supervisor powered by Glacier.Clavier.
/// Evaluates developer intent across 12 software engineering archetypes, gates dynamic skills,
/// and supervises ReAct turns in sub-millisecond turnaround.
/// </summary>
public static class GlacierIntentRouter
{
    private static readonly string[] s_archetypeNames = Enum.GetNames<DeveloperIntentArchetype>();

    private static readonly ClavierSession s_clavier = new(
        embeddingDim: 768,
        hiddenDim: 256,
        numActions: s_archetypeNames.Length,
        temperature: 0.85f);

    private static readonly string[] s_allCoreTools =
    [
        "run_command",
        "read_file",
        "write_file",
        "list_directory",
        "search_workspace",
        "query_knowledge_graph",
        "inspect_document",
        "call_mcp_tool"
    ];

    private static readonly (DeveloperIntentArchetype Archetype, string SemanticPrototype)[] s_archetypePrototypes =
    [
        (DeveloperIntentArchetype.CodeModification, "Modify, edit, refactor, fix bug, patch, update, or change source code in existing files"),
        (DeveloperIntentArchetype.CodeSearch, "Find, locate, search, where is symbol defined, trace call flow, or look up file path in workspace"),
        (DeveloperIntentArchetype.FeatureCreation, "Create, scaffold, implement new feature, add new class, build new module or service"),
        (DeveloperIntentArchetype.TestingAndVerification, "Run unit tests, execute xunit test suite, benchmark performance, verify build and test assertions"),
        (DeveloperIntentArchetype.ArchitectureReview, "Review architecture, analyze project structure, inspect codebase design, audit module organization"),
        (DeveloperIntentArchetype.BuildAndDiagnostics, "Compile project, run dotnet build, diagnose compiler errors, MSBuild warnings, NuGet restore"),
        (DeveloperIntentArchetype.SecurityAudit, "Audit security vulnerabilities, scan for secrets, credentials, API keys, OWASP compliance"),
        (DeveloperIntentArchetype.PerformanceProfiling, "Profile CPU and GPU performance, SIMD vectorization, memory allocations, throughput and latency"),
        (DeveloperIntentArchetype.DatabaseAndData, "Query SQL database, inspect tables, schemas, stored procedures, migrations, data pipelines"),
        (DeveloperIntentArchetype.DevOpsAndGit, "Git status, commit history, branches, pull requests, CI/CD pipelines, Docker containers"),
        (DeveloperIntentArchetype.DocumentationAndSpecs, "Write or update documentation, README markdown, technical specifications, API docs"),
        (DeveloperIntentArchetype.GeneralInquiry, "General question, conversational greeting, status check, system time, environment inquiry, or open request")
    ];

    public static AnalyzedDeveloperIntent Analyze(string prompt, string workspaceRoot)
    {
        var sw = Stopwatch.StartNew();
        string trimmed = prompt.Trim();

        // 1. Execute Clavier System-1 Forward Passes (Choice, Noul, Score)
        var clavierChoice = s_clavier.DecideWithDistribution(trimmed.AsSpan(), s_archetypeNames);
        var clavierNoulTool = s_clavier.VerifyWithDetails(
            trimmed.AsSpan(),
            "Request inspects, modifies, searches, builds, or runs commands on workspace files or the local system".AsSpan());
        var clavierNoulMutate = s_clavier.VerifyWithDetails(
            trimmed.AsSpan(),
            "Request modifies, creates, or refactors source code files".AsSpan());
        var clavierScore = s_clavier.ScoreWithDetails(
            trimmed.AsSpan(),
            "Multi-step software engineering complexity".AsSpan(),
            min: 1.0f,
            max: 10.0f);

        // 2. Identify Workspace Target (Project, File, or Symbol)
        string target = ExtractTarget(trimmed, workspaceRoot);

        // 3. Semantic Clavier Prototype + Neural Distribution Selection
        var (archetype, calibratedConfidence) = ClassifyArchetypeWithClavier(trimmed, target, clavierChoice);

        bool needsMutation = archetype is DeveloperIntentArchetype.CodeModification
            or DeveloperIntentArchetype.FeatureCreation
            or DeveloperIntentArchetype.DocumentationAndSpecs;

        float complexity = archetype switch
        {
            DeveloperIntentArchetype.GeneralInquiry => 1.5f,
            DeveloperIntentArchetype.CodeSearch => 3.5f,
            DeveloperIntentArchetype.BuildAndDiagnostics or DeveloperIntentArchetype.TestingAndVerification => 5.5f,
            DeveloperIntentArchetype.ArchitectureReview or DeveloperIntentArchetype.CodeModification or DeveloperIntentArchetype.FeatureCreation => 7.5f,
            _ => Math.Clamp(clavierScore.Value, 2.0f, 9.0f)
        };

        // 4. Unified Skill Availability: Always provide core tools (including run_command)
        // so the model itself can decide whether to answer directly, run a CLI command (e.g. Get-Date), or inspect files.
        var allowedTools = GetDynamicSkillsForArchetype(archetype);

        sw.Stop();
        double latencyMs = sw.Elapsed.TotalMilliseconds;

        // 5. Formulate Archetype-Specific Strategic Roadmap and Directives
        var (plan, nudge, hint) = BuildRoadmapAndDirectives(
            archetype,
            target,
            calibratedConfidence,
            latencyMs,
            complexity,
            allowedTools);

        return new AnalyzedDeveloperIntent
        {
            Archetype = archetype,
            Target = target,
            Confidence = calibratedConfidence,
            LatencyMs = latencyMs,
            NeedsToolExecution = archetype != DeveloperIntentArchetype.GeneralInquiry && clavierNoulTool.IsAffirmative,
            NeedsWorkspaceMutation = needsMutation && clavierNoulMutate.IsAffirmative,
            ComplexityScore = complexity,
            AllowedTools = allowedTools,
            StrategicPlan = plan,
            NudgeDirective = nudge,
            SystemInstructionHint = hint
        };
    }

    /// <summary>
    /// Stage 4 Clavier In-Flight Post-Tool Supervisor:
    /// Evaluates tool execution output for errors/missing files and formulates immediate recovery hints using the Knowledge Graph.
    /// </summary>
    public static string? SuperviseToolExecution(
        string toolName,
        string argumentsJson,
        string toolResult,
        GlacierWorkspaceGraphService graphService)
    {
        if (string.IsNullOrWhiteSpace(toolResult)) return null;

        var noulError = s_clavier.Verify(toolResult.AsSpan(), "Tool execution failed with an error or missing file".AsSpan());
        bool isError = toolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                       toolResult.Contains("Exit Code: 1", StringComparison.OrdinalIgnoreCase) ||
                       toolResult.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

        if (!isError && !noulError.IsAffirmative) return null;

        if (toolName.Equals("read_file", StringComparison.OrdinalIgnoreCase) &&
            toolResult.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(toolResult, @"File '([^']+)' does not exist");
            if (match.Success)
            {
                string missingPath = match.Groups[1].Value;
                string? resolved = graphService.TryResolveFilePath(missingPath);
                if (!string.IsNullOrEmpty(resolved))
                {
                    string relResolved = graphService.NormalizeRelativePath(resolved);
                    return $"[CLAVIER SUPERVISOR RECOVERY]: '{missingPath}' was resolved via Knowledge Graph to '{relResolved}'. Call `read_file` with `{{\"path\": \"{relResolved}\"}}`.";
                }
            }
            return "[CLAVIER SUPERVISOR RECOVERY]: File path was not found. Use `search_workspace` or `list_directory` to verify the exact path before calling `read_file`.";
        }

        return null;
    }

    /// <summary>
    /// Stage 4 Clavier In-Flight Turn Supervisor:
    /// Respects the model's autonomous decision on whether a question was answered directly,
    /// and only intervenes if the model explicitly paused mid-plan asking a timid continuation question
    /// on an active multi-step workspace inspection.
    /// </summary>
    public static StepSupervisionDecision SuperviseTurnCompletion(
        string assistantText,
        int currentTurn,
        int maxTurns,
        int sessionToolCount,
        AnalyzedDeveloperIntent? intent,
        StudioWorkingMemory memory,
        GlacierWorkspaceGraphService graphService)
    {
        var sw = Stopwatch.StartNew();

        // If the model already answered or no multi-step workspace target is being actively reviewed, let it finish naturally
        if (intent == null ||
            intent.Archetype == DeveloperIntentArchetype.GeneralInquiry ||
            currentTurn >= maxTurns ||
            string.IsNullOrWhiteSpace(assistantText))
        {
            sw.Stop();
            return new StepSupervisionDecision
            {
                ShouldContinueAutonomously = false,
                DirectiveMessage = "",
                Confidence = 0.99f,
                LatencyMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Only continue if the model explicitly stopped mid-plan asking permission to continue a multi-phase workspace task
        bool asksPermissionToContinue = Regex.IsMatch(
            assistantText,
            @"(?:which (?:specific|file|aspect|part|component) would you like|would you like me to (?:proceed|continue|inspect|read)|shall i (?:proceed|inspect|read)|please let me know if you want me to inspect)",
            RegexOptions.IgnoreCase);

        if (asksPermissionToContinue && sessionToolCount > 0 && sessionToolCount < 2 && intent.Target != "Workspace")
        {
            string nextDirective = BuildNextConcreteActionDirective(intent, memory, graphService);
            sw.Stop();
            return new StepSupervisionDecision
            {
                ShouldContinueAutonomously = true,
                DirectiveMessage = nextDirective,
                Confidence = 0.94f,
                LatencyMs = sw.Elapsed.TotalMilliseconds
            };
        }

        sw.Stop();
        return new StepSupervisionDecision
        {
            ShouldContinueAutonomously = false,
            DirectiveMessage = "",
            Confidence = 0.96f,
            LatencyMs = sw.Elapsed.TotalMilliseconds
        };
    }

    private static string BuildNextConcreteActionDirective(
        AnalyzedDeveloperIntent intent,
        StudioWorkingMemory memory,
        GlacierWorkspaceGraphService graphService)
    {
        string target = intent.Target;

        // If we listed a directory in Working Memory, find the highest-value unvisited file
        if (memory.ListedDirectories.TryGetValue(target, out var entries) ||
            memory.ListedDirectories.Values.FirstOrDefault() is { } anyEntries && (entries = anyEntries) != null)
        {
            string dirKey = memory.ListedDirectories.ContainsKey(target)
                ? target
                : memory.ListedDirectories.Keys.First();

            var readmeCandidate = entries.FirstOrDefault(e =>
                e.EndsWith("README.md", StringComparison.OrdinalIgnoreCase) ||
                e.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(readmeCandidate))
            {
                string cleanFile = readmeCandidate.Replace("[FILE] ", "").Trim();
                string fullRel = dirKey == "Workspace" ? cleanFile : $"{dirKey}/{cleanFile}";
                if (!memory.InspectedFiles.ContainsKey(fullRel))
                {
                    return $"[CLAVIER STEP SUPERVISOR]: Proceed autonomously: invoke `read_file` on `{fullRel}` now.";
                }
            }
        }

        // Check if target itself resolves to a file
        string? resolvedFile = graphService.TryResolveFilePath(target);
        if (!string.IsNullOrEmpty(resolvedFile))
        {
            string relFile = graphService.NormalizeRelativePath(resolvedFile);
            if (!memory.InspectedFiles.ContainsKey(relFile))
            {
                return $"[CLAVIER STEP SUPERVISOR]: Proceed autonomously. Invoke `read_file` with `{{\"path\": \"{relFile}\"}}` now.";
            }
        }

        return intent.NudgeDirective;
    }

    private static (DeveloperIntentArchetype Archetype, float Confidence) ClassifyArchetypeWithClavier(
        string prompt,
        string extractedTarget,
        ClavierDecisionResult clavierChoice)
    {
        // Short conversational prompts, greetings, or general questions without a specific codebase target
        // default to GeneralInquiry so the model decides freely whether to answer directly or run a quick command.
        int wordCount = prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount <= 2 && extractedTarget == "Workspace")
        {
            return (DeveloperIntentArchetype.GeneralInquiry, 0.94f);
        }

        // Evaluate Clavier semantic similarity against each archetype's prototype description
        DeveloperIntentArchetype bestArchetype = DeveloperIntentArchetype.GeneralInquiry;
        float bestScore = -1.0f;

        foreach (var (candidate, prototype) in s_archetypePrototypes)
        {
            var score = s_clavier.Score(prompt.AsSpan(), prototype.AsSpan(), min: 0.0f, max: 1.0f);
            float tokenOverlap = ComputeSemanticTokenOverlap(prompt, prototype);
            float combined = (score.Value * 0.45f) + (tokenOverlap * 0.55f);

            if (combined > bestScore)
            {
                bestScore = combined;
                bestArchetype = candidate;
            }
        }

        // If semantic overlap with specialized engineering archetypes is weak and no specific project/file was targeted,
        // classify as GeneralInquiry (where all tools including run_command remain available to the model).
        if (bestScore < 0.35f && extractedTarget == "Workspace")
        {
            return (DeveloperIntentArchetype.GeneralInquiry, 0.91f);
        }

        float confidence = Math.Clamp(0.85f + (bestScore * 0.12f) + (clavierChoice.Decision.Confidence * 0.03f), 0.85f, 0.98f);
        return (bestArchetype, confidence);
    }

    private static float ComputeSemanticTokenOverlap(string prompt, string prototype)
    {
        var promptTokens = TokenizeWords(prompt);
        if (promptTokens.Count == 0) return 0f;

        var protoTokens = TokenizeWords(prototype);
        int matches = 0;
        foreach (var pt in promptTokens)
        {
            if (pt.Length <= 2) continue;
            if (protoTokens.Any(pr => pr.StartsWith(pt, StringComparison.OrdinalIgnoreCase) ||
                                      pt.StartsWith(pr, StringComparison.OrdinalIgnoreCase)))
            {
                matches++;
            }
        }

        return Math.Clamp((float)matches / Math.Max(2, Math.Min(promptTokens.Count, 5)), 0f, 1f);
    }

    private static List<string> TokenizeWords(string text)
    {
        var list = new List<string>();
        var matches = Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]{2,}");
        foreach (Match m in matches)
        {
            string w = m.Value;
            if (w is not ("the" or "and" or "for" or "with" or "from" or "that" or "this" or "what" or "how" or "can" or "you"))
            {
                list.Add(w);
            }
        }
        return list;
    }

    private static IReadOnlyList<string> GetDynamicSkillsForArchetype(DeveloperIntentArchetype archetype)
    {
        // Always include run_command and core workspace tools so the model can autonomously decide
        // whether to run a shell command (e.g. Get-Date, system info, git, dotnet), read a file, or answer directly.
        return s_allCoreTools;
    }

    private static string ExtractTarget(string prompt, string workspaceRoot)
    {
        string pLower = prompt.ToLowerInvariant();

        // Check for known ecosystem projects
        string[] knownProjects =
        [
            "Glacier.Gpu", "Glacier.Inference.Distributed", "Glacier.Inference",
            "Glacier.Studio", "Glacier.Clavier", "Glacier.Polaris", "Glacier.Vector",
            "Glacier.Graph", "Glacier.DocTree", "Glacier.Grep", "Glacier.SpanCoder",
            "Glacier.Cluster", "Glacier.Tune", "Glacier.Rag", "Glacier.Tensor",
            "AgentDevKit.Adk", "tinypdf-csharp"
        ];

        foreach (var proj in knownProjects)
        {
            if (pLower.Contains(proj.ToLowerInvariant()))
                return proj;
        }

        // Check for explicit file paths or extensions
        var fileMatch = Regex.Match(prompt, @"([a-zA-Z0-9_\-\.\/\\]+\.(?:cs|razor|json|md|slnx|csproj|yml|yaml|py))", RegexOptions.IgnoreCase);
        if (fileMatch.Success) return fileMatch.Groups[1].Value.Trim();

        // Check for CamelCase / PascalCase C# symbol names (e.g. CudaSassDispatcher, ClavierSession, GraphStore)
        var pascalMatches = Regex.Matches(prompt, @"\b([A-Z][a-z0-9]+(?:[A-Z][a-zA-Z0-9]+)+)\b");
        if (pascalMatches.Count > 0)
        {
            return pascalMatches[0].Groups[1].Value;
        }

        return "Workspace";
    }

    private static (string Plan, string Nudge, string Hint) BuildRoadmapAndDirectives(
        DeveloperIntentArchetype archetype,
        string target,
        float confidence,
        double latencyMs,
        float complexity,
        IReadOnlyList<string> allowedTools)
    {
        string skillsBadge = allowedTools.Count > 0
            ? string.Join(", ", allowedTools.Select(t => $"`{t}`"))
            : "`Direct Streaming`";

        string telemetryHeader = $"- **Clavier System-1 Policy**: `{archetype}` ({confidence * 100f:F1}% conf | {latencyMs:F2} ms | Complexity: {complexity:F1}/10)\n- **Target & Scope**: `{target}`\n- **Available Skills**: {skillsBadge}";

        return archetype switch
        {
            DeveloperIntentArchetype.CodeModification => (
                $@"### 🛠️ Super Agent Strategic Mission Plan
{telemetryHeader}
- **Execution Roadmap**:
  1. **Phase 1: Target Inspection**: Inspect `{target}` using `read_file` or `search_workspace`.
  2. **Phase 2: Semantic Modification**: Apply changes using `write_file`.
  3. **Phase 3: Verification**: Verify with `run_command` if needed.",
                $"Proceed autonomously with the modification on `{target}`.",
                $"Inspect real code with `read_file`, apply updates with `write_file`, and verify with `run_command`."
            ),

            DeveloperIntentArchetype.CodeSearch => (
                $@"### 🔍 Super Agent Strategic Mission Plan
{telemetryHeader}
- **Execution Roadmap**:
  1. **Phase 1: Symbol & Workspace Search**: Use `search_workspace` or `query_knowledge_graph` for `{target}`.
  2. **Phase 2: Source Inspection**: Read matched files and report exact paths and line numbers.",
                $"Proceed autonomously with the search for `{target}`.",
                $"Locate `{target}` using `search_workspace`, `query_knowledge_graph`, or `read_file`."
            ),

            DeveloperIntentArchetype.ArchitectureReview => (
                $@"### 📋 Super Agent Strategic Mission Plan
{telemetryHeader}
- **Execution Roadmap**:
  1. **Phase 1: Structure Discovery**: Inspect `{target}` directory and documentation.
  2. **Phase 2: Source Code Analysis**: Read key source files in `{target}` and synthesize findings.",
                $"Proceed autonomously with the review of `{target}`.",
                $"Review `{target}` using `list_directory` and `read_file` before writing your synthesis."
            ),

            _ => (
                $@"### 💡 Super Agent Execution Plan
{telemetryHeader}
- **Execution Mode**: Autonomous Decision — respond directly if no tools are needed, invoke `run_command` / workspace tools if live system or file data is needed, or state clearly if a request cannot be fulfilled.",
                "Respond directly or invoke a tool if live system/workspace data is needed.",
                "Answer directly if you can, call `run_command` or workspace tools if live system/environment/file information is required (for example, `Get-Date` for the current time), or state clearly if the request is outside your capabilities."
            )
        };
    }
}
