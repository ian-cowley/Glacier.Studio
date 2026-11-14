namespace Glacier.Studio.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

[Trait("Category", "Unit")]
public class GlacierAgentEngineTests
{
    [Fact]
    public async Task SlashCommand_Help_ReturnsCommandCatalog()
    {
        var (engine, state, _) = CreateTestEngine();

        await engine.ProcessUserMessageAsync("/help");

        var session = state.ActiveSession;
        Assert.True(session.Messages.Count >= 2); // User prompt + Assistant response
        var assistantMsg = session.Messages.Last();
        Assert.Equal("assistant", assistantMsg.Role);
        Assert.Contains("Glacier Super Agent Studio Slash Commands", assistantMsg.Content);
        Assert.Contains("/help", assistantMsg.Content);
        Assert.Contains("/reset", assistantMsg.Content);
        Assert.Contains("/clear", assistantMsg.Content);
        Assert.Contains("/status", assistantMsg.Content);
        Assert.Contains("/mcp", assistantMsg.Content);
    }

    [Fact]
    public async Task SlashCommand_Status_ReturnsSessionAndModelStatus()
    {
        var (engine, state, _) = CreateTestEngine();
        state.SelectedModel = "Glacier-Grandmaster-7B-Q4_K_M";
        state.Provider = ModelServerProvider.LocalGlacierInference;

        await engine.ProcessUserMessageAsync("/status");

        var session = state.ActiveSession;
        var assistantMsg = session.Messages.Last();
        Assert.Equal("assistant", assistantMsg.Role);
        Assert.Contains("Glacier Studio Status", assistantMsg.Content);
        Assert.Contains("Glacier-Grandmaster-7B-Q4_K_M", assistantMsg.Content);
        Assert.Contains("LocalGlacierInference", assistantMsg.Content);
        Assert.Contains(session.Id, assistantMsg.Content);
    }

    [Fact]
    public async Task SlashCommand_ResetAndClear_ResetsSessionAndWorkingMemory()
    {
        var (engine, state, _) = CreateTestEngine();
        var session = state.ActiveSession;

        // Seed messages and working memory
        session.Messages.Add(new ChatMessage { Role = "user", Content = "Test query 1" });
        session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Test answer 1" });
        session.WorkingMemory.EstablishedFacts.Add("Fact 1");
        session.WorkingMemory.InspectedFiles["test.cs"] = "summary 1";

        Assert.True(session.Messages.Count >= 2);
        Assert.NotEmpty(session.WorkingMemory.EstablishedFacts);

        // Execute /reset
        await engine.ProcessUserMessageAsync("/reset");

        Assert.Single(session.Messages); // Only the reset confirmation message
        Assert.Empty(session.WorkingMemory.EstablishedFacts);
        Assert.Empty(session.WorkingMemory.InspectedFiles);
        Assert.Contains("cleared", session.Messages[0].Content, StringComparison.OrdinalIgnoreCase);

        // Seed again and test /clear
        session.Messages.Add(new ChatMessage { Role = "user", Content = "Test query 2" });
        session.WorkingMemory.EstablishedFacts.Add("Fact 2");

        await engine.ProcessUserMessageAsync("/clear");
        Assert.Single(session.Messages);
        Assert.Empty(session.WorkingMemory.EstablishedFacts);
    }

