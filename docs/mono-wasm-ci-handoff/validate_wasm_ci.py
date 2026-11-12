import argparse
import collections
import json
import pathlib
import re
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent / "yaml-parser"))
import yaml
from yaml.nodes import MappingNode, ScalarNode, SequenceNode

parser = argparse.ArgumentParser(description="Statically validate the Mono Wasm removal-only patch.")
parser.add_argument("--base", default="4ab4fb694b9aaf1c3b26a89d236aa75af7d0a2be")
BASE = parser.parse_args().base
ROOT = pathlib.Path.cwd()
ARTIFACTS = pathlib.Path(__file__).parent
REMOVED = {
    "browser-wasm-build-tests.yml",
    "simple-wasm-build-tests.yml",
    "wasm-library-aot-tests.yml",
    "wasm-library-tests.yml",
    "wasm-runtime-tests.yml",
}


def git(*args):
    return subprocess.check_output(["git", *args], text=True)


def get(node, key, default=None):
    if not isinstance(node, MappingNode):
        return default
    found = [value for name, value in node.value if name.value == key]
    assert len(found) <= 1, (key, node.start_mark)
    return found[0] if found else default


def scalar(node, default=""):
    return node.value if isinstance(node, ScalarNode) else default


def values(node):
    return [scalar(value) for value in node.value] if isinstance(node, SequenceNode) else []


def freeze(node, omit=(), replacements=None):
    if node is None:
        return None
    if isinstance(node, MappingNode):
        return ("map", tuple(
            (freeze(key), freeze(value, omit, replacements))
            for key, value in node.value if scalar(key) not in omit
        ))
    if isinstance(node, SequenceNode):
        return ("seq", tuple(freeze(value, omit, replacements) for value in node.value))
    return (node.tag, node.value)


def walk(node):
    yield node
    if isinstance(node, MappingNode):
        for key, value in node.value:
            yield from walk(key)
            yield from walk(value)
    elif isinstance(node, SequenceNode):
        for value in node.value:
            yield from walk(value)


def invocations(node, guards=()):
    if isinstance(node, MappingNode):
        template = scalar(get(node, "template"))
        parameters = get(node, "parameters")
        if template and get(parameters, "stages") is None:
            yield template, parameters, guards, node
            return
        for key, value in node.value:
            name = scalar(key)
            yield from invocations(value, guards + ((name,) if name.startswith("${{") else ()))
    elif isinstance(node, SequenceNode):
        for value in node.value:
            yield from invocations(value, guards)


def canonical(path, invocation):
    template, parameters, guards, node = invocation
    omit = ()
    if template.endswith("/wasm-build-only.yml"):
        omit = ("publishWBT",)
        assert scalar(get(parameters, "publishWBT")) in ("true", "false")
    elif template.endswith("/runtime-extra-platforms-wasm.yml"):
        omit = ("excludeOptional",)
    return path, guards, freeze(node, omit)


def summary(path, invocation):
    template, parameters, guards, node = invocation
    job = get(parameters, "jobParameters")
    return {
        "file": path,
        "template": template,
        "platforms": values(get(parameters, "platforms")),
        "suffix": scalar(get(parameters, "nameSuffix")) or scalar(get(job, "nameSuffix")),
        "scenarios": values(get(parameters, "scenarios")),
        "guards": guards,
    }


def allowed_removal(path, invocation):
    template, parameters, guards, node = invocation
    name = pathlib.PurePosixPath(template).name
    if name in REMOVED:
        return True
    job = get(parameters, "jobParameters")
    if path == "eng/pipelines/runtime-linker-tests.yml":
        return (
            name == "platform-matrix.yml"
            and values(get(parameters, "platforms")) == ["browser_wasm"]
            and scalar(get(job, "nameSuffix")) == "Runtime_Release"
            and scalar(get(job, "buildArgs")).startswith("-s mono+libs+tools.illink ")
        )
    if path == "eng/pipelines/extra-platforms/runtime-extra-platforms-wasm.yml":
        return (
            name == "platform-matrix.yml"
            and scalar(get(job, "nameSuffix")) == "CoreCLR"
            and scalar(get(job, "buildArgs")) ==
            "-s clr+libs+packs -c Release -rc $(_BuildConfig) /p:TestAssemblies=false "
            "/p:InstallWorkloadForTesting=false /p:BuildCrossgen2HostPackForWorkloadTesting=true"
        )
    return (
        path == "eng/pipelines/runtime-extra-platforms.yml"
        and name == "runtime-extra-platforms-wasm.yml"
    )


