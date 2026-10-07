using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AIStudio.Core;
using AIStudio.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024);
var dataRootOverride = Environment.GetEnvironmentVariable("AISTUDIO_DATA_DIR");
var keyDirectory = !string.IsNullOrWhiteSpace(dataRootOverride)
    ? Path.Combine(Path.GetFullPath(dataRootOverride), "settings", "keys")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Studio", "settings", "keys");
Directory.CreateDirectory(keyDirectory);
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("AIStudio")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
builder.Services.AddHttpClient("comfy");
builder.Services.AddHttpClient("ai");
builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<JsonFileStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<ComfyClient>();
builder.Services.AddSingleton<WorkflowService>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<AiProviderService>();
builder.Services.AddSingleton<GraphProposalService>();
builder.Services.AddSingleton<EngineManager>();
builder.Services.AddSingleton<ComfyEventRelay>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ComfyEventRelay>());
builder.Services.AddHostedService<JobReconciler>();
builder.Services.AddHostedService<BrowserLauncher>();

var app = builder.Build();
var appPaths = app.Services.GetRequiredService<AppPaths>();
var startupSettings = await app.Services.GetRequiredService<SettingsStore>().GetAsync();
app.Urls.Add($"http://{(startupSettings.AllowLan ? "0.0.0.0" : "127.0.0.1")}:{startupSettings.Port}");

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.Equals("sw.js", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25) });

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/ws"))
    {
        await next();
        return;
    }

    var settings = await context.RequestServices.GetRequiredService<SettingsStore>().GetAsync(context.RequestAborted);
    var supplied = context.Request.Headers["X-AIStudio-Token"].FirstOrDefault() ?? context.Request.Query["token"].FirstOrDefault() ?? "";
    var expectedBytes = Encoding.UTF8.GetBytes(settings.AccessToken);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    if (expectedBytes.Length != suppliedBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Требуется токен AI Studio." }, context.RequestAborted);
        return;
    }
    await next();
});

app.MapGet("/api/auth/check", () => Results.Ok(new { authenticated = true }));

app.MapGet("/api/status", async (EngineManager engine, CancellationToken ct) =>
    Results.Ok(await engine.GetSnapshotAsync(ct)));

app.MapPost("/api/engine/start", async (EngineManager engine, CancellationToken ct) =>
{
    await engine.StartManagedAsync(ct);
    return Results.Ok(await engine.GetSnapshotAsync(ct));
});

app.MapPost("/api/engine/stop", async (EngineManager engine, CancellationToken ct) =>
{
    await engine.StopManagedAsync(ct);
    return Results.Ok(new { stopped = true });
});

app.MapPost("/api/engine/free", async (ComfyClient comfy, CancellationToken ct) =>
{
    await comfy.FreeMemoryAsync(ct);
    return Results.Ok(new { requested = true, note = "ComfyUI получил запрос на выгрузку моделей и освобождение своей памяти." });
});

app.MapGet("/api/settings", async (SettingsStore settings, SecretStore secrets, CancellationToken ct) =>
{
    var value = await settings.GetAsync(ct);
    return Results.Ok(new { settings = value with { AccessToken = "" }, hasApiKey = !string.IsNullOrWhiteSpace(await secrets.GetAsync(ct)) });
});

app.MapPut("/api/settings", async (SettingsUpdate request, SettingsStore settings, SecretStore secrets, CancellationToken ct) =>
{
    var saved = await settings.SaveAsync(request.Settings, ct);
    if (request.ApiKey is not null) await secrets.SaveAsync(request.ApiKey, ct);
    return Results.Ok(new { settings = saved with { AccessToken = "" }, restartRequired = true });
});

app.MapGet("/api/workflows", async (WorkflowService workflows, CancellationToken ct) => Results.Ok(await workflows.ListAsync(ct)));
app.MapGet("/api/workflows/{id}", async (string id, WorkflowService workflows, CancellationToken ct) =>
    await workflows.GetAsync(id, ct) is { } profile ? Results.Ok(profile) : Results.NotFound());

