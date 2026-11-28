using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glacier.Showcase.Services.GlacierStudio;
using Glacier.Studio.Services;

namespace Glacier.Studio.Serialization;

public sealed class StudioPreferencesPayload
{
    public int UiZoomPercent { get; set; } = 100;
    public string? ActiveWorkspaceRoot { get; set; }
    public List<string>? Projects { get; set; }
}

public sealed class StudioEncryptedSecretsPayload
{
    public int Version { get; set; } = 2;
    public Dictionary<string, string> EncryptedProviderKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class McpJsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }
}

public sealed class McpJsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }
}

public sealed class McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public McpCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("clientInfo")]
    public McpClientInfo ClientInfo { get; set; } = new();
}

public sealed class McpCapabilities
{
    [JsonPropertyName("tools")]
    public McpToolsCapability Tools { get; set; } = new();
}

public sealed class McpToolsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}

public sealed class McpClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Glacier.Studio";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.2.1";
}

public sealed class McpEmptyParams
{
}

public sealed class McpCallToolParams
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }
}

public sealed class McpToolErrorResponse
{
    [JsonPropertyName("error")]
    public bool Error { get; set; } = true;

    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class ChatCompletionRequestDto
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<ChatRequestMessageDto> Messages { get; set; } = new();

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }

    [JsonPropertyName("repetition_penalty")]
    public float RepetitionPenalty { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OpenAiToolDefinitionDto>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; set; }
}

public sealed class ChatRequestMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatRequestToolCallDto>? ToolCalls { get; set; }
}

public sealed class ChatRequestToolCallDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatRequestFunctionCallDto Function { get; set; } = new();
}

public sealed class ChatRequestFunctionCallDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

public sealed class OpenAiToolDefinitionDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionSpecDto Function { get; set; } = new();
}

public sealed class OpenAiFunctionSpecDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("parameters")]
    public OpenAiToolParametersDto Parameters { get; set; } = new();
}

public sealed class OpenAiToolParametersDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, OpenAiToolPropertyDto> Properties { get; set; } = new();

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Required { get; set; }
}

public sealed class OpenAiToolPropertyDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

public sealed class FetchWebUrlArgsDto
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class SearchWebArgsDto
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";
}

public sealed class RawToolArgsDto
{
    [JsonPropertyName("raw")]
    public string Raw { get; set; } = "";
}

public sealed class FallbackToolArgumentsDto
{
    [JsonPropertyName("raw")]
    public string Raw { get; set; } = "";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";
}

[JsonSerializable(typeof(StudioSession))]
[JsonSerializable(typeof(List<StudioSession>))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(AgentToolCallRecord))]
[JsonSerializable(typeof(List<AgentToolCallRecord>))]
[JsonSerializable(typeof(StudioWorkingMemory))]
[JsonSerializable(typeof(StudioPreferencesPayload))]
[JsonSerializable(typeof(StudioEncryptedSecretsPayload))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(McpServerDefinition))]
[JsonSerializable(typeof(McpToolDefinition))]
[JsonSerializable(typeof(ToolParameterSchema))]
[JsonSerializable(typeof(McpJsonRpcRequest))]
[JsonSerializable(typeof(McpJsonRpcNotification))]
[JsonSerializable(typeof(McpInitializeParams))]
[JsonSerializable(typeof(McpCapabilities))]
[JsonSerializable(typeof(McpToolsCapability))]
[JsonSerializable(typeof(McpClientInfo))]
[JsonSerializable(typeof(McpEmptyParams))]
[JsonSerializable(typeof(McpCallToolParams))]
[JsonSerializable(typeof(McpToolErrorResponse))]
[JsonSerializable(typeof(ChatCompletionRequestDto))]
[JsonSerializable(typeof(ChatRequestMessageDto))]
[JsonSerializable(typeof(List<ChatRequestMessageDto>))]
[JsonSerializable(typeof(ChatRequestToolCallDto))]
[JsonSerializable(typeof(ChatRequestFunctionCallDto))]
[JsonSerializable(typeof(OpenAiToolDefinitionDto))]
[JsonSerializable(typeof(List<OpenAiToolDefinitionDto>))]
[JsonSerializable(typeof(OpenAiFunctionSpecDto))]
[JsonSerializable(typeof(OpenAiToolParametersDto))]
[JsonSerializable(typeof(OpenAiToolPropertyDto))]
[JsonSerializable(typeof(FetchWebUrlArgsDto))]
[JsonSerializable(typeof(SearchWebArgsDto))]
[JsonSerializable(typeof(RawToolArgsDto))]
[JsonSerializable(typeof(FallbackToolArgumentsDto))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class StudioJsonContext : JsonSerializerContext
{
    public static StudioJsonContext Indented { get; } = new(new JsonSerializerOptions(Default?.Options ?? new JsonSerializerOptions())
    {
        WriteIndented = true
    });
}
