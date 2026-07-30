#!/usr/bin/env python3
"""Build a self-contained SeekClaw Runtime and portable Windows Desktop folder."""

from __future__ import annotations

import argparse
import os
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
FINAL_OUTPUT = REPO_ROOT / "publish" / "SeekClaw-win-x64"


class BuildError(RuntimeError):
    """Raised when a release prerequisite or output is missing."""


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


def package_desktop(pnpm: str, env: dict[str, str], attempts: int = 3) -> None:
    arguments = ["exec", "electron-builder", "--win", "dir", "--x64"]
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


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build the latest self-contained Runtime and Desktop into one portable folder."
    )
    parser.add_argument("--skip-tests", action="store_true", help="Skip .NET and Desktop tests.")
    parser.add_argument("--skip-install", action="store_true", help="Skip pnpm install.")
    return parser.parse_args()


def main() -> int:
    args = parse_arguments()
    dotnet = require_command("dotnet")
    pnpm = require_command("pnpm")
    build_env = os.environ.copy()
    if not build_env.get("ELECTRON_MIRROR", "").strip():
        build_env["ELECTRON_MIRROR"] = "https://npmmirror.com/mirrors/electron/"
    if not build_env.get("ELECTRON_BUILDER_BINARIES_MIRROR", "").strip():
        build_env["ELECTRON_BUILDER_BINARIES_MIRROR"] = (
            "https://npmmirror.com/mirrors/electron-builder-binaries/"
        )

    reset_directory(RUNTIME_STAGE)
    reset_directory(FINAL_OUTPUT)
    remove_directory(BUILDER_OUTPUT)

    try:
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
        package_desktop(pnpm, build_env)

        if not UNPACKED_OUTPUT.is_dir():
            raise BuildError(f"Electron builder output was not found: {UNPACKED_OUTPUT}")
        shutil.copytree(UNPACKED_OUTPUT, FINAL_OUTPUT, dirs_exist_ok=True)

        desktop_executable = FINAL_OUTPUT / "SeekClaw.exe"
        runtime_executable = FINAL_OUTPUT / "resources" / "runtime" / "seekclaw.exe"
        if not desktop_executable.is_file():
            raise BuildError(f"Desktop executable is missing: {desktop_executable}")
        if not runtime_executable.is_file():
            raise BuildError(f"Bundled Runtime executable is missing: {runtime_executable}")

        print(f"\nSeekClaw release is ready:\n{FINAL_OUTPUT}")
        print(f"Run: {desktop_executable}")
        return 0
    finally:
        remove_directory(DESKTOP_DIR / "runtime")
        remove_directory(BUILDER_OUTPUT)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (BuildError, subprocess.CalledProcessError, OSError) as error:
        print(f"\nBuild failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
