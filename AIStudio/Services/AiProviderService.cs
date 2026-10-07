using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AIStudio.Core;

namespace AIStudio.Services;

public sealed record AiChatRequest(string Message, bool UseVision = false, List<string>? ReferenceIds = null, string? SystemInstruction = null);
public sealed record AiChatResponse(string Text, string Provider, string Model);

public sealed class AiProviderService
{
    private readonly IHttpClientFactory _clients;
    private readonly SettingsStore _settings;
    private readonly SecretStore _secrets;
    private readonly AppPaths _paths;

    public AiProviderService(IHttpClientFactory clients, SettingsStore settings, SecretStore secrets, AppPaths paths)
    {
        _clients = clients;
        _settings = settings;
        _secrets = secrets;
        _paths = paths;
    }

    public async Task<AiChatResponse> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        var ai = settings.Ai;
        if (string.IsNullOrWhiteSpace(ai.Model)) throw new InvalidOperationException("Выберите модель ассистента в настройках.");
        var provider = ai.Provider.ToLowerInvariant();
        var model = request.UseVision && !string.IsNullOrWhiteSpace(ai.VisionModel) ? ai.VisionModel : ai.Model;
        var key = await _secrets.GetAsync(cancellationToken);
        var system = string.IsNullOrWhiteSpace(request.SystemInstruction) ? ai.SystemInstruction : request.SystemInstruction;

        return provider switch
        {
            "ollama" => new AiChatResponse(await ChatOllamaAsync(ai, model, system, request, cancellationToken), provider, model),
            "openai" => new AiChatResponse(await ChatOpenAiResponsesAsync(ai, model, system, request, key, cancellationToken), provider, model),
            "lmstudio" or "compatible" => new AiChatResponse(await ChatCompatibleAsync(ai, model, system, request, key, cancellationToken), provider, model),
            _ => throw new InvalidOperationException("Неизвестный провайдер ИИ.")
        };
    }

    public async Task<JsonNode?> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var ai = (await _settings.GetAsync(cancellationToken)).Ai;
        var client = CreateClient(ai.Endpoint, await _secrets.GetAsync(cancellationToken));
        var path = ai.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase) ? "api/tags" : "v1/models";
        using var response = await client.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Проверка провайдера: {(int)response.StatusCode} {body}");
        return JsonNode.Parse(body);
    }

    private async Task<string> ChatOllamaAsync(AiSettings settings, string model, string system, AiChatRequest request, CancellationToken cancellationToken)
    {
        var user = new JsonObject { ["role"] = "user", ["content"] = request.Message };
        var images = await LoadImagesAsync(request.ReferenceIds, cancellationToken);
        if (request.UseVision && images.Count > 0) user["images"] = new JsonArray(images.Select(x => (JsonNode)x.Base64).ToArray());
        var payload = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, user),
            ["options"] = new JsonObject { ["temperature"] = settings.Temperature, ["num_predict"] = settings.MaxTokens },
            ["keep_alive"] = "5m"
        };
        var client = CreateClient(settings.Endpoint, null);
        using var response = await client.PostAsJsonAsync("api/chat", payload, cancellationToken);
        return Extract(await ReadSuccessAsync(response, cancellationToken), "message", "content");
    }

    private async Task<string> ChatCompatibleAsync(AiSettings settings, string model, string system, AiChatRequest request, string? apiKey, CancellationToken cancellationToken)
    {
        JsonNode content = request.Message;
        var images = await LoadImagesAsync(request.ReferenceIds, cancellationToken);
        if (request.UseVision && images.Count > 0)
        {
            var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.Message });
            foreach (var image in images)
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{image.Base64}" } });
            content = parts;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = content }),
            ["temperature"] = settings.Temperature,
            ["max_tokens"] = settings.MaxTokens,
            ["stream"] = false
        };
        var client = CreateClient(settings.Endpoint, apiKey);
        using var response = await client.PostAsJsonAsync("v1/chat/completions", payload, cancellationToken);
        var root = await ReadSuccessAsync(response, cancellationToken);
        return root?["choices"]?[0]?["message"]?["content"]?.ToString() ?? throw new InvalidOperationException("Провайдер не вернул текст ответа.");
    }

    private async Task<string> ChatOpenAiResponsesAsync(AiSettings settings, string model, string system, AiChatRequest request, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Для OpenAI API требуется отдельный API-ключ; подписка ChatGPT не является API-доступом.");
        var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = request.Message });
        foreach (var image in await LoadImagesAsync(request.ReferenceIds, cancellationToken))
            content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{image.MediaType};base64,{image.Base64}" });
        var payload = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = system,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
            ["max_output_tokens"] = settings.MaxTokens
        };
        var client = CreateClient(settings.Endpoint.Length == 0 ? "https://api.openai.com" : settings.Endpoint, apiKey);
        using var response = await client.PostAsJsonAsync("v1/responses", payload, cancellationToken);
        var root = await ReadSuccessAsync(response, cancellationToken);
        if (root?["output_text"] is JsonValue outputText) return outputText.ToString();
        var parts = root?["output"]?.AsArray()
            .SelectMany(x => x?["content"]?.AsArray() ?? [])
            .Where(x => x?["type"]?.ToString() == "output_text")
            .Select(x => x?["text"]?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x));
        var text = string.Join("\n", parts ?? []);
        return text.Length > 0 ? text : throw new InvalidOperationException("OpenAI Responses API не вернул текст.");
    }

    private HttpClient CreateClient(string endpoint, string? apiKey)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Некорректный адрес ИИ-провайдера.");
        var client = _clients.CreateClient("ai");
        client.BaseAddress = uri;
        client.Timeout = TimeSpan.FromMinutes(5);
        if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private async Task<List<(string MediaType, string Base64)>> LoadImagesAsync(IEnumerable<string>? ids, CancellationToken cancellationToken)
    {
        var result = new List<(string, string)>();
        foreach (var id in ids ?? [])
        {
            var files = Directory.EnumerateFiles(_paths.References, Path.GetFileName(id) + ".*").Take(1).ToArray();
            if (files.Length == 0) continue;
            var mediaType = Path.GetExtension(files[0]).ToLowerInvariant() switch
            {
                ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg"
            };
            result.Add((mediaType, Convert.ToBase64String(await File.ReadAllBytesAsync(files[0], cancellationToken))));
        }
        return result;
    }

    private static async Task<JsonNode?> ReadSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ИИ-провайдер: {(int)response.StatusCode} {body}");
        return JsonNode.Parse(body);
    }

    private static string Extract(JsonNode? root, params string[] path)
    {
        foreach (var segment in path) root = root?[segment];
        return root?.ToString() ?? throw new InvalidOperationException("Провайдер не вернул текст ответа.");
    }
}