tracked = git("ls-files", "*.yml", "*.yaml").splitlines()
paths = [
    path for path in tracked
    if path.startswith(("eng/pipelines/", ".github/workflows/", ".azuredevops/"))
]
before = {}
after = {}
duplicate_conditionals = 0
for path in paths:
    before[path] = yaml.compose(git("show", f"{BASE}:{path}"))
    if (ROOT / path).exists():
        after[path] = yaml.compose((ROOT / path).read_text())
    for node in walk(before[path]):
        if isinstance(node, MappingNode):
            keys = collections.Counter(scalar(key) for key, value in node.value)
            duplicate_conditionals += sum(
                count - 1 for key, count in keys.items() if key.startswith("${{") and count > 1
            )
assert set(before) - set(after) == {
    f"eng/pipelines/common/templates/{name}" for name in REMOVED
}
print(f"PASS: parsed {len(before)} base / {len(after)} current CI YAML files as node trees; "
      f"preserved {duplicate_conditionals} repeated conditional mapping entries in base.")

changed = git("diff", "--name-only", BASE).splitlines()
removed_jobs = []
preserved = 0
for path in changed:
    if path not in after:
        continue
    if path == "eng/pipelines/common/evaluate-default-paths.yml":
        original = before[path]
        removed_subset = 0
        for node in walk(original):
            if isinstance(node, SequenceNode):
                retained = [
                    item for item in node.value
                    if scalar(get(item, "subset")) != "wasm_mono_runtimetests"
                ]
                removed_subset += len(node.value) - len(retained)
                node.value = retained
        assert removed_subset == 1
        assert freeze(original) == freeze(after[path])
        continue
    old_calls = list(invocations(before[path]))
    new_calls = list(invocations(after[path]))
    old = collections.Counter(canonical(path, call) for call in old_calls)
    new = collections.Counter(canonical(path, call) for call in new_calls)
    assert not new - old, ("Unexpected new/changed invocation", path, new - old)
    removed = old - new
    preserved += sum((old & new).values())
    for call in old_calls:
        key = canonical(path, call)
        if removed[key]:
            assert allowed_removal(path, call), ("Unexpected removal", summary(path, call))
            removed_jobs.append(summary(path, call))
            removed[key] -= 1
    assert not +removed
    if not old_calls:
        assert freeze(before[path]) == freeze(after[path]), path
print(f"PASS: {preserved} surviving template invocations unchanged, including conditional guards "
      "(only obsolete publishWBT/excludeOptional parameters normalized).")
print("PASS: every removed invocation belongs to a Mono test, its dedicated CoreCLR package "
      "producer, or the now-empty extra-platforms caller.")

# Verify the only changes hidden by canonicalization are exactly the intended parameter edits.
for path in ("eng/pipelines/runtime.yml", "eng/pipelines/extra-platforms/runtime-extra-platforms-wasm.yml"):
    calls = [
        parameters for template, parameters, guards, node in invocations(after[path])
        if template.endswith("/wasm-build-only.yml")
    ]
    assert calls and all(scalar(get(parameters, "publishWBT")) == "false" for parameters in calls)
    assert all(
        scalar(get(parameters, "publishArtifactsForWorkload")) == "true"
        for parameters in calls
        if values(get(parameters, "platforms")) == ["browser_wasm", "browser_wasm_win"]
    )

# All unchanged CI files are byte-identical; explicitly check critical preservation targets.
for path in (
    "eng/pipelines/libraries/outerloop.yml",
    "eng/pipelines/runtime-codeql.yml",
    "eng/pipelines/runtime-diagnostictests.yml",
    "eng/pipelines/common/templates/browser-wasm-coreclr-build-tests.yml",
    "eng/pipelines/common/templates/wasm-coreclr-library-tests.yml",
    "eng/pipelines/common/templates/wasi-wasm-coreclr-library-tests.yml",
    "eng/pipelines/common/templates/wasi-wasm-coreclr-build-only.yml",
):
    assert (ROOT / path).read_text() == git("show", f"{BASE}:{path}"), path
