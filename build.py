#!/usr/bin/env python3
"""Build a self-contained SeekClaw Runtime and Windows Desktop release."""

from __future__ import annotations

import argparse
import json
import os
import re 
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Sequence


REPO_ROOT = Path(__file__).resolve().parent
DESKTOP_DIR = REPO_ROOT / "seekclaw_desktop"
RUNTIME_STAGE = DESKTOP_DIR / "runtime" / "win-x64"
BUILDER_OUTPUT = DESKTOP_DIR / "release"
UNPACKED_OUTPUT = BUILDER_OUTPUT / "win-unpacked"
PUBLISH_DIR = REPO_ROOT / "publish"
PORTABLE_OUTPUT = PUBLISH_DIR / "SeekClaw-win-x64"
INSTALLER_OUTPUT = PUBLISH_DIR / "SeekClaw-Setup-win-x64.exe"
DESKTOP_PACKAGE_FILE = DESKTOP_DIR / "package.json"
VERSION_PATTERN = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
PORTABLE_TARGET = "portable"
INSTALLER_TARGET = "installer"


class BuildError(RuntimeError):
    """Raised when a release prerequisite or output is missing."""


def read_desktop_version(package_file: Path = DESKTOP_PACKAGE_FILE) -> str:
    try:
        package = json.loads(package_file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise BuildError(f"Could not read Desktop package metadata: {package_file}") from error

    version = package.get("version")
    if not isinstance(version, str) or not VERSION_PATTERN.fullmatch(version):
        raise BuildError(
            f"Desktop version must use major.minor.patch format, found: {version!r}"
        )
    return version


def next_patch_version(version: str) -> str:
    match = VERSION_PATTERN.fullmatch(version)
    if not match:
        raise BuildError(f"Desktop version must use major.minor.patch format, found: {version!r}")
    major, minor, patch = (int(part) for part in match.groups())
    return f"{major}.{minor}.{patch + 1}"


def write_desktop_version(version: str, package_file: Path = DESKTOP_PACKAGE_FILE) -> None:
    if not VERSION_PATTERN.fullmatch(version):
        raise BuildError(f"Desktop version must use major.minor.patch format, found: {version!r}")

    try:
        contents = package_file.read_text(encoding="utf-8")
        package = json.loads(contents)
    except (OSError, json.JSONDecodeError) as error:
        raise BuildError(f"Could not read Desktop package metadata: {package_file}") from error

    current_version = package.get("version")
    if not isinstance(current_version, str):
        raise BuildError(f"Desktop package metadata has no string version: {package_file}")

    pattern = re.compile(
        rf'(?m)^(\s*"version"\s*:\s*)"{re.escape(current_version)}"(\s*,\s*)$'
    )
    updated, replacements = pattern.subn(rf'\g<1>"{version}"\g<2>', contents, count=1)
    if replacements != 1:
        raise BuildError(f"Could not update Desktop version in: {package_file}")

    try:
        package_file.write_text(updated, encoding="utf-8")
    except OSError as error:
        raise BuildError(f"Could not write Desktop package metadata: {package_file}") from error


def workspace_path(path: Path) -> Path:
    resolved = path.resolve()
    try:
        resolved.relative_to(REPO_ROOT)
    except ValueError as error:
        raise BuildError(f"Refusing to modify a path outside the repository: {resolved}") from error
    if resolved == REPO_ROOT:
        raise BuildError("Refusing to modify the repository root.")
    return resolved


def remove_directory(path: Path) -> None:
    target = workspace_path(path)
    if target.exists():
        shutil.rmtree(target)


def remove_file(path: Path) -> None:
    target = workspace_path(path)
    if target.exists():
        if not target.is_file():
            raise BuildError(f"Expected a file but found a different path: {target}")
        target.unlink()


def reset_directory(path: Path) -> None:
    target = workspace_path(path)
    remove_directory(target)
    target.mkdir(parents=True, exist_ok=True)


def require_command(name: str) -> str:
    command = shutil.which(name)
    if not command:
        raise BuildError(f"Required command was not found in PATH: {name}")
    return command


def run(command: str, arguments: Sequence[str], cwd: Path, env: dict[str, str]) -> None:
    printable = subprocess.list2cmdline([command, *arguments])
    print(f"\n> {printable}", flush=True)
    subprocess.run([command, *arguments], cwd=cwd, env=env, check=True)


def package_desktop(
    pnpm: str, env: dict[str, str], build_target: str, attempts: int = 3
) -> None:
    if build_target == PORTABLE_TARGET:
        electron_target = "dir"
    elif build_target == INSTALLER_TARGET:
        electron_target = "nsis"
    else:
        raise BuildError(f"Unknown Desktop build target: {build_target}")

    arguments = ["exec", "electron-builder", "--win", electron_target, "--x64"]
    for attempt in range(1, attempts + 1):
        try:
            run(pnpm, arguments, DESKTOP_DIR, env)
            return
        except subprocess.CalledProcessError:
            if attempt == attempts:
                raise
            print(
                f"\nElectron packaging failed (attempt {attempt}/{attempts}); "
                "retrying after a short delay...",
                file=sys.stderr,
                flush=True,
            )
            remove_directory(BUILDER_OUTPUT)
            time.sleep(3 * attempt)


def find_installer_artifact(version: str) -> Path:
    expected = BUILDER_OUTPUT / f"SeekClaw Setup {version}.exe"
    if expected.is_file():
        return expected

    candidates = sorted(
        path for path in BUILDER_OUTPUT.glob("*.exe") if path.is_file()
    )
    versioned_candidates = [path for path in candidates if version in path.stem]
    if len(versioned_candidates) == 1:
        return versioned_candidates[0]
    if len(candidates) == 1:
        return candidates[0]
    if not candidates:
        raise BuildError(f"Installer executable was not found in: {BUILDER_OUTPUT}")
    names = ", ".join(path.name for path in candidates)
    raise BuildError(f"Could not identify a unique installer executable: {names}")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build the latest self-contained Runtime and Desktop release."
    )
    parser.add_argument("--skip-tests", action="store_true", help="Skip .NET and Desktop tests.")
    parser.add_argument("--skip-install", action="store_true", help="Skip pnpm install.")
    return parser.parse_args()


