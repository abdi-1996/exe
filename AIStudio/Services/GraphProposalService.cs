using System.Text.Json.Nodes;
using AIStudio.Core;

namespace AIStudio.Services;

public sealed record GraphProposal(
    string Id,
    string WorkflowId,
    long WorkflowRevision,
    string Summary,
    List<GraphInputOperation> Operations,
    DateTimeOffset CreatedAt);

public sealed class GraphProposalService
{
    private readonly WorkflowService _workflows;
    private readonly ComfyClient _comfy;
    private readonly AiProviderService _ai;
    private readonly JsonFileStore _files;
    private readonly string _directory;

    public GraphProposalService(WorkflowService workflows, ComfyClient comfy, AiProviderService ai, JsonFileStore files, AppPaths paths)
    {
        _workflows = workflows;
        _comfy = comfy;
        _ai = ai;
        _files = files;
        _directory = Path.Combine(paths.Settings, "proposals");
        Directory.CreateDirectory(_directory);
    }

    public async Task<GraphProposal> PrepareAsync(string workflowId, string instruction, CancellationToken cancellationToken = default)
    {
        var workflow = await _workflows.GetAsync(workflowId, cancellationToken) ?? throw new FileNotFoundException("Workflow не найден.");
        var schemas = (await _comfy.GetObjectInfoAsync(cancellationToken))?.AsObject() ?? throw new InvalidOperationException("Не удалось получить схемы нод ComfyUI.");
        var usedSchemas = new JsonObject();
        foreach (var node in workflow.ApiGraph.Select(x => x.Value).OfType<JsonObject>())
        {
            var type = node["class_type"]?.ToString();
            if (type is not null && schemas[type] is not null) usedSchemas[type] = schemas[type]!.DeepClone();
        }
        var system = "Ты изменяешь граф ComfyUI только через существующие скалярные входы. " +
                     "Верни только JSON: {\"summary\":\"...\",\"operations\":[{\"nodeId\":\"1\",\"inputName\":\"steps\",\"value\":30}]}. " +
                     "Не добавляй ноды, которых нет в переданных схемах, не меняй соединения и не предлагай shell-команды.";
        var context = $"Запрос пользователя:\n{instruction}\n\nТекущий API workflow:\n{workflow.ApiGraph.ToJsonString()}\n\nСхемы используемых нод:\n{usedSchemas.ToJsonString()}";
        var response = await _ai.ChatAsync(new AiChatRequest(context, false, null, system), cancellationToken);
        var parsed = ParseJson(response.Text);
        var summary = parsed["summary"]?.ToString() ?? "Изменения workflow";
        var operations = new List<GraphInputOperation>();
        foreach (var operation in parsed["operations"]?.AsArray() ?? [])
        {
            if (operation is not JsonObject item) continue;
            operations.Add(new GraphInputOperation(item["nodeId"]?.ToString() ?? "", item["inputName"]?.ToString() ?? "", item["value"]?.DeepClone()));
        }
        if (operations.Count == 0) throw new InvalidOperationException("Ассистент не предложил применимых операций.");
        Validate(workflow, schemas, operations);
        var proposal = new GraphProposal(Guid.NewGuid().ToString("N"), workflow.Id, workflow.Revision, summary, operations, DateTimeOffset.UtcNow);
        await _files.WriteAsync(Path.Combine(_directory, proposal.Id + ".json"), proposal, cancellationToken);
        return proposal;
    }

    public async Task<WorkflowProfile> ApplyAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = await _files.ReadAsync<GraphProposal>(Path.Combine(_directory, Path.GetFileName(proposalId) + ".json"), cancellationToken)
            ?? throw new FileNotFoundException("Предложение не найдено.");
        return await _workflows.ApplyOperationsAsync(proposal.WorkflowId, proposal.WorkflowRevision, proposal.Operations, cancellationToken);
    }

    private static void Validate(WorkflowProfile workflow, JsonObject schemas, IEnumerable<GraphInputOperation> operations)
    {
        foreach (var operation in operations)
        {
            if (workflow.ApiGraph[operation.NodeId] is not JsonObject node) throw new InvalidOperationException($"Ассистент сослался на отсутствующую ноду {operation.NodeId}.");
            var inputs = node["inputs"] as JsonObject;
            if (inputs is null || !inputs.ContainsKey(operation.InputName)) throw new InvalidOperationException($"Ассистент сослался на отсутствующий вход {operation.NodeId}.{operation.InputName}.");
            if (inputs[operation.InputName] is JsonArray array && array.Count == 2) throw new InvalidOperationException("Предложение пытается заменить соединение. Это запрещено без отдельного редактора связей.");
            var classType = node["class_type"]?.ToString() ?? "";
            if (schemas[classType] is null) throw new InvalidOperationException($"Класс ноды {classType} не установлен.");
        }
    }

    private static JsonObject ParseJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine) trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
        }
        return JsonNode.Parse(trimmed)?.AsObject() ?? throw new InvalidOperationException("Ассистент вернул некорректный JSON предложения.");
    }
}
