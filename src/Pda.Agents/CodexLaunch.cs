using System.Text.Json;
using Pda.Execution;
using Pda.Security;

namespace Pda.Agents;

public static class CodexLaunch
{
    public static IReadOnlyList<string> Arguments(AgentRequest request,
        IReadOnlyList<string> options)
    {
        List<string> args = [..options, "--no-daemon",
            "exec", "--strict-config", "--ignore-user-config", "--ignore-rules",
            "--ephemeral", "--json", "--cd", request.Worktree, "--output-schema",
            WorkerReport.SchemaPath, "--output-last-message", request.ReportPath];
        if (request.Model != null)
        {
            if (string.IsNullOrWhiteSpace(request.Model)
                || request.Model.StartsWith('-') || request.Model.Any(char.IsControl))
                throw new ArgumentException("Invalid runtime model.");
            args.Add("--model");
            args.Add(request.Model);
        }
        args.Add("-");
        return args.AsReadOnly();
    }

    internal static ProcessRequest Process(CodexProfile profile,
        AgentRequest request, IReadOnlyDictionary<string, string> environment)
    {
        string prompt = JsonSerializer.Serialize(new {
            task_id = request.TaskId, attempt_id = request.AttemptId,
            objective = request.Prompt
        });
        return new(profile.Executable, Arguments(request,
            CodexPolicy.WorkerOptions(profile, request.Worktree)),
            request.Worktree,
            request.OutputLog, request.ErrorLog, request.Timeout, prompt,
            Environment: environment);
    }
}
