using System.Text.Json.Nodes;
using AIStudio.Core;

namespace AIStudio.Services;

public sealed class WorkflowService
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _files;
    private readonly ComfyClient _comfy;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkflowService(AppPaths paths, JsonFileStore files, ComfyClient comfy)
    {
        _paths = paths;
        _files = files;
        _comfy = comfy;
    }

    public async Task<IReadOnlyList<WorkflowProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<WorkflowProfile>();
        foreach (var path in Directory.EnumerateFiles(_paths.Workflows, "*.json"))
        {
            var profile = await _files.ReadAsync<WorkflowProfile>(path, cancellationToken);
            if (profile is not null) result.Add(profile);
        }
        return result.OrderByDescending(x => x.UpdatedAt).ToArray();
    }

    public Task<WorkflowProfile?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        _files.ReadAsync<WorkflowProfile>(ProfilePath(id), cancellationToken);

    public async Task<WorkflowProfile> ImportAsync(string name, JsonObject source, CancellationToken cancellationToken = default)
    {
        JsonObject schemas;
        var warnings = new List<string>();
        try { schemas = (await _comfy.GetObjectInfoAsync(cancellationToken))?.AsObject() ?? new JsonObject(); }
        catch (Exception exception)
        {
            schemas = new JsonObject();
            warnings.Add("Схемы нод недоступны: " + exception.Message);
        }

        var format = DetectFormat(source);
        JsonObject api;
        JsonObject? ui;
        if (format == "api")
        {
            api = source.DeepClone().AsObject();
            ui = ApiToUi(api, schemas);
        }
        else
        {
            if (schemas.Count == 0) throw new InvalidOperationException("Для преобразования UI JSON подключите ComfyUI, чтобы получить схемы нод.");
            ui = source.DeepClone().AsObject();
            api = UiToApi(ui, schemas, warnings);
        }

        var id = Guid.NewGuid().ToString("N");
        var profile = Analyze(new WorkflowProfile
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? "Workflow" : name.Trim(),
            SourceFormat = format,
            ApiGraph = api,
            UiGraph = ui,
            Warnings = warnings
        }, schemas);
        await SaveAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<WorkflowProfile> UpdateParametersAsync(
        string id,
        long expectedRevision,
        IReadOnlyDictionary<string, JsonNode?> values,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Workflow не найден.");
            if (profile.Revision != expectedRevision)
                throw new InvalidOperationException($"Workflow уже изменён (актуальная ревизия {profile.Revision}). Обновите страницу перед генерацией.");

            var graph = profile.ApiGraph.DeepClone().AsObject();
            foreach (var (bindingId, value) in values)
            {
                var binding = profile.Bindings.FirstOrDefault(x => x.Id == bindingId)
                    ?? throw new InvalidOperationException($"Привязка {bindingId} не найдена.");
                var node = graph[binding.NodeId]?.AsObject() ?? throw new InvalidOperationException($"Нода {binding.NodeId} отсутствует.");
                var inputs = node["inputs"]?.AsObject() ?? throw new InvalidOperationException($"У ноды {binding.NodeId} отсутствуют inputs.");
                inputs[binding.InputName] = value?.DeepClone();
            }

            JsonObject schemas;
            try { schemas = (await _comfy.GetObjectInfoAsync(cancellationToken))?.AsObject() ?? new JsonObject(); }
            catch { schemas = new JsonObject(); }
            var updated = Analyze(profile with
            {
                ApiGraph = graph,
                UiGraph = ApiToUi(graph, schemas),
                Revision = profile.Revision + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            }, schemas);
            await SaveAsync(updated, cancellationToken);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkflowProfile> ReplaceGraphAsync(string id, long expectedRevision, JsonObject source, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Workflow не найден.");
        if (existing.Revision != expectedRevision) throw new InvalidOperationException("Workflow изменился в другой вкладке.");
        var schemas = (await _comfy.GetObjectInfoAsync(cancellationToken))?.AsObject() ?? new JsonObject();
        var warnings = new List<string>();
        var format = DetectFormat(source);
        var api = format == "api" ? source.DeepClone().AsObject() : UiToApi(source, schemas, warnings);
        var ui = format == "ui" ? source.DeepClone().AsObject() : ApiToUi(api, schemas);
        var updated = Analyze(existing with
        {
            SourceFormat = format,
            ApiGraph = api,
            UiGraph = ui,
            Warnings = warnings,
            Revision = existing.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow
        }, schemas);
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<WorkflowProfile> ApplyOperationsAsync(string id, long expectedRevision, IReadOnlyList<GraphInputOperation> operations, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await GetAsync(id, cancellationToken) ?? throw new FileNotFoundException("Workflow не найден.");
            if (profile.Revision != expectedRevision) throw new InvalidOperationException("Workflow изменён после подготовки предложения. Подготовьте изменения заново.");
            var schemas = (await _comfy.GetObjectInfoAsync(cancellationToken))?.AsObject() ?? throw new InvalidOperationException("Схемы ComfyUI недоступны.");
            var graph = profile.ApiGraph.DeepClone().AsObject();
            foreach (var operation in operations)
            {
                var node = graph[operation.NodeId] as JsonObject ?? throw new InvalidOperationException($"Нода {operation.NodeId} не существует.");
                var classType = node["class_type"]?.ToString() ?? "";
                var inputs = node["inputs"] as JsonObject ?? throw new InvalidOperationException($"У ноды {operation.NodeId} нет входов.");
                if (!inputs.ContainsKey(operation.InputName)) throw new InvalidOperationException($"Вход {operation.InputName} отсутствует у ноды {operation.NodeId}.");
                if (IsConnection(inputs[operation.InputName])) throw new InvalidOperationException("Ассистент не может незаметно заменить соединение значением.");
                var descriptor = FindDescriptor(schemas[classType] as JsonObject, operation.InputName);
                var choices = GetChoices(descriptor);
                if (choices is { Count: > 0 } && operation.Value is not null && !choices.Contains(operation.Value.ToString(), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Значение {operation.Value} недоступно для {classType}.{operation.InputName}.");
                inputs[operation.InputName] = operation.Value?.DeepClone();
            }
            var updated = Analyze(profile with { ApiGraph = graph, UiGraph = ApiToUi(graph, schemas), Revision = profile.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow }, schemas);
            await SaveAsync(updated, cancellationToken);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public static string DetectFormat(JsonObject graph)
    {
        if (graph["nodes"] is JsonArray && graph["links"] is JsonArray) return "ui";
        if (graph.Count > 0 && graph.All(item => item.Value is JsonObject node && node["class_type"] is not null)) return "api";
        throw new InvalidOperationException("Не удалось распознать workflow. Ожидается UI JSON с nodes/links или API JSON с class_type/inputs.");
    }

    private WorkflowProfile Analyze(WorkflowProfile profile, JsonObject schemas)
    {
        var bindings = new List<WorkflowBinding>();
        var missingNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = profile.Warnings.ToList();

        foreach (var (nodeId, value) in profile.ApiGraph)
        {
            if (value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";
            var title = node["_meta"]?["title"]?.ToString() ?? classType;
            var schema = schemas[classType] as JsonObject;
            if (schemas.Count > 0 && schema is null) missingNodes.Add(classType);
            if (node["inputs"] is not JsonObject inputs) continue;

            foreach (var (inputName, inputValue) in inputs)
            {
                if (IsConnection(inputValue)) continue;
                var kind = InferKind(classType, title, inputName);
                if (kind is null) continue;
                var descriptor = FindDescriptor(schema, inputName);
                var choices = GetChoices(descriptor);
                var valueType = InferValueType(inputValue, descriptor);
                bindings.Add(new WorkflowBinding(
                    $"{nodeId}:{inputName}", kind, nodeId, inputName,
                    FriendlyLabel(kind, title, inputName), valueType, inputValue?.DeepClone(), choices));

                if (kind == "model" && inputValue is JsonValue && choices is { Count: > 0 } && !choices.Contains(inputValue.ToString(), StringComparer.OrdinalIgnoreCase))
                    missingModels.Add(inputValue.ToString());
            }
        }

        if (profile.UiGraph?["definitions"] is not null)
            warnings.Add("Workflow содержит subgraph definitions. Они сохранены в UI JSON, но API-граф должен быть развёрнут совместимой версией ComfyUI frontend.");

        return profile with
        {
            Bindings = bindings,
            MissingNodes = missingNodes.Order().ToList(),
            MissingModels = missingModels.Order().ToList(),
            Warnings = warnings.Distinct().ToList()
        };
    }

    private static JsonObject UiToApi(JsonObject ui, JsonObject schemas, List<string> warnings)
    {
        var result = new JsonObject();
        var links = ParseLinks(ui["links"] as JsonArray);
        if (ui["nodes"] is not JsonArray nodes) throw new InvalidOperationException("UI workflow не содержит nodes.");

        var idMap = new Dictionary<string, string>();
        foreach (var item in nodes.OfType<JsonObject>())
        {
            var uiId = item["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
            var apiId = item["properties"]?["aistudio_api_id"]?.ToString() ?? uiId;
            idMap[uiId] = apiId;
        }

        foreach (var item in nodes.OfType<JsonObject>())
        {
            var uiId = item["id"]?.ToString() ?? "";
            var nodeId = idMap[uiId];
            var classType = item["type"]?.GetValue<string>() ?? throw new InvalidOperationException($"Нода {uiId} не содержит type.");
            var schema = schemas[classType] as JsonObject;
            if (schema is null) warnings.Add($"Схема ноды {classType} отсутствует; значения виджетов могут потребовать ручной привязки.");
            var inputs = new JsonObject();
            var connected = new HashSet<string>();

            if (item["inputs"] is JsonArray uiInputs)
            {
                foreach (var input in uiInputs.OfType<JsonObject>())
                {
                    var name = input["name"]?.ToString();
                    var linkId = input["link"]?.ToString();
                    if (name is null || linkId is null || !links.TryGetValue(linkId, out var link)) continue;
                    inputs[name] = new JsonArray(idMap.GetValueOrDefault(link.OriginNode, link.OriginNode), link.OriginSlot);
                    connected.Add(name);
                }
            }

            var widgetNames = InputNames(schema).Where(name => !connected.Contains(name) && IsWidgetDescriptor(FindDescriptor(schema, name))).ToArray();
            if (item["widgets_values"] is JsonArray widgetValues)
            {
                for (var i = 0; i < Math.Min(widgetNames.Length, widgetValues.Count); i++)
                {
                    var widget = widgetValues[i];
                    inputs[widgetNames[i]] = widget is JsonObject objectValue && objectValue["value"] is { } wrapped
                        ? wrapped.DeepClone()
                        : widget?.DeepClone();
                }
            }

            result[nodeId] = new JsonObject
            {
                ["class_type"] = classType,
                ["inputs"] = inputs,
                ["_meta"] = new JsonObject { ["title"] = item["title"]?.ToString() ?? classType }
            };
        }
        return result;
    }

    private static JsonObject ApiToUi(JsonObject api, JsonObject schemas)
    {
        var nodes = new JsonArray();
        var links = new JsonArray();
        var idMap = new Dictionary<string, int>();
        var nextId = 1;
        foreach (var key in api.Select(x => x.Key)) idMap[key] = int.TryParse(key, out var parsed) ? parsed : 100000 + nextId++;
        var usedLinkId = 1;
        var index = 0;

        foreach (var (apiId, value) in api)
        {
            if (value is not JsonObject apiNode) continue;
            var classType = apiNode["class_type"]?.ToString() ?? "Unknown";
            var schema = schemas[classType] as JsonObject;
            var uiInputs = new JsonArray();
            var widgets = new JsonArray();
            if (apiNode["inputs"] is JsonObject apiInputs)
            {
                foreach (var (name, inputValue) in apiInputs)
                {
                    if (IsConnection(inputValue) && inputValue is JsonArray connection)
                    {
                        var origin = connection[0]?.ToString() ?? "";
                        var slot = connection[1]?.GetValue<int>() ?? 0;
                        var linkId = usedLinkId++;
                        uiInputs.Add(new JsonObject { ["name"] = name, ["type"] = ConnectionType(FindDescriptor(schema, name)), ["link"] = linkId });
                        links.Add(new JsonArray(linkId, idMap.GetValueOrDefault(origin), slot, idMap[apiId], uiInputs.Count - 1, ConnectionType(FindDescriptor(schema, name))));
                    }
                    else
                    {
                        uiInputs.Add(new JsonObject { ["name"] = name, ["type"] = ConnectionType(FindDescriptor(schema, name)), ["link"] = null });
                        widgets.Add(inputValue?.DeepClone());
                    }
                }
            }

            var outputs = new JsonArray();
            if (schema?["output"] is JsonArray schemaOutputs)
            {
                for (var i = 0; i < schemaOutputs.Count; i++)
                    outputs.Add(new JsonObject { ["name"] = schemaOutputs[i]?.ToString() ?? $"output_{i}", ["type"] = schemaOutputs[i]?.ToString() ?? "*", ["links"] = new JsonArray() });
            }
            nodes.Add(new JsonObject
            {
                ["id"] = idMap[apiId], ["type"] = classType, ["title"] = apiNode["_meta"]?["title"]?.ToString() ?? classType,
                ["pos"] = new JsonArray(60 + (index % 4) * 300, 60 + (index / 4) * 220),
                ["size"] = new JsonArray(260, 150), ["flags"] = new JsonObject(), ["order"] = index,
                ["mode"] = 0, ["inputs"] = uiInputs, ["outputs"] = outputs, ["widgets_values"] = widgets,
                ["properties"] = new JsonObject { ["aistudio_api_id"] = apiId }
            });
            index++;
        }
        foreach (var link in links.OfType<JsonArray>())
        {
            var originId = link[1]?.GetValue<int>() ?? -1;
            var originSlot = link[2]?.GetValue<int>() ?? -1;
            var originNode = nodes.OfType<JsonObject>().FirstOrDefault(node => node["id"]?.GetValue<int>() == originId);
            var output = originNode?["outputs"] is JsonArray nodeOutputs && originSlot >= 0 && originSlot < nodeOutputs.Count
                ? nodeOutputs[originSlot] as JsonObject
                : null;
            if (output?["links"] is JsonArray outputLinks) outputLinks.Add(link[0]?.DeepClone());
        }
        return new JsonObject { ["last_node_id"] = idMap.Values.DefaultIfEmpty().Max(), ["last_link_id"] = usedLinkId - 1, ["nodes"] = nodes, ["links"] = links, ["groups"] = new JsonArray(), ["config"] = new JsonObject(), ["version"] = 0.4 };
    }

    private static Dictionary<string, (string OriginNode, int OriginSlot)> ParseLinks(JsonArray? links)
    {
        var result = new Dictionary<string, (string, int)>();
        if (links is null) return result;
        foreach (var item in links)
        {
            if (item is JsonArray array && array.Count >= 3)
                result[array[0]!.ToString()] = (array[1]!.ToString(), array[2]!.GetValue<int>());
            else if (item is JsonObject obj && obj["id"] is not null)
                result[obj["id"]!.ToString()] = (obj["origin_id"]!.ToString(), obj["origin_slot"]?.GetValue<int>() ?? 0);
        }
        return result;
    }

    private static IEnumerable<string> InputNames(JsonObject? schema)
    {
        foreach (var group in new[] { "required", "optional" })
            if (schema?["input"]?[group] is JsonObject inputs)
                foreach (var name in inputs.Select(x => x.Key)) yield return name;
    }

    private static JsonNode? FindDescriptor(JsonObject? schema, string name) =>
        schema?["input"]?["required"]?[name] ?? schema?["input"]?["optional"]?[name];

    private static bool IsWidgetDescriptor(JsonNode? descriptor)
    {
        if (descriptor is not JsonArray array || array.Count == 0) return true;
        if (array[0] is JsonArray) return true;
        return array[0]?.ToString().ToUpperInvariant() is "STRING" or "INT" or "FLOAT" or "BOOLEAN" or "COMBO";
    }

    private static IReadOnlyList<string>? GetChoices(JsonNode? descriptor) =>
        descriptor is JsonArray { Count: > 0 } array && array[0] is JsonArray choices
            ? choices.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToArray()
            : null;

    private static bool IsConnection(JsonNode? value) => value is JsonArray array && array.Count == 2 && array[1] is JsonValue;

    private static string ConnectionType(JsonNode? descriptor)
    {
        if (descriptor is not JsonArray array || array.Count == 0) return "*";
        return array[0] is JsonArray ? "COMBO" : array[0]?.ToString() ?? "*";
    }

    private static string? InferKind(string classType, string title, string input)
    {
        var name = input.ToLowerInvariant();
        var node = (classType + " " + title).ToLowerInvariant();
        if (name is "seed" or "noise_seed") return "seed";
        if (name == "steps") return "steps";
        if (name is "cfg" or "guidance") return "guidance";
        if (name == "width") return "width";
        if (name == "height") return "height";
        if (name.Contains("fps")) return "fps";
        if (name.Contains("frame") && !name.Contains("frame_rate")) return "frames";
        if (name == "denoise") return "denoise";
        if (name == "batch_size") return "batch";
        if (name.Contains("lora") && name.Contains("name")) return "lora";
        if ((name.Contains("ckpt") || name.Contains("model") || name.Contains("vae") || name.Contains("clip")) && name.EndsWith("name")) return "model";
        if (name is "image" or "video" or "audio" && !IsLikelyOutputName(name)) return "reference";
        if (name is "text" or "prompt" or "positive" or "negative")
            return node.Contains("negative") || name == "negative" ? "negativePrompt" : "prompt";
        return null;
    }

    private static bool IsLikelyOutputName(string name) => name.StartsWith("save", StringComparison.OrdinalIgnoreCase);
    private static string FriendlyLabel(string kind, string title, string input) => kind switch
    {
        "prompt" => title.Contains("positive", StringComparison.OrdinalIgnoreCase) ? "Положительный промт" : $"Промт — {title}",
        "negativePrompt" => "Негативный промт",
        "seed" => "Seed", "steps" => "Шаги", "guidance" => "Guidance / CFG", "width" => "Ширина", "height" => "Высота",
        "fps" => "FPS", "frames" => "Кадры", "denoise" => "Denoise", "batch" => "Batch size", "model" => $"Модель — {title}",
        "lora" => $"LoRA — {title}", "reference" => $"Референс — {title}", _ => input
    };

    private static string InferValueType(JsonNode? value, JsonNode? descriptor)
    {
        if (GetChoices(descriptor) is not null) return "choice";
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out _)) return "boolean";
            if (jsonValue.TryGetValue<long>(out _)) return "integer";
            if (jsonValue.TryGetValue<double>(out _)) return "number";
        }
        return "string";
    }

    private async Task SaveAsync(WorkflowProfile profile, CancellationToken cancellationToken)
    {
        var path = ProfilePath(profile.Id);
        if (File.Exists(path))
        {
            var previous = await _files.ReadAsync<WorkflowProfile>(path, cancellationToken);
            if (previous is not null && previous.Revision != profile.Revision)
            {
                var history = Path.Combine(_paths.Workflows, "history", profile.Id);
                Directory.CreateDirectory(history);
                var backup = Path.Combine(history, $"revision-{previous.Revision}.json");
                if (!File.Exists(backup)) await _files.WriteAsync(backup, previous, cancellationToken);
            }
        }
        await _files.WriteAsync(path, profile, cancellationToken);
    }
    private string ProfilePath(string id) => Path.Combine(_paths.Workflows, Path.GetFileName(id) + ".json");
}

public sealed record GraphInputOperation(string NodeId, string InputName, JsonNode? Value);