    [Fact]
    public async Task SystemPrompt_InAgentMode_RegistersToolsAndGuidelines()
    {
        var mockHttp = new CapturingMockHttpMessageHandler();
        mockHttp.SetResponse("I am ready to help.");

        var (engine, state, _) = CreateTestEngine(mockHttp);
        state.IsAgentMode = true;

        await engine.ProcessUserMessageAsync("Analyze the codebase structure");

        Assert.NotEmpty(mockHttp.CapturedRequests);
        var firstRequest = mockHttp.CapturedRequests[0];
        using var doc = JsonDocument.Parse(firstRequest.Content);
        var root = doc.RootElement;

        // Verify Chat messages structure
        var messages = root.GetProperty("messages");
        Assert.True(messages.GetArrayLength() >= 2);

        var systemMsg = messages[0];
        Assert.Equal("system", systemMsg.GetProperty("role").GetString());
        string systemContent = systemMsg.GetProperty("content").GetString() ?? "";

        Assert.Contains("Glacier Antigravity Super Agent", systemContent);
        Assert.Contains("Zero Secrets Policy", systemContent);
        Assert.Contains("AVAILABLE TOOLS", systemContent);
        Assert.Contains("run_command", systemContent);
        Assert.Contains("read_file", systemContent);
        Assert.Contains("write_file", systemContent);
        Assert.Contains("list_directory", systemContent);
        Assert.Contains("<tool_call>", systemContent);

        // Verify tool registration
        Assert.True(root.TryGetProperty("tools", out var toolsArray));
        Assert.True(toolsArray.GetArrayLength() > 0);
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task SystemPrompt_InNonAgentMode_DoesNotRegisterToolSchemas()
    {
        var mockHttp = new CapturingMockHttpMessageHandler();
        mockHttp.SetResponse("Direct conversation response without tool calling.");

        var (engine, state, _) = CreateTestEngine(mockHttp);
        state.IsAgentMode = false;

        await engine.ProcessUserMessageAsync("Tell me about .NET 10");

        Assert.NotEmpty(mockHttp.CapturedRequests);
        var firstRequest = mockHttp.CapturedRequests[0];
        using var doc = JsonDocument.Parse(firstRequest.Content);

        // In non-agent mode, tools payload must not be populated
        bool hasTools = doc.RootElement.TryGetProperty("tools", out var toolsElem) && toolsElem.GetArrayLength() > 0;
        Assert.False(hasTools, "Non-agent mode should not register tool calling schema.");
    }

    [Fact]
    public async Task ChatMlParsing_StripsTokensFromContent()
    {
        var mockHttp = new CapturingMockHttpMessageHandler();
        mockHttp.SetResponse("<|im_start|>assistant\nArchitecture analysis completed successfully.<|im_end|><|endoftext|>");

        var (engine, state, _) = CreateTestEngine(mockHttp);
        state.IsAgentMode = false;

        await engine.ProcessUserMessageAsync("Verify ChatML token stripping");

        var assistantMsg = state.ActiveSession.Messages.Last();
        Assert.Equal("assistant", assistantMsg.Role);
        Assert.DoesNotContain("<|im_start|>", assistantMsg.Content);
        Assert.DoesNotContain("<|im_end|>", assistantMsg.Content);
        Assert.DoesNotContain("<|endoftext|>", assistantMsg.Content);
        Assert.Contains("Architecture analysis completed successfully.", assistantMsg.Content);
    }

    [Fact]
    public async Task ChatMlParsing_ExtractsXmlToolCallsAndDispatchesTool()
    {
        var mockHttp = new CapturingMockHttpMessageHandler();
        // Turn 1: Model outputs XML tool call
        mockHttp.QueueResponse("Let me list the directory.\n<tool_call>{\"name\": \"list_directory\", \"arguments\": {\"path\": \"\"}}</tool_call>");
        // Turn 2: Model outputs final synthesis
        mockHttp.QueueResponse("I found all repository root items.");

        var (engine, state, _) = CreateTestEngine(mockHttp);
        state.IsAgentMode = true;

        await engine.ProcessUserMessageAsync("List the workspace root items");

        var session = state.ActiveSession;
        // Turn 1 assistant message should have extracted tool call
        var turn1Assistant = session.Messages.FirstOrDefault(m => m.Role == "assistant" && m.ToolCalls.Count > 0);
        Assert.NotNull(turn1Assistant);
        Assert.Single(turn1Assistant.ToolCalls);
        Assert.Equal("list_directory", turn1Assistant.ToolCalls[0].ToolName);
        Assert.DoesNotContain("<tool_call>", turn1Assistant.Content);

        // A tool result message must be recorded in session history
        var toolMsg = session.Messages.FirstOrDefault(m => m.Role == "tool");
        Assert.NotNull(toolMsg);
        Assert.Contains(".git", toolMsg.Content);

        // Final assistant message must deliver synthesis
        var finalAssistant = session.Messages.Last();
        Assert.Equal("assistant", finalAssistant.Role);
        Assert.Contains("I found all repository root items.", finalAssistant.Content);
    }

    [Fact]
    public async Task ChatMlParsing_ExtractsMarkdownCodeBlockToolCalls()
    {
        var mockHttp = new CapturingMockHttpMessageHandler();
        mockHttp.QueueResponse("Inspecting solution:\n```tool_call\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"Glacier.Studio.slnx\"}}\n```");
        mockHttp.QueueResponse("File read complete.");

        var (engine, state, _) = CreateTestEngine(mockHttp);
        state.IsAgentMode = true;

        await engine.ProcessUserMessageAsync("Check the solution structure");

        var session = state.ActiveSession;
        var toolAssistant = session.Messages.FirstOrDefault(m => m.Role == "assistant" && m.ToolCalls.Count > 0);
        Assert.NotNull(toolAssistant);
        Assert.Equal("read_file", toolAssistant.ToolCalls[0].ToolName);
        Assert.DoesNotContain("```tool_call", toolAssistant.Content);
    }

    [Fact]
    public async Task ReActTurnLoop_ExecutesMultiTurnToolExecutionAndReturnsFinalAnswer()
    {
        var mockHttp = new CapturingMockHttpMessageHandler();
        // Turn 1: tool call
        mockHttp.QueueResponse("<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"Glacier.Studio.slnx\"}}</tool_call>");
        // Turn 2: final answer
        mockHttp.QueueResponse("The solution contains Glacier.Studio and Glacier.Studio.Tests projects.");

        var (engine, state, _) = CreateTestEngine(mockHttp);
        state.IsAgentMode = true;

        await engine.ProcessUserMessageAsync("What projects are in Glacier.Studio.slnx?");

        Assert.Equal(AgentExecutionStatus.Completed, state.ExecutionStatus);
        var session = state.ActiveSession;

        // Total messages: User prompt, Assistant turn 1 (tool call), Tool result, Assistant turn 2 (synthesis)
        Assert.True(session.Messages.Count >= 4);

        var toolMsg = session.Messages.First(m => m.Role == "tool");
        Assert.NotNull(toolMsg.Content);

        var finalMsg = session.Messages.Last();
        Assert.Equal("assistant", finalMsg.Role);
        Assert.Contains("Glacier.Studio.Tests", finalMsg.Content);
    }

    [Fact]
    public void CancelCurrentExecution_AbortsActiveLoopAndSetsStatus()
    {
        var (engine, state, _) = CreateTestEngine();

        engine.CancelCurrentExecution();

        Assert.Equal(AgentExecutionStatus.Cancelled, state.ExecutionStatus);
        Assert.Equal("Cancelled by user", state.StatusText);
    }

    [Fact]
    public async Task SlashCommand_Schedule_ExecutesDelayedCommandOnTimer()
    {
        var (engine, state, _) = CreateTestEngine();
        state.SelectedModel = "Glacier-Grandmaster-7B-Q4_K_M";

        await engine.ProcessUserMessageAsync("/schedule 80ms /status");
        Assert.Contains("Timer Armed", state.ActiveSession.Messages.Last().Content);
        Assert.NotEmpty(state.ActiveSession.WorkingMemory.EstablishedFacts);

        for (int i = 0; i < 60 && !state.ActiveSession.Messages.Any(m => m.Role == "assistant" && m.Content.Contains("Glacier Studio Status")); i++)
        {
            await Task.Delay(50);
        }
        Assert.Contains(state.ActiveSession.Messages, m => m.Role == "assistant" && m.Content.Contains("Glacier Studio Status"));
    }

    private static (GlacierAgentEngine Engine, GlacierStudioState State, CapturingMockHttpMessageHandler MockHttp) CreateTestEngine(CapturingMockHttpMessageHandler? handler = null)
    {
        var mockHttp = handler ?? new CapturingMockHttpMessageHandler();
        var httpClient = new HttpClient(mockHttp);
        var state = new GlacierStudioState();
        var consoleService = new GlacierConsoleService();
        var docPreviewService = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        var engine = new GlacierAgentEngine(
            httpClient,
            state,
            consoleService,
            docPreviewService,
            mcpManager);

        return (engine, state, mockHttp);
    }

    /// <summary>
    /// Mock HTTP message handler that captures incoming requests and returns configurable Server-Sent Events (SSE) or JSON responses.
    /// </summary>
    private sealed class CapturingMockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responseQueue = new();
        private string? _defaultResponse;

        public List<(string Url, string Content)> CapturedRequests { get; } = new();

        public void SetResponse(string content)
        {
            _defaultResponse = content;
        }

        public void QueueResponse(string content)
        {
            _responseQueue.Enqueue(content);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string requestBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : "";

            CapturedRequests.Add((request.RequestUri?.ToString() ?? "", requestBody));

            string textToEmit = _responseQueue.Count > 0
                ? _responseQueue.Dequeue()
                : (_defaultResponse ?? "OK");

            // Format as OpenAI-compatible Server-Sent Events stream
            var sseSb = new StringBuilder();
            var chunkJson = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new { content = textToEmit }
                    }
                }
            });
            sseSb.AppendLine($"data: {chunkJson}");
            sseSb.AppendLine();
            sseSb.AppendLine("data: [DONE]");
            sseSb.AppendLine();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseSb.ToString(), Encoding.UTF8, "text/event-stream")
            };

            return response;
        }
    }
}
