"""Ed25519 (RFC 8032) for catalogue signatures. Standard library only.

This is the RFC's own reference algorithm (section 6), so the C# verifier in
godot/GUO/src/Store/Ed25519.cs can be checked against it line by line. It is
not constant-time. Signing uses the `cryptography` package when it is
installed, and falls back to this code when it is not. Signing happens only
in the publisher's own tools or CI, never in the client. Verifying a public
signature needs no secrecy.
"""
from __future__ import annotations

import base64
import hashlib

P = 2**255 - 19
Q = 2**252 + 27742317777372353535851937790883648493
D = -121665 * pow(121666, P - 2, P) % P
SQRT_M1 = pow(2, (P - 1) // 4, P)
PREFIX = "ed25519:"


def _inv(x: int) -> int:
    return pow(x, P - 2, P)


def _add(a, b):
    x1, y1, z1, t1 = a
    x2, y2, z2, t2 = b
    aa = (y1 - x1) * (y2 - x2) % P
    bb = (y1 + x1) * (y2 + x2) % P
    cc = 2 * t1 * t2 * D % P
    dd = 2 * z1 * z2 % P
    e, f, g, h = bb - aa, dd - cc, dd + cc, bb + aa
    return (e * f % P, g * h % P, f * g % P, e * h % P)


def _mul(s: int, point):
    result = (0, 1, 1, 0)
    while s > 0:
        if s & 1:
            result = _add(result, point)
        point = _add(point, point)
        s >>= 1
    return result


def _equal(a, b) -> bool:
    return (a[0] * b[2] - b[0] * a[2]) % P == 0 and (a[1] * b[2] - b[1] * a[2]) % P == 0


def _recover_x(y: int, sign: int):
    if y >= P:
        return None
    x2 = (y * y - 1) * _inv(D * y * y + 1) % P
    if x2 == 0:
        return None if sign else 0
    x = pow(x2, (P + 3) // 8, P)
    if (x * x - x2) % P:
        x = x * SQRT_M1 % P
    if (x * x - x2) % P:
        return None
    if (x & 1) != sign:
        x = P - x
    return x


_GY = 4 * _inv(5) % P
_GX = _recover_x(_GY, 0)
G = (_GX, _GY, 1, _GX * _GY % P)


def _compress(point) -> bytes:
    zinv = _inv(point[2])
    x, y = point[0] * zinv % P, point[1] * zinv % P
    return int.to_bytes(y | ((x & 1) << 255), 32, "little")


def _decompress(data: bytes):
    if len(data) != 32:
        return None
    y = int.from_bytes(data, "little")
    sign = y >> 255
    y &= (1 << 255) - 1
    x = _recover_x(y, sign)
    return None if x is None else (x, y, 1, x * y % P)


def _expand(secret: bytes):
    if len(secret) != 32:
        raise ValueError("an Ed25519 secret key is 32 bytes")
    h = hashlib.sha512(secret).digest()
    a = int.from_bytes(h[:32], "little")
    a &= (1 << 254) - 8
    a |= 1 << 254
    return a, h[32:]


def _hash_q(data: bytes) -> int:
    return int.from_bytes(hashlib.sha512(data).digest(), "little") % Q


def public_key(secret: bytes) -> bytes:
    return _compress(_mul(_expand(secret)[0], G))


def sign(secret: bytes, message: bytes) -> bytes:
    try:
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
    except ImportError:
        return sign_reference(secret, message)
    return Ed25519PrivateKey.from_private_bytes(secret).sign(message)


def sign_reference(secret: bytes, message: bytes) -> bytes:
    a, prefix = _expand(secret)
    public = _compress(_mul(a, G))
    r = _hash_q(prefix + message)
    big_r = _compress(_mul(r, G))
    s = (r + _hash_q(big_r + public + message) * a) % Q
    return big_r + int.to_bytes(s, 32, "little")


def verify(public: bytes, message: bytes, signature: bytes) -> bool:
    if len(public) != 32 or len(signature) != 64:
        return False
    a = _decompress(public)
    r = _decompress(signature[:32])
    if a is None or r is None:
        return False
    s = int.from_bytes(signature[32:], "little")
    if s >= Q:
        return False
    h = _hash_q(signature[:32] + public + message)
    return _equal(_mul(s, G), _add(r, _mul(h, a)))


def encode(raw: bytes) -> str:
    """A key or signature as text: "ed25519:" + standard base64."""
    return PREFIX + base64.b64encode(raw).decode("ascii")


def decode(text: str, length: int) -> bytes:
    text = text.strip()
    if not text.startswith(PREFIX):
        raise ValueError("expected an ed25519: value")
    raw = base64.b64decode(text[len(PREFIX):], validate=True)
    if len(raw) != length:
        raise ValueError(f"expected {length} bytes, got {len(raw)}")
    return raw


def fingerprint(public: bytes) -> str:
    """What a person compares: the first 16 hex digits of the key's SHA-256, in groups of four."""
    digest = hashlib.sha256(public).hexdigest()[:16].upper()
    return " ".join(digest[i:i + 4] for i in range(0, 16, 4))
