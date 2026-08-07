#!/usr/bin/env python3
"""Build a self-contained SeekClaw Runtime and Windows Desktop release with enhanced CLI UI/UX."""

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

# 中文 Windows 默认控制台/管道编码可能是 GBK，无法编码 emoji，会直接崩溃；
# 统一强制 UTF-8 输出（errors="replace" 兜底），保证在任意终端/重定向下可用。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

# 引入 Rich 与 Questionary 提升 UI/UX 体验；未安装时自动降级为纯文本 UI
try:
    import questionary
    from rich.console import Console
    from rich.panel import Panel
    from rich.table import Table
    from rich.text import Text

    console = Console()
    HAS_UI = True
except ImportError:  # pragma: no cover - 标准库降级，保证构建脚本在任何环境可用
    questionary = None
    HAS_UI = False

    def _strip_rich_markup(text: str) -> str:
        return re.sub(r"\[/?[a-zA-Z][a-zA-Z #0-9_\-]*\]", "", text)

    class _NullContext:
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
            return _NullContext()

    console = _Console()

    def Panel(content="", title="", border_style=None):  # noqa: N802 (rich 兼容 API)
        rendered = str(content)
        if title:
            return f"──── {title} ────\n{rendered}"
        return rendered

    class _Table:
        def __init__(self, title="", **kwargs):
            self._title = title
            self._columns = []
            self._rows = []

        def add_column(self, name, **kwargs):
            self._columns.append(name)

        def add_row(self, *cells):
            self._rows.append([_strip_rich_markup(str(cell)) for cell in cells])

        def __str__(self):
            if not self._columns:
                return self._title or ""
            widths = [len(col) for col in self._columns]
            for row in self._rows:
                for i, cell in enumerate(row):
                    widths[i] = max(widths[i], len(cell))
            lines = []
            if self._title:
                lines.append(self._title)
            lines.append(" | ".join(col.ljust(widths[i]) for i, col in enumerate(self._columns)))
            lines.append("-+-".join("-" * w for w in widths))
            for row in self._rows:
                lines.append(" | ".join(cell.ljust(widths[i]) for i, cell in enumerate(row)))
            return "\n".join(lines)

    def Table(title="", **kwargs):
        return _Table(title=title)

    def Text(text, style=None):
        return text

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

def run(command: str, arguments: Sequence[str], cwd: Path, env: dict[str, str], verbose: bool = False) -> None:
    """运行子进程。默认静默刷屏输出，失败时保留并显示 stderr 与 stdout 详情。"""
    printable = subprocess.list2cmdline([command, *arguments])

    if verbose:
        console.print(f"[dim]> {printable}[/dim]")
        subprocess.run([command, *arguments], cwd=cwd, env=env, check=True)
        return

    # ✅ 显式指定 encoding="utf-8" 并设置 errors="replace" 防止遇到非标准字符时崩溃
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
            f"\n[bold red]❌ 子进程命令执行失败 (退出码 {result.returncode}):[/bold red] "
            f"[dim]{printable}[/dim]"
        )
        _print_failure_output(result.stderr, "Error Output (stderr)")
        _print_failure_output(result.stdout, "Standard Output (stdout)")
        console.print(
            "[dim]提示: 使用 -v/--verbose 重新运行可查看子进程完整原始输出。[/dim]\n"
        )
        raise subprocess.CalledProcessError(
            result.returncode, [command, *arguments], result.stdout, result.stderr
        )


MAX_ERROR_LINES = 200


def _print_failure_output(text: str, title: str) -> None:
    """打印子进程失败时的输出；内容过长时仅保留尾部并标注截断行数。"""
    content = text.strip()
    if not content:
        return
    lines = content.splitlines()
    if len(lines) > MAX_ERROR_LINES:
        truncated = len(lines) - MAX_ERROR_LINES
        content = "\n".join(lines[-MAX_ERROR_LINES:])
        content = f"… (已省略前 {truncated} 行，共 {len(lines)} 行) …\n" + content
    console.print(Panel(content, title=title, border_style="red"))


