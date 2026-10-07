using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIStudio.Core;

namespace AIStudio.Services;

public sealed class JobStore
{
    private readonly string _path;
    private readonly JsonFileStore _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<JobRecord>? _jobs;

    public JobStore(AppPaths paths, JsonFileStore files)
    {
        _path = Path.Combine(paths.Settings, "jobs.json");
        _files = files;
    }

    public static string HashGraph(JsonObject graph) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(graph.ToJsonString()))).ToLowerInvariant();

    public async Task<JobRecord> AddAsync(JobRecord job, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var jobs = await LoadUnsafeAsync(cancellationToken);
            jobs.Insert(0, job);
            if (jobs.Count > 500) jobs.RemoveRange(500, jobs.Count - 500);
            await _files.WriteAsync(_path, jobs, cancellationToken);
            return job;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await LoadUnsafeAsync(cancellationToken)).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<JobRecord?> FindAsync(string idOrPromptId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await LoadUnsafeAsync(cancellationToken)).FirstOrDefault(x => x.Id == idOrPromptId || x.PromptId == idOrPromptId); }
        finally { _gate.Release(); }
    }

    public async Task HandleComfyEventAsync(JsonNode? message, CancellationToken cancellationToken = default)
    {
        if (message is not JsonObject root || root["type"]?.GetValue<string>() is not { } type || root["data"] is not JsonObject data) return;
        var promptId = data["prompt_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(promptId)) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var jobs = await LoadUnsafeAsync(cancellationToken);
            var index = jobs.FindIndex(x => x.PromptId == promptId);
            if (index < 0) return;
            var job = jobs[index];
            var status = job.Status;
            string? currentNode = job.CurrentNode;
            double? progress = job.Progress;
            string? error = job.Error;

            switch (type)
            {
                case "execution_start": status = "running"; progress = 0; break;
                case "executing":
                    currentNode = data["node"]?.ToString();
                    if (data["node"] is null) { status = "completed"; progress = 1; }
                    break;
                case "progress":
                    var value = data["value"]?.GetValue<double>() ?? 0;
                    var maximum = data["max"]?.GetValue<double>() ?? 0;
                    progress = maximum > 0 ? value / maximum : null;
                    currentNode = data["node"]?.ToString() ?? currentNode;
                    break;
                case "execution_success": status = "completed"; progress = 1; currentNode = null; break;
                case "execution_error": status = "failed"; error = data["exception_message"]?.ToString() ?? "Ошибка выполнения ComfyUI"; break;
                case "execution_interrupted": status = "cancelled"; break;
            }

            jobs[index] = job with { Status = status, CurrentNode = currentNode, Progress = progress, Error = error, UpdatedAt = DateTimeOffset.UtcNow };
            await _files.WriteAsync(_path, jobs, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkCancelledAsync(string promptId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var jobs = await LoadUnsafeAsync(cancellationToken);
            var index = jobs.FindIndex(x => x.PromptId == promptId);
            if (index < 0) return;
            jobs[index] = jobs[index] with { Status = "cancelled", UpdatedAt = DateTimeOffset.UtcNow };
            await _files.WriteAsync(_path, jobs, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ReconcileAsync(JsonNode? queueNode, JsonNode? historyNode, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var jobs = await LoadUnsafeAsync(cancellationToken);
            var running = PromptIds(queueNode?["queue_running"] as JsonArray);
            var pending = PromptIds(queueNode?["queue_pending"] as JsonArray);
            var changed = false;
            for (var index = 0; index < jobs.Count; index++)
            {
                var job = jobs[index];
                if (job.Status is "completed" or "failed" or "cancelled") continue;
                string? status = null;
                string? error = job.Error;
                double? progress = job.Progress;
                if (running.Contains(job.PromptId)) status = "running";
                else if (pending.Contains(job.PromptId)) status = "queued";
                else if (historyNode?[job.PromptId] is JsonObject history)
                {
                    var statusText = history["status"]?["status_str"]?.ToString();
                    var completed = history["status"]?["completed"]?.GetValue<bool>() ?? false;
                    status = completed || statusText == "success" ? "completed" : statusText == "error" ? "failed" : job.Status;
                    if (status == "completed") progress = 1;
                    if (status == "failed") error = "ComfyUI сообщил об ошибке; откройте историю и журнал движка.";
                }
                if (status is not null && status != job.Status)
                {
                    jobs[index] = job with { Status = status, Progress = progress, Error = error, UpdatedAt = DateTimeOffset.UtcNow };
                    changed = true;
                }
            }
            if (changed) await _files.WriteAsync(_path, jobs, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static HashSet<string> PromptIds(JsonArray? queue)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in queue ?? [])
            if (item is JsonArray { Count: > 1 } entry && entry[1] is not null) result.Add(entry[1]!.ToString());
        return result;
    }

    private async Task<List<JobRecord>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        _jobs ??= await _files.ReadAsync<List<JobRecord>>(_path, cancellationToken) ?? [];
        return _jobs;
    }
}

public sealed class JobReconciler : BackgroundService
{
    private readonly ComfyClient _comfy;
    private readonly JobStore _jobs;

    public JobReconciler(ComfyClient comfy, JobStore jobs)
    {
        _comfy = comfy;
        _jobs = jobs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var queue = await _comfy.GetQueueAsync(stoppingToken);
                var history = await _comfy.GetHistoryAsync(cancellationToken: stoppingToken);
                await _jobs.ReconcileAsync(queue, history, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch { /* Engine status is reported by /api/status; retry after reconnect. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}

