# Mono Wasm CI retirement: coverage gaps and decisions

## Status and scope

**Decision document, not an approved removal plan. All dispositions below are recommendations pending user decisions.**

User priority: **Wasm.Build.Tests is an important use case.** Preserving its relevant behavior is a migration requirement, not an optional cleanup detail.

Comparison base: `7d68ded739b86cbaf01d8d06b7969aad3f9496f4`, the head inherited from `silver-disco`.
The exploratory removal patch is preserved in this WIP branch for local continuation;
the user authorized pushing the WIP to their fork, not submission of the removals.
The rebased patch base is `4ab4fb694b9aaf1c3b26a89d236aa75af7d0a2be`.
See [README.md](README.md) for rebase verification, local setup and current scope.

**Overall finding: overlapping coverage, not parity.** CoreCLR has substantial browser/WASI
library/runtime coverage and a browser Wasm.Build.Tests lane on both Linux and Windows.
Removing Mono nevertheless removes host/engine combinations, feature modes and test cases.
CoreCLR ReadyToRun and NativeAOT are not substitutes for exercising the Mono AOT implementation.

Evidence here is static source/template tracing. A checked-in invocation is not proof of
successful execution, test discovery or the number of cases that actually ran.
No live Azure expansion, Helix logs, test-result comparison or product tests were run.
This is an inventory of known differences and verification work, not an executed-test census.

### Corrections to the earlier high-level comparison

1. **The curated CoreCLR WASI runtime subset is a pre-existing limitation, not demonstrated
   loss of a Mono WASI runtime lane.** The removed `wasm-runtime-tests.yml` callers select
   browser only. Do not describe the WASI subset as a newly introduced regression.
2. **The old WASI job named WasmBuildTests has unverified actual test coverage.**
   Its template requests `TestWasmBuildTests=true`, `TestAssemblies=false` and the
   `buildwasmapps` scenario. However, the `tests.proj` reference that builds
   Wasm.Build.Tests is browser-only, and the test project's workload defaults are browser.
   The job declaration alone does not establish a working WASI build-test suite.
3. **Do not count all browser samples/functional tests as lost.** The shared library build
   still includes WebAssembly functional tests on CoreCLR. Mono sample projects are
   broadly excluded already; the threading smoke list separately includes its sample.
4. **Do not call every Mono-only WBT case obsolete.** Some protect portable behaviors and
   shared tooling. Others encode Mono-only implementation details or already have
   CoreCLR-specific replacements. These require separate decisions.

## 1. Decision ledger

`Port` means retain the behavior by adapting/adding coverage for CoreCLR and verifying
execution before deleting its last Mono test path. `Retire` means consciously stop testing
a feature whose support is being retired. `Accept` means consciously retain reduced coverage.
These are alternatives, not authorization to implement them.

