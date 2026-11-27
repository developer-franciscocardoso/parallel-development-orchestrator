using Pda.Core;

namespace Pda.Planning;

public sealed record SourceEvidence(string Path, string Kind, string BlobId,
    long Bytes, string? Content = null);

public sealed record ProjectCheck(string Source, string Scope,
    VerificationCommand Command);

public sealed record ProjectProfile(string Root, string GitCommonDirectory,
    string Commit, DateTimeOffset InspectedUtc, string[] Languages,
    string[] Frameworks, string[] BuildSystems, string[] Databases,
    SourceEvidence[] Evidence, ProjectCheck[] Verification, string[] Warnings) : IProjectProfile;