app.MapPost("/api/workflows/import", async (WorkflowImport request, WorkflowService workflows, CancellationToken ct) =>
    Results.Ok(await workflows.ImportAsync(request.Name, request.Graph, ct)));

app.MapPut("/api/workflows/{id}/graph", async (string id, WorkflowGraphUpdate request, WorkflowService workflows, CancellationToken ct) =>
    Results.Ok(await workflows.ReplaceGraphAsync(id, request.Revision, request.Graph, ct)));

app.MapGet("/api/workflows/{id}/export/{format}", async (string id, string format, WorkflowService workflows, CancellationToken ct) =>
{
    var profile = await workflows.GetAsync(id, ct);
    if (profile is null) return Results.NotFound();
    var graph = format.Equals("ui", StringComparison.OrdinalIgnoreCase) ? profile.UiGraph : profile.ApiGraph;
    return graph is null ? Results.BadRequest(new { error = "UI JSON для этого workflow недоступен." }) : Results.Json(graph);
});

app.MapPost("/api/generate", async (
    GenerationRequest request,
    WorkflowService workflows,
    ComfyClient comfy,
    ComfyEventRelay relay,
    JobStore jobs,
    CancellationToken ct) =>
{
    var profile = await workflows.UpdateParametersAsync(request.WorkflowId, request.Revision, request.Values, ct);
    if (profile.MissingNodes.Count > 0) return Results.BadRequest(new { error = "Отсутствуют ноды", missingNodes = profile.MissingNodes });
    if (profile.MissingModels.Count > 0) return Results.BadRequest(new { error = "Отсутствуют модели", missingModels = profile.MissingModels });
    var queued = await comfy.QueuePromptAsync(profile.ApiGraph, relay.ClientId, ct);
    var promptId = queued["prompt_id"]?.ToString() ?? throw new InvalidOperationException("ComfyUI не вернул prompt_id.");
    var job = new JobRecord
    {
        Id = Guid.NewGuid().ToString("N"), PromptId = promptId, WorkflowId = profile.Id, WorkflowName = profile.Name,
        GraphSha256 = JobStore.HashGraph(profile.ApiGraph), SubmittedGraph = profile.ApiGraph.DeepClone().AsObject(),
        Parameters = request.Values.ToDictionary(x => x.Key, x => x.Value?.DeepClone()), OriginalPrompt = request.OriginalPrompt ?? "",
        EnhancedPrompt = request.EnhancedPrompt ?? ""
    };
    await jobs.AddAsync(job, ct);
    return Results.Ok(new { job, workflowRevision = profile.Revision, comfy = queued });
});

app.MapGet("/api/jobs", async (JobStore jobs, CancellationToken ct) => Results.Ok(await jobs.ListAsync(ct)));
app.MapGet("/api/jobs/{id}/history", async (string id, JobStore jobs, ComfyClient comfy, CancellationToken ct) =>
{
    var job = await jobs.FindAsync(id, ct);
    return job is null ? Results.NotFound() : Results.Ok(await comfy.GetHistoryAsync(job.PromptId, ct));
});

app.MapPost("/api/jobs/{id}/reference", async (string id, JobStore jobs, ComfyClient comfy, AppPaths paths, CancellationToken ct) =>
{
    var job = await jobs.FindAsync(id, ct);
    if (job is null) return Results.NotFound();
    var history = await comfy.GetHistoryAsync(job.PromptId, ct);
    var output = FindFirstOutput(history);
    if (output is null) return Results.BadRequest(new { error = "В истории нет финального файла." });
    var content = await comfy.DownloadViewAsync(output.Value.FileName, output.Value.Subfolder, output.Value.Type, ct);
    var extension = Path.GetExtension(output.Value.FileName);
    var target = Path.Combine(paths.References, Guid.NewGuid().ToString("N") + extension);
    await File.WriteAllBytesAsync(target, content.Content, ct);
    return Results.Ok(ToReference(target));
});