| ID | Coverage difference | Classification | Recommended next disposition | Decision |
| --- | --- | --- | --- | --- |
| W01 | Shared browser WBT on Linux/Windows | Substantial retained overlap; parity unmeasured | Keep CoreCLR lane and verify workload/no-workload permutations and executed cases | Pending |
| W02 | Browser WBT Mono AOT, IL stripping and AOT rebuild rows | Engine-specific loss; some portable outcomes overlap | Separate retire-only AOT assertions from behaviors worth porting to R2R; retain Mono WBT during assessment | Pending |
| W03 | Windows native publish with special characters in TEMP | Portable behavior excluded from CoreCLR | Port/validate this regression case on CoreCLR before losing the Mono execution path | Pending |
| W04 | Unix workload permission manifests and referenced files | Shared-toolchain validation excluded from CoreCLR | Split Mono AOT pack assertions from generic manifest/file checks; retain generic checks | Pending |
| W05 | Large heap followed by repeated JS interop | Runtime execution excluded from CoreCLR workload lane | Port supported equivalent or explicitly record unsupported feature; no-workload negative case is not replacement | Pending |
| W06 | Undefined-symbol policy and marshalling diagnostics | Mixed product behavior and implementation-specific checks | Decide supported CoreCLR semantics, then port applicable outcomes; do not simply remove traits | Pending |
| W07 | Browser multithreaded WBT | No equivalent in selected CoreCLR suite | Retain a focused Mono threading WBT lane if feature remains supported; otherwise explicitly retire | Pending |
| W08 | Mono profiler and interpreter PGO tests | Mono-only feature; PGO test already ActiveIssue-disabled | Decide profiler retirement independently; do not count disabled PGO as newly lost passing coverage | Pending |
| W09 | Non-Webcil browser WBT permutations | Mono-only matrix dimension; shared loader behavior may overlap | Retire only Mono format path if no longer supported; verify retained shared loader/cache behavior | Pending |
| W10 | WASI WasmBuildTests invocation | Actual baseline payload/execution unverified; no retained matching CoreCLR invocation found | Inspect baseline payload/results, define real WASI publish/run tests, then migrate or retire explicitly | Pending |
| B01 | Browser library tests on Linux Chrome | Retained broad suite, different exclusions/runtime guards | Accept engine switch only after checking excluded suite/test membership; avoid asserting parity | Pending |
| B02 | Browser full Firefox suite versus CoreCLR smoke | Confirmed depth reduction on Linux Firefox | Promote CoreCLR Firefox to full library suite, or explicitly accept smoke-only | Pending |
| B03 | Browser Windows library tests | Confirmed host-matrix reduction | Add equivalent CoreCLR Windows library invocation, or explicitly accept Linux-only libraries | Pending |
| B04 | macOS Chrome smoke/version-update check | Confirmed host-matrix reduction | Keep a CoreCLR macOS smoke check if that host matters; otherwise explicitly accept loss | Pending |
| B05 | Browser threaded library tests | Confirmed feature-mode loss | Retain focused threading coverage while supported, or explicitly retire threading coverage | Pending |
| B06 | Browser EAT/interpreter library run | Full aggressive-trimming mode not reproduced by R2R | Add CoreCLR aggressively trimmed interpreter lane or accept R2R-only trimming plus reduced interpreter coverage | Pending |
| B07 | Browser AOT smoke/high-resource library runs | Mono AOT lost; R2R has different suite exclusions | Retire Mono compiler coverage only deliberately; assess JSON/regex functional coverage separately | Pending |
| B08 | Browser runtime-test suite on Mono interpreter | CoreCLR counterpart exists; case-set equality unverified | Compare discovered/ran/skipped test manifests; classify engine-only versus portable exclusions | Pending |
| B09 | Browser library trimming-test applications in linker pipeline | No matching retained browser execution in that pipeline | Migrate browser trimming vertical to CoreCLR and verify applicable test apps, or explicitly accept loss | Pending |
| S01 | WASI interpreter library smoke on Linux/Windows | Broad Linux CoreCLR run remains; Windows library host lost | Keep broad Linux suite; decide Windows separately and compare Mono smoke list to CoreCLR exclusions | Pending |
| S02 | WASI Mono AOT libraries | Engine-specific loss; baseline uses a limited explicit test list | Decide Mono AOT retirement; verify equivalent portable scenarios under CoreCLR R2R, not assumed full parity | Pending |
| S03 | CoreCLR WASI runtime subset, R2R and NativeAOT smoke | Retained existing coverage, not newly removed Mono WASI runtime tests | Track as baseline limitations; do not block retirement on unrelated expansion unless chosen | Pending |
| C01 | Manual Wasm wrappers and rolling extra-platforms test entry points | Test selection/cadence loss beyond test content | Decide which wrappers should run retained CoreCLR tests and which should be retained as empty/deprecated entry points | Pending |
| C02 | Mono runtime packs/toolchains still built | Build support retained without equivalent Mono execution | Keep dependencies; decide a minimal Mono WBT/smoke floor while products remain supported | Pending |

**Suggested first decision:** retain browser Mono WBT temporarily while W03-W06 are
classified/ported and W02/W07-W09 receive explicit retirement decisions. This does not
mean restoring all Mono library/runtime lanes. WASI WBT needs W10's baseline verification
before choosing how to preserve its intended use case.

## 2. Wasm.Build.Tests: detailed evidence and acceptance criteria

### W01. Shared browser build/publish/workload scenarios

**Old:** Mono `WasmBuildTests` jobs on `browser_wasm` and `browser_wasm_win` in main
and manual Wasm invocations. The Mono template supplies `/p:RuntimeFlavor=Mono`.

**Retained:** `browser-wasm-coreclr-build-tests.yml` remains called from main on the same
two build-host platforms and supplies `/p:RuntimeFlavor=CoreCLR`. It is still the shared
`src/mono/wasm/Wasm.Build.Tests/Wasm.Build.Tests.csproj`, not a different replacement suite.
The filesystem path containing `mono` does not mean every test uses the Mono runtime.

