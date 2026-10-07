using System.Text.Json.Nodes;

namespace AIStudio.Core;

public sealed record StudioSettings
{
    public int Port { get; init; } = 7123;
    public bool AllowLan { get; init; }
    public string AccessToken { get; init; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public EngineSettings Engine { get; init; } = new();
    public AiSettings Ai { get; init; } = new();
    public List<string> ModelRoots { get; init; } = [];
    public string OptimizationProfile { get; init; } = "balanced";
}

public sealed record EngineSettings
{
    public string Mode { get; init; } = "external";
    public string ExternalUrl { get; init; } = "http://127.0.0.1:8188";
    public string ManagedDirectory { get; init; } = "";
    public string PythonExecutable { get; init; } = "";
    public int ManagedPort { get; init; } = 8188;
    public bool AutoStart { get; init; } = true;
    public string ExtraArguments { get; init; } = "";
}

public sealed record AiSettings
{
    public string Provider { get; init; } = "ollama";
    public string Endpoint { get; init; } = "http://127.0.0.1:11434";
    public string Model { get; init; } = "";
    public string VisionModel { get; init; } = "";
    public string SystemInstruction { get; init; } = "Ты — ассистент AI Studio. Предлагай только ноды и модели, которые переданы тебе в контексте.";
    public double Temperature { get; init; } = 0.7;
    public int MaxTokens { get; init; } = 2048;
}

public sealed record EngineSnapshot(
    bool Reachable,
    bool ManagedProcessRunning,
    string Mode,
    string BaseUrl,
    string? Error,
    JsonNode? SystemStats,
    JsonNode? Queue,
    DateTimeOffset CheckedAt);

public sealed record WorkflowBinding(
    string Id,
    string Kind,
    string NodeId,
    string InputName,
    string Label,
    string ValueType,
    JsonNode? Value,
    IReadOnlyList<string>? Choices = null);

public sealed record WorkflowProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string SourceFormat { get; init; }
    public required JsonObject ApiGraph { get; init; }
    public JsonObject? UiGraph { get; init; }
    public List<WorkflowBinding> Bindings { get; init; } = [];
    public List<string> MissingNodes { get; init; } = [];
    public List<string> MissingModels { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public long Revision { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record JobRecord
{
    public required string Id { get; init; }
    public required string PromptId { get; init; }
    public required string WorkflowId { get; init; }
    public required string WorkflowName { get; init; }
    public required string GraphSha256 { get; init; }
    public required JsonObject SubmittedGraph { get; init; }
    public Dictionary<string, JsonNode?> Parameters { get; init; } = [];
    public string OriginalPrompt { get; init; } = "";
    public string EnhancedPrompt { get; init; } = "";
    public string Status { get; init; } = "queued";
    public string? CurrentNode { get; init; }
    public double? Progress { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ReferenceAsset(
    string Id,
    string FileName,
    string MediaType,
    long Size,
    DateTimeOffset CreatedAt,
    string DownloadUrl);

