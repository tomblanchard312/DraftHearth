#!/usr/bin/env python3
"""Write a macOS .icns from PNGs (PNG-backed entries), for hosts without iconutil.

usage: icns.py out.icns size=png [size=png ...]   sizes: 16 32 64 128 256 512 1024
"""
import struct
import sys

TYPES = {16: b"icp4", 32: b"icp5", 64: b"icp6", 128: b"ic07", 256: b"ic08", 512: b"ic09", 1024: b"ic10"}


def main(out, pairs):
    chunks = b""
    for pair in pairs:
        size, path = pair.split("=", 1)
        data = open(path, "rb").read()
        if not data.startswith(b"\x89PNG"):
            raise SystemExit(f"{path} is not a PNG")
        chunks += TYPES[int(size)] + struct.pack(">I", len(data) + 8) + data
    with open(out, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2:])