`sendtohelix.proj:88-142` constructs workload and no-workload runs, fingerprinting variants,
the JavaScript bundler variant, and the CoreCLR wasm-opt variant. Browser flavor filters
exclude `category=mono` on CoreCLR and `category=coreclr` on Mono
(`sendtohelix-browser.targets:174-192`). Workload/default permutations fan out per class;
other permutations have a single work item (`sendtohelix-wasm.targets:36-77`).

**What is protected:** many shared app build/run, native relink, cache/loading, interop,
globalization and incremental build behaviors retain tests. Examples include
`NativeBuildTests.SimpleNativeBuild`, interpreter ABI cases in `PInvokeTableGeneratorTests`,
and `Blazor.EventPipeDiagnosticsTests.BlazorEventPipeTestWithCpuSamples`.
CoreCLR additionally has its own `ReadyToRunTests` and `CoreCLRWasmNativeDefaultsTests`.

**What is not established:** matching executed test counts, identical theory rows,
equal assertion behavior, all permutations being selected successfully, or equal skip sets.
Class-name presence alone does not prove a specific method/theory row executed.

**Acceptance:** retain Linux/Windows CoreCLR WBT and package dependencies; collect generated
class lists plus per-method/theory results for both flavors and permutations; map every
removed portable outcome to a retained passing assertion. Preserve no-workload tests as
negative/SDK behavior tests rather than counting them as successful app execution.

### W02. Mono AOT/IL stripping and native rebuild behavior

Mono-only AOT tests include entry points (`WasmBuildAppTest`), build/publish
(`BuildPublishTests`, `Blazor/BuildPublishTests`), native references, lazy loading,
satellite assemblies, globalization/timezone, ABI and incremental rebuild cases.
Some shared theory providers explicitly drop AOT rows on CoreCLR:
`MainWithArgsTests.cs:34`, `SatelliteAssembliesTests.cs:36-43`,
`NativeRebuildTests/FlagsChangeRebuildTest.cs:34` and
`NativeRebuildTests/OptimizationFlagChangeTests.cs:34`.

**Loss:** no test of Mono's compiler/bitcode/IL stripping/AOT-specific rebuild path remains
from these WBT submissions. R2R tests exercise Crossgen2 and a different execution path.
They can protect analogous portable outcomes but do not test Mono AOT correctness.

**Options:** consciously retire Mono AOT tests; keep a reduced Mono AOT WBT set while packs
ship; port portable app outcomes to R2R while retiring implementation-specific assertions.
**Acceptance:** a per-case mapping, not "both are AOT"; verify positive run results as well
as build success and negative/diagnostic cases where those are the tested contract.

### W03. Windows special-character TEMP path

`NativeBuildTests.cs:47-80` marks `NativeBuildWithSpecialCharsInTempPath` as Mono-only
although its test data use `aot:false`. It skips Unix and publishes/runs with TEMP/TMP
containing parentheses and Unicode, expecting exit 42. The comment describes cmd quoting.

**Why it matters:** retaining a Windows CoreCLR WBT job does not retain this method.
The input partition is meaningful for developer build environments, not just Mono AOT.
Whether CoreCLR reaches the same process-launch path must be checked before adapting it.

**Recommendation:** port the supported CoreCLR native publish/run outcome. Acceptance is
the actual special-character-TEMP case executing on Windows and asserting exit 42; a
normal-path publish or Linux run is not an equivalent check. Keep old coverage until then.

### W04. Installed workload/package integrity

`WorkloadTests.cs:19-79` marks the entire class Mono-only.
`FilesInUnixFilesPermissionsXmlExist` checks the constructed SDK's installed workload packs,
every file referenced by `UnixFilePermissions.xml`, Emscripten Cache/Node/Sdk pack names,
and a Mono cross-AOT browser pack. Windows is explicitly skipped.

**Loss:** generic shared Emscripten manifest/file checks disappear with Mono WBT even though
retained CoreCLR WBT still consumes workload products.

**Recommendation:** split shared package-integrity expectations from the Mono AOT pack
assertion; adapt pack expectations for the actual CoreCLR workload cohort. Acceptance
includes nonempty expected pack/manifests and every referenced file existing, not merely
successful workload install. Do not retain obsolete hard-coded Mono pack assumptions.

### W05. Large heap/JS interop

`MemoryTests.cs:24-63` has a shared no-workload entry point and a Mono-only workload
`AllocateLargeHeapThenRepeatedlyInterop` method, with a source TODO identifying a
CoreCLR limitation. The test requests `EmccMaximumHeapSize=4294901760`; only the
workload branch runs the app and asserts its completion marker and absence of codegen errors.

