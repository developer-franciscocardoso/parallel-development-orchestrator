using System.Security.Cryptography;
using System.Text.Json;

namespace Pda.Verification.Tests;

public sealed class SpecialistReviewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory().FullName;
    private readonly CandidateBinding binding =
        new(new string('a', 40), new string('b', 40), new string('c', 64));
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecisionsRequireTrustedSignatureAndExactBinding(bool tamper)
    {
        var reviews = new SpecialistReviews(folder);
        Assert.Throws<ReviewPendingException>(() => reviews.Read(binding));
        foreach (var area in new[] {"architecture", "correctness", "security",
            "performance", "maintainability"})
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(Path.Combine(folder, area + ".pem"),
                key.ExportSubjectPublicKeyInfoPem());
            var decision = new SpecialistDecision(binding, area, "Reviewer",
                "Inspected change and tests in isolated checkout.", true, []);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(decision, json);
            var signature = key.SignData(bytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            if (tamper && area == "security")
                bytes = JsonSerializer.SerializeToUtf8Bytes(
                    decision with { Reviewer = "Someone else" }, json);
            var envelope = new SignedDecision(Convert.ToBase64String(bytes),
                Convert.ToBase64String(signature));
            var name = $"{binding.CandidateCommit}.{binding.ContractDigest}.{area}.json";
            File.WriteAllText(Path.Combine(folder, name),
                JsonSerializer.Serialize(envelope, json));
        }
        if (tamper)
            Assert.Throws<InvalidDataException>(() => reviews.Read(binding));
        else
            Assert.True(reviews.Read(binding).Approved);
        Assert.Throws<ReviewPendingException>(() =>
            reviews.Read(binding with { CandidateCommit = new string('d', 40) }));
    }

    public void Dispose() => Directory.Delete(folder, true);
}
