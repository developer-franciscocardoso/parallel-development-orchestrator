using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pda.Security;

namespace Pda.Verification;

public sealed record SpecialistDecision(CandidateBinding Binding,
    string Specialty, string Reviewer, string Verification, bool Approved,
    ReviewFinding[] Findings);

public sealed record SignedDecision(string Payload, string Signature);

public sealed class ReviewPendingException(string message) : Exception(message);

public sealed class SpecialistReviews(string root)
{
    private static readonly string[] Disciplines =
        ["architecture", "correctness", "security", "performance", "maintainability"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // Keys are installed by an administrator outside all task worktrees.
    public ReviewReport Read(CandidateBinding binding)
    {
        var directory = TrustedPaths.Directory(root);
        TrustedPaths.OutsideRepository(directory);
        if (binding.CandidateCommit.Length is not (40 or 64) ||
            binding.ContractDigest.Length != 64 ||
            !(binding.CandidateCommit + binding.ContractDigest).All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid specialist review binding.");
        var findings = new List<ReviewFinding>();
        var reviewers = new List<string>();
        foreach (var discipline in Disciplines)
        {
            var key = Path.Combine(directory, discipline + ".pem");
            var name = string.Join(".", binding.CandidateCommit,
                binding.ContractDigest, discipline, "json");
            var file = Path.Combine(directory, name);
            if (!File.Exists(file) || !File.Exists(key))
                throw new ReviewPendingException("Waiting for signed " +
                    discipline + " review: " + name);
            var envelope = JsonSerializer.Deserialize<SignedDecision>(ReadFile(file), Json)
                ?? throw new InvalidDataException("Missing signed review.");
            var payload = Convert.FromBase64String(envelope.Payload);
            using var verifier = ECDsa.Create();
            var pem = System.Text.Encoding.UTF8.GetString(ReadFile(key));
            if (!pem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
                throw new InvalidDataException("A public review key is required.");
            verifier.ImportFromPem(pem);
            if (!verifier.VerifyData(payload, Convert.FromBase64String(envelope.Signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new InvalidDataException("Invalid specialist signature.");
            var review = JsonSerializer.Deserialize<SpecialistDecision>(payload, Json)
                ?? throw new InvalidDataException("Missing specialist decision.");
            if (review.Binding != binding || review.Specialty != discipline ||
                string.IsNullOrWhiteSpace(review.Reviewer) ||
                string.IsNullOrWhiteSpace(review.Verification) || review.Findings == null)
                throw new InvalidDataException("Specialist evidence mismatch.");
            reviewers.Add(discipline + "/" + review.Reviewer);
            findings.Add(new("info", discipline, review.Reviewer, review.Verification));
            findings.AddRange(review.Findings);
            if (!review.Approved)
                findings.Add(new("blocking", discipline, "Specialist rejected candidate.",
                    review.Verification));
        }
        var approved = findings.All(f => f.Severity is "info" or "warning");
        return new(binding, approved, string.Join(", ", reviewers), findings);
    }

    private static byte[] ReadFile(string path)
    {
        using var stream = File.OpenRead(TrustedPaths.File(path));
        if (stream.Length is <= 0 or > 262144)
            throw new InvalidDataException("Specialist review exceeds size bound.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