app.MapPost("/api/jobs/{id}/open-folder", async (string id, JobStore jobs, ComfyClient comfy, SettingsStore settings, AppPaths paths, CancellationToken ct) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "Открытие проводника доступно только на Windows." });
    var configuration = await settings.GetAsync(ct);
    if (!configuration.Engine.Mode.Equals("managed", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Папка результатов внешнего ComfyUI неизвестна AI Studio." });
    var job = await jobs.FindAsync(id, ct);
    if (job is null) return Results.NotFound();
    var output = FindFirstOutput(await comfy.GetHistoryAsync(job.PromptId, ct));
    if (output is null) return Results.BadRequest(new { error = "В истории нет финального файла." });
    var root = Path.GetFullPath(paths.Results);
    var file = Path.GetFullPath(Path.Combine(root, output.Value.Subfolder, output.Value.FileName));
    if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return Results.NotFound();
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    return Results.Ok(new { opened = true });
});

app.MapPost("/api/jobs/{id}/cancel", async (string id, JobStore jobs, ComfyClient comfy, CancellationToken ct) =>
{
    var job = await jobs.FindAsync(id, ct);
    if (job is null) return Results.NotFound();
    await comfy.InterruptAsync(job.PromptId, ct);
    await comfy.RemovePendingAsync(job.PromptId, ct);
    await jobs.MarkCancelledAsync(job.PromptId, ct);
    return Results.Ok(new { cancelled = true });
});

app.MapGet("/api/comfy/nodes", async (ComfyClient comfy, CancellationToken ct) => Results.Ok(await comfy.GetObjectInfoAsync(ct)));
app.MapGet("/api/comfy/models/{category}", async (string category, ComfyClient comfy, CancellationToken ct) => Results.Ok(await comfy.GetModelsAsync(category, ct)));
app.MapGet("/api/comfy/view", async (string filename, string? subfolder, string? type, ComfyClient comfy, CancellationToken ct) =>
{
    var result = await comfy.DownloadViewAsync(filename, subfolder ?? "", type ?? "output", ct);
    return Results.File(result.Content, result.ContentType, enableRangeProcessing: true);
});

app.MapGet("/api/references", (AppPaths paths) =>
{
    var assets = Directory.EnumerateFiles(paths.References)
        .Select(path => ToReference(path))
        .OrderByDescending(x => x.CreatedAt);
    return Results.Ok(assets);
});

app.MapPost("/api/references", async (HttpRequest httpRequest, AppPaths paths, ComfyClient comfy, CancellationToken ct) =>
{
    if (!httpRequest.HasFormContentType) return Results.BadRequest(new { error = "Ожидается multipart/form-data." });
    var form = await httpRequest.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Файл не выбран." });
    if (file.Length > 2L * 1024 * 1024 * 1024) return Results.BadRequest(new { error = "Файл превышает 2 ГБ." });
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    var allowed = new HashSet<string> { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".webm", ".mov", ".mp3", ".wav", ".flac", ".m4a" };
    if (!allowed.Contains(extension)) return Results.BadRequest(new { error = "Неподдерживаемый формат файла." });
    var id = Guid.NewGuid().ToString("N");
    var path = Path.Combine(paths.References, id + extension);
    await using (var target = File.Create(path)) await file.CopyToAsync(target, ct);
    JsonObject? comfyUpload = null;
    if ((file.ContentType ?? "").StartsWith("image/"))
    {
        await using var source = File.OpenRead(path);
        try { comfyUpload = await comfy.UploadAsync(source, Path.GetFileName(path), file.ContentType ?? "image/png", ct); }
        catch { /* The local asset remains available; engine status reports the connection problem. */ }
    }
    return Results.Ok(new { asset = ToReference(path), comfy = comfyUpload });
});

app.MapGet("/api/references/{id}", (string id, AppPaths paths) =>
{
    var path = Directory.EnumerateFiles(paths.References, Path.GetFileName(id) + ".*").FirstOrDefault();
    return path is null ? Results.NotFound() : Results.File(path, ContentType(path), enableRangeProcessing: true);
});