def package_desktop(
    pnpm: str, env: dict[str, str], build_target: str, attempts: int = 3, verbose: bool = False
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
            run(pnpm, arguments, DESKTOP_DIR, env, verbose=verbose)
            return
        except subprocess.CalledProcessError:
            if attempt == attempts:
                raise
            console.print(
                f"[yellow]⚠️ Electron 打包失败 (第 {attempt}/{attempts} 次尝试); 正在清理并准备重试...[/yellow]"
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
    parser.add_argument("-v", "--verbose", action="store_true", help="Show full stdout from subcommands.")
    parser.add_argument(
        "--target",
        choices=[PORTABLE_TARGET, INSTALLER_TARGET],
        help="构建目标；省略时以交互菜单选择。",
    )
    parser.add_argument(
        "--keep-output",
        action="store_true",
        help="构建结束后保留 staging / electron-builder 输出目录（默认清理）。",
    )
    return parser.parse_args()


def prompt_build_target() -> str:
    """使用 Questionary 库提供交互式键盘光标选择菜单；未安装时退回文本选择。"""
    if questionary is None:
        return _prompt_build_target_stdlib()
    try:
        choice = questionary.select(
            "请选择构建类型：",
            choices=[
                questionary.Choice("📦 免安装版 (Portable 绿色解压文件夹)", value=PORTABLE_TARGET),
                questionary.Choice("💿 安装包版 (NSIS 可执行安装程序)", value=INSTALLER_TARGET),
            ],
            style=questionary.Style([
                ('qmark', 'fg:#00ffff bold'),
                ('question', 'bold'),
                ('pointer', 'fg:#00ff00 bold'),
                ('highlighted', 'fg:#00ff00 bold'),
            ])
        ).ask()
    except (EOFError, KeyboardInterrupt):
        choice = None

    if not choice:
        raise BuildError("未选择构建类型，构建已取消。")
    return choice


def _prompt_build_target_stdlib() -> str:
    """未安装 questionary 时的纯文本选择菜单。"""
    print("\n请选择构建类型：")
    print(f"1. 📦 免安装版 (Portable 绿色解压文件夹) [{PORTABLE_TARGET}]")
    print(f"2. 💿 安装包版 (NSIS 可执行安装程序) [{INSTALLER_TARGET}]")
    while True:
        try:
            raw = input("请输入 1 或 2: ").strip()
        except (EOFError, KeyboardInterrupt):
            raise BuildError("未选择构建类型，构建已取消。")
        if raw in ("1", PORTABLE_TARGET):
            return PORTABLE_TARGET
        if raw in ("2", INSTALLER_TARGET):
            return INSTALLER_TARGET
        print("无效输入，请重新输入 1 或 2。")


def main() -> int:
    args = parse_arguments()
    
    # 顶部 UI Banner 渲染
    console.clear()
    banner = Text("SeekClaw Runtime & Desktop Release Builder", style="bold cyan")
    console.print(Panel(banner, expand=False, border_style="cyan"))

    build_target = args.target or prompt_build_target()
    
    start_time = time.time()
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
    
    console.print(
        f"\n[bold green]✓[/bold green] 版本号更新: [dim]{previous_version}[/dim] ➔ [bold cyan]{release_version}[/bold cyan]\n"
    )

    try:
        # 1. 准备环境与工作目录
        with console.status("[bold blue]正在重置与清理构建目录...[/bold blue]", spinner="dots"):
            reset_directory(RUNTIME_STAGE)
            if build_target == PORTABLE_TARGET:
                reset_directory(PORTABLE_OUTPUT)
            else:
                PUBLISH_DIR.mkdir(parents=True, exist_ok=True)
                remove_file(INSTALLER_OUTPUT)
            remove_directory(BUILDER_OUTPUT)
        console.print("[bold green]✓[/bold green] 构建目录准备完成")

        # 2. 安装依赖
        if not args.skip_install:
            with console.status("[bold blue]正在安装前端依赖 (pnpm install)...[/bold blue]", spinner="dots"):
                run(pnpm, ["install", "--frozen-lockfile"], DESKTOP_DIR, build_env, verbose=args.verbose)
            console.print("[bold green]✓[/bold green] 前端依赖安装完成")

        # 3. 运行测试
        if not args.skip_tests:
            with console.status("[bold blue]正在运行 .NET 及桌面端单元测试...[/bold blue]", spinner="dots"):
                run(dotnet, ["test", "SeekClaw.slnx", "-c", "Release"], REPO_ROOT, build_env, verbose=args.verbose)
                run(pnpm, ["test"], DESKTOP_DIR, build_env, verbose=args.verbose)
            console.print("[bold green]✓[/bold green] 测试全部通过")

        # 4. 发布 .NET 独立运行时
        with console.status("[bold blue]正在编译与发布 .NET 自包含 Runtime...[/bold blue]", spinner="dots"):
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
                verbose=args.verbose,
            )
        console.print("[bold green]✓[/bold green] .NET Runtime 编译完成")

        # 5. 构建与打包 Electron 应用
        with console.status("[bold blue]正在构建前端组件并打包 Electron...[/bold blue]", spinner="dots"):
            run(pnpm, ["build"], DESKTOP_DIR, build_env, verbose=args.verbose)
            package_desktop(pnpm, build_env, build_target, verbose=args.verbose)
        console.print("[bold green]✓[/bold green] Electron 应用打包完成")

        # 6. 校验产物与移动定位
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
        elapsed = time.time() - start_time

        # 渲染最终构建结果摘要表格 (Summary Card)
        console.print("\n")
        table = Table(title="🎉 SeekClaw 构建成功", border_style="green", header_style="bold green")
        table.add_column("属性", style="bold cyan")
        table.add_column("详情", style="white")

        table.add_row("目标类型", "免安装版 (Portable)" if build_target == PORTABLE_TARGET else "NSIS 安装程序")
        table.add_row("发布版本", f"[bold yellow]{release_version}[/bold yellow]")
        table.add_row("输出文件/路径", f"[underline cyan]{release_output}[/underline cyan]")
        if build_target == PORTABLE_TARGET:
            table.add_row("启动入口", str(desktop_executable))
        table.add_row("总计耗时", f"{elapsed:.1f} 秒")

        console.print(table)
        return 0

    finally:
        # 回滚机制处理
        if not version_committed:
            write_desktop_version(previous_version)
            console.print(
                f"\n[bold yellow]已将 Desktop 版本恢复为 {previous_version}（因为构建未能正常完成）。[/bold yellow]"
            )
        if not args.keep_output:
            remove_directory(DESKTOP_DIR / "runtime")
            remove_directory(BUILDER_OUTPUT)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (BuildError, subprocess.CalledProcessError, OSError) as error:
        console.print(f"\n[bold red]❌ 构建过程异常终止:[/bold red] {error}")
        raise SystemExit(1) from error