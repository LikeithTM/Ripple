using System.Text.Json;

namespace Ripple.Core.Services;

/// <summary>
/// Parses JSONL trajectory lines emitted by Antigravity (<c>transcript.jsonl</c>) into
/// typed <see cref="ApiAgentEventRequest"/> updates for the Notchy module and Live Activity bus.
/// </summary>
public static class AntigravityTranscriptParser
{
    private const string AgentName = "Antigravity";
    private const int MaxPreviewLength = 64;

    public static bool TryParseStepLine(string jsonLine, out ApiAgentEventRequest? evt, out int stepIndex)
    {
        evt = null;
        stepIndex = -1;

        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("step_index", out var stepProp) && stepProp.TryGetInt32(out var idx))
            {
                stepIndex = idx;
            }

            var source = GetStringProp(root, "source");
            var type = GetStringProp(root, "type");
            var status = GetStringProp(root, "status");
            var content = GetStringProp(root, "content");

            if (string.Equals(source, "USER_EXPLICIT", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(type, "USER_INPUT", StringComparison.OrdinalIgnoreCase))
            {
                var detail = ExtractUserRequestText(content);
                evt = new ApiAgentEventRequest(
                    AgentName,
                    "running",
                    "Antigravity • Thinking…",
                    detail,
                    null,
                    null);
                return true;
            }

            if (string.Equals(source, "MODEL", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(type, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase))
            {
                int? outputTokens = root.TryGetProperty("output_tokens", out var tokProp) && tokProp.TryGetInt32(out var tokVal)
                    ? tokVal
                    : null;

                if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    evt = new ApiAgentEventRequest(
                        AgentName,
                        "error",
                        "Antigravity • Step Error",
                        CleanPreview(content, MaxPreviewLength) ?? "Error reported",
                        null,
                        outputTokens);
                    return true;
                }

                if (root.TryGetProperty("tool_calls", out var toolCalls) &&
                    toolCalls.ValueKind == JsonValueKind.Array &&
                    toolCalls.GetArrayLength() > 0)
                {
                    var lastCall = toolCalls[toolCalls.GetArrayLength() - 1];
                    var toolName = GetStringProp(lastCall, "name") ?? "tool";
                    lastCall.TryGetProperty("args", out var args);

                    var (evtStatus, title, detail) = MapToolCall(toolName, args);
                    evt = new ApiAgentEventRequest(
                        AgentName,
                        evtStatus,
                        title,
                        detail,
                        null,
                        outputTokens);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(content))
                {
                    evt = new ApiAgentEventRequest(
                        AgentName,
                        "completed",
                        "Antigravity • Turn Complete",
                        CleanPreview(content, MaxPreviewLength),
                        null,
                        outputTokens);
                    return true;
                }

                var thinking = GetStringProp(root, "thinking");
                if (!string.IsNullOrWhiteSpace(thinking) ||
                    string.Equals(status, "RUNNING", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
                {
                    evt = new ApiAgentEventRequest(
                        AgentName,
                        "running",
                        "Antigravity • Thinking…",
                        CleanPreview(thinking, MaxPreviewLength) ?? "Reasoning…",
                        null,
                        outputTokens);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore malformed or truncated JSONL lines safely.
        }

        return false;
    }

    private static (string Status, string Title, string Detail) MapToolCall(string toolName, JsonElement args) =>
        toolName switch
        {
            "run_command" => (
                "running",
                "Antigravity • Running Command",
                ExtractArg(args, "toolSummary") ?? ExtractArg(args, "CommandLine") ?? "Executing shell command"),

            "edit_file" or "write_file" => (
                "running",
                "Antigravity • Editing File",
                ExtractFileName(ReadRawArg(args, "Path")) ?? "Updating workspace file"),

            "read_file" => (
                "running",
                "Antigravity • Reading File",
                ExtractFileName(ReadRawArg(args, "Path")) ?? "Inspecting file"),

            "exit_plan_mode" => (
                "running",
                "Antigravity • Executing Plan",
                "Handed plan to execution model"),

            "enter_plan_mode" => (
                "running",
                "Antigravity • Reviewing Plan",
                "Planner review in progress"),

            "ask_question" => (
                "permission",
                "Antigravity • Waiting for Input",
                "Clarification question pending"),

            "invoke_subagent" => (
                "running",
                "Antigravity • Subagent Active",
                ExtractArg(args, "Role") ?? ExtractArg(args, "TypeName") ?? "Subagent running"),

            "search_web" => (
                "running",
                "Antigravity • Web Search",
                ExtractArg(args, "query") ?? "Searching web"),

            _ => (
                "running",
                $"Antigravity • {toolName}",
                ExtractArg(args, "toolSummary") ?? "Running tool")
        };

    private static string? GetStringProp(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(key, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? ReadRawArg(JsonElement args, string key)
    {
        var raw = GetStringProp(args, key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1].Trim();
        }
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? ExtractArg(JsonElement args, string key) =>
        CleanPreview(ReadRawArg(args, key), MaxPreviewLength);

    private static string? ExtractFileName(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        try
        {
            var name = Path.GetFileName(rawPath);
            return CleanPreview(string.IsNullOrWhiteSpace(name) ? rawPath : name, MaxPreviewLength);
        }
        catch
        {
            return CleanPreview(rawPath, MaxPreviewLength);
        }
    }

    private static string ExtractUserRequestText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "New task";

        const string openTag = "<USER_REQUEST>";
        const string closeTag = "</USER_REQUEST>";
        var start = content.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += openTag.Length;
            var end = content.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
            var slice = end > start ? content[start..end] : content[start..];
            var inner = CleanPreview(slice, MaxPreviewLength);
            if (!string.IsNullOrWhiteSpace(inner))
            {
                return inner;
            }
        }

        return CleanPreview(content, MaxPreviewLength) ?? "New task";
    }

    private static string? CleanPreview(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrEmpty(collapsed)) return null;

        return collapsed.Length <= maxLength
            ? collapsed
            : collapsed[..(maxLength - 1)].TrimEnd() + "…";
    }
}
