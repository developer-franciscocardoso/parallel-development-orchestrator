using System.Text.RegularExpressions;
using Pda.Core;
using Pda.Repositories;

namespace Pda.Verification;

public sealed class MechanicalReviewer(SpecialistReviews? specialists = null) : ICandidateReviewer
{
    private const string Identity = "pda.mechanical-review.v1";
    private static readonly string[] Protected =
    [
        ".git", ".codex", ".agents", ".execution", ".github/workflows",
        ".github/instructions", ".cursor/rules", ".gitmodules", ".gitattributes"
    ];

    public Task<ReviewReport> ReviewAsync(VerificationInput input,
        CancellationToken token)
    {
        var findings = new List<ReviewFinding>();
        try
        {
            token.ThrowIfCancellationRequested();
            var changes = Checkout.Changes(input);
            var snapshot = Checkout.Fresh(input);
            var files = snapshot.Files.ToDictionary(f => f.Path);
            long bytes = 0;
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                var paths = new[] {change.OldPath, change.NewPath};
                foreach (var path in paths.OfType<string>().Distinct())
                    CheckPath(input.Contract, path, findings);
                if (change.NewPath == null) continue;
                if (!files.TryGetValue(change.NewPath, out var file) ||
                    file.Bytes > 262144 || (bytes += file.Bytes) > 4 * 1024 * 1024)
                {
                    findings.Add(new("blocking", change.NewPath,
                        "Changed content exceeds mechanical review bounds.",
                        "Obtain independent specialist review."));
                    break;
                }
                InspectText(file.Path, snapshot.ReadText(file), findings);
                if (findings.Count >= 64) break;
            }
            if (NeedsSpecialist(input.Contract) && specialists != null &&
                findings.Count == 0)
            {
                var decision = specialists.Read(input.Binding);
                Checkout.Verify(input);
                return Task.FromResult(decision);
            }
            if (NeedsSpecialist(input.Contract))
                findings.Add(new("blocking", input.Contract.Id,
                    "This contract requires independent specialist judgement.",
                    "Record a candidate-bound reviewer decision."));
            Checkout.Verify(input);
        }
        catch (Exception e) when (e is GitRepositoryException or
            ArgumentException or IOException or InvalidDataException or
            UnauthorizedAccessException)
        {
            findings.Add(new("blocking", input.Contract.Id,
                "Candidate evidence could not be verified.", e.Message));
        }
        return Task.FromResult(new ReviewReport(input.Binding,
            findings.Count == 0, Identity, findings.AsReadOnly()));
    }

    private static void CheckPath(TaskContract task, string path,
        List<ReviewFinding> findings)
    {
        ContractValidator.ValidatePath(path);
        string name = Path.GetFileName(path).ToLowerInvariant();
        var protectedPath = name is "agents.md" or "agents.override.md"
            or "claude.md" or "copilot-instructions.md" ||
            Protected.Concat(task.ReadOnly).Concat(task.Excluded).Any(p =>
                ContractValidator.ContainsPath(p, path));
        if (protectedPath || !task.Owned.Any(p =>
            ContractValidator.ContainsPath(p, path)))
            findings.Add(new("blocking", path,
                "A changed path exceeds the task's writable scope.",
                "Compare both sides of the commit diff to the task contract."));
    }

    private static bool NeedsSpecialist(TaskContract task) =>
        task.Risk == Risk.High || task.Criteria.Any(s => Regex.IsMatch(s,
            @"\b(human|manual|specialist|semantic|security|architecture)" +
            @"[ -]+(code[ -]+)?(review|approval|sign[ -]?off)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static void InspectText(string path, string text,
        List<ReviewFinding> findings)
    {
        if (text.Contains('\0') || text.StartsWith(
            "version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
            findings.Add(new("blocking", path, "Content needs binary or LFS review.",
                "Inspect the actual candidate artifact in a specialist review."));
        if (text.Split('\n').Any(line =>
            line.StartsWith("<<<<<<< ", StringComparison.Ordinal) ||
            line.StartsWith(">>>>>>> ", StringComparison.Ordinal)))
            findings.Add(new("blocking", path, "Unresolved merge marker found.",
                "Resolve the conflict and inspect the replacement diff."));
        if (Regex.IsMatch(text, @"-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----",
            RegexOptions.CultureInvariant))
            findings.Add(new("critical", path, "Private key material found.",
                "Remove it and verify that no credential was committed."));
    }
}