**Loss:** the positive large-heap/repeated-interop execution case. The no-workload path
checks an expected unsuccessful build, so retaining it is not equivalent execution coverage.

**Options:** port positive execution if supported; keep a focused Mono test while supported;
or explicitly classify this as an unsupported CoreCLR capability. Acceptance must include
successful app execution and the original observable completion condition, with a
runtime-appropriate error assertion rather than mechanically retaining a jiterpreter message.

### W06. Native build policy, interop diagnostics and defaults

These are not a homogeneous "Mono-only" group:

| Example | Existing distinction | Proposed assessment |
| --- | --- | --- |
| `Templates/NativeBuildTests.cs:23-59`, `BuildWithUndefinedNativeSymbol` | Mono-only; comment says CoreCLR does not honor `WasmAllowUndefinedSymbols=false` | Decide intended CoreCLR policy; do not claim a supported equivalent by removing the trait |
| `PInvokeTableGeneratorTests.cs:56-65`, non-blittable callback rejection | Mono-specific generator rejection; shared blittable cases remain | Retire generator-specific diagnostic if inapplicable; retain portable interop outcomes |
| `PInvokeTableGeneratorTests.cs:128-178`, assembly marshalling-attribute combinations | Mono-only accepted/rejected input combinations | Map each supported success/failure outcome before porting; do not drop error-path expectations silently |
| `DllImportTests.cs:26-29`, variadic native functions | Mono generator warning differs; related shared function-pointer cases remain | Separate runtime/call correctness from generator-specific warning text |
| `WasmNativeDefaultsTests.cs:14-115` | Mono default/relink rules, including AOT | Use existing `CoreCLRWasmNativeDefaultsTests` as partial replacement; compare property partitions |

The CoreCLR defaults class checks matching/unset versus differing property values, explicit
native-build suppression, and native-reference relinking. That is meaningful replacement
coverage for some outcomes, not proof it covers every Mono property or default.

**Acceptance:** for each portable public property/input, record whether CoreCLR supports
it, the expected build/run/error result, and the retained/new test. Implementation-specific
warning text and Mono icall tables may be deliberately retired separately.

### W07. Multithreaded WBT

`Blazor/SimpleMultiThreadedTests.cs:16-47` is Mono-only and covers build/run and publish/run.
Several theory rows were already commented out, so do not claim they are newly lost.
`Common/BuildEnvironment.cs:112-114` explicitly makes
`IsWorkloadWithMultiThreadingForDefaultFramework` false for CoreCLR.

**Loss:** retained CoreCLR WBT does not execute this class or substitute its feature mode.
Building the Mono multithreaded pack for CoreCLR workload install is not running a
multithreaded app.

**Decision:** if multithreaded Mono products remain supported, retain a focused build/publish/run
case until a supported successor exists. If threading support is being retired, record that
as a feature decision; it cannot be achieved as a YAML runtime-flavor substitution.

### W08. Profiling and interpreter-specific features

`DiagnosticsTests.cs:12-32` is Mono-only and runs a browser profiler-enabled app.
`InterpPgoTests.cs:18-30` is Mono-only but its PGO method already carries an `ActiveIssue`
without a limiting platform predicate. Treat that method as already disabled, not as
verified passing coverage being removed.

Shared non-AOT EventPipe CPU-sampling tests remain, and CoreCLR has explicit R2R variants
(`Blazor/EventPipeDiagnosticsTests.cs:37-51`). Neither verifies the Mono browser profiler.

**Decision:** retire Mono-specific diagnostics only if those products/features are being
retired; otherwise keep a focused test. Do not restore a disabled PGO test merely to
inflate nominal parity.

### W09. Asset-format permutations

`sendtohelix.proj:91` adds true/false Webcil permutations only for browser Mono.
CoreCLR has different Webcil and R2R format expectations, with explicit version/default
tests in `CoreCLRWasmNativeDefaultsTests` and format validation in `ReadyToRunTests`.

**Loss:** the Mono non-Webcil matrix dimension, not all asset-loading tests.
Shared cache/bootstrap/appsettings coverage still exists but flavor-specific outcomes
can differ. Do not infer every lost format is a supported CoreCLR format.

**Decision:** retire the obsolete format path if support ends; preserve shared loading,
cache/retry, fingerprinting and bundler behaviors. Acceptance requires checking the
appropriate supported format and configuration, not adding unsupported permutations.

### W10. WASI build/publish/workload use case

