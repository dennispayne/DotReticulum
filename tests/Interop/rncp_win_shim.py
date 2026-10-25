"""Runs an upstream RNS utility (rncp, rnx, ...) unchanged except for one Windows-only workaround.

Upstream rncp saves a received file with shutil.move() while the Resource still holds the source file open; Windows refuses
that (WinError 32), so upstream rncp cannot receive files on Windows. This shim replaces shutil.move with a copy, which leaves the
wire behaviour untouched. Usage: python rncp_win_shim.py <module name, e.g. rncp> [utility arguments]
"""
import runpy
import shutil
import sys
import os
from reference_environment import verify_reference

verify_reference(require_lxmf=True)


def _copy_instead_of_move(src, dst, *args, **kwargs):
    shutil.copyfile(src, dst)
    return dst


if os.name == "nt":
    shutil.move = _copy_instead_of_move
module = sys.argv.pop(1)
runpy.run_module("RNS.Utilities." + module, run_name="__main__", alter_sys=True)
