#!/usr/bin/env python3
"""Build and package the SeekClaw .NET CLI for npm.

This script intentionally targets only `seekclaw_cli`. It never builds or
modifies `seekclaw_desktop` or any other runtime/webserver component.
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Sequence


for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass


try:
    from rich.console import Console
    from rich.panel import Panel
    from rich.table import Table
    from rich.text import Text

    console = Console()
    HAS_UI = True
except ImportError:  # pragma: no cover - fallback for minimal environments
    HAS_UI = False

    def _strip_rich_markup(text: str) -> str:
        return re.sub(r"\[/?[a-zA-Z][a-zA-Z #0-9_\-]*\]", "", text)

    class _Status:
        def __enter__(self):
            return self

        def __exit__(self, *exc_info):
            return False

    class _Console:
        def print(self, message="", **kwargs):
            text = _strip_rich_markup(message) if isinstance(message, str) else str(message)
            print(text)

        def clear(self):
            pass

        def status(self, message="", spinner="dots"):
            return _Status()

    console = _Console()

    def Panel(content="", title="", border_style=None, **kwargs):  # noqa: N802
        rendered = str(content)
        return f"──── {title} ────\n{rendered}" if title else rendered

    class _Table:
        def __init__(self, title="", **kwargs):
            self._title = title
            self._columns: list[str] = []
            self._rows: list[list[str]] = []

        def add_column(self, name, **kwargs):
            self._columns.append(name)

        def add_row(self, *cells):
            self._rows.append([_strip_rich_markup(str(cell)) for cell in cells])

        def __str__(self):
            if not self._columns:
                return self._title or ""
            widths = [len(col) for col in self._columns]
            for row in self._rows:
                for index, cell in enumerate(row):
                    widths[index] = max(widths[index], len(cell))
            lines = [self._title] if self._title else []
            lines.append(" | ".join(col.ljust(widths[i]) for i, col in enumerate(self._columns)))
            lines.append("-+-".join("-" * width for width in widths))
            for row in self._rows:
                lines.append(" | ".join(cell.ljust(widths[i]) for i, cell in enumerate(row)))
            return "\n".join(lines)

    def Table(title="", **kwargs):  # noqa: N802
        return _Table(title=title)

    def Text(text, style=None):  # noqa: N802
        return text


REPO_ROOT = Path(__file__).resolve().parent
CLI_DIR = REPO_ROOT / "seekclaw_cli"
CLI_CSPROJ = CLI_DIR / "seekclaw_cli.csproj"
CLI_PROMPTS = CLI_DIR / "prompts"
NPM_ROOT = REPO_ROOT / "packaging" / "npm"
STAGING_ROOT = NPM_ROOT / "staging"
BUILD_ROOT = STAGING_ROOT / "build"
PACKAGES_ROOT = STAGING_ROOT / "packages"
TARBALL_ROOT = STAGING_ROOT / "tarballs"

MAIN_PACKAGE_NAME = "seekclaw-cli"
PACKAGE_DESCRIPTION = "SeekClaw AI coding agent CLI, distributed as a self-contained .NET binary."
PACKAGE_LICENSE = "SEE LICENSE IN LICENSE"

VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+$")

RID_INFO: dict[str, dict[str, str]] = {
    "win-x64": {"os": "win32", "cpu": "x64", "exe": "seekclaw.exe"},
    "win-arm64": {"os": "win32", "cpu": "arm64", "exe": "seekclaw.exe"},
    "linux-x64": {"os": "linux", "cpu": "x64", "exe": "seekclaw"},
    "linux-arm64": {"os": "linux", "cpu": "arm64", "exe": "seekclaw"},
    "osx-x64": {"os": "darwin", "cpu": "x64", "exe": "seekclaw"},
    "osx-arm64": {"os": "darwin", "cpu": "arm64", "exe": "seekclaw"},
}

WRAPPER_JS = r"""#!/usr/bin/env node
'use strict';

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

const RID_BY_PLATFORM = {
  'win32-x64': 'win-x64',
  'win32-arm64': 'win-arm64',
  'linux-x64': 'linux-x64',
  'linux-arm64': 'linux-arm64',
  'darwin-x64': 'osx-x64',
  'darwin-arm64': 'osx-arm64',
};