The deleted `simple-wasm-build-tests.yml:16-59` selected Mono and the Linux/Windows WASI
platforms, requested `TestWasmBuildTests=true`, and submitted `buildwasmapps`.
There is no matching retained CoreCLR WASI WBT invocation in the traced pipelines.

**Important baseline uncertainty:** `src/libraries/tests.proj:761-766` references WBT only
when `TargetOS=browser`. Its ordinary test references require `TestAssemblies=true`, which
the old WASI job disables. `Wasm.Build.Tests/Directory.Build.props:13-15` also sets browser
workload defaults. Static source does not establish what successful WASI WBT payload the
old job could have produced on a clean agent.

**Recommendation:** treat WASI build/publish testing as required, but first verify whether
the old lane ran any relevant tests. Inspect its build binlog, generated payload/class list
and xUnit results; require nonzero relevant cases. Then define a real WASI-specific
project build/publish/run check (Linux first, Windows according to S01), including workload
selection and generated-host/interop behavior where applicable.

Do not solve this by renaming the browser template or changing only `runtimeFlavor`.
Successful WASI library/runtime tests compile apps using in-tree targets, but they do not
by themselves prove a user's workload-installed template/publish experience.

## 3. Library and runtime coverage details

### B01-B04. Browser suite depth, hosts and engines

Base main `runtime.yml:1266-1298` ran Mono Linux Chrome/Firefox full libraries, Windows Chrome
full libraries, and macOS Chrome smoke on Chrome updates (and rolling runs).
Retained CoreCLR `runtime.yml` has Linux Chrome broad libraries, trimmed R2R smoke, and
Firefox/V8 smoke. There is no matching retained Windows library or macOS library smoke
invocation in this inventory. Windows WBT is a different test category.

The browser smoke lists are different: `tests.proj:578-592` adds intrinsics, JS interop
and LINQ for Mono, versus JS interop, threading/tasks, HTTP and WebSockets for CoreCLR,
in addition to common smoke entries. Merely labeling both "smoke" does not make them equal.

CoreCLR browser interpreter innerloop excludes six slow suites
(`tests.proj:183-195`): LINQ, Memory, Uri, XML, Numerics and JSON.
The retained libraries outerloop uses `testscope all`, extended timeouts and
`RunLongRunningWasmTests`, so these are not all "gone everywhere."
Browser R2R separately excludes Dynamic.Runtime and JSON (`tests.proj:91-96`).
Other project exclusions and per-test feature/runtime guards can also differ.

**Minimum decision per host/engine:** choose full versus smoke and PR versus rolling/manual
cadence; retain existing tests until that choice is explicit. For Firefox, full CoreCLR
execution would restore suite depth but not Mono engine implementation coverage.
For Windows/macOS, first verify runtime/build-host support; adding a platform entry alone
is not evidence that the suite works there.

### B05. Browser threaded libraries

The removed `_Threading` invocation sets `WasmEnableThreads=true`, runs Chrome on Linux,
continues on error, and sometimes selects smoke based on library/linker-only changes.
The threading smoke list (`tests.proj:645-653`) replaces the ordinary smoke list with
the browser-threads sample, Common, Thread, JS interop, WebSockets and HTTP.
Thus single-threaded CoreCLR threading/tasks tests are not feature-mode equivalence.

**Recommendation:** decide alongside W07. Retain the smallest meaningful Mono threaded
app/library set while that runtime pack remains a supported product; otherwise record
the explicit loss. Historical continue-on-error is weaker gating, not proof no tests ran.

### B06. Aggressive trimming without AOT (EAT)

The removed EAT lane used `EnableAggressiveTrimming=true` and `RunAOTCompilation=false`
on Linux browser, with full libraries rather than smoke.
The retained CoreCLR R2R library template also sets aggressive trimming, but additionally
publishes R2R. The ordinary CoreCLR interpreter template does not enable EAT.

**Loss:** a distinct trimmed-interpreter mode. R2R coverage provides some trimming overlap,
but can conceal interpreter-specific metadata/execution issues.
**Options:** add a CoreCLR full EAT interpreter variant, a focused EAT set, or consciously
accept R2R-only aggressive-trimming coverage. Acceptance must include actual test execution
after trimming, not just successful linking.

### B07. Browser AOT and high-resource suites

Main and rolling/manual invocations cover Mono AOT smoke; rolling/manual invocations also
select `_HighResource_AOT`. High-resource explicitly selects regex functional and JSON
projects (`tests.proj:46-53`), although later exclusions must still be honored.

