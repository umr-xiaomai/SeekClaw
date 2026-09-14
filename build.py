#!/usr/bin/env python3
"""Build a self-contained SeekClaw Runtime and Desktop release for Windows and Linux."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tarfile
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

    def Panel(content="", title="", border_style=None, **kwargs):  # noqa: N802 (rich 兼容 API)
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
DESKTOP_PACKAGE_FILE = DESKTOP_DIR / "package.json"
BUILDER_OUTPUT = DESKTOP_DIR / "release"
PUBLISH_DIR = REPO_ROOT / "publish"
ICON_PNG_PATH = DESKTOP_DIR / "resources" / "logo.png"

VERSION_PATTERN = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")

PLATFORM_WINDOWS = "windows"
PLATFORM_LINUX = "linux"
PLATFORM_BOTH = "both"

# Linux 软件包标识符（deb 的 Package 字段 / rpm 的 Name 标签），与 Electron appId 保持一致。
LINUX_PACKAGE_NAME = "com.hoilai.seekclaw"

TARGET_PORTABLE = "portable"
TARGET_INSTALLER = "installer"
TARGET_BOTH = "both"
TARGET_DEB = "deb"
TARGET_RPM = "rpm"
TARGET_ALL = "all"


class BuildError(RuntimeError):
    """Raised when a release prerequisite or output is missing."""


# ==============================================================================
# RPM & DEB 打包器 (纯 Python 标准库实现，零外部依赖，跨平台生成)
# ==============================================================================

RPM_MAGIC = b"\xed\xab\xee\xdb"
RPM_HEADER_MAGIC = b"\x8e\xad\xe8\x01\x00\x00\x00\x00"

TYPE_NULL = 0
TYPE_CHAR = 1
TYPE_INT8 = 2
TYPE_INT16 = 3
TYPE_INT32 = 4
TYPE_INT64 = 5
TYPE_STRING = 6
TYPE_BIN = 7
TYPE_STRING_ARRAY = 8
TYPE_I18NSTRING = 9


class RpmHeaderBuilder:
    def __init__(self):
        self.entries = []  # list of (tag, type, value_bytes, count)

    def add_string(self, tag: int, value: str):
        val_bytes = value.encode("utf-8") + b"\x00"
        self.entries.append((tag, TYPE_STRING, val_bytes, 1))

    def add_i18n_string(self, tag: int, values: Sequence[str]):
        val_bytes = b"".join(v.encode("utf-8") + b"\x00" for v in values)
        self.entries.append((tag, TYPE_I18NSTRING, val_bytes, len(values)))

    def add_string_array(self, tag: int, values: Sequence[str]):
        val_bytes = b"".join(v.encode("utf-8") + b"\x00" for v in values)
        self.entries.append((tag, TYPE_STRING_ARRAY, val_bytes, len(values)))

    def add_int16_array(self, tag: int, values: Sequence[int]):
        val_bytes = struct.pack(f">{len(values)}H", *[v & 0xFFFF for v in values])
        self.entries.append((tag, TYPE_INT16, val_bytes, len(values)))

    def add_int32_array(self, tag: int, values: Sequence[int]):
        val_bytes = struct.pack(f">{len(values)}I", *[v & 0xFFFFFFFF for v in values])
        self.entries.append((tag, TYPE_INT32, val_bytes, len(values)))

    def add_int64_array(self, tag: int, values: Sequence[int]):
        val_bytes = struct.pack(f">{len(values)}Q", *[v & 0xFFFFFFFFFFFFFFFF for v in values])
        self.entries.append((tag, TYPE_INT64, val_bytes, len(values)))

    def add_bin(self, tag: int, data: bytes):
        self.entries.append((tag, TYPE_BIN, data, len(data)))

    def build(self) -> bytes:
        # RPM 要求索引标签必须严格按 Tag ID 升序排列
        self.entries.sort(key=lambda e: e[0])

        index_entries = []
        data_section = bytearray()

        for tag, typ, val_bytes, count in self.entries:
            if typ in (TYPE_INT16,) and len(data_section) % 2 != 0:
                data_section.extend(b"\x00" * (2 - len(data_section) % 2))
            elif typ in (TYPE_INT64,) and len(data_section) % 8 != 0:
                data_section.extend(b"\x00" * (8 - len(data_section) % 8))
            elif typ in (TYPE_INT32,) and len(data_section) % 4 != 0:
                data_section.extend(b"\x00" * (4 - len(data_section) % 4))

            offset = len(data_section)
            data_section.extend(val_bytes)
            index_entries.append((tag, typ, offset, count))

        nindex = len(index_entries)
        hsize = len(data_section)

        header_bytes = bytearray(RPM_HEADER_MAGIC) + bytearray(struct.pack(">II", nindex, hsize))

        for tag, typ, offset, count in index_entries:
            header_bytes.extend(struct.pack(">IIII", tag, typ, offset, count))

        header_bytes.extend(data_section)
        return bytes(header_bytes)


def _make_cpio_entry(path: str, data: bytes, mode: int, inode: int, mtime: int) -> bytes:
    # SVR4 new ASCII format (070701)
    path_bytes = path.encode("utf-8") + b"\x00"
    namesize = len(path_bytes)
    filesize = len(data)

    header = (
        f"070701"
        f"{inode:08x}"
        f"{mode:08x}"
        f"{0:08x}"  # uid
        f"{0:08x}"  # gid
        f"{1:08x}"  # nlink
        f"{mtime:08x}"
        f"{filesize:08x}"
        f"{3:08x}"  # maj
        f"{1:08x}"  # min
        f"{0:08x}"  # rmaj
        f"{0:08x}"  # rmin
        f"{namesize:08x}"
        f"{0:08x}"  # check
    ).encode("ascii")

    name_padding = b"\x00" * ((4 - (len(header) + len(path_bytes)) % 4) % 4)
    data_padding = b"\x00" * ((4 - len(data) % 4) % 4)
    return header + path_bytes + name_padding + data + data_padding


def create_linux_portable_tar(
    source_dir: Path,
    output_tar_path: Path,
    base_folder_name: str = "SeekClaw-linux-x64",
) -> Path:
    """将 linux-unpacked 目录打包为标准的 .tar.gz 便携包，设置正确的 Unix 文件权限。"""
    remove_file(output_tar_path)
    output_tar_path.parent.mkdir(parents=True, exist_ok=True)
    now = int(time.time())

    with gzip.GzipFile(filename="", mode="wb", fileobj=open(output_tar_path, "wb"), mtime=now) as gz:
        with tarfile.open(mode="w:", fileobj=gz) as tar:
            for root, dirs, files in os.walk(source_dir):
                rel_root = os.path.relpath(root, source_dir).replace("\\", "/")
                tar_dir = f"{base_folder_name}/{rel_root}" if rel_root != "." else base_folder_name
                dir_info = tarfile.TarInfo(name=tar_dir)
                dir_info.type = tarfile.DIRTYPE
                dir_info.mode = 0o755
                dir_info.mtime = now
                dir_info.uname = "root"
                dir_info.gname = "root"
                tar.addfile(dir_info)

                for file in files:
                    file_path = Path(root) / file
                    rel_path = os.path.relpath(file_path, source_dir).replace("\\", "/")
                    tar_file_path = f"{base_folder_name}/{rel_path}"
                    
                    is_executable = (
                        file in ("seekclaw-desktop", "seekclaw", "chrome-sandbox", "chrome_crashpad_handler")
                        or file.endswith(".so")
                        or ("runtime" in rel_path and not file.endswith(".txt"))
                    )
                    mode = 0o4755 if file == "chrome-sandbox" else (0o755 if is_executable else 0o644)

                    data = file_path.read_bytes()
                    file_info = tarfile.TarInfo(name=tar_file_path)
                    file_info.size = len(data)
                    file_info.mode = mode
                    file_info.mtime = now
                    file_info.uname = "root"
                    file_info.gname = "root"
                    tar.addfile(file_info, io.BytesIO(data))

    if not output_tar_path.is_file():
        raise BuildError(f"Portable tar.gz archive is missing: {output_tar_path}")
    return output_tar_path


def create_deb_package(
    source_dir: Path,
    output_deb_path: Path,
    version: str,
    package_name: str = LINUX_PACKAGE_NAME,
    maintainer: str = "SeekClaw <support@seekclaw.local>",
    description: str = "SeekClaw desktop client",
    icon_path: Path | None = None,
) -> Path:
    """生成符合 Debian / Ubuntu / Deepin / UOS 标准的 .deb 软件包（纯 Python 实现）。"""
    remove_file(output_deb_path)
    output_deb_path.parent.mkdir(parents=True, exist_ok=True)

    total_size_bytes = sum(f.stat().st_size for f in source_dir.rglob("*") if f.is_file())
    installed_size_kb = (total_size_bytes + 1023) // 1024

    control_content = (
        f"Package: {package_name}\n"
        f"Version: {version}\n"
        f"Section: devel\n"
        f"Priority: optional\n"
        f"Architecture: amd64\n"
        f"Maintainer: {maintainer}\n"
        f"Installed-Size: {installed_size_kb}\n"
        f"Homepage: https://github.com/umr-xiaomai/SeekClaw\n"
        f"Description: {description}\n"
    )

    postinst_content = (
        "#!/bin/sh\n"
        "set -e\n"
        "if [ -f /opt/SeekClaw/chrome-sandbox ]; then\n"
        "    chmod 4755 /opt/SeekClaw/chrome-sandbox || true\n"
        "fi\n"
        "if [ -f /opt/SeekClaw/resources/runtime/seekclaw ]; then\n"
        "    chmod 755 /opt/SeekClaw/resources/runtime/seekclaw || true\n"
        "fi\n"
        "if [ -f /opt/SeekClaw/seekclaw-desktop ]; then\n"
        "    chmod 755 /opt/SeekClaw/seekclaw-desktop || true\n"
        "fi\n"
        "if which update-desktop-database >/dev/null 2>&1; then\n"
        "    update-desktop-database -q || true\n"
        "fi\n"
        "if which gtk-update-icon-cache >/dev/null 2>&1; then\n"
        "    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true\n"
        "fi\n"
    )

    postrm_content = (
        "#!/bin/sh\n"
        "set -e\n"
        "if which update-desktop-database >/dev/null 2>&1; then\n"
        "    update-desktop-database -q || true\n"
        "fi\n"
        "if which gtk-update-icon-cache >/dev/null 2>&1; then\n"
        "    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true\n"
        "fi\n"
    )

    # 1. 生成 control.tar.gz
    control_tar_buf = io.BytesIO()
    with gzip.GzipFile(fileobj=control_tar_buf, mode="wb", mtime=0) as gz:
        with tarfile.open(fileobj=gz, mode="w:") as tar:
            def add_str_file(name: str, str_data: str, mode: int = 0o644):
                data = str_data.encode("utf-8")
                ti = tarfile.TarInfo(name=name)
                ti.size = len(data)
                ti.mode = mode
                ti.uid = 0
                ti.gid = 0
                ti.uname = "root"
                ti.gname = "root"
                ti.mtime = 0
                tar.addfile(ti, io.BytesIO(data))

            add_str_file("./control", control_content, 0o644)
            add_str_file("./postinst", postinst_content, 0o755)
            add_str_file("./postrm", postrm_content, 0o755)

    control_tar_gz = control_tar_buf.getvalue()

    # 2. 生成 data.tar.gz
    desktop_entry = (
        "[Desktop Entry]\n"
        "Name=SeekClaw\n"
        f"Comment={description}\n"
        "Exec=/opt/SeekClaw/seekclaw-desktop %U\n"
        "Terminal=false\n"
        "Type=Application\n"
        "Icon=seekclaw\n"
        "StartupWMClass=SeekClaw\n"
        "Categories=Development;\n"
    ).encode("utf-8")

    launcher_script = b"#!/bin/sh\nexec /opt/SeekClaw/seekclaw-desktop \"$@\"\n"

    data_tar_buf = io.BytesIO()
    with gzip.GzipFile(fileobj=data_tar_buf, mode="wb", mtime=0) as gz:
        with tarfile.open(fileobj=gz, mode="w:") as tar:
            def add_file_entry(tar_path: str, data: bytes, mode: int = 0o644):
                ti = tarfile.TarInfo(name=tar_path)
                ti.size = len(data)
                ti.mode = mode
                ti.uid = 0
                ti.gid = 0
                ti.uname = "root"
                ti.gname = "root"
                ti.mtime = 0
                tar.addfile(ti, io.BytesIO(data))

            def add_dir_entry(tar_path: str, mode: int = 0o755):
                ti = tarfile.TarInfo(name=tar_path)
                ti.type = tarfile.DIRTYPE
                ti.mode = mode
                ti.uid = 0
                ti.gid = 0
                ti.uname = "root"
                ti.gname = "root"
                ti.mtime = 0
                tar.addfile(ti)

            for d in [
                "./opt", "./opt/SeekClaw", "./usr", "./usr/bin", "./usr/share",
                "./usr/share/applications", "./usr/share/icons", "./usr/share/icons/hicolor",
                "./usr/share/icons/hicolor/512x512", "./usr/share/icons/hicolor/512x512/apps",
                "./usr/share/pixmaps",
            ]:
                add_dir_entry(d)

            for root, dirs, files in os.walk(source_dir):
                rel_root = os.path.relpath(root, source_dir).replace("\\", "/")
                if rel_root != ".":
                    add_dir_entry(f"./opt/SeekClaw/{rel_root}")
                for file in files:
                    file_path = os.path.join(root, file)
                    rel_path = os.path.relpath(file_path, source_dir).replace("\\", "/")
                    target_path = f"./opt/SeekClaw/{rel_path}"
                    is_executable = (
                        file in ("seekclaw-desktop", "seekclaw", "chrome-sandbox", "chrome_crashpad_handler")
                        or file.endswith(".so")
                        or ("runtime" in rel_path and not file.endswith(".txt"))
                    )
                    mode = 0o4755 if file == "chrome-sandbox" else (0o755 if is_executable else 0o644)
                    with open(file_path, "rb") as f:
                        data = f.read()
                    add_file_entry(target_path, data, mode)

            add_file_entry("./usr/share/applications/seekclaw.desktop", desktop_entry, 0o644)
            add_file_entry("./usr/bin/seekclaw", launcher_script, 0o755)
            if icon_path and icon_path.is_file():
                icon_bytes = icon_path.read_bytes()
                add_file_entry("./usr/share/icons/hicolor/512x512/apps/seekclaw.png", icon_bytes, 0o644)
                add_file_entry("./usr/share/pixmaps/seekclaw.png", icon_bytes, 0o644)

    data_tar_gz = data_tar_buf.getvalue()

    # 3. 组装标准 ar 归档
    def make_ar_header(name: str, size: int) -> bytes:
        name_field = name.ljust(16)[:16].encode("ascii")
        mtime_field = b"0           "
        uid_field = b"0     "
        gid_field = b"0     "
        mode_field = b"100644  "
        size_field = str(size).ljust(10)[:10].encode("ascii")
        magic = b"\x60\n"
        return name_field + mtime_field + uid_field + gid_field + mode_field + size_field + magic

    debian_binary_content = b"2.0\n"

    with open(output_deb_path, "wb") as f:
        f.write(b"!<arch>\n")
        # 1. debian-binary
        f.write(make_ar_header("debian-binary", len(debian_binary_content)))
        f.write(debian_binary_content)
        if len(debian_binary_content) % 2 != 0:
            f.write(b"\n")
        # 2. control.tar.gz
        f.write(make_ar_header("control.tar.gz", len(control_tar_gz)))
        f.write(control_tar_gz)
        if len(control_tar_gz) % 2 != 0:
            f.write(b"\n")
        # 3. data.tar.gz
        f.write(make_ar_header("data.tar.gz", len(data_tar_gz)))
        f.write(data_tar_gz)
        if len(data_tar_gz) % 2 != 0:
            f.write(b"\n")

    if not output_deb_path.is_file():
        raise BuildError(f"Debian package is missing: {output_deb_path}")
    return output_deb_path


def create_rpm_package(
    source_dir: Path,
    output_rpm_path: Path,
    version: str,
    package_name: str = LINUX_PACKAGE_NAME,
    release: str = "1",
    maintainer: str = "SeekClaw <support@seekclaw.local>",
    description: str = "SeekClaw desktop client",
    icon_path: Path | None = None,
) -> Path:
    """生成符合 RedHat / Fedora / CentOS / openSUSE 标准的 .rpm 软件包（纯 Python 实现）。"""
    remove_file(output_rpm_path)
    output_rpm_path.parent.mkdir(parents=True, exist_ok=True)
    now = int(time.time())

    desktop_entry = (
        "[Desktop Entry]\n"
        "Name=SeekClaw\n"
        f"Comment={description}\n"
        "Exec=/opt/SeekClaw/seekclaw-desktop %U\n"
        "Terminal=false\n"
        "Type=Application\n"
        "Icon=seekclaw\n"
        "StartupWMClass=SeekClaw\n"
        "Categories=Development;\n"
    ).encode("utf-8")

    launcher_script = b"#!/bin/sh\nexec /opt/SeekClaw/seekclaw-desktop \"$@\"\n"

    postin_script = (
        "if [ -f /opt/SeekClaw/chrome-sandbox ]; then\n"
        "    chmod 4755 /opt/SeekClaw/chrome-sandbox || true\n"
        "fi\n"
        "if [ -f /opt/SeekClaw/resources/runtime/seekclaw ]; then\n"
        "    chmod 755 /opt/SeekClaw/resources/runtime/seekclaw || true\n"
        "fi\n"
        "if [ -f /opt/SeekClaw/seekclaw-desktop ]; then\n"
        "    chmod 755 /opt/SeekClaw/seekclaw-desktop || true\n"
        "fi\n"
        "if which update-desktop-database >/dev/null 2>&1; then\n"
        "    update-desktop-database -q || true\n"
        "fi\n"
        "if which gtk-update-icon-cache >/dev/null 2>&1; then\n"
        "    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true\n"
        "fi\n"
    )

    postun_script = (
        "if which update-desktop-database >/dev/null 2>&1; then\n"
        "    update-desktop-database -q || true\n"
        "fi\n"
        "if which gtk-update-icon-cache >/dev/null 2>&1; then\n"
        "    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true\n"
        "fi\n"
    )

    file_list = []

    def add_file(rel_path: str, data: bytes, mode: int):
        clean_path = rel_path.replace("\\", "/")
        if not clean_path.startswith("/"):
            clean_path = "/" + clean_path
        file_list.append({
            "path": clean_path,
            "data": data,
            "mode": mode | 0o100000,
            "mtime": now,
        })

    def add_dir(rel_path: str, mode: int = 0o755):
        clean_path = rel_path.replace("\\", "/")
        if not clean_path.startswith("/"):
            clean_path = "/" + clean_path
        file_list.append({
            "path": clean_path,
            "data": b"",
            "mode": mode | 0o040000,
            "mtime": now,
        })

    for d in [
        "/opt", "/opt/SeekClaw", "/usr", "/usr/bin", "/usr/share",
        "/usr/share/applications", "/usr/share/icons", "/usr/share/icons/hicolor",
        "/usr/share/icons/hicolor/512x512", "/usr/share/icons/hicolor/512x512/apps",
        "/usr/share/pixmaps",
    ]:
        add_dir(d)

    for root, dirs, files in os.walk(source_dir):
        rel_root = os.path.relpath(root, source_dir).replace("\\", "/")
        if rel_root != ".":
            add_dir(f"/opt/SeekClaw/{rel_root}")
        for file in sorted(files):
            file_path = os.path.join(root, file)
            rel_path = os.path.relpath(file_path, source_dir).replace("\\", "/")
            target_path = f"/opt/SeekClaw/{rel_path}"
            is_executable = (
                file in ("seekclaw-desktop", "seekclaw", "chrome-sandbox", "chrome_crashpad_handler")
                or file.endswith(".so")
                or ("runtime" in rel_path and not file.endswith(".txt"))
            )
            mode = 0o4755 if file == "chrome-sandbox" else (0o755 if is_executable else 0o644)
            with open(file_path, "rb") as f:
                data = f.read()
            add_file(target_path, data, mode)

    add_file("/usr/share/applications/seekclaw.desktop", desktop_entry, 0o644)
    add_file("/usr/bin/seekclaw", launcher_script, 0o755)
    if icon_path and icon_path.is_file():
        icon_bytes = icon_path.read_bytes()
        add_file("/usr/share/icons/hicolor/512x512/apps/seekclaw.png", icon_bytes, 0o644)
        add_file("/usr/share/pixmaps/seekclaw.png", icon_bytes, 0o644)

    file_list.sort(key=lambda x: x["path"])

    # 1. 组装 CPIO 归档并使用 Gzip 压缩
    cpio_buf = bytearray()
    for i, item in enumerate(file_list, start=1):
        cpio_path = "." + item["path"]
        entry = _make_cpio_entry(cpio_path, item["data"], item["mode"], i, item["mtime"])
        cpio_buf.extend(entry)

    trailer_path = "TRAILER!!!"
    trailer_header = (
        f"070701"
        f"{0:08x}"
        f"{0:08x}"
        f"{0:08x}"
        f"{0:08x}"
        f"{1:08x}"
        f"{0:08x}"
        f"{0:08x}"
        f"{0:08x}"
        f"{0:08x}"
        f"{0:08x}"
        f"{0:08x}"
        f"{len(trailer_path) + 1:08x}"
        f"{0:08x}"
    ).encode("ascii")
    trailer_path_bytes = trailer_path.encode("ascii") + b"\x00"
    trailer_padding = b"\x00" * ((4 - (len(trailer_header) + len(trailer_path_bytes)) % 4) % 4)
    cpio_buf.extend(trailer_header + trailer_path_bytes + trailer_padding)
    cpio_padding = b"\x00" * ((512 - len(cpio_buf) % 512) % 512)
    cpio_buf.extend(cpio_padding)

    uncompressed_payload_size = len(cpio_buf)
    compressed_payload = gzip.compress(bytes(cpio_buf), compresslevel=6, mtime=0)

    # 2. 收集文件列表元数据
    dir_list = []
    dir_map = {}
    basenames = []
    dirindexes = []
    filesizes = []
    filemodes = []
    filemtimes = []
    filemd5s = []
    filelinktos = []
    fileflags = []
    fileusernames = []
    filegroupnames = []
    filedevices = []
    fileinodes = []
    filelangs = []
    total_installed_size = 0

    for i, item in enumerate(file_list, start=1):
        p = item["path"]
        dirname, basename = p.rsplit("/", 1)
        dirname = dirname + "/"
        if dirname not in dir_map:
            dir_map[dirname] = len(dir_list)
            dir_list.append(dirname)

        dirindexes.append(dir_map[dirname])
        basenames.append(basename)
        filesizes.append(len(item["data"]))
        filemodes.append(item["mode"])
        filemtimes.append(item["mtime"])
        filemd5s.append(hashlib.md5(item["data"]).hexdigest() if len(item["data"]) > 0 else "")
        filelinktos.append("")
        fileflags.append(0)
        fileusernames.append("root")
        filegroupnames.append("root")
        filedevices.append(1)
        fileinodes.append(i)
        filelangs.append("")
        total_installed_size += len(item["data"])

    # 3. 构建 Main Header
    hb = RpmHeaderBuilder()
    hb.add_string(1000, package_name)
    hb.add_string(1001, version)
    hb.add_string(1002, release)
    hb.add_i18n_string(1004, [description])
    hb.add_i18n_string(1005, [description])
    hb.add_int32_array(1006, [now])
    hb.add_int32_array(1009, [total_installed_size])
    hb.add_string(1010, "SeekClaw")
    hb.add_string(1011, "SeekClaw")
    hb.add_string(1014, "MIT")
    hb.add_string(1015, maintainer)
    hb.add_i18n_string(1016, ["Development/Tools"])
    hb.add_string(1020, "https://github.com/umr-xiaomai/SeekClaw")
    hb.add_string(1021, "linux")
    hb.add_string(1022, "x86_64")
    hb.add_string(1024, postin_script)
    hb.add_string(1026, postun_script)
    hb.add_int32_array(1028, filesizes)
    hb.add_int16_array(1030, filemodes)
    hb.add_int16_array(1033, [0] * len(file_list))
    hb.add_int32_array(1034, filemtimes)
    hb.add_string_array(1035, filemd5s)
    hb.add_string_array(1036, filelinktos)
    hb.add_int32_array(1037, fileflags)
    hb.add_string_array(1039, fileusernames)
    hb.add_string_array(1040, filegroupnames)
    hb.add_string(1044, f"{package_name}-{version}-{release}.src.rpm")
    hb.add_string(1086, "/bin/sh")
    hb.add_string(1088, "/bin/sh")
    hb.add_int32_array(1095, filedevices)
    hb.add_int32_array(1096, fileinodes)
    hb.add_string_array(1097, filelangs)
    hb.add_int32_array(1116, dirindexes)
    hb.add_string_array(1117, basenames)
    hb.add_string_array(1118, dir_list)
    hb.add_string(1124, "cpio")
    hb.add_string(1125, "gzip")
    hb.add_string(1126, "9")

    main_header_bytes = hb.build()

    # 4. 构建 Signature Header
    combined_header_and_payload = main_header_bytes + compressed_payload
    sig_builder = RpmHeaderBuilder()
    sig_builder.add_int32_array(1000, [len(combined_header_and_payload)])
    sig_builder.add_bin(1004, hashlib.md5(combined_header_and_payload).digest())
    sig_builder.add_string(1007, hashlib.sha1(main_header_bytes).hexdigest())
    sig_builder.add_int32_array(1008, [uncompressed_payload_size])
    sig_header_bytes = sig_builder.build()

    sig_padding_len = (8 - len(sig_header_bytes) % 8) % 8
    sig_header_padded = sig_header_bytes + (b"\x00" * sig_padding_len)

    # 5. 构建 96 字节 Lead
    lead_name = f"{package_name}-{version}-{release}".encode("utf-8")[:65]
    lead = bytearray(96)
    lead[0:4] = RPM_MAGIC
    lead[4] = 3   # Major
    lead[5] = 0   # Minor
    struct.pack_into(">h", lead, 6, 1)   # Type: binary
    struct.pack_into(">h", lead, 8, 1)   # Arch: x86_64
    lead[10:10+len(lead_name)] = lead_name
    struct.pack_into(">h", lead, 76, 1)  # OS: Linux
    struct.pack_into(">h", lead, 78, 5)  # Signature type: 5 (Header-style)

    with open(output_rpm_path, "wb") as f:
        f.write(lead)
        f.write(sig_header_padded)
        f.write(main_header_bytes)
        f.write(compressed_payload)

    if not output_rpm_path.is_file():
        raise BuildError(f"RPM package is missing: {output_rpm_path}")
    return output_rpm_path


# ==============================================================================
# 通用构建与路径辅助函数
# ==============================================================================

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
        package_file.write_text(updated, encoding="utf-8", newline="\n")
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
    content = text.strip()
    if not content:
        return
    lines = content.splitlines()
    if len(lines) > MAX_ERROR_LINES:
        truncated = len(lines) - MAX_ERROR_LINES
        content = "\n".join(lines[-MAX_ERROR_LINES:])
        content = f"… (已省略前 {truncated} 行，共 {len(lines)} 行) …\n" + content
    console.print(Panel(content, title=title, border_style="red"))


def package_desktop_windows(
    pnpm: str, env: dict[str, str], build_target: str, attempts: int = 3, verbose: bool = False
) -> None:
    if build_target == TARGET_PORTABLE:
        electron_target = "dir"
    elif build_target == TARGET_INSTALLER:
        electron_target = "nsis"
    else:
        raise BuildError(f"Unknown Windows Desktop build target: {build_target}")

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


def package_desktop_linux(
    pnpm: str, env: dict[str, str], attempts: int = 3, verbose: bool = False
) -> None:
    arguments = ["exec", "electron-builder", "--linux", "dir", "--x64"]
    for attempt in range(1, attempts + 1):
        try:
            run(pnpm, arguments, DESKTOP_DIR, env, verbose=verbose)
            return
        except subprocess.CalledProcessError:
            if attempt == attempts:
                raise
            console.print(
                f"[yellow]⚠️ Electron Linux 打包失败 (第 {attempt}/{attempts} 次尝试); 正在清理并准备重试...[/yellow]"
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


def create_portable_zip(source_dir: Path, output_zip_path: Path) -> Path:
    r"""将发布目录压缩为 .zip 文件。"""
    remove_file(output_zip_path)
    base_name = str(output_zip_path.with_suffix(""))
    shutil.make_archive(
        base_name,
        "zip",
        root_dir=source_dir.parent,
        base_dir=source_dir.name,
    )
    if not output_zip_path.is_file():
        raise BuildError(f"Portable zip archive is missing: {output_zip_path}")
    return output_zip_path


# ==============================================================================
# 发布产物签名 (GnuPG 分离签名 + SHA256 校验和)
# ==============================================================================

# SeekClaw 发布签名密钥指纹；可用环境变量 SEEKCLAW_GPG_KEY_ID 覆盖（例如 CI 使用另一把密钥）。
DEFAULT_SIGNING_KEY_ID = "1BC91E2EF845559EF11A57310EB78EEE44D45714"
SIGNING_PUBLIC_KEY_NAME = "seekclaw-signing-key.asc"
SHA256SUMS_NAME = "SHA256SUMS"
GPG_TIMEOUT_SECONDS = 300


def persisted_path_directories() -> list[Path]:
    """读取注册表中持久化的 PATH 目录（Windows），用于弥补进程持有旧环境变量快照的情况。"""
    if os.name != "nt":
        return []

    try:
        import winreg
    except ImportError:  # pragma: no cover - 仅在非 Windows 上出现
        return []

    directories: list[Path] = []
    locations = (
        (winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
        (winreg.HKEY_CURRENT_USER, "Environment"),
    )
    for hive, subkey in locations:
        try:
            with winreg.OpenKey(hive, subkey) as key:
                value, _ = winreg.QueryValueEx(key, "Path")
        except OSError:
            continue
        if not isinstance(value, str):
            continue
        for entry in value.split(";"):
            expanded = os.path.expandvars(entry.strip())
            if expanded:
                directories.append(Path(expanded))
    return directories


def find_gpg_executable() -> str | None:
    """定位 gpg：优先 SEEKCLAW_GPG，其次 PATH，最后常见的安装目录。"""
    override = os.environ.get("SEEKCLAW_GPG", "").strip()
    if override:
        if Path(override).is_file():
            return override
        located = shutil.which(override)
        if located:
            return located
        raise BuildError(f"SEEKCLAW_GPG 指向的 gpg 不存在：{override}")

    located = shutil.which("gpg") or shutil.which("gpg2")
    if located:
        return located

    candidates: list[Path] = []
    if os.name == "nt":
        # 安装 GnuPG 会改写注册表里的 PATH，但已经启动的终端仍持有旧环境变量快照；
        # 这里补一层回退，避免必须重开终端才能签名。
        for directory in persisted_path_directories():
            candidates.append(directory / "gpg.exe")
        for root in (
            os.environ.get("ProgramFiles", r"C:\Program Files"),
            os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
        ):
            if root:
                candidates.append(Path(root) / "GnuPG" / "bin" / "gpg.exe")
                candidates.append(Path(root) / "Gpg4win" / "bin" / "gpg.exe")
        local_app_data = os.environ.get("LOCALAPPDATA", "")
        if local_app_data:
            candidates.append(Path(local_app_data) / "Programs" / "GnuPG" / "bin" / "gpg.exe")
    else:
        candidates += [
            Path("/usr/bin/gpg"),
            Path("/usr/local/bin/gpg"),
            Path("/opt/homebrew/bin/gpg"),
        ]

    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    return None


def resolve_signing_key_id() -> str:
    return os.environ.get("SEEKCLAW_GPG_KEY_ID", "").strip() or DEFAULT_SIGNING_KEY_ID


def resolve_signing_passphrase() -> str | None:
    return os.environ.get("SEEKCLAW_GPG_PASSPHRASE", "") or None


def sha256_of_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_sha256sums(artifacts: Sequence[Path], output_path: Path) -> Path:
    """按 GNU coreutils 格式写出 SHA256SUMS，便于 sha256sum -c 直接校验。"""
    lines = [
        f"{sha256_of_file(artifact)}  {artifact.name}"
        for artifact in sorted(artifacts, key=lambda item: item.name)
    ]
    output_path.write_text("\n".join(lines) + "\n", encoding="ascii", newline="\n")
    return output_path


def run_gpg(gpg: str, arguments: Sequence[str], passphrase: str | None, verbose: bool = False) -> None:
    """调用 gpg。提供口令时走 loopback 模式从 stdin 读取，保证 CI 免交互签名。"""
    command = [gpg, "--batch", "--yes"]
    if passphrase is not None:
        command += ["--pinentry-mode", "loopback", "--passphrase-fd", "0"]
    command += list(arguments)

    if verbose:
        console.print(f"[dim]> {subprocess.list2cmdline(command)}[/dim]")

    try:
        result = subprocess.run(
            command,
            input=f"{passphrase}\n" if passphrase is not None else None,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=GPG_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired as error:
        raise BuildError(
            f"gpg 超过 {GPG_TIMEOUT_SECONDS} 秒未返回，多半是卡在 pinentry 口令提示上。"
            "请在 CI 或无人值守构建中设置 SEEKCLAW_GPG_PASSPHRASE 环境变量。"
        ) from error
    if result.returncode != 0:
        console.print(
            f"\n[bold red]❌ gpg 执行失败 (退出码 {result.returncode}):[/bold red] "
            f"[dim]{subprocess.list2cmdline(command)}[/dim]"
        )
        _print_failure_output(result.stderr, "gpg stderr")
        _print_failure_output(result.stdout, "gpg stdout")
        raise BuildError(
            "GnuPG 签名失败；请检查签名私钥是否存在、口令 (SEEKCLAW_GPG_PASSPHRASE) 是否正确。"
        )


def assert_signing_key_available(gpg: str, key_id: str) -> None:
    """确认钥匙串中存在签名私钥，避免只落到 gpg 的晦涩报错上。"""
    result = subprocess.run(
        [gpg, "--batch", "--list-secret-keys", "--with-colons", key_id],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=GPG_TIMEOUT_SECONDS,
    )
    if result.returncode != 0 or "sec:" not in result.stdout:
        raise BuildError(
            f"钥匙串中找不到签名私钥 {key_id}。请先导入私钥备份（gpg --import <私钥文件>），"
            "或用 SEEKCLAW_GPG_KEY_ID 指定其他密钥。"
        )


def sign_linux_release(
    artifacts: Sequence[Path],
    output_dir: Path,
    verbose: bool = False,
) -> list[Path]:
    """为 Linux 发布产物生成 SHA256SUMS、逐文件分离签名 (.asc) 与签名公钥。

    返回本次生成或更新的签名相关文件。
    """
    existing = [artifact for artifact in artifacts if artifact.is_file()]
    if not existing:
        raise BuildError("没有可供签名的 Linux 产物。")

    gpg = find_gpg_executable()
    if gpg is None:
        raise BuildError(
            "未找到 gpg 可执行文件，无法为 Linux 产物签名。请安装 GnuPG"
            "（Windows 可执行 winget install GnuPG.GnuPG），或用 SEEKCLAW_GPG 指定 gpg 路径。"
        )

    key_id = resolve_signing_key_id()
    passphrase = resolve_signing_passphrase()
    assert_signing_key_available(gpg, key_id)
    if passphrase is None:
        console.print(
            "[yellow]提示: 未设置 SEEKCLAW_GPG_PASSPHRASE，将交由 GnuPG pinentry 询问口令；"
            "无人值守场景请改用环境变量。[/yellow]"
        )

    produced: list[Path] = []

    checksum_path = write_sha256sums(existing, output_dir / SHA256SUMS_NAME)
    produced.append(checksum_path)

    for artifact in existing:
        signature_path = artifact.with_name(f"{artifact.name}.asc")
        run_gpg(
            gpg,
            [
                "--armor",
                "--detach-sign",
                "--local-user",
                key_id,
                "--output",
                str(signature_path),
                str(artifact),
            ],
            passphrase,
            verbose,
        )
        produced.append(signature_path)

    checksum_signature_path = checksum_path.with_name(f"{checksum_path.name}.asc")
    run_gpg(
        gpg,
        [
            "--armor",
            "--detach-sign",
            "--local-user",
            key_id,
            "--output",
            str(checksum_signature_path),
            str(checksum_path),
        ],
        passphrase,
        verbose,
    )
    produced.append(checksum_signature_path)

    public_key_path = output_dir / SIGNING_PUBLIC_KEY_NAME
    run_gpg(
        gpg,
        ["--armor", "--export", "--output", str(public_key_path), key_id],
        passphrase,
        verbose,
    )
    produced.append(public_key_path)

    return produced


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build the latest self-contained Runtime and Desktop release for Windows & Linux."
    )
    parser.add_argument("--skip-tests", action="store_true", help="Skip .NET and Desktop tests.")
    parser.add_argument("--skip-install", action="store_true", help="Skip pnpm install.")
    parser.add_argument("-v", "--verbose", action="store_true", help="Show full stdout from subcommands.")
    parser.add_argument(
        "--platform",
        "--os",
        dest="platform",
        choices=["windows", "win", "linux", "both"],
        help="目标操作系统平台 (windows / linux / both)；省略时以交互菜单选择。",
    )
    parser.add_argument(
        "--target",
        choices=[TARGET_PORTABLE, TARGET_INSTALLER, TARGET_BOTH, TARGET_DEB, TARGET_RPM, TARGET_ALL],
        help="构建目标 (portable/installer/both/deb/rpm/all)；省略时以交互菜单选择。",
    )
    parser.add_argument(
        "--keep-output",
        action="store_true",
        help="构建结束后保留 staging / electron-builder 输出目录（默认清理）。",
    )
    parser.add_argument(
        "--sign",
        action="store_true",
        help="为 Linux 产物额外生成 SHA256SUMS 与 GPG 分离签名（默认关闭）。",
    )
    return parser.parse_args()


def prompt_platform() -> str:
    """交互选择目标平台：Windows、Linux 或同时打包 Windows + Linux。"""
    if questionary is None:
        return _prompt_platform_stdlib()
    try:
        choice = questionary.select(
            "请选择编译目标操作系统 (OS / Architecture: 64位 x64)：",
            choices=[
                questionary.Choice("🪟 Windows (win-x64)", value=PLATFORM_WINDOWS),
                questionary.Choice("🐧 Linux (linux-x64 / amd64)", value=PLATFORM_LINUX),
                questionary.Choice("🌐 Windows + Linux (同时打包 win-x64 与 linux-x64)", value=PLATFORM_BOTH),
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
        raise BuildError("未选择目标平台，构建已取消。")
    return choice


def _prompt_platform_stdlib() -> str:
    print("\n请选择编译目标操作系统 (OS / Architecture: 64位 x64)：")
    print(f"1. 🪟 Windows (win-x64) [{PLATFORM_WINDOWS}]")
    print(f"2. 🐧 Linux (linux-x64 / amd64) [{PLATFORM_LINUX}]")
    print(f"3. 🌐 Windows + Linux 同时打包 [{PLATFORM_BOTH}]")
    while True:
        try:
            raw = input("请输入 1、2 或 3: ").strip()
        except (EOFError, KeyboardInterrupt):
            raise BuildError("未选择目标平台，构建已取消。")
        if raw in ("1", PLATFORM_WINDOWS, "win"):
            return PLATFORM_WINDOWS
        if raw in ("2", PLATFORM_LINUX):
            return PLATFORM_LINUX
        if raw in ("3", PLATFORM_BOTH):
            return PLATFORM_BOTH
        print("无效输入，请重新输入 1、2 或 3。")


def prompt_build_target(platform: str) -> str:
    """根据所选操作系统平台展示对应的构建目标菜单。"""
    if questionary is None:
        return _prompt_build_target_stdlib(platform)
    try:
        if platform == PLATFORM_WINDOWS:
            choices = [
                questionary.Choice("📦 免安装便携版 (Portable 绿色解压文件夹及 .zip)", value=TARGET_PORTABLE),
                questionary.Choice("💿 安装包版 (NSIS 可执行安装程序)", value=TARGET_INSTALLER),
                questionary.Choice("🚀 同时打包免安装版和安装版", value=TARGET_BOTH),
            ]
        elif platform == PLATFORM_BOTH:
            choices = [
                questionary.Choice("🚀 全量发布 (Win 便携版 + NSIS，Linux 便携版 + DEB + RPM)", value=TARGET_ALL),
                questionary.Choice("📦 双平台便携版 (Win .zip + Linux .tar.gz)", value=TARGET_PORTABLE),
            ]
        else:
            choices = [
                questionary.Choice("📦 便携版 (Portable .tar.gz 压缩包及运行目录)", value=TARGET_PORTABLE),
                questionary.Choice("📦 DEB 安装包 (Debian / Ubuntu / Deepin / UOS .deb)", value=TARGET_DEB),
                questionary.Choice("📦 RPM 安装包 (Fedora / RHEL / CentOS / openSUSE .rpm)", value=TARGET_RPM),
                questionary.Choice("🚀 一键打包全部 (便携版 + DEB + RPM)", value=TARGET_ALL),
            ]

        choice = questionary.select(
            "请选择构建打包类型：",
            choices=choices,
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


def _prompt_build_target_stdlib(platform: str) -> str:
    print("\n请选择构建打包类型：")
    if platform == PLATFORM_WINDOWS:
        print(f"1. 📦 免安装便携版 (Portable 绿色解压文件夹及 .zip) [{TARGET_PORTABLE}]")
        print(f"2. 💿 安装包版 (NSIS 可执行安装程序) [{TARGET_INSTALLER}]")
        print(f"3. 🚀 同时打包免安装版和安装版 [{TARGET_BOTH}]")
        while True:
            try:
                raw = input("请输入 1、2 或 3: ").strip()
            except (EOFError, KeyboardInterrupt):
                raise BuildError("未选择构建类型，构建已取消。")
            if raw in ("1", TARGET_PORTABLE):
                return TARGET_PORTABLE
            if raw in ("2", TARGET_INSTALLER):
                return TARGET_INSTALLER
            if raw in ("3", TARGET_BOTH):
                return TARGET_BOTH
            print("无效输入，请重新输入 1、2 或 3。")
    elif platform == PLATFORM_BOTH:
        print(f"1. 🚀 全量发布 (Win 便携版 + NSIS，Linux 便携版 + DEB + RPM) [{TARGET_ALL}]")
        print(f"2. 📦 双平台便携版 (Win .zip + Linux .tar.gz) [{TARGET_PORTABLE}]")
        while True:
            try:
                raw = input("请输入 1 或 2: ").strip().lower()
            except (EOFError, KeyboardInterrupt):
                raise BuildError("未选择构建类型，构建已取消。")
            if raw in ("1", TARGET_ALL, "all"):
                return TARGET_ALL
            if raw in ("2", TARGET_PORTABLE, "portable"):
                return TARGET_PORTABLE
            print("无效输入，请重新输入 1 或 2。")
    else:
        print(f"1. 📦 便携版 (Portable .tar.gz 压缩包及运行目录) [{TARGET_PORTABLE}]")
        print(f"2. 📦 DEB 安装包 (Debian / Ubuntu / Deepin / UOS .deb) [{TARGET_DEB}]")
        print(f"3. 📦 RPM 安装包 (Fedora / RHEL / CentOS / openSUSE .rpm) [{TARGET_RPM}]")
        print(f"4. 🚀 一键打包全部 (便携版 + DEB + RPM) [{TARGET_ALL}]")
        while True:
            try:
                raw = input("请输入 1、2、3 或 4: ").strip().lower()
            except (EOFError, KeyboardInterrupt):
                raise BuildError("未选择构建类型，构建已取消。")
            if raw in ("1", TARGET_PORTABLE, "portable"):
                return TARGET_PORTABLE
            if raw in ("2", TARGET_DEB, "deb"):
                return TARGET_DEB
            if raw in ("3", TARGET_RPM, "rpm"):
                return TARGET_RPM
            if raw in ("4", TARGET_ALL, "all"):
                return TARGET_ALL
            print("无效输入，请重新输入 1、2、3 或 4。")


def main() -> int:
    args = parse_arguments()

    # 顶部 UI Banner 渲染
    console.clear()
    banner = Text("SeekClaw Runtime & Desktop Release Builder (Cross-Platform)", style="bold cyan")
    console.print(Panel(banner, expand=False, border_style="cyan"))

    # 确定平台
    platform = args.platform
    if platform:
        if platform in ("win", "windows"):
            platform = PLATFORM_WINDOWS
        elif platform == PLATFORM_LINUX:
            platform = PLATFORM_LINUX
        else:
            platform = PLATFORM_BOTH
    else:
        platform = prompt_platform()

    # 确定构建目标
    build_target = args.target or prompt_build_target(platform)

    # 验证目标与平台匹配
    if platform == PLATFORM_WINDOWS:
        if build_target in (TARGET_DEB, TARGET_RPM):
            raise BuildError(
                f"构建目标 '{build_target}' 不适用于 Windows 平台。"
                f"Windows 支持的目标为: {TARGET_PORTABLE}, {TARGET_INSTALLER}, {TARGET_BOTH}。"
            )
        if build_target == TARGET_ALL:
            if args.target:
                console.print(f"[yellow]提示: Windows 平台下目标 '{TARGET_ALL}' 自动映射为 '{TARGET_BOTH}' (便携版 + 安装包)。[/yellow]")
            build_target = TARGET_BOTH
    elif platform == PLATFORM_LINUX:
        if build_target == TARGET_INSTALLER:
            raise BuildError(
                f"构建目标 '{TARGET_INSTALLER}' (NSIS 安装程序) 不适用于 Linux 平台。"
                f"Linux 支持的目标为: {TARGET_PORTABLE}, {TARGET_DEB}, {TARGET_RPM}, {TARGET_ALL}。"
            )
        if build_target == TARGET_BOTH:
            if args.target:
                console.print(f"[yellow]提示: Linux 平台下目标 '{TARGET_BOTH}' 自动映射为 '{TARGET_ALL}' (便携版 + DEB + RPM)。[/yellow]")
            build_target = TARGET_ALL
    elif platform == PLATFORM_BOTH:
        if build_target in (TARGET_INSTALLER, TARGET_DEB, TARGET_RPM):
            if args.target:
                console.print(f"[yellow]提示: 双平台构建下目标 '{build_target}' 自动调整为全量打包 '{TARGET_ALL}'。[/yellow]")
            build_target = TARGET_ALL

    if platform == PLATFORM_BOTH and build_target == TARGET_PORTABLE:
        platform_targets = (
            (PLATFORM_WINDOWS, TARGET_PORTABLE),
            (PLATFORM_LINUX, TARGET_PORTABLE),
        )
    elif platform == PLATFORM_BOTH:
        platform_targets = (
            (PLATFORM_WINDOWS, TARGET_BOTH),
            (PLATFORM_LINUX, TARGET_ALL),
        )
    else:
        platform_targets = ((platform, build_target),)

    build_meta: dict[str, dict[str, Path | str]] = {}
    for cur_platform, cur_target in platform_targets:
        if cur_platform == PLATFORM_WINDOWS:
            rid: str = "win-x64"
            unpacked = BUILDER_OUTPUT / "win-unpacked"
        else:
            rid = "linux-x64"
            unpacked = BUILDER_OUTPUT / "linux-unpacked"
        build_meta[cur_platform] = {
            "target": cur_target,
            "rid": rid,
            "runtime_stage": DESKTOP_DIR / "runtime" / rid,
            "unpacked_output": unpacked,
        }

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

    platform_display = (
        "WINDOWS + LINUX (win-x64 / linux-x64)"
        if platform == PLATFORM_BOTH
        else f"{platform.upper()} (64-bit {'win-x64' if platform == PLATFORM_WINDOWS else 'linux-x64'})"
    )

    console.print(
        f"\n[bold green]✓[/bold green] 版本号更新: [dim]{previous_version}[/dim] ➔ [bold cyan]{release_version}[/bold cyan]"
        f"  (目标平台: [bold magenta]{platform_display}[/bold magenta])\n"
    )

    try:
        # 1. 准备工作目录
        with console.status("[bold blue]正在重置与清理构建目录...[/bold blue]", spinner="dots"):
            for meta in build_meta.values():
                reset_directory(Path(meta["runtime_stage"]))
            reset_directory(PUBLISH_DIR)
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

        # 4. 发布 .NET 独立运行时 (自包含 SingleFile，无任何外部 runtime 依赖)
        for cur_platform, meta in build_meta.items():
            rid: str = meta["rid"]
            runtime_stage: Path = meta["runtime_stage"]
            with console.status(f"[bold blue]正在编译与发布 .NET 自包含 Runtime ({cur_platform} {rid})...[/bold blue]", spinner="dots"):
                run(
                    dotnet,
                    [
                        "publish",
                        "seekclaw_cli/seekclaw_cli.csproj",
                        "-c",
                        "Release",
                        "-r",
                        rid,
                        "--self-contained",
                        "true",
                        "-p:PublishSingleFile=true",
                        "-p:IncludeNativeLibrariesForSelfExtract=true",
                        "-p:DebugType=None",
                        "-p:DebugSymbols=false",
                        "-o",
                        str(runtime_stage),
                    ],
                    REPO_ROOT,
                    build_env,
                    verbose=args.verbose,
                )
            console.print(f"[bold green]✓[/bold green] .NET 自包含 Runtime ({cur_platform} {rid}) 编译完成")

        # 5. 构建前端并逐个平台打包 Electron
        with console.status("[bold blue]正在构建前端组件...[/bold blue]", spinner="dots"):
            run(pnpm, ["build"], DESKTOP_DIR, build_env, verbose=args.verbose)
        console.print("[bold green]✓[/bold green] 前端组件构建完成")

        # 6. 生成分发包与产物组织（按平台循环，先打包再组装）
        release_outputs: list[Path] = []
        launch_entries: list[Path] = []
        target_labels: list[str] = []

        for cur_platform, meta in build_meta.items():
            cur_target: str = meta["target"]
            rid = meta["rid"]
            unpacked_output: Path = meta["unpacked_output"]

            with console.status(f"[bold blue]正在打包 Electron {cur_platform.upper()} 应用...[/bold blue]", spinner="dots"):
                if cur_platform == PLATFORM_WINDOWS:
                    if cur_target in (TARGET_PORTABLE, TARGET_BOTH):
                        package_desktop_windows(pnpm, build_env, TARGET_PORTABLE, verbose=args.verbose)
                    if cur_target in (TARGET_INSTALLER, TARGET_BOTH):
                        package_desktop_windows(pnpm, build_env, TARGET_INSTALLER, verbose=args.verbose)
                else:
                    package_desktop_linux(pnpm, build_env, verbose=args.verbose)

            console.print(
                f"[dim]electron-builder 完成 ({cur_platform}, target={cur_target})，正在组装 publish/ 产物...[/dim]"
            )

            if cur_platform == PLATFORM_WINDOWS:
                portable_output = PUBLISH_DIR / "SeekClaw-win-x64"
                portable_zip_output = PUBLISH_DIR / f"SeekClaw-portable-{release_version}-win-x64.zip"
                installer_output = PUBLISH_DIR / f"SeekClaw-Setup-{release_version}-win-x64.exe"

                if cur_target in (TARGET_PORTABLE, TARGET_BOTH):
                    if not unpacked_output.is_dir():
                        raise BuildError(f"Electron builder output was not found: {unpacked_output}")

                    desktop_executable = unpacked_output / "SeekClaw.exe"
                    runtime_executable = unpacked_output / "resources" / "runtime" / "seekclaw.exe"
                    if not desktop_executable.is_file():
                        raise BuildError(f"Desktop executable is missing: {desktop_executable}")
                    if not runtime_executable.is_file():
                        raise BuildError(f"Bundled Runtime executable is missing: {runtime_executable}")

                    shutil.copytree(unpacked_output, portable_output, dirs_exist_ok=True)
                    release_outputs.append(portable_output)
                    release_outputs.append(create_portable_zip(portable_output, portable_zip_output))
                    launch_entries.append(portable_output / "SeekClaw.exe")

                if cur_target in (TARGET_INSTALLER, TARGET_BOTH):
                    installer_artifact = find_installer_artifact(release_version)
                    shutil.copy2(installer_artifact, installer_output)
                    if not installer_output.is_file():
                        raise BuildError(f"Installer executable is missing: {installer_output}")
                    release_outputs.append(installer_output)

                if cur_target == TARGET_BOTH:
                    target_labels.append("Windows: 免安装版 + NSIS 安装程序")
                elif cur_target == TARGET_INSTALLER:
                    target_labels.append("Windows: NSIS 安装程序")
                else:
                    target_labels.append("Windows: 免安装便携版 (Portable)")
            else:
                # Linux 打包产物生成
                if not unpacked_output.is_dir():
                    raise BuildError(f"Electron Linux builder output was not found: {unpacked_output}")

                desktop_executable = unpacked_output / "seekclaw-desktop"
                runtime_executable = unpacked_output / "resources" / "runtime" / "seekclaw"
                if not desktop_executable.is_file():
                    raise BuildError(f"Linux desktop executable is missing: {desktop_executable}")
                if not runtime_executable.is_file():
                    raise BuildError(f"Bundled Linux Runtime executable is missing: {runtime_executable}")

                portable_output = PUBLISH_DIR / "SeekClaw-linux-x64"
                portable_tar_output = PUBLISH_DIR / f"SeekClaw-portable-{release_version}-linux-x64.tar.gz"
                deb_output = PUBLISH_DIR / f"SeekClaw-{release_version}_amd64.deb"
                rpm_output = PUBLISH_DIR / f"SeekClaw-{release_version}.x86_64.rpm"
                linux_artifacts: list[Path] = []

                if cur_target in (TARGET_PORTABLE, TARGET_ALL):
                    with console.status("[bold blue]正在生成 Linux 便携版 (.tar.gz)...[/bold blue]", spinner="dots"):
                        shutil.copytree(unpacked_output, portable_output, dirs_exist_ok=True)
                        create_linux_portable_tar(unpacked_output, portable_tar_output)
                    console.print("[bold green]✓[/bold green] Linux 便携版打包完成")
                    release_outputs.append(portable_output)
                    release_outputs.append(portable_tar_output)
                    linux_artifacts.append(portable_tar_output)
                    launch_entries.append(portable_output / "seekclaw-desktop")

                if cur_target in (TARGET_DEB, TARGET_ALL):
                    with console.status("[bold blue]正在生成 Debian / Ubuntu 安装包 (.deb)...[/bold blue]", spinner="dots"):
                        create_deb_package(
                            source_dir=unpacked_output,
                            output_deb_path=deb_output,
                            version=release_version,
                            icon_path=ICON_PNG_PATH,
                        )
                    console.print("[bold green]✓[/bold green] Linux DEB 安装包生成完成")
                    release_outputs.append(deb_output)
                    linux_artifacts.append(deb_output)

                if cur_target in (TARGET_RPM, TARGET_ALL):
                    with console.status("[bold blue]正在生成 RedHat / Fedora / CentOS 安装包 (.rpm)...[/bold blue]", spinner="dots"):
                        create_rpm_package(
                            source_dir=unpacked_output,
                            output_rpm_path=rpm_output,
                            version=release_version,
                            icon_path=ICON_PNG_PATH,
                        )
                    console.print("[bold green]✓[/bold green] Linux RPM 安装包生成完成")
                    release_outputs.append(rpm_output)
                    linux_artifacts.append(rpm_output)

                if cur_target == TARGET_ALL:
                    target_labels.append("Linux: 全量包 (便携版 + DEB + RPM)")
                elif cur_target == TARGET_DEB:
                    target_labels.append("Linux: Debian / Ubuntu 安装包 (.deb)")
                elif cur_target == TARGET_RPM:
                    target_labels.append("Linux: RedHat / Fedora / CentOS 安装包 (.rpm)")
                else:
                    target_labels.append("Linux: 免安装便携版 (.tar.gz)")

                if linux_artifacts and args.sign:
                    with console.status(
                        "[bold blue]正在为 Linux 产物生成校验和与 GPG 签名...[/bold blue]", spinner="dots"
                    ):
                        release_outputs.extend(
                            sign_linux_release(linux_artifacts, PUBLISH_DIR, verbose=args.verbose)
                        )
                    console.print(
                        f"[bold green]✓[/bold green] Linux 产物签名完成 "
                        f"[dim](密钥 {resolve_signing_key_id()} / gpg {find_gpg_executable()})[/dim]"
                    )

            console.print(f"[bold green]✓[/bold green] {cur_platform.upper()} 应用打包与产物组装完成")

        target_label = " / ".join(target_labels)
        version_committed = True
        elapsed = time.time() - start_time

        # 渲染最终构建结果摘要表格
        console.print("\n")
        table_title = (
            f"🎉 SeekClaw (WINDOWS + LINUX x64) 构建成功"
            if platform == PLATFORM_BOTH
            else f"🎉 SeekClaw ({next(iter(build_meta)).upper()} x64) 构建成功"
        )
        table = Table(title=table_title, border_style="green", header_style="bold green")
        table.add_column("属性", style="bold cyan")
        table.add_column("详情", style="white")

        table.add_row("目标平台", f"[bold magenta]{platform_display}[/bold magenta]")
        table.add_row("打包类型", target_label)
        table.add_row("发布版本", f"[bold yellow]{release_version}[/bold yellow]")
        for output in release_outputs:
            table.add_row("输出文件/路径", f"[underline cyan]{output}[/underline cyan]")
        if any(output.name.endswith(".asc") for output in release_outputs):
            table.add_row("签名密钥", f"[bold yellow]{resolve_signing_key_id()}[/bold yellow]")
        for launch_entry in launch_entries:
            table.add_row("便携启动入口", str(launch_entry))
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