app.MapGet("/api/files", async (string? path, SettingsStore settings, CancellationToken ct) =>
{
    var roots = (await settings.GetAsync(ct)).ModelRoots.Where(Directory.Exists).Select(Path.GetFullPath).ToArray();
    var requested = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    if (requested is null) return Results.Ok(roots.Select(x => new { name = Path.GetFileName(x), path = x, directory = true, root = true }));
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (!roots.Any(root => requested.Equals(root, comparison) || requested.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)))
        return Results.Forbid();
    var entries = Directory.EnumerateFileSystemEntries(requested).Take(2000).Select(entry => new
    {
        name = Path.GetFileName(entry), path = entry, directory = Directory.Exists(entry),
        size = File.Exists(entry) ? new FileInfo(entry).Length : (long?)null
    });
    return Results.Ok(entries);
});

app.MapPost("/api/assistant/chat", async (AiChatRequest request, AiProviderService ai, CancellationToken ct) => Results.Ok(await ai.ChatAsync(request, ct)));
app.MapPost("/api/assistant/enhance", async (PromptEnhanceRequest request, AiProviderService ai, CancellationToken ct) =>
{
    var instruction = "Улучши промт для указанного workflow и модели. Сохрани смысл. Верни только готовый промт без пояснений." +
                      $"\nWorkflow: {request.WorkflowName}\nМодель: {request.ModelName}";
    return Results.Ok(await ai.ChatAsync(new AiChatRequest(request.Prompt, request.ReferenceIds?.Count > 0, request.ReferenceIds, instruction), ct));
});
app.MapGet("/api/assistant/models", async (AiProviderService ai, CancellationToken ct) => Results.Ok(await ai.ListModelsAsync(ct)));
app.MapPost("/api/assistant/graph/propose", async (GraphProposalRequest request, GraphProposalService proposals, CancellationToken ct) =>
    Results.Ok(await proposals.PrepareAsync(request.WorkflowId, request.Instruction, ct)));
app.MapPost("/api/assistant/graph/apply/{proposalId}", async (string proposalId, GraphProposalService proposals, CancellationToken ct) =>
    Results.Ok(await proposals.ApplyAsync(proposalId, ct)));

app.Map("/ws/events", async (HttpContext context, EventHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.AcceptAsync(socket, context.RequestAborted);
});

app.MapFallbackToFile("index.html");

try
{
    await app.RunAsync();
}
catch (IOException exception) when (exception.Message.Contains("address", StringComparison.OrdinalIgnoreCase))
{
    await File.WriteAllTextAsync(Path.Combine(appPaths.Logs, "startup-error.log"), $"{DateTimeOffset.Now:O}\n{exception}");
    throw;
}

static ReferenceAsset ToReference(string path)
{
    var info = new FileInfo(path);
    var id = Path.GetFileNameWithoutExtension(path);
    return new ReferenceAsset(id, info.Name, ContentType(path), info.Length, info.CreationTimeUtc, $"/api/references/{id}");
}

static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
    ".mp4" => "video/mp4", ".webm" => "video/webm", ".mov" => "video/quicktime", ".mp3" => "audio/mpeg",
    ".wav" => "audio/wav", ".flac" => "audio/flac", ".m4a" => "audio/mp4", _ => "application/octet-stream"
};

static (string FileName, string Subfolder, string Type)? FindFirstOutput(JsonNode? node)
{
    if (node is JsonObject obj)
    {
        if (obj["filename"] is not null) return (obj["filename"]!.ToString(), obj["subfolder"]?.ToString() ?? "", obj["type"]?.ToString() ?? "output");
        foreach (var value in obj.Select(x => x.Value)) if (FindFirstOutput(value) is { } found) return found;
    }
    if (node is JsonArray array) foreach (var value in array) if (FindFirstOutput(value) is { } found) return found;
    return null;
}

public sealed record SettingsUpdate(StudioSettings Settings, string? ApiKey);
public sealed record WorkflowImport(string Name, JsonObject Graph);
public sealed record WorkflowGraphUpdate(long Revision, JsonObject Graph);
public sealed record GenerationRequest(string WorkflowId, long Revision, Dictionary<string, JsonNode?> Values, string? OriginalPrompt, string? EnhancedPrompt);
public sealed record PromptEnhanceRequest(string Prompt, string WorkflowName, string ModelName, List<string>? ReferenceIds);
public sealed record GraphProposalRequest(string WorkflowId, string Instruction);