The retained browser R2R full lanes exclude JSON; the retained CoreCLR interpreter
outerloop can include it. Regex has different compiler/interpreter paths.
Do not assert the high-resource AOT cases remain merely because the same project appears
in a non-AOT outerloop. Conversely, do not assert all JSON/regex coverage disappears.

**Decision:** separate compiler retirement from suite behavior. If Mono AOT remains in the
product, some compiler smoke validation may still be necessary. If it is retired, preserve
applicable JSON/regex behavior through supported interpreter/R2R coverage, respecting known
exclusions and measuring runs before considering the gap closed.

### B08. Browser runtime tests

Removed Mono runtime template selects Release, `monointerpreter`, innerloop/priority 0,
and builds `src/tests` without explicit tree restrictions. Retained CoreCLR browser
Checked/Release-libraries job also builds runtime tests without explicit tree restrictions
and submits normal and R2R variants.

**Overlap:** much of the same source tree. **Not proven:** identical discovered tests or
per-test skips. Runtime/flavor/feature gates differ. As a caution against superficial
comparisons, `JIT/Stress/ABI/Directory.Build.props:7-10` disables Mono already and marks
browser CoreCLR unsupported; that is not newly lost Mono ABI coverage.
`Interop/MonoAPI/MonoMono/Directory.Build.props:5` excludes browser CoreCLR, a likely
engine-specific rather than portable surface.

**Acceptance:** compare built/discovered/executed/failed/skipped sets under real runtime
selection and classify remaining differences. Do not migrate every Mono-specific API test
to CoreCLR or call a source-tree match sufficient evidence.

### B09. Browser trimming applications

Base `runtime-linker-tests.yml:132-154` builds a browser Mono vertical and runs the
library `*.TrimmingTests.proj` apps using `TestTrimming=true`, `WasmBuildNative=false`,
Debug ILLink tools, and `RuntimeFlavor=Mono`. The patch removes this entire vertical.
Windows/Linux CoreCLR trimming and NativeAOT tests remain, but are not browser tests.
Aggressively trimmed browser xUnit libraries are not the same trimming-test applications.

**Recommendation:** migrate this vertical to a supported CoreCLR browser app runner and
validate the selected trimming applications and exit codes. Alternatively explicitly
accept losing browser-specific trimming-app checks. Preserve the desktop tool tests.

### S01-S02. WASI libraries, host matrix and AOT

Removed manual/rolling WASI library variants run on Linux and Windows with wasmtime.
The interpreter variants select smoke; the optional variant also requests smoke.
The old Mono AOT template defaults to building AOT tests on Helix. For WASI it activates
`RunLimitedSetOfTests`, not an unrestricted full library suite.
`tests.proj:620-628` selects XML serializer generator, specialized collections, buffers,
invariant globalization and globalization (the latter omitted on Windows).

Retained CoreCLR WASI libraries are Linux-hosted: broad interpreter, R2R smoke in main,
and full R2R innerloop through outerloop/manual wrappers. No equivalent Windows-hosted
library test invocation is retained. The Mono interpreter smoke list is substantially
broader than CoreCLR's R2R smoke list (`tests.proj:594-619`), but broad interpreter/full
R2R runs may cover those projects subject to exclusions.

CoreCLR WASI exclusions include Runtime.Loader.DefaultContext; aggressive trimming
excludes ComponentModel.Composition; R2R excludes JSON and regex functional tests
(`tests.proj:404-418`). Interpreter default timeouts exclude seven slow suites
(`tests.proj:199-208`). The full WASI R2R template does not offer a `testScope` parameter;
the outerloop caller explicitly documents innerloop scope, not full slow outerloop parity.

**Decisions:** preserve Linux broad library coverage; separately decide Windows build/run
support, Mono AOT retirement, and specific portable tests missing from retained exclusions.
Do not claim the old AOT lane was a full suite or that the new broad lane necessarily
contains every Mono smoke/AOT test case.

### S03. WASI runtime and NativeAOT baseline

CoreCLR WASI submits interpreter and R2R runtime tests using eight explicit trees:
`JIT/CodeGenBringUpTests`, `JIT/Generics`, `JIT/Directed`, `JIT/opt`, `JIT/jit64`,
`Loader`, `Exceptions`, and `reflection`, with priority 1 enabled
(`wasi-wasm-coreclr-runtime-tests.yml:67-77`).
NativeAOT WASI Checked smoke tests under the nativeaot tree remain.

These cover meaningful functionality, but neither proves arbitrary runtime-tree nor
Mono AOT parity. Since there was no removed Mono WASI runtime-template invocation,
expanding CoreCLR's WASI trees is a separate baseline improvement, not an automatic
prerequisite for replacing a supposedly existing Mono WASI runtime run.