assert not git("diff", "--name-only", BASE, "--", "eng/pipelines/performance", "src")
print("PASS: CoreCLR test invocations/guards and NativeAOT jobs preserved; outerloop, CoreCLR "
      "WBT, diagnostics, CodeQL, performance and product sources untouched.")


def dangling(documents):
    missing = set()
    for path, document in documents.items():
        for node in walk(document):
            if not isinstance(node, MappingNode):
                continue
            template = scalar(get(node, "template"))
            if not template or "@" in template or "${{" in template:
                continue
            target = (
                pathlib.PurePosixPath(template.lstrip("/"))
                if template.startswith("/")
                else pathlib.PurePosixPath(path).parent / template
            )
            target = str(target)
            if target not in documents and not (ROOT / target).exists():
                missing.add((path, target))
    return missing


assert not dangling(after) - dangling(before), dangling(after) - dangling(before)
for path, document in after.items():
    for node in walk(document):
        if isinstance(node, ScalarNode):
            assert "wasm_mono_runtimetests" not in node.value
            assert not any(node.value.endswith("/" + name) for name in REMOVED)
print("PASS: no new dangling static local template references or removed-template/path-filter references.")

# Derive browser/WASI platform and host names from the real matrix, not filename assumptions.
matrix_platforms = {}
for node in walk(after["eng/pipelines/common/platform-matrix.yml"]):
    parameters = get(node, "parameters")
    platform = scalar(get(parameters, "platform"))
    if platform.startswith(("browser_wasm", "wasi_wasm")):
        matrix_platforms[platform] = (
            scalar(get(parameters, "osGroup")),
            scalar(get(parameters, "archType")),
            scalar(get(get(parameters, "jobParameters"), "hostedOs")),
        )
main_calls = list(invocations(after["eng/pipelines/runtime.yml"]))
providers = set()
for template, parameters, guards, node in main_calls:
    name = pathlib.PurePosixPath(template).name
    if name not in ("platform-matrix.yml", "wasm-build-only.yml"):
        continue
    suffix = scalar(get(get(parameters, "jobParameters"), "nameSuffix"))
    config = scalar(get(parameters, "buildConfig"))
    if name == "wasm-build-only.yml":
        suffix = scalar(get(parameters, "nameSuffix")) + "_BuildOnly"
        config = "Release"
    for platform in values(get(parameters, "platforms")):
        if platform in matrix_platforms:
            os_group, arch, host = matrix_platforms[platform]
            providers.add(f"build_{os_group}_{arch}_{host}_{config}_{suffix}")
dependencies = {
    node.value
    for node in walk(after["eng/pipelines/common/templates/browser-wasm-coreclr-build-tests.yml"])
    if isinstance(node, ScalarNode) and node.value.startswith("build_")
}
assert dependencies and dependencies <= providers, dependencies - providers
for host in ("linux", "windows"):
    for suffix in ("SingleThreaded_BuildOnly", "MultiThreaded_BuildOnly", "CoreCLR"):
        expected = f"build_browser_wasm_{host}_Release_{suffix}"
        assert expected in dependencies
print(f"PASS: all {len(dependencies)} static CoreCLR WBT dependency job IDs resolve to retained "
      "main-pipeline producers; workload-package publication remains enabled.")

for path in ("eng/pipelines/runtime-wasm.yml", "eng/pipelines/runtime-wasm-optional.yml"):
    assert all(
        get(parameters, "excludeOptional") is None
        for template, parameters, guards, node in invocations(after[path])
    )
inventory = {
    "base": BASE,
    "removed_invocations": removed_jobs,
    "limitations": [
        "Static syntax, node preservation, local references and dependency checks only.",
        "No live Azure template expansion, product build, Helix execution or benchmark run.",
        "Conditions retained structurally, not evaluated against Azure runtime variables.",
    ],
}
(ARTIFACTS / "wasm-ci-inventory.json").write_text(json.dumps(inventory, indent=2) + "\n")
print("Removed invocation inventory:")
for entry in removed_jobs:
    print(f"  {entry['file']}: {pathlib.PurePosixPath(entry['template']).name} "
          f"{entry['platforms']} {entry['suffix']} {entry['scenarios']}")
print("PASS: static validation complete.")
