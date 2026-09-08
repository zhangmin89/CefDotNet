"""Report glibc TLS allocation and reproduce CEF dlopen outside the test runner."""

import ctypes
import os
from pathlib import Path
import platform
import struct
import sys

if len(sys.argv) != 2 or platform.system() != "Linux":
    raise SystemExit("Usage on Linux: python3 probe-cef-load.py <libcef.so>")

library = Path(sys.argv[1]).resolve(strict=True)
with library.open("rb") as binary:
    header = binary.read(64)
    if header[:6] != b"\x7fELF\x02\x01":
        raise SystemExit("Expected little-endian ELF64")
    offset = struct.unpack_from("<Q", header, 32)[0]
    entry_size, entry_count = struct.unpack_from("<HH", header, 54)
    for index in range(entry_count):
        binary.seek(offset + index * entry_size)
        entry = binary.read(entry_size)
        if struct.unpack_from("<I", entry)[0] == 7:  # PT_TLS
            size, alignment = struct.unpack_from("<QQ", entry, 40)
            print(f"CEF TLS: size={size}, alignment={alignment}", flush=True)
            break
    else:
        raise SystemExit("CEF has no PT_TLS segment")

print(f"libc={platform.libc_ver()}, GLIBC_TUNABLES={os.environ.get('GLIBC_TUNABLES')}", flush=True)
loader = ctypes.CDLL(None)
get_tls_info = loader._dl_get_tls_static_info
get_tls_info.argtypes = [ctypes.POINTER(ctypes.c_size_t), ctypes.POINTER(ctypes.c_size_t)]
get_tls_info.restype = None
static_size, static_alignment = ctypes.c_size_t(), ctypes.c_size_t()
get_tls_info(ctypes.byref(static_size), ctypes.byref(static_alignment))
print(f"Probe process static TLS: size={static_size.value}, alignment={static_alignment.value}", flush=True)
ctypes.CDLL(str(library))
print("CEF dlopen succeeded in the probe process", flush=True)