## 4. Entry points, scheduling and retained build products

### C01. Wrapper/cadence differences

After the exploratory patch:

| Entry point | Retained Wasm behavior |
| --- | --- |
| Main `runtime` | Existing CoreCLR browser/WASI library/runtime/WBT lanes and build-only dependencies |
| Libraries outerloop | Existing CoreCLR browser broad/trimmed R2R and WASI full R2R innerloop |
| Rolling `runtime-extra-platforms` | Its dedicated Mono Wasm AOT invocations disappear; other platform tests remain |
| `runtime-wasm-libtests` | Full CoreCLR trimmed R2R browser and WASI jobs remain |
| `runtime-wasm`, `runtime-wasm-non-libtests` | Browser Mono build-only producers remain; removed Mono tests are not replaced with manual CoreCLR WBT/runtime tests |
| `runtime-wasm-optional` | No test jobs remain; monitor allows zero Helix submissions |
| `runtime-wasm-dbgtests` | Already selected no test jobs at this base; unchanged |
| `runtime-linker-tests` | Windows/Linux tool and trimming/NativeAOT tests remain; browser trimming apps removed |

**Decision:** even when main retains an overlapping test, losing a manual rerun entry point
or rolling expensive configuration can matter. Decide whether to rewire wrappers to
CoreCLR equivalents, keep clearly documented empty entry points, or retire them later
with Azure-definition authorization. No Azure definitions are changed by this experiment.

Path filters and continue-on-error/timeout policies also differ. Keep existing retained
guards until changes are deliberate; when adding a replacement, verify that changes to
its shared tooling actually select it, rather than only testing a manual always-run case.

### C02. Build products do not equal execution coverage

Retained Mono browser single-/multithreaded producers supply manifests/runtime packages
required by CoreCLR browser WBT. CoreCLR WBT explicitly downloads both types.
Main's WASI build-only lanes, eight-host browser/WASI cross-AOT toolchains, offset-header
validation, optional Mono performance artifact production, and CodeQL remain.
Diagnostics' migrated CoreCLR producer remains unchanged.

**Risk:** if those Mono products remain supported, deleting all their WBT execution removes
functional validation while continuing to ship/build them. That may be acceptable as a
product-retirement policy, but it is not justified by keeping build-only jobs.

**Recommendation:** keep needed producers and establish a minimal Mono WBT/smoke support
floor until per-feature retirement decisions are made. Do not remove packs or toolchains
as an incidental way to avoid deciding coverage. Performance and other repositories
remain out of scope.

## 5. Complete explicit Mono WBT category inventory

Static search found **45 `TestCategory("mono")` annotations across 25 C# files**.
These are source annotations, not executed tests: class annotations cover multiple
methods, methods have multiple theory rows, and additional skips/ActiveIssue apply.
CoreCLR filters exclude these annotations at class-list generation and/or execution.
Runtime-aware data providers can remove further rows even without a Mono category.

Paths below are relative to `src/mono/wasm/Wasm.Build.Tests/` at the pinned base.

| File and annotation lines | Decision group |
| --- | --- |
| `Blazor/NativeRefTests.cs:12` | W02 |
| `Blazor/MiscTests.cs:34,62` | W02 |
| `Blazor/BuildPublishTests.cs:53,133,153` | W02 |
| `Blazor/EventPipeDiagnosticsTests.cs:38` | W02/W08; shared non-AOT and CoreCLR R2R methods remain |
| `Blazor/SimpleMultiThreadedTests.cs:16` | W07 |
| `InterpPgoTests.cs:18` | W08; method already ActiveIssue-disabled |
| `DllImportTests.cs:28` | W06 |
| `NativeRebuildTests/ReferenceNewAssemblyRebuildTest.cs:17` | W02 |
| `NativeRebuildTests/NoopNativeRebuildTest.cs:40` | W02 |
| `BuildPublishTests.cs:26,54` | W02 |
| `SatelliteAssembliesTests.cs:110` | W02; also flavor-dependent theory data |
| `LazyLoadingTests.cs:77` | W02 |
| `WasmNativeDefaultsTests.cs:14` | W06; CoreCLR-specific defaults class provides partial replacement |
| `WasmBuildAppTest.cs:23,38,69,125` | W02 |
| `InvariantTimezoneTests.cs:32` | W02; non-AOT relink method remains |
| `WorkloadTests.cs:19` | W04 |
| `MemoryTests.cs:28` | W05 |
| `Templates/NativeBuildTests.cs:27` | W06 |
| `PInvokeTableGeneratorTests.cs:56,128,139,297,480,546,559` | W02/W06 |
| `Templates/WasmTemplateTests.cs:264,442` | W02/W06; assess property/diagnostic purpose before porting |
| `NativeLibraryTests.cs:29,109,137,163` | W02 |
| `InvariantGlobalizationTests.cs:35` | W02; non-AOT relink and CoreCLR-specific checks remain |
| `DiagnosticsTests.cs:12` | W08 |
| `NativeBuildTests.cs:49,85,104,137` | W03/W02 |
| `WasmSIMDTests.cs:53` | W02; separate CoreCLR-specific variant remains |

