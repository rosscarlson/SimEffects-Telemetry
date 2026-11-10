"""Client for `gtt nfsu2 serve`'s probe port: reads NFSU2's memory on nfsu2.xyz through the Chrome extension.

    from nfsu2probe import Probe
    p = Probe()
    p.u32(0x008654A4)

Read-only, like the bridge. Guest addresses are offsets into the page's WebAssembly memory.
"""
import json
import socket
import struct

PORT = 28021


class Probe:
    def __init__(self, port=PORT):
        self.s = socket.create_connection(("127.0.0.1", port))
        self.s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.f = self.s.makefile("rb")

    def state(self):
        self.s.sendall(b"S\n")
        return json.loads(self.f.readline())

    def read(self, addr, n):
        out = bytearray()
        chunk = 4 * 1024 * 1024
        while n > 0:
            k = min(n, chunk)
            self.s.sendall(f"R {addr:#x} {k}\n".encode())
            line = self.f.readline().decode().strip()
            if not line.startswith("OK"):
                raise IOError(line)
            out += self.f.read(k)
            addr += k
            n -= k
        return bytes(out)

    def u32(self, a):
        return struct.unpack("<I", self.read(a, 4))[0]

    def s32(self, a):
        return struct.unpack("<i", self.read(a, 4))[0]

    def f32(self, a):
        return struct.unpack("<f", self.read(a, 4))[0]

    def ptr_ok(self, a):
        return 0x10000 <= a < 0x80000000
