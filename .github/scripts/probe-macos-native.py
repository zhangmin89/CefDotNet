"""Load each native library separately, then together, to isolate ObjC collisions.

Each invocation is a fresh process. This probes dlopen and class ownership only;
it does not claim that native file dialogs or rendering work correctly.
"""

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("avalonia", "cef", "combined"))
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    if platform.system() != "Darwin" or not args.directory.is_dir():
        parser.error("Run on macOS with an existing Avalonia test output directory.")

    libraries = {
        "avalonia": args.directory / "runtimes/osx/native/libAvaloniaNative.dylib",
        "cef": args.directory / "libcef.dylib",
    }
    # Match TestBase: CEF initialization precedes Avalonia initialization.
    selected = ("cef", "avalonia") if args.mode == "combined" else (args.mode,)
    objc = ctypes.CDLL("/usr/lib/libobjc.A.dylib")
    objc.objc_getClass.argtypes = [ctypes.c_char_p]
    objc.objc_getClass.restype = ctypes.c_void_p
    objc.class_getImageName.argtypes = [ctypes.c_void_p]
    objc.class_getImageName.restype = ctypes.c_char_p
    loaded = []
    print(json.dumps({"pid": os.getpid(), "mode": args.mode, "os": platform.platform(), "arch": platform.machine()}), flush=True)
    for name in selected:
        path = libraries[name].resolve(strict=True)
        with path.open("rb") as binary:
            digest = hashlib.file_digest(binary, "sha256").hexdigest()
        loaded.append(ctypes.CDLL(str(path)))
        klass = objc.objc_getClass(b"ExtensionDropdownHandler")
        owner = objc.class_getImageName(klass).decode() if klass else None
        print(json.dumps({"loaded": str(path), "sha256": digest, "ExtensionDropdownHandler_owner": owner}), flush=True)


if __name__ == "__main__":
    main()