This list makes the explicitly excluded set inspectable. It does not imply all methods
in the listed files are lost, or that each annotation protects a portable contract.
Every row must be marked port / retire / retain / already covered with evidence before
claiming WBT migration complete.

## 6. Evidence index and verification plan

All source references use the fixed child base; repository sources did not change during
this documentation pass. Deleted YAML can be read with `git show <base>:<path>`.

- [Base main Wasm jobs](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/eng/pipelines/runtime.yml#L1264-L1421).
- [Base extra/manual Wasm matrix](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/eng/pipelines/extra-platforms/runtime-extra-platforms-wasm.yml).
- [Retained CoreCLR browser WBT template](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/eng/pipelines/common/templates/browser-wasm-coreclr-build-tests.yml).
- [Library selection, exclusions, smoke/AOT lists and project references](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/src/libraries/tests.proj).
- [WBT project and flavor-specific class-list filtering](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/src/mono/wasm/Wasm.Build.Tests/Wasm.Build.Tests.csproj).
- [WBT workload defaults](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/src/mono/wasm/Wasm.Build.Tests/Directory.Build.props).
- [Helix browser flavor filters](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/src/libraries/sendtohelix-browser.targets#L174-L192).
- [Helix WBT permutations](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/src/libraries/sendtohelix.proj#L87-L142).
- [Helix WBT work-item creation and missing-list error](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/src/libraries/sendtohelix-wasm.targets#L36-L77).
- [Host queue mapping](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/eng/pipelines/libraries/helix-queues-setup.yml#L178-L203).
- [Libraries outerloop](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/eng/pipelines/libraries/outerloop.yml#L123-L194).
- [Base browser trimming vertical](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/eng/pipelines/runtime-linker-tests.yml#L129-L154).
- [CoreCLR WASI runtime subset](https://github.com/dotnet/runtime/blob/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/eng/pipelines/common/templates/wasi-wasm-coreclr-runtime-tests.yml#L60-L85).
- [WBT source directory](https://github.com/dotnet/runtime/tree/7d68ded739b86cbaf01d8d06b7969aad3f9496f4/src/mono/wasm/Wasm.Build.Tests) contains the file/line evidence for W01-W09 and the explicit-category inventory.

### Evidence needed to close decisions

1. **WBT first:** collect successful baseline Mono/CoreCLR Linux/Windows class lists and xUnit
   results for workload/no-workload and retained configuration permutations. Record failing,
   skipped and disabled cases separately; require nonzero relevant tests for each lane.
2. Verify W10's WASI payload/binlog before crediting old WASI WBT execution. Define the
   supported WASI build/publish/run scenarios independently of the misleading job name.
3. For each portable WBT gap, identify exact input and assertion, supported CoreCLR result,
   retained/new test and actual passing run. For each retired case, record the feature
   decision instead. Do not remove `mono` traits indiscriminately.
4. Compare browser/WASI library project and per-test manifests, including slow-suite and
   R2R exclusions. Choose host/engine depth and cadence separately from compiler mode.
5. Before any revised YAML patch is submitted, run structural preservation/dependency
   checks and live Azure expansion where available. Builds and tests for newly introduced
   replacement lanes must demonstrate relevant executed counts and successful outcomes.

The existing removal-only validator passes syntax/node preservation, retained invocation
and dependency checks; it does **not** close these behavioral coverage decisions.
The original applied patch's SHA-256 is
`d3ceae95c4b3755457c1c863b9ac5a43b0aa023dd5dd3e15a5b4b82adb302a35`.
That fingerprint identifies the removal-only patch before adding these handoff files.
This copy is included in the WIP branch for local continuation, not as approved
permanent repository documentation.

> [!NOTE]
> This coverage analysis was prepared with GitHub Copilot.
