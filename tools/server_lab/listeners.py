"""Read the local IPv4 listening table without connecting to a shard.

An empty TCP connection can trip ModernUO's behavioural ban, even on loopback.
Keep readiness probes out of the protocol entirely.
"""

import ctypes
import socket
import sys
from pathlib import Path


def listening(port: int) -> bool:
    if sys.platform == "win32":
        return _windows_listening(port)
    if sys.platform.startswith("linux"):
        for line in Path("/proc/net/tcp").read_text(encoding="ascii").splitlines()[1:]:
            fields = line.split()
            address, local_port = fields[1].split(":")
            if fields[3] == "0A" and int(local_port, 16) == port and address in ("0100007F", "00000000"):
                return True
        return False
    raise RuntimeError("server lab listener-table readiness supports Windows and Linux")


def _windows_listening(port: int) -> bool:
    from ctypes import wintypes

    class Row(ctypes.Structure):
        _fields_ = [(name, wintypes.DWORD) for name in
                    ("state", "local_address", "local_port", "remote_address", "remote_port")]

    get_table = ctypes.WinDLL("iphlpapi").GetTcpTable
    get_table.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]
    get_table.restype = wintypes.DWORD
    size = wintypes.DWORD()
    result = get_table(None, ctypes.byref(size), False)
    while result == 122:  # ERROR_INSUFFICIENT_BUFFER; the table can grow between calls.
        buffer = ctypes.create_string_buffer(size.value)
        result = get_table(buffer, ctypes.byref(size), False)
    if result:
        raise OSError(result, "Could not read the TCP listening table")
    count = wintypes.DWORD.from_buffer_copy(buffer).value
    for index in range(count):
        row = Row.from_buffer_copy(buffer, ctypes.sizeof(wintypes.DWORD) + index * ctypes.sizeof(Row))
        if (row.state == 2 and socket.ntohs(row.local_port & 0xFFFF) == port
                and row.local_address in (0, 0x0100007F)):
            return True
    return False
