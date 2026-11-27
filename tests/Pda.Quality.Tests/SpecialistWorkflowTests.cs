using System.Security.Cryptography;
using System.Text.Json;
using Pda.Core;
using Pda.Persistence;
using Pda.Repositories;
using Pda.Verification;

namespace Pda.Quality.Tests;

public sealed class SpecialistWorkflowTests
{
    [Fact]
    public async Task PendingAttemptResumesWithSignedCandidateDecisions()
    {
        using var fixture = new QualityTests(Risk.High, "src/feature");
        await fixture.CheckSignedApproval();
    }

    [Fact]
    public async Task SignedSpecialistApprovalsCannotOverrideScopeViolation()
    {
        using var fixture = new QualityTests(Risk.High, "outside.txt");
        await fixture.CheckSignedScopeViolation();
    }
}

public sealed partial class QualityTests
{
    internal async Task CheckSignedApproval()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "specialists")).FullName;
        var binding = CandidateBinding.For(plan.Baseline.Commit, candidate.Commit, contract);
        var waiting = await RunSpecialist(folder);
        Assert.Equal(attempt.Id, waiting.Id);
        Assert.Equal(AttemptState.AwaitingVerification, waiting.State);
        Assert.Single(store.Artifacts<string>("plan", "review-pending"));
        Assert.Empty(store.Artifacts<ReviewReport>("plan", "review"));
        Assert.Empty(store.Artifacts<IntegratedChange>("plan", "integration"));
        Assert.Equal(plan.Baseline.Commit, git.Register(plan.Path).Commit);

        var checkedBefore = Assert.Single(store.Artifacts<CheckReport>("plan", "check"));
        Assert.Equal(binding, checkedBefore.Data.Binding);
        SignApprovals(folder, binding);
        var integrated = await RunSpecialist(folder);
        Assert.Equal(waiting.Id, integrated.Id);
        Assert.Equal(AttemptState.Integrated, integrated.State);
        Assert.Single(store.Attempts("plan"));
        var reviewed = Assert.Single(store.Artifacts<ReviewReport>("plan", "review")).Data;
        Assert.True(reviewed.Approved);
        Assert.Equal(binding, reviewed.Binding);
        var checkedAfter = Assert.Single(store.Artifacts<CheckReport>("plan", "check"));
        Assert.Equal(checkedBefore.Version, checkedAfter.Version);
        Assert.Equal(checkedBefore.Data.Checks[0].OutputLog,
            checkedAfter.Data.Checks[0].OutputLog);
        var change = Assert.Single(store.Artifacts<IntegratedChange>("plan", "integration")).Data;
        Assert.Equal(candidate.Commit, change.CandidateCommit);
        Assert.Equal(change.Commit, git.Register(plan.Path).Commit);
        var scratch = store.Artifacts<JsonElement>("plan", "quality-worktree")
            .Select(a => a.Data.GetProperty("scratch").GetProperty("Path").GetString())
            .ToArray();
        Assert.Equal(4, scratch.Distinct().Count());
        Assert.DoesNotContain(candidate.Attempt.Path, scratch);
    }

    internal async Task CheckSignedScopeViolation()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "specialists")).FullName;
        var binding = CandidateBinding.For(plan.Baseline.Commit, candidate.Commit, contract);
        SignApprovals(folder, binding);
        Assert.True(new SpecialistReviews(folder).Read(binding).Approved);
        Assert.Equal(AttemptState.Blocked, (await RunSpecialist(folder)).State);
        Assert.Equal(AttemptState.Blocked, (await RunSpecialist(folder)).State);
        Assert.Empty(store.Artifacts<IntegratedChange>("plan", "integration"));
        Assert.Equal(plan.Baseline.Commit, git.Register(plan.Path).Commit);
    }

    private Task<Attempt> RunSpecialist(string folder)
    {
        var db = new StateStore(Path.Combine(root, "state.db"));
        var ops = new GitQualityOperations(git.CaptureCandidate, git.PrepareMerge,
            git.AdvancePlan);
        var reviewer = new MechanicalReviewer(new SpecialistReviews(folder));
        return new QualityCoordinator(db, ops, this, reviewer, evidence)
            .ProcessAsync("plan", attempt.Id);
    }

    private static void SignApprovals(string folder, CandidateBinding binding)
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        foreach (var area in new[] {"architecture", "correctness", "security",
            "performance", "maintainability"})
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(Path.Combine(folder, area + ".pem"),
                key.ExportSubjectPublicKeyInfoPem());
            var decision = new SpecialistDecision(binding, area, "Fixture " + area,
                "Inspected the pinned fixture candidate.", true, []);
            var payload = JsonSerializer.SerializeToUtf8Bytes(decision, json);
            var signature = key.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            var envelope = new SignedDecision(Convert.ToBase64String(payload),
                Convert.ToBase64String(signature));
            var name = $"{binding.CandidateCommit}.{binding.ContractDigest}.{area}.json";
            File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(envelope, json));
        }
    }
}
