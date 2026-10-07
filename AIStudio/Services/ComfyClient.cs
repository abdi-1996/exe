using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIStudio.Core;

namespace AIStudio.Services;

public sealed class ComfyClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsStore _settings;

    public ComfyClient(IHttpClientFactory httpClientFactory, SettingsStore settings)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    public async Task<Uri> GetBaseUriAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        var raw = settings.Engine.Mode.Equals("managed", StringComparison.OrdinalIgnoreCase)
            ? $"http://127.0.0.1:{settings.Engine.ManagedPort}"
            : settings.Engine.ExternalUrl;
        if (!Uri.TryCreate(raw.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Некорректный адрес ComfyUI.");
        }
        return uri;
    }

    public Task<JsonNode?> GetSystemStatsAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync("system_stats", cancellationToken);

    public Task<JsonNode?> GetQueueAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync("queue", cancellationToken);

    public Task<JsonNode?> GetHistoryAsync(string? promptId = null, CancellationToken cancellationToken = default) =>
        GetJsonAsync(promptId is null ? "history?max_items=100" : $"history/{Uri.EscapeDataString(promptId)}", cancellationToken);

    public Task<JsonNode?> GetObjectInfoAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync("object_info", cancellationToken);

    public Task<JsonNode?> GetModelsAsync(string category, CancellationToken cancellationToken = default) =>
        GetJsonAsync($"models/{Uri.EscapeDataString(category)}", cancellationToken);

    public async Task<JsonNode?> GetJsonAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(await GetBaseUriAsync(cancellationToken));
        using var response = await client.GetAsync(relativePath, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ComfyUI {relativePath}: {(int)response.StatusCode} {body}");
        }
        return JsonNode.Parse(body);
    }

    public async Task<JsonObject> QueuePromptAsync(JsonObject graph, string clientId, CancellationToken cancellationToken = default)
    {
        var promptId = Guid.NewGuid().ToString();
        var payload = new JsonObject
        {
            ["prompt"] = graph.DeepClone(),
            ["client_id"] = clientId,
            ["prompt_id"] = promptId,
            ["extra_data"] = new JsonObject
            {
                ["extra_pnginfo"] = new JsonObject { ["workflow"] = graph.DeepClone() },
                ["aistudio"] = true
            }
        };

        var client = CreateClient(await GetBaseUriAsync(cancellationToken));
        using var response = await client.PostAsJsonAsync("prompt", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ComfyUI отклонил workflow: {body}");
        }
        return JsonNode.Parse(body)?.AsObject() ?? throw new InvalidOperationException("ComfyUI вернул пустой ответ.");
    }

    public async Task InterruptAsync(string? promptId, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(await GetBaseUriAsync(cancellationToken));
        var payload = new JsonObject();
        if (promptId is not null) payload["prompt_id"] = promptId;
        using var response = await client.PostAsJsonAsync("interrupt", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task RemovePendingAsync(string promptId, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(await GetBaseUriAsync(cancellationToken));
        using var response = await client.PostAsJsonAsync("queue", new { delete = new[] { promptId } }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task FreeMemoryAsync(CancellationToken cancellationToken = default)
    {
        var client = CreateClient(await GetBaseUriAsync(cancellationToken));
        using var response = await client.PostAsJsonAsync("free", new { unload_models = true, free_memory = true }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonObject> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(await GetBaseUriAsync(cancellationToken));
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(content, "image", fileName);
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent("true"), "overwrite");
        using var response = await client.PostAsync("upload/image", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);
        return JsonNode.Parse(body)?.AsObject() ?? new JsonObject();
    }

    public async Task<(byte[] Content, string ContentType)> DownloadViewAsync(string fileName, string subfolder, string type, CancellationToken cancellationToken = default)
    {
        var query = $"view?filename={Uri.EscapeDataString(fileName)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
        var client = CreateClient(await GetBaseUriAsync(cancellationToken));
        using var response = await client.GetAsync(query, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new FileNotFoundException("ComfyUI не вернул указанный результат.");
        return (await response.Content.ReadAsByteArrayAsync(cancellationToken), response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
    }

    private HttpClient CreateClient(Uri baseUri)
    {
        var client = _httpClientFactory.CreateClient("comfy");
        client.BaseAddress = baseUri;
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }
}