const platformKey = `${process.platform}-${process.arch}`;
const rid = RID_BY_PLATFORM[platformKey];

if (!rid) {
  console.error(`seekclaw-cli does not support this platform yet: ${platformKey}`);
  process.exit(1);
}

const dependencyName = `seekclaw-cli-${rid}`;
let packageRoot;

try {
  packageRoot = path.dirname(require.resolve(`${dependencyName}/package.json`));
} catch {
  console.error(`Missing binary package: ${dependencyName}`);
  console.error('Reinstall seekclaw-cli or run: npm install -g seekclaw-cli');
  process.exit(1);
}

const executableName = process.platform === 'win32' ? 'seekclaw.exe' : 'seekclaw';
const executablePath = path.join(packageRoot, 'bin', executableName);

if (!fs.existsSync(executablePath)) {
  console.error(`CLI binary not found: ${executablePath}`);
  process.exit(1);
}

const child = spawn(executablePath, process.argv.slice(2), {
  stdio: 'inherit',
  windowsHide: false,
});

child.on('error', (error) => {
  console.error(`Failed to start seekclaw: ${error.message}`);
  process.exit(1);
});

child.on('exit', (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
    return;
  }
  process.exit(code ?? 1);
});
"""


class BuildError(RuntimeError):
    """Raised when packaging prerequisites or outputs are missing."""


def require_command(name: str) -> str:
    command = shutil.which(name)
    if not command:
        raise BuildError(f"Required command was not found in PATH: {name}")
    return command


def ensure_npm_path(path: Path) -> Path:
    resolved = path.resolve()
    root = NPM_ROOT.resolve()
    try:
        resolved.relative_to(root)
    except ValueError as error:
        raise BuildError(f"Refusing to modify a path outside the npm packaging directory: {resolved}") from error
    if resolved == root:
        raise BuildError("Refusing to modify the npm packaging root.")
    return resolved


def reset_directory(path: Path) -> None:
    target = ensure_npm_path(path)
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True, exist_ok=True)


def remove_directory(path: Path) -> None:
    target = ensure_npm_path(path)
    if target.exists():
        shutil.rmtree(target)


def run(command: str, arguments: Sequence[str], cwd: Path, env: dict[str, str], verbose: bool = False) -> None:
    printable = subprocess.list2cmdline([command, *arguments])
    if verbose:
        console.print(f"[dim]> {printable}[/dim]")
        subprocess.run([command, *arguments], cwd=cwd, env=env, check=True)
        return

    result = subprocess.run(
        [command, *arguments],
        cwd=cwd,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0:
        console.print(
            f"\n[bold red]❌ 子进程命令执行失败 (退出码 {result.returncode}):[/bold red] [dim]{printable}[/dim]"
        )
        _print_failure_output(result.stderr, "Error Output (stderr)")
        _print_failure_output(result.stdout, "Standard Output (stdout)")
        console.print("[dim]提示: 使用 -v/--verbose 重新运行可查看子进程完整原始输出。[/dim]\n")
        raise subprocess.CalledProcessError(
            result.returncode, [command, *arguments], result.stdout, result.stderr
        )


def capture(command: str, arguments: Sequence[str], cwd: Path, env: dict[str, str]) -> str:
    result = subprocess.run(
        [command, *arguments],
        cwd=cwd,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip()
        raise BuildError(f"Command failed: {subprocess.list2cmdline([command, *arguments])}\n{detail}")
    return result.stdout.strip()


def _print_failure_output(text: str, title: str) -> None:
    content = text.strip()
    if not content:
        return
    lines = content.splitlines()
    if len(lines) > 200:
        content = "\n".join(lines[-200:])
        content = f"… (已省略前 {len(lines) - 200} 行，共 {len(lines)} 行) …\n" + content
    console.print(Panel(content, title=title, border_style="red"))


def read_cli_version() -> str:
    banner = CLI_DIR / "Ui" / "Banner.cs"
    if not banner.is_file():
        raise BuildError(f"CLI version file is missing: {banner}")
    match = re.search(r'public const string Version = "([^"]+)";', banner.read_text(encoding="utf-8"))
    if not match:
        raise BuildError(f"Could not read SeekClaw CLI version from: {banner}")
    version = match.group(1)
    if not VERSION_PATTERN.fullmatch(version):
        raise BuildError(f"Unsupported CLI version for npm: {version!r}")
    return version


def parse_rids(raw: str) -> list[str]:
    rids = [item.strip().lower() for item in raw.split(",") if item.strip()]
    if not rids:
        raise BuildError("At least one Runtime Identifier is required.")
    unknown = [rid for rid in rids if rid not in RID_INFO]
    if unknown:
        raise BuildError(f"Unsupported RID(s): {', '.join(unknown)}")
    return list(dict.fromkeys(rids))


def package_dir_for(rid: str | None) -> Path:
    if rid is None:
        return PACKAGES_ROOT / MAIN_PACKAGE_NAME
    return PACKAGES_ROOT / f"{MAIN_PACKAGE_NAME}-{rid}"


def write_json(path: Path, data: dict) -> None:
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def publish_dotnet(dotnet: str, rid: str, env: dict[str, str], verbose: bool) -> Path:
    output_dir = BUILD_ROOT / rid
    reset_directory(output_dir)
    arguments = [
        "publish",
        str(CLI_CSPROJ),
        "-c",
        "Release",
        "-r",
        rid,
        "--self-contained",
        "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishTrimmed=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o",
        str(output_dir),
    ]
    with console.status(f"[bold blue]正在发布 .NET 自包含 CLI: {rid}...[/bold blue]", spinner="dots"):
        run(dotnet, arguments, REPO_ROOT, env, verbose=verbose)
    console.print(f"[bold green]✓[/bold green] CLI 发布完成: [cyan]{rid}[/cyan]")
    return output_dir


def published_executable(rid: str) -> Path:
    info = RID_INFO[rid]
    executable = BUILD_ROOT / rid / info["exe"]
    if not executable.is_file():
        raise BuildError(f"Published CLI executable is missing: {executable}")
    return executable


def stage_platform_package(rid: str, version: str) -> Path:
    info = RID_INFO[rid]
    package_dir = package_dir_for(rid)
    reset_directory(package_dir)

    bin_dir = package_dir / "bin"
    bin_dir.mkdir(parents=True, exist_ok=True)
    executable = published_executable(rid)
    destination = bin_dir / info["exe"]
    shutil.copy2(executable, destination)
    if info["os"] != "win32":
        os.chmod(destination, 0o755)

    prompts_in_publish = BUILD_ROOT / rid / "prompts"
    prompts_source = prompts_in_publish if prompts_in_publish.is_dir() else CLI_PROMPTS
    if not prompts_source.is_dir():
        raise BuildError(f"CLI prompts directory is missing: {prompts_source}")
    shutil.copytree(prompts_source, package_dir / "prompts", dirs_exist_ok=True)

    copy_license_and_readme(package_dir)
    write_json(
        package_dir / "package.json",
        {
            "name": f"{MAIN_PACKAGE_NAME}-{rid}",
            "version": version,
            "description": f"SeekClaw CLI binary package for {info['os']}/{info['cpu']} ({rid}).",
            "license": PACKAGE_LICENSE,
            "os": [info["os"]],
            "cpu": [info["cpu"]],
            "files": [
                "bin/**/*",
                "prompts/**/*",
                "LICENSE",
                "README.md",
            ],
            "keywords": ["seekclaw", "cli", "ai", "coding-agent", "dotnet"],
        },
    )
    console.print(f"[bold green]✓[/bold green] 平台包已生成: [cyan]{package_dir.name}[/cyan]")
    return package_dir


def stage_main_package(version: str, rids: list[str]) -> Path:
    package_dir = package_dir_for(None)
    reset_directory(package_dir)

    bin_dir = package_dir / "bin"
    bin_dir.mkdir(parents=True, exist_ok=True)
    (bin_dir / "seekclaw.js").write_text(WRAPPER_JS.lstrip(), encoding="utf-8")

    copy_license_and_readme(package_dir)
    optional_dependencies = {f"{MAIN_PACKAGE_NAME}-{rid}": version for rid in rids}
    write_json(
        package_dir / "package.json",
        {
            "name": MAIN_PACKAGE_NAME,
            "version": version,
            "description": PACKAGE_DESCRIPTION,
            "license": PACKAGE_LICENSE,
            "bin": {"seekclaw": "bin/seekclaw.js"},
            "files": [
                "bin/seekclaw.js",
                "LICENSE",
                "README.md",
            ],
            "optionalDependencies": optional_dependencies,
            "preferGlobal": True,
            "engines": {"node": ">=18"},
            "keywords": ["seekclaw", "cli", "ai", "coding-agent", "dotnet"],
        },
    )
    console.print("[bold green]✓[/bold green] npm 主包已生成: [cyan]seekclaw-cli[/cyan]")
    return package_dir


def copy_license_and_readme(package_dir: Path) -> None:
    license_file = REPO_ROOT / "LICENSE"
    readme_file = REPO_ROOT / "README.md"
    if not license_file.is_file():
        raise BuildError(f"License file is missing: {license_file}")
    if not readme_file.is_file():
        raise BuildError(f"README file is missing: {readme_file}")
    shutil.copy2(license_file, package_dir / "LICENSE")
    shutil.copy2(readme_file, package_dir / "README.md")


def dry_run_packages(npm: str, package_dirs: Sequence[Path], env: dict[str, str], verbose: bool) -> None:
    with console.status("[bold blue]正在执行 npm pack --dry-run 校验...[/bold blue]", spinner="dots"):
        for package_dir in package_dirs:
            run(npm, ["pack", "--dry-run"], package_dir, env, verbose=verbose)
    console.print("[bold green]✓[/bold green] npm 打包校验通过")


def pack_packages(npm: str, package_dirs: Sequence[Path], env: dict[str, str], verbose: bool) -> list[Path]:
    reset_directory(TARBALL_ROOT)
    outputs: list[Path] = []
    with console.status("[bold blue]正在生成 npm tarball...[/bold blue]", spinner="dots"):
        for package_dir in package_dirs:
            run(npm, ["pack", "--pack-destination", str(TARBALL_ROOT)], package_dir, env, verbose=verbose)
    for archive in sorted(TARBALL_ROOT.glob("*.tgz")):
        outputs.append(archive)
    if not outputs:
        raise BuildError("npm pack did not produce any .tgz files.")
    console.print("[bold green]✓[/bold green] npm tarball 已生成")
    return outputs


def publish_packages(
    npm: str,
    package_dirs: Sequence[Path],
    env: dict[str, str],
    registry: str | None,
    otp: str | None,
    verbose: bool,
) -> None:
    identity = capture(npm, ["whoami"], NPM_ROOT, env)
    console.print(f"[bold green]✓[/bold green] npm 登录身份: [cyan]{identity}[/cyan]")

    for package_dir in package_dirs:
        arguments = ["publish", str(package_dir)]
        if registry:
            arguments += ["--registry", registry]
        if otp:
            arguments += ["--otp", otp]
        with console.status(f"[bold blue]正在发布 {package_dir.name}...[/bold blue]", spinner="dots"):
            run(npm, arguments, package_dir, env, verbose=verbose)
        console.print(f"[bold green]✓[/bold green] 已发布: [cyan]{package_dir.name}[/cyan]")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Package and optionally publish the SeekClaw .NET CLI to npm."
    )
    parser.add_argument(
        "--rids",
        default="win-x64",
        help="Comma-separated .NET RIDs to publish (default: win-x64).",
    )
    parser.add_argument("--version", help="Override the package version read from Banner.cs.")
    parser.add_argument("--skip-tests", action="store_true", help="Skip CLI tests before packaging.")
    parser.add_argument("--skip-build", action="store_true", help="Reuse previously published binaries in staging/build.")
    parser.add_argument(
        "--pack",
        action="store_true",
        help="Create npm tarballs in packaging/npm/staging/tarballs instead of only dry-running.",
    )
    parser.add_argument(
        "--publish",
        action="store_true",
        help="Publish platform packages and the main package to npm.",
    )
    parser.add_argument("--registry", help="Override the npm registry used for publishing.")
    parser.add_argument("--otp", help="npm one-time password for publishing.")
    parser.add_argument("-v", "--verbose", action="store_true", help="Show full stdout from subcommands.")
    return parser.parse_args()


def main() -> int:
    args = parse_arguments()

    console.clear()
    banner = Text("SeekClaw CLI npm Packager", style="bold cyan")
    console.print(Panel(banner, expand=False, border_style="cyan"))

    version = args.version or read_cli_version()
    if not VERSION_PATTERN.fullmatch(version):
        raise BuildError(f"Invalid npm version: {version!r}")

    rids = parse_rids(args.rids)
    mode = "publish" if args.publish else "pack" if args.pack else "dry-run"

    dotnet = require_command("dotnet")
    npm = require_command("npm")
    env = os.environ.copy()

    info = Table(title="📦 npm 发布信息", border_style="cyan", header_style="bold cyan")
    info.add_column("属性", style="bold cyan")
    info.add_column("值", style="white")
    info.add_row("主包名", MAIN_PACKAGE_NAME)
    info.add_row("版本", f"[bold yellow]{version}[/bold yellow]")
    info.add_row("目标 RID", ", ".join(rids))
    info.add_row("运行模式", mode)
    info.add_row("发布范围", "仅 seekclaw_cli，不触碰 Desktop")
    console.print(info)
    console.print()

    start_time = time.time()

    NPM_ROOT.mkdir(parents=True, exist_ok=True)
    STAGING_ROOT.mkdir(parents=True, exist_ok=True)
    if not args.skip_build:
        reset_directory(BUILD_ROOT)
    reset_directory(PACKAGES_ROOT)

    try:
        if not args.skip_tests:
            with console.status("[bold blue]正在运行 CLI 单元测试...[/bold blue]", spinner="dots"):
                run(
                    dotnet,
                    ["test", "seekclaw_cli_tests/seekclaw_cli_tests.csproj", "-c", "Release"],
                    REPO_ROOT,
                    env,
                    verbose=args.verbose,
                )
            console.print("[bold green]✓[/bold green] CLI 测试通过")

        if not args.skip_build:
            for rid in rids:
                publish_dotnet(dotnet, rid, env, args.verbose)
        else:
            for rid in rids:
                published_executable(rid)
            console.print("[bold green]✓[/bold green] 已复用现有 CLI 发布产物")

        platform_dirs = [stage_platform_package(rid, version) for rid in rids]
        main_dir = stage_main_package(version, rids)
        package_dirs = [*platform_dirs, main_dir]

        dry_run_packages(npm, package_dirs, env, args.verbose)

        tarballs: list[Path] = []
        if args.pack or args.publish:
            tarballs = pack_packages(npm, package_dirs, env, args.verbose)

        if args.publish:
            publish_packages(npm, package_dirs, env, args.registry, args.otp, args.verbose)

        elapsed = time.time() - start_time
        summary = Table(title="🎉 SeekClaw CLI npm 打包完成", border_style="green", header_style="bold green")
        summary.add_column("属性", style="bold cyan")
        summary.add_column("详情", style="white")
        summary.add_row("模式", mode)
        summary.add_row("版本", f"[bold yellow]{version}[/bold yellow]")
        summary.add_row("包目录", str(PACKAGES_ROOT))
        for package_dir in package_dirs:
            summary.add_row("包", package_dir.name)
        for tarball in tarballs:
            summary.add_row("Tarball", str(tarball))
        summary.add_row("总计耗时", f"{elapsed:.1f} 秒")
        console.print()
        console.print(summary)
        return 0

    finally:
        if not args.skip_build:
            remove_directory(BUILD_ROOT)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (BuildError, subprocess.CalledProcessError, OSError) as error:
        console.print(f"\n[bold red]❌ npm 打包过程异常终止:[/bold red] {error}")
        raise SystemExit(1) from error