def prompt_build_target() -> str:
    prompt = (
        "\n请选择构建类型：\n"
        "1. 免安装版（可直接运行的文件夹）\n"
        "2. 安装版（NSIS 安装包）\n"
        "请输入 1 或 2: "
    )
    while True:
        try:
            choice = input(prompt).strip()
        except (EOFError, KeyboardInterrupt) as error:
            raise BuildError("未选择构建类型，构建已取消。") from error

        if choice == "1":
            return PORTABLE_TARGET
        if choice == "2":
            return INSTALLER_TARGET
        print("输入无效，请输入 1 或 2。", file=sys.stderr, flush=True)


def main() -> int:
    args = parse_arguments()
    build_target = prompt_build_target()
    dotnet = require_command("dotnet")
    pnpm = require_command("pnpm")
    build_env = os.environ.copy()
    if not build_env.get("ELECTRON_MIRROR", "").strip():
        build_env["ELECTRON_MIRROR"] = "https://npmmirror.com/mirrors/electron/"
    if not build_env.get("ELECTRON_BUILDER_BINARIES_MIRROR", "").strip():
        build_env["ELECTRON_BUILDER_BINARIES_MIRROR"] = (
            "https://npmmirror.com/mirrors/electron-builder-binaries/"
        )

    previous_version = read_desktop_version()
    release_version = next_patch_version(previous_version)
    version_committed = False
    write_desktop_version(release_version)
    print(f"Desktop version: {previous_version} -> {release_version}")

    try:
        reset_directory(RUNTIME_STAGE)
        if build_target == PORTABLE_TARGET:
            reset_directory(PORTABLE_OUTPUT)
        else:
            PUBLISH_DIR.mkdir(parents=True, exist_ok=True)
            remove_file(INSTALLER_OUTPUT)
        remove_directory(BUILDER_OUTPUT)

        if not args.skip_install:
            run(pnpm, ["install", "--frozen-lockfile"], DESKTOP_DIR, build_env)

        if not args.skip_tests:
            run(dotnet, ["test", "SeekClaw.slnx", "-c", "Release"], REPO_ROOT, build_env)
            run(pnpm, ["test"], DESKTOP_DIR, build_env)

        run(
            dotnet,
            [
                "publish",
                "seekclaw_cli/seekclaw_cli.csproj",
                "-c",
                "Release",
                "-r",
                "win-x64",
                "--self-contained",
                "true",
                "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:DebugType=None",
                "-p:DebugSymbols=false",
                "-o",
                str(RUNTIME_STAGE),
            ],
            REPO_ROOT,
            build_env,
        )

        run(pnpm, ["build"], DESKTOP_DIR, build_env)
        package_desktop(pnpm, build_env, build_target)

        if not UNPACKED_OUTPUT.is_dir():
            raise BuildError(f"Electron builder output was not found: {UNPACKED_OUTPUT}")

        desktop_executable = UNPACKED_OUTPUT / "SeekClaw.exe"
        runtime_executable = UNPACKED_OUTPUT / "resources" / "runtime" / "seekclaw.exe"
        if not desktop_executable.is_file():
            raise BuildError(f"Desktop executable is missing: {desktop_executable}")
        if not runtime_executable.is_file():
            raise BuildError(f"Bundled Runtime executable is missing: {runtime_executable}")

        if build_target == PORTABLE_TARGET:
            shutil.copytree(UNPACKED_OUTPUT, PORTABLE_OUTPUT, dirs_exist_ok=True)
            desktop_executable = PORTABLE_OUTPUT / "SeekClaw.exe"
            runtime_executable = PORTABLE_OUTPUT / "resources" / "runtime" / "seekclaw.exe"
            if not desktop_executable.is_file():
                raise BuildError(f"Desktop executable is missing: {desktop_executable}")
            if not runtime_executable.is_file():
                raise BuildError(f"Bundled Runtime executable is missing: {runtime_executable}")
            release_output = PORTABLE_OUTPUT
        else:
            installer_artifact = find_installer_artifact(release_version)
            shutil.copy2(installer_artifact, INSTALLER_OUTPUT)
            if not INSTALLER_OUTPUT.is_file():
                raise BuildError(f"Installer executable is missing: {INSTALLER_OUTPUT}")
            release_output = INSTALLER_OUTPUT

        version_committed = True
        if build_target == PORTABLE_TARGET:
            print(f"\nSeekClaw {release_version} portable release is ready:\n{release_output}")
            print(f"Run: {desktop_executable}")
        else:
            print(f"\nSeekClaw {release_version} installer is ready:\n{release_output}")
        return 0
    finally:
        if not version_committed:
            write_desktop_version(previous_version)
            print(
                f"Restored Desktop version to {previous_version} because the build did not finish.",
                file=sys.stderr,
            )
        remove_directory(DESKTOP_DIR / "runtime")
        remove_directory(BUILDER_OUTPUT)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (BuildError, subprocess.CalledProcessError, OSError) as error:
        print(f"\nBuild failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
