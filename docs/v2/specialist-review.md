# Independent specialist decisions

The default reviewer checks the committed diff mechanically. To require
authenticated specialist decisions for high-risk tasks or an explicit
specialist approval criterion, create `specialist-reviews` under the
trusted state home before opening the host. The folder is outside every
managed checkout. Install administrator-approved ECDSA public keys named
`architecture.pem`, `correctness.pem`, `security.pem`, `performance.pem`
and `maintainability.pem`. Each file is a PEM SubjectPublicKeyInfo. Keep
the private signing keys with their respective reviewers.

A worker cannot approve a candidate. A specialist receives the actual
candidate commit, base commit, task contract and independent command
evidence. The check artifact contains the exact `CandidateBinding`.
The journal records missing decisions under `review-pending`. Operators
can retrieve the evidence from the authenticated plan export.

The signing input is the UTF-8 encoding of a JSON object with these fields:

```json
{
  "binding": {
    "baseCommit": "<full base SHA>",
    "candidateCommit": "<full candidate SHA>",
    "contractDigest": "<exact digest from the check>"
  },
  "specialty": "architecture",
  "reviewer": "<independent reviewer identity>",
  "verification": "<what was examined and how it was verified>",
  "approved": true,
  "findings": []
}
```

A finding carries `severity`, `location`, `impact` and `verification`.
Only informational and warning findings allow approval. Sign the exact
payload bytes with ECDSA/SHA-256 and DER signature encoding, then save
a JSON envelope with base64 `payload` and `signature` strings under
`<candidateCommit>.<contractDigest>.<specialty>.json`. Resume the plan
with `pda plan execute PLAN` after the decisions are available.

Missing decisions leave the attempt awaiting verification when the
specialist directory is configured. A rejected, altered or mismatched
decision blocks the attempt. Mechanical scope and integrity checks still
apply before specialist review. Changing the candidate or contract
requires fresh decisions.
