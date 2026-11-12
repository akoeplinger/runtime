# WIP: Mono browser/WASI CI coverage handoff

**Start with [coverage-decisions.md](coverage-decisions.md).** It documents 24 pending
coverage decisions, evidence, options and acceptance criteria.

This branch preserves an exploratory removal-only patch. It is **not an approved
coverage migration or a PR-ready change**. The user wants to review the gaps and
decide each case before changing the implementation. They specifically said:

> WasmBuildTests is an important use case

When offered implementation choices, they selected:

> Review the document before making implementation decisions

## Resume locally

Fork: `https://github.com/akoeplinger/runtime`.
Branch: `wip/mono-wasm-ci-coverage-handoff`.
For an existing local runtime checkout, fetch the branch, then create a separate worktree:

```sh
git fetch https://github.com/akoeplinger/runtime.git \
  refs/heads/wip/mono-wasm-ci-coverage-handoff:refs/remotes/akoeplinger/wip-mono-wasm-ci-coverage-handoff
git worktree add ../runtime-mono-wasm-ci-handoff \
  -b mono-wasm-ci-handoff refs/remotes/akoeplinger/wip-mono-wasm-ci-coverage-handoff
```

These are suggested local commands, not commands run on the user's machine.
Choose different local names if those refs or paths already exist.
Alternatively clone the fork with that branch checked out. The WIP already contains
the patch; do not apply a second copy.

Suggested prompt for the next local Copilot session:

> Continue from docs/mono-wasm-ci-handoff/README.md and coverage-decisions.md.
> This is a removal-only experiment, not an approved migration. Review the 24 pending
> coverage decisions with me, prioritizing Wasm.Build.Tests. Do not silently accept
> coverage loss or implement recommendations before we decide them.

## State and base

