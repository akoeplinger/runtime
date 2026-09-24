#!/usr/bin/env python3
#
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Run with python3 eng/native/tests/test_cmake_args.py; requires dotnet, CMake, and Ninja."""

import copy
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


REPO_ROOT = Path(__file__).resolve().parents[3]
DOTNET = REPO_ROOT / ".dotnet" / "dotnet"
if not DOTNET.exists():
    DOTNET = shutil.which("dotnet")

PROJECTS = (
    ("src/coreclr/runtime.proj", "BuildRuntime", "build-runtime.sh"),
    ("src/native/corehost/corehost.proj", "BuildCoreHostOnUnix", "build.sh"),
    ("src/native/libs/build-native.proj", "BuildNativeUnix", "build-native.sh"),
)

WRAPPER = """#!/usr/bin/env bash
set -e
__RepoRootDir="$TEST_REPO_ROOT"
__CMakeArgs=()
__BuildType=Debug
__PortableBuild=1
__CrossBuild=0
handle_arguments()
{
    case "$1" in
        -commithash|-runtimeflavor|outconfig)
            __ShiftArgs=1
            ;;
    esac
}
source "$__RepoRootDir/eng/native/build-commons.sh"
printf '%s\\0' "${__CMakeArgs[@]}" > "$TEST_CAPTURE"
"$__RepoRootDir/eng/native/gen-buildsys.sh" "$TEST_SOURCE" "$TEST_BINARY" "$__TargetArch" "$__TargetOS" default Debug ninja "${__CMakeArgs[@]}"
"""


@unittest.skipIf(os.name == "nt", "Exercises the Unix native build scripts")
class CMakeArgumentsTests(unittest.TestCase):
    def test_msbuild_forwarding(self):
        cases = (
            ("empty", [], []),
            ("multiple", ["-DFIRST=1 -DSECOND=2"], ["-DFIRST=1", "-DSECOND=2"]),
            ("repeated", ["-DFIRST=1", "-DSECOND=2"], ["-DFIRST=1", "-DSECOND=2"]),
            ("single_quotes", ["-DCMAKE_C_FLAGS='-O2 -g'"], ["-DCMAKE_C_FLAGS=-O2 -g"]),
            ("double_quotes", ['-DANDROID_CPP_FEATURES="no-rtti exceptions"'],
             ["-DANDROID_CPP_FEATURES=no-rtti exceptions"]),
            ("path", ['-DTEST_PATH="/tmp/path with spaces"'], ["-DTEST_PATH=/tmp/path with spaces"]),
            ("duplicate", ["-DFIRST=1 -DFIRST=2"], ["-DFIRST=1", "-DFIRST=2"]),
            ("cache", ['-C "{cache}"'], ["-C", "{cache}"]),
            ("list", ["-DTEST_LIST='a;b'"], ["-DTEST_LIST=a;b"]),
            ("percent", ["-DTEST_PERCENT='a%20b'"], ["-DTEST_PERCENT=a%20b"]),
            ("empty_value", ["-DTEST_EMPTY=''"], ["-DTEST_EMPTY="]),
            ("literal", [r"""-DTEST_LITERAL='a $b * \c "d"'"""], [r'''-DTEST_LITERAL=a $b * \c "d"''']),
            ("unclosed_quote", ["-DTEST_INVALID='a b"], None),
        )
        for project_path, target_name, script_name in PROJECTS:
            for name, payloads, expected in cases:
                with self.subTest(project=project_path, case=name):
                    with tempfile.TemporaryDirectory(prefix="cmake args ") as directory:
                        directory = Path(directory)
                        cache = directory / "initial cache.cmake"
                        cache.write_text('set(FROM_CACHE yes CACHE STRING "")\n')
                        payloads = [value.format(cache=cache) for value in payloads]
                        if expected is not None:
                            expected = [value.format(cache=cache) for value in expected]
                        (directory / "CMakeLists.txt").write_text(
                            "cmake_minimum_required(VERSION 3.20)\n"
                            "project(ArgumentForwarding NONE)\n"
                        )
                        wrapper = directory / script_name
                        wrapper.write_text(WRAPPER)
                        wrapper.chmod(0o755)
                        self.write_project(directory, project_path, target_name)
                        common = directory / "eng" / "common"
                        common.mkdir(parents=True)
                        (common / "native").symlink_to(REPO_ROOT / "eng" / "common" / "native")
                        shutil.copyfile(REPO_ROOT / "eng" / "build.sh", directory / "eng" / "build.sh")
                        entrypoint = common / "build.sh"
                        entrypoint.write_text(
                            '#!/usr/bin/env bash\nset -e\n'
                            'for arg in "$@"; do\n'
                            '    case "$arg" in /p:CMakeArgs=*) cmake_property="$arg";; esac\n'
                            'done\n'
                            'exec "$TEST_DOTNET" msbuild "$TEST_PROJECT" -nologo -v:minimal "$cmake_property"\n'
                        )
                        entrypoint.chmod(0o755)
                        environment = os.environ.copy()
                        environment.update(
                            TEST_REPO_ROOT=str(REPO_ROOT),
                            TEST_DOTNET=str(DOTNET),
                            TEST_PROJECT=str(directory / "test.proj"),
                            TEST_CAPTURE=str(directory / "arguments"),
                            TEST_SOURCE=str(directory),
                            TEST_BINARY=str(directory / "build"),
                        )
                        result = subprocess.run(
                            ["bash", str(directory / "eng" / "build.sh"),
                             *[arg for payload in payloads for arg in ("--cmakeargs", payload)]],
                            env=environment, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                        )
                        if expected is None:
                            self.assertNotEqual(result.returncode, 0, result.stdout)
                            self.assertFalse((directory / "arguments").exists(), result.stdout)
                            continue
                        self.assertEqual(result.returncode, 0, result.stdout)
                        arguments = (directory / "arguments").read_bytes().decode().rstrip("\0").split("\0")
                        user_arguments = [
                            arg for arg in arguments if arg in expected
                            or arg.startswith(("-DFIRST=", "-DSECOND=", "-DCMAKE_C_FLAGS=",
                                               "-DANDROID_CPP_FEATURES=", "-DTEST_"))
                        ]
                        self.assertEqual(user_arguments, expected, arguments)
                        if project_path == "src/coreclr/runtime.proj":
                            self.assertIn(f"-DCLR_DOTNET_HOST_PATH={directory}/dotnet", arguments)
                        cmake_cache = (directory / "build" / "CMakeCache.txt").read_text()
                        if name == "duplicate":
                            self.assertIn("FIRST:UNINITIALIZED=2\n", cmake_cache)
                        if name == "cache":
                            self.assertIn("FROM_CACHE:STRING=yes\n", cmake_cache)

    def write_project(self, directory, project_path, target_name):
        source = ET.parse(REPO_ROOT / project_path).getroot()
        project = ET.Element("Project", DefaultTargets="Test")
        properties = ET.SubElement(project, "PropertyGroup")
        for name, value in {
            "Configuration": "Debug",
            "HostOS": "osx",
            "TargetOS": "osx",
            "TargetArchitecture": "arm64",
            "TargetRid": "osx-arm64",
            "PortableTargetRid": "osx-arm64",
            "PortableBuild": "true",
            "RuntimeFlavor": "CoreCLR",
            "NetCoreAppCurrent": "net11.0",
            "Ninja": "true",
            "BuildNativeHostTests": "false",
            "BuildNativeHostProduct": "true",
            "DOTNET_HOST_PATH": str(directory / "dotnet"),
        }.items():
            ET.SubElement(properties, name).text = value
        # Evaluate the real argument-building groups without unrelated SDK/build prerequisites.
        for group in source:
            if group.tag in ("PropertyGroup", "ItemGroup") and group.find("ProjectReference") is None:
                project.append(copy.deepcopy(group))
        target = ET.SubElement(project, "Target", Name="Test")
        for group in source.find(f"Target[@Name='{target_name}']"):
            if group.tag in ("PropertyGroup", "ItemGroup"):
                target.append(copy.deepcopy(group))
        command = source.find(f"Target[@Name='{target_name}']/Exec").get("Command")
        ET.SubElement(target, "Exec", Command=command)
        ET.ElementTree(project).write(directory / "test.proj", encoding="unicode")


if __name__ == "__main__":
    unittest.main()
