# P04: repository discovery and executable planning

Prerequisite: accepted runtime contracts and security gate. Risk medium,
maximum attempts 2, timeout 3600 seconds. Own Planning and its tests;
coordinate any Core/Persistence additions for project profile storage.

Inspect actual registered Git top-level and bounded tracked file inventory.
Discover languages, manifests, framework/build/test evidence, databases,
instructions, workflows and documentation. Skip links and generated/vendor
trees; bound input file sizes/counts and JSON depth. Preserve all source
instructions. Unknown languages remain supported as files with explicitly
supplied verification; never invent a build system from a file extension.
Persist project knowledge with source paths, hashes and inspection time.

Accept development objectives and explicit task-contract plans. Transform
objectives into validated executable DAGs, conservatively retaining coupled
work in one task. Automatic splitting may use explicit structured sections
and ownership, but must not invent independent boundaries. Where metadata
provides no reliable verification command, report the missing requirement
instead of substituting a no-op command. Do not execute repository commands
during discovery. Argument-vector checks must be approved by trusted policy
before execution. Review task contracts against Core limits and exclusions.

Tests: mixed language repositories, malformed/large manifests, path escapes,
unsupported frameworks with explicit checks, preserved instructions,
duplicate/missing/cyclic dependencies, ordered ownership, and repeatable
inspection. Document the exact supported automatic decomposition behavior.
Do not claim general semantic understanding from filename heuristics.
