"""Execute pinned presentation helpers; no page routing or transport emulation.

The discovery-only cases replace shutil.which, explicitly recorded below.
WebP header cases intentionally include minimally accepted (not decoded) data.
All expected strings, dimensions and encoder arguments come from upstream.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import sys
import subprocess
import tempfile
import shutil

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import PINNED_RNS_SHA, verify_reference

import RNS
from RNS.Utilities.rngit import media, pages, server, util


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--work-directory", required=True)
    parser.add_argument("--encoder", help="Owned FFmpeg executable; actual conversion is optional, never emulated")
    parser.add_argument("--source-image", help="Input PNG for actual upstream encoder execution")
    parser.add_argument("--template-executable", help="Built owned C# fixture executable for actual executable-template calls")
    parser.add_argument("--editor", help="Benign native editor command (such as Windows sort.exe, which leaves input unchanged)")
    args = parser.parse_args()
    verify_reference()
    root = Path(args.work_directory).resolve()
    root.mkdir(parents=True, exist_ok=True)
    node = pages.NomadNetworkNode.__new__(pages.NomadNetworkNode)
    node.templatesdir = str(root)
    node.node_name = "Nœud ♥"
    node.templates = {
        name.removeprefix("DEFAULT_").removesuffix("_TEMPLATE").lower(): value
        for name, value in vars(pages).items()
        if name.startswith("DEFAULT_") and name.endswith("_TEMPLATE")
    }
    template_text = "  π {PAGE_CONTENT} {UNKNOWN}\t \n"
    path = root / "vector.mu"
    path.write_text(template_text, encoding="utf-8")
    # Windows os.access(X_OK) regards any existing file as executable. The
    # static-loader vector must execute upstream's static branch deliberately.
    original_access = pages.os.access
    pages.os.access = lambda path, mode: False
    try:
        loaded = node.get_template("vector")
        rendered = node.render_template("a\t`b\n", "Navigation ♥", "front").decode("utf-8")
    finally:
        pages.os.access = original_access
        path.unlink()
    icons = {}
    for unicode_icons in (False, True):
        node.use_nerdfonts = not unicode_icons
        icons[str(unicode_icons).lower()] = {
            name: node.icon(name) for name in
            ("sep", "folder", "file", "branch", "commits", "tag", "stats", "heart", "package", "work", "unknown")
        }
    diff = "diff --git a/x b/x\nindex abc..def\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-`old\\path\n+`new\n context"
    markup = {
        "escape": node.m_escape("`x` [unchanged]"),
        "tabs": node.format_tabs("a\tb\tc"),
        "diff_input": diff,
        "diff": node.format_diff(diff),
        "link": node.m_link_r("[`Nœud]", "/page/tree.mu", g="hello world", r="a+b/π"),
    }
    headers = []
    for fourcc in (b"VP8X", b"VP8 ", b"VP8L", b"BAD!"):
        data = bytearray(30)
        data[:4], data[8:12], data[12:16] = b"RIFF", b"WEBP", fourcc
        data[21:30] = bytes([0x10, 0x34, 0x56, 0x78, 0x90, 0x12, 0x34, 0x56, 0x78])
        headers.append({"data": base64.b64encode(data).decode(), "dimensions": media._webp_info(data)})
    headers += [{"data": base64.b64encode(data).decode(), "dimensions": media._webp_info(data)}
                for data in (b"", b"not webp", bytes(30))]
    original_which = media.shutil.which
    original_env = media._ENV_BACKEND
    configured = []
    try:
        media.shutil.which = lambda executable: executable
        for name, _ in media.BACKENDS:
            media._ENV_BACKEND = name
            selected = media._configured_backend(quality=120, max_dimension=64)
            configured.append({"backend": name, "arguments": selected[1][1:]})
        media._ENV_BACKEND = "pillow"
        unsupported_backend = media._selected_backend()
    finally:
        media.shutil.which = original_which
        media._ENV_BACKEND = original_env
    result = {
        "upstream": PINNED_RNS_SHA,
        "version": RNS.__version__,
        "imports": {name: str(Path(module.__file__).resolve()) for name, module in
                    (("RNS", RNS), ("pages", pages), ("media", media))},
        "sha256": {name: hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest()
                   for name, module in (("pages", pages), ("media", media), ("server", server), ("util", util))},
        "disclosed_shims": ["os.access=False solely for static template branches on Windows",
                            "shutil.which returns requested executable solely for encoder argument discovery"],
        "template_input": template_text,
        "template_loaded": loaded,
        "rendered": rendered,
        "icons": icons,
        "markup": markup,
        "webp_headers": headers,
        "configured": configured,
        "unsupported_backend": unsupported_backend,
    }
    if args.template_executable:
        executable = Path(args.template_executable).resolve(strict=True)
        executable_root = root / "executable"
        executable_root.mkdir()
        for dependency in executable.parent.iterdir():
            if dependency.is_file():
                shutil.copy2(dependency, executable_root / dependency.name)
        shutil.copy2(executable, executable_root / "dynamic.mu")
        os.chmod(executable_root / "dynamic.mu", 0o700)
        (executable_root / "rngit-backend-mode").write_text("template", encoding="utf-8")
        original_directory = node.templatesdir
        node.templatesdir = str(executable_root)
        try:
            dynamic = node.get_template("dynamic")
            if dynamic != "owned π {PAGE_CONTENT}":
                raise RuntimeError("Actual executable template did not return the owned fixture output")
            (executable_root / "rngit-backend-mode").write_text("fail", encoding="utf-8")
            # Upstream ignores exit status; our deliberate safety difference
            # reports nonzero exits as failure instead of accepting their output.
            failed_dynamic = node.get_template("dynamic")
            result["executable_template"] = {"success": dynamic, "nonzero_exit_output": failed_dynamic,
                                             "owned_fixture": str(executable)}
        finally:
            node.templatesdir = original_directory
            shutil.rmtree(executable_root)
    if args.editor:
        editor = Path(args.editor).resolve(strict=True)
        original_editor = os.environ.get("EDITOR")
        original_temp = tempfile.tempdir
        before = set(root.iterdir())
        try:
            tempfile.tempdir = str(root)
            os.environ["EDITOR"] = str(editor)
            client = server.ReticulumGitClient.__new__(server.ReticulumGitClient)
            content = "owned permission input\n"
            result["editor"] = {"input": content, "permissions_result": client._edit_permissions(content),
                               "command": str(editor), "shell": False}
            if set(root.iterdir()) != before:
                raise RuntimeError("Reference editor failed to clean its owned temporary file")
        finally:
            tempfile.tempdir = original_temp
            if original_editor is None:
                os.environ.pop("EDITOR", None)
            else:
                os.environ["EDITOR"] = original_editor
    if args.encoder:
        if not args.source_image:
            raise RuntimeError("--encoder requires --source-image")
        encoder = Path(args.encoder).resolve(strict=True)
        source_image = Path(args.source_image).resolve(strict=True)
        original_path = os.environ.get("PATH", "")
        original_temp = tempfile.tempdir
        original_env = media._ENV_BACKEND
        cases = []
        try:
            os.environ["PATH"] = str(encoder.parent) + os.pathsep + original_path
            tempfile.tempdir = str(root)
            media._ENV_BACKEND = "ffmpeg"
            if Path(media.shutil.which("ffmpeg")).resolve() != encoder:
                raise RuntimeError("Reference selected an unexpected FFmpeg executable")
            for quality, dimension in ((85, 64), (0, 32), (120, 32), (None, 64)):
                converted = media.convert_file_to_webp(str(source_image), quality=quality, max_dimension=dimension)
                if not converted:
                    raise RuntimeError("Actual upstream FFmpeg conversion failed")
                try:
                    output = Path(converted).read_bytes()
                    cases.append({"quality": quality, "max_dimension": dimension,
                                  "dimensions": media._webp_info(output),
                                  "output": base64.b64encode(output).decode(),
                                  "sha256": hashlib.sha256(output).hexdigest()})
                finally:
                    Path(converted).unlink()
            invalid = root / "invalid.png"
            invalid.write_bytes(b"not an image")
            try:
                invalid_result = media.convert_file_to_webp(str(invalid))
                if invalid_result:
                    Path(invalid_result).unlink()
                    raise RuntimeError("Reference unexpectedly accepted invalid image bytes")
            finally:
                invalid.unlink()
        finally:
            os.environ["PATH"] = original_path
            tempfile.tempdir = original_temp
            media._ENV_BACKEND = original_env
        result["native"] = {
            "encoder": str(encoder),
            "encoder_sha256": hashlib.sha256(encoder.read_bytes()).hexdigest(),
            "encoder_version": subprocess.run([str(encoder), "-version"], check=True, capture_output=True, text=True).stdout.splitlines()[0],
            "source_image": str(source_image),
            "source_sha256": hashlib.sha256(source_image.read_bytes()).hexdigest(),
            "cases": cases,
            "invalid_image_result": invalid_result,
            "owned_temp_directory": str(root),
        }
    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"imports": result["imports"], "sha256": result["sha256"]}, indent=2))


if __name__ == "__main__":
    main()