The original task was to explore removing Mono-based browser/WASI test runs on top of
the non-Wasm Mono CI retirement in [dotnet/runtime#137421](https://github.com/dotnet/runtime/pull/137421).
The exploration subsequently paused for the coverage review described above.

The WIP commit is based on `4ab4fb694b9aaf1c3b26a89d236aa75af7d0a2be`, after the parent
change merged as `88ad06a0`. The earlier source references in the decision document are
pinned to the former parent head, `7d68ded739b86cbaf01d8d06b7969aad3f9496f4`; it is not
an ancestor of this base after the parent change was merged.

Compared those two bases across the pipeline YAML, WBT suite, library selection/Helix
targets, relevant runtime-test configuration and Wasm test targets. The sole changed
file in those areas was `ReadyToRunTests.cs`: it strengthened an existing CoreCLR R2R
unnormalized-path test with a doubled slash and an explicit IntermediateLinkDir.
It does not change the documented Mono category inventory or removed job definitions.
Read newer main changes before continuing there; source-level parity is not execution proof.

The original app's Changes view used a stale/cached base and could show already-merged
changes. Compare against `4ab4fb694b9aaf1c3b26a89d236aa75af7d0a2be` to see only this WIP.
No shared main-checkout branch refs were changed as a UI workaround.

## What is in this WIP

The removal-only patch changes 13 pipeline YAML files, with 6 insertions and 809 deletions:

- Removes Mono browser/WASI library, threading, EAT/aggressive-trimming, AOT,
  high-resource, browser runtime, browser/WASI build-test invocations, and the
  browser Mono trimming-app vertical in runtime-linker-tests.
- Deletes five uncalled test templates and removes dead Mono runtime-test path
  filtering, optional parameter wiring and an empty extra-platforms caller.
- Disables generation/publication of the no-longer-consumed Mono WBT archive.
- Retains existing CoreCLR browser/WASI and NativeAOT tests, libraries outerloop,
  CoreCLR browser WBT and its Mono workload-package build dependencies.
- Keeps needed Mono build-only/runtime-pack/cross-AOT/offset-validation producers.
- Removes a manual CoreCLR pack-only producer used solely by removed Mono WBT,
  not a CoreCLR test lane.
- Leaves CodeQL, diagnostics' migrated CoreCLR producer, performance YAML, product
  sources, Azure definitions and Build Analysis registrations unchanged.

**The patch still removes Mono WBT. This preserves the experiment, not an approval to
submit those removals.** The next patch may retain those jobs after the user's decisions.
The three files in this handoff directory are continuation aids, not proposed permanent
repository documentation/tooling.

## Findings to carry forward

CoreCLR browser WBT retains substantial coverage from the shared
`src/mono/wasm/Wasm.Build.Tests` suite on Linux and Windows. That directory name does
not imply Mono runtime selection. However, CoreCLR excludes Mono-category cases:
45 annotations in 25 C# files, plus flavor-dependent theory rows. Those are source
counts, not executed test counts.

Portable gaps include Windows TEMP quoting, Unix workload permission manifests,
positive large-heap/JS-interop execution, and some native/interop policies.
Other cases are truly Mono-specific (compiler/bitcode/IL stripping, browser profiler,
threaded runtime). R2R/NativeAOT do not exercise Mono's implementation.

Three important corrections to the initial high-level summary:

- The old WASI job named WasmBuildTests has unverified actual payload execution:
  the WBT project reference in tests.proj is browser-only despite the WASI job flags.
  Inspect baseline binlogs/payload/results before crediting a working WASI test suite.
- CoreCLR's curated WASI runtime subset was pre-existing. The removed Mono
  runtime-template callers were browser-only, not a broader Mono WASI runtime lane.
- Shared browser functional tests remain on CoreCLR; most Mono sample projects were
  already excluded. Do not count all samples/functional tests as lost.

Suggested first decision, not accepted yet: temporarily keep Mono browser WBT while
classifying/porting portable exclusions, then explicitly decide Mono-specific features.

## Validation and limitations

At handoff, the following commands passed from the remote repository root:

```sh
PYTHONPATH=/root/.copilot/session-state/04ab6997f1014756bed17f74e4578da0/files/yaml-parser \
  python3 artifacts/mono-wasm-ci-handoff/validate_wasm_ci.py \
  --base 4ab4fb694b9aaf1c3b26a89d236aa75af7d0a2be
git diff --check
```

The session-specific PYTHONPATH above only supplied PyYAML. For local reproduction
with the checked-in copy, install PyYAML in an isolated environment, then run:

```sh
python3 docs/mono-wasm-ci-handoff/validate_wasm_ci.py \
  --base 4ab4fb694b9aaf1c3b26a89d236aa75af7d0a2be
git diff --check 4ab4fb694b9aaf1c3b26a89d236aa75af7d0a2be
```

The validator uses assertions; do not use Python `-O`. It is specific to the removal-only
patch and writes `wasm-ci-inventory.json` next to itself. Do not commit generated output
by accident. Update its expectations if the user approves replacement/retained lanes.

Static results: 229 base / 224 remaining CI YAML documents parsed as node trees,
preserving three repeated Azure conditional mapping entries; 83 retained invocations
and guards preserved; no new dangling static local templates; all six static CoreCLR
browser WBT dependency job IDs resolve to retained producers. The exported patch
also passed apply-check against a temporary Git index populated from the rebased base.

No product baseline/build, runtime/library tests, benchmark, Helix run, live Azure
template expansion or test-count comparison was run. No claim of passing runtime
behavior or complete coverage parity is justified.

## Boundaries for continuation

The user authorized publishing this WIP to a branch in their fork for local continuation.
No PR, issue/comment publication, main-repository push or other-repository changes
were requested. The former parent session is detached; no automatic report-back remains.

Preserve CoreCLR/NativeAOT tests and required Mono workload/package/toolchain producers.
Keep CodeQL and diagnostics intact. The performance counterpart
[dotnet/performance#5362](https://github.com/dotnet/performance/pull/5362) already exists;
do not modify that repository as part of this handoff.
Read the current repository instructions and required skills before future edits or builds.

> [!NOTE]
> This handoff and the associated exploratory patch were prepared with GitHub Copilot.
