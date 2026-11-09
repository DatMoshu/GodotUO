"""LAN mode for the web client: a phone on the same network plays from this PC.

Off unless UO_WEB_LAN=1 (config.local.bat) or --lan is given. Then:

- the web server (tools/web serve) and the shard's WebSocket bridge
  (tools/ws_bridge serve) listen on this PC's private LAN address only, never
  on every interface, and refuse any connection that does not come from a
  private or loopback address, so a router port-forward still reaches nothing;
- both speak TLS (https, wss): a phone's browser gives cross-origin isolation,
  which a Godot web export needs, only to a secure origin, and anything but
  localhost is only secure over HTTPS;
- the certificate is signed by a local CA made here, into
  build/web/lan_certs (gitignored). The phone trusts that CA after it is
  installed from /guo-ca.crt (tools/web/README.md, "Play on a phone").
  A CA without a subject key identifier and an authority key identifier
  is reissued in that folder; the key is kept when it still matches.

The address is found at run time (UO_WEB_LAN_HOST overrides it, in
config.local.bat) and is written nowhere in the repository: no LAN address is
ever committed.
"""
from __future__ import annotations

import datetime
import ipaddress
import os
import socket
import ssl
from pathlib import Path


# What the phone shows in its list of trusted certificates.
CA_NAME = "GUO local dev CA"


def is_private(address: str) -> bool:
    """A private (RFC 1918, link-local, ULA) or loopback address; IPv4-mapped IPv6 as its IPv4."""
    try:
        ip = ipaddress.ip_address(address.split("%", 1)[0])
    except ValueError:
        return False
    if isinstance(ip, ipaddress.IPv6Address) and ip.ipv4_mapped:
        ip = ip.ipv4_mapped
    return ip.is_loopback or (ip.is_private and not ip.is_unspecified and not ip.is_reserved)


def lan_address(override: str = "") -> str:
    """This PC's private IPv4 address on the LAN (the one its default route uses), or the override.

    Refuses a public, loopback or unspecified address: LAN mode never listens
    anywhere the internet could reach, and loopback would not reach the phone.
    """
    address = override.strip()
    if not address:
        # A UDP "connect" sends nothing; it only picks the interface a packet
        # to a private network would leave by.
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            try:
                s.connect(("10.255.255.255", 1))
                address = s.getsockname()[0]
            except OSError:
                address = ""
    if not address or not is_private(address) or ipaddress.ip_address(address).is_loopback:
        raise ValueError(f"LAN mode needs this PC's private network address, not {address or 'nothing'}; "
                         "set UO_WEB_LAN_HOST in config.local.bat to the right one")
    return address


def host_names(address: str) -> list[str]:
    """The names a phone may use for this PC in LAN mode: the address, the PC's name and name.local."""
    host = socket.gethostname().lower()
    return [address, host, f"{host}.local"]


def _write_owned(path: Path, data: bytes) -> None:
    """Replace one file in the LAN certificate folder.

    A symlink is refused. Replacing through it would write whatever the link
    points at, which might be outside this folder.
    """
    if path.is_symlink():
        raise OSError(f"{path} is a symlink; refusing to replace it")
    temporary = path.with_name(path.name + ".tmp")
    if temporary.is_symlink():
        raise OSError(f"{temporary} is a symlink; refusing to write it")
    temporary.write_bytes(data)
    os.replace(temporary, path)


def _public_bytes(public_key) -> bytes:
    from cryptography.hazmat.primitives import serialization
    return public_key.public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo)


def _key_matches(certificate, key) -> bool:
    return _public_bytes(certificate.public_key()) == _public_bytes(key.public_key())


def _extension(certificate, kind):
    from cryptography import x509
    try:
        return certificate.extensions.get_extension_for_class(kind).value
    except x509.ExtensionNotFound:
        return None


def _ca_reusable(certificate, key) -> bool:
    """A cached CA we can keep: our name, its own key, and RFC 5280 key ids.

    OpenSSL's strict verifier (ssl.VERIFY_X509_STRICT, `openssl verify
    -x509_strict`) rejects a CA with no subject key identifier and a leaf
    with no authority key identifier. A CA from before those extensions
    existed cannot stay on disk as the trust anchor.
    """
    from cryptography import x509
    if not _key_matches(certificate, key):
        return False
    if not certificate.subject.rfc4514_string().startswith(f"CN={CA_NAME}"):
        return False
    basic = _extension(certificate, x509.BasicConstraints)
    subject_key = _extension(certificate, x509.SubjectKeyIdentifier)
    authority_key = _extension(certificate, x509.AuthorityKeyIdentifier)
    if basic is None or not basic.ca or subject_key is None or authority_key is None:
        return False
    return authority_key.key_identifier == subject_key.digest


def _issue_ca(ca_key, now):
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes
    from cryptography.x509.oid import NameOID
    subject_key = x509.SubjectKeyIdentifier.from_public_key(ca_key.public_key())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, f"{CA_NAME} ({socket.gethostname()})")])
    return (x509.CertificateBuilder().subject_name(name).issuer_name(name)
            .public_key(ca_key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=3650))
            .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
            .add_extension(x509.KeyUsage(digital_signature=True, key_cert_sign=True, crl_sign=True,
                                         content_commitment=False, key_encipherment=False, data_encipherment=False,
                                         key_agreement=False, encipher_only=False, decipher_only=False), critical=True)
            .add_extension(subject_key, critical=False)
            .add_extension(x509.AuthorityKeyIdentifier.from_issuer_subject_key_identifier(subject_key), critical=False)
            .sign(ca_key, hashes.SHA256()))


def _server_reusable(certificate, ca, wanted, now) -> bool:
    from cryptography import x509
    from cryptography.exceptions import InvalidSignature
    san = _extension(certificate, x509.SubjectAlternativeName)
    authority_key = _extension(certificate, x509.AuthorityKeyIdentifier)
    ca_subject_key = _extension(ca, x509.SubjectKeyIdentifier)
    if san is None or authority_key is None or ca_subject_key is None:
        return False
    have = {str(v) for v in san.get_values_for_type(x509.IPAddress)} | \
           {v.lower() for v in san.get_values_for_type(x509.DNSName)}
    # not_valid_after_utc needs cryptography 42+; older ones have the naive UTC one.
    expires = getattr(certificate, "not_valid_after_utc", None) or certificate.not_valid_after.replace(tzinfo=datetime.timezone.utc)
    fresh = expires - now > datetime.timedelta(days=30)
    if not (have >= wanted and fresh and certificate.issuer == ca.subject):
        return False
    if authority_key.key_identifier != ca_subject_key.digest:
        return False
    try:
        certificate.verify_directly_issued_by(ca)
    except (InvalidSignature, ValueError):
        return False
    return True


def _issue_server(key, ca, host, address, now, ca_key):
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes
    from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID
    ca_subject_key = _extension(ca, x509.SubjectKeyIdentifier)
    names = [x509.IPAddress(ipaddress.ip_address(a)) for a in (address, "127.0.0.1")]
    names += [x509.DNSName(n) for n in (host, f"{host}.local", "localhost")]
    # 397 days: the longest a browser accepts for a server certificate.
    return (x509.CertificateBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, host)]))
            .issuer_name(ca.subject).public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=397))
            .add_extension(x509.SubjectAlternativeName(names), critical=False)
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False)
            .add_extension(x509.AuthorityKeyIdentifier.from_issuer_subject_key_identifier(ca_subject_key), critical=False)
            .sign(ca_key, hashes.SHA256()))


def ensure_certificates(folder: Path, address: str) -> tuple[Path, Path, Path]:
    """(CA certificate, server certificate, server key) under folder, made or renewed as needed.

    The CA is kept once it carries a subject key identifier and an authority
    key identifier, so the phone installs it once. A cached CA without those
    extensions is reissued in this folder. Its private key is kept when the
    key still matches the certificate. Nothing outside the folder is written.
    The server certificate names this address, the PC's name and localhost,
    and is made again when the address changes, it is within a month of
    expiring, or it has no authority key identifier for the current CA.
    """
    from cryptography import x509
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric import ec

    folder.mkdir(parents=True, exist_ok=True)
    ca_key_file, ca_file = folder / "guo-ca.key", folder / "guo-ca.crt"
    key_file, cert_file = folder / "server.key", folder / "server.crt"
    for path in (ca_key_file, ca_file, key_file, cert_file):
        if path.is_symlink():
            raise OSError(f"{path} is a symlink; refusing to use it")
    now = datetime.datetime.now(datetime.timezone.utc)
    pem = serialization.Encoding.PEM
    plain = serialization.NoEncryption()
    pkcs8 = serialization.PrivateFormat.PKCS8

    ca = None
    ca_key = None
    if ca_file.exists() and ca_key_file.exists():
        # Unreadable or encrypted material is left in place: this raises
        # instead of replacing a key it could not parse.
        ca_key = serialization.load_pem_private_key(ca_key_file.read_bytes(), None)
        ca = x509.load_pem_x509_certificate(ca_file.read_bytes())
        if not _ca_reusable(ca, ca_key):
            if _key_matches(ca, ca_key) and ca.subject.rfc4514_string().startswith(f"CN={CA_NAME}"):
                ca = _issue_ca(ca_key, now)
                _write_owned(ca_file, ca.public_bytes(pem))
            else:
                ca = None
                ca_key = None
    if ca is None:
        ca_key = ec.generate_private_key(ec.SECP256R1())
        ca = _issue_ca(ca_key, now)
        _write_owned(ca_key_file, ca_key.private_bytes(pem, pkcs8, plain))
        _write_owned(ca_file, ca.public_bytes(pem))

    host = socket.gethostname()
    wanted = {address, "127.0.0.1", host.lower(), f"{host.lower()}.local", "localhost"}
    if cert_file.exists() and key_file.exists():
        cert = x509.load_pem_x509_certificate(cert_file.read_bytes())
        if _server_reusable(cert, ca, wanted, now):
            return ca_file, cert_file, key_file

    key = ec.generate_private_key(ec.SECP256R1())
    cert = _issue_server(key, ca, host, address, now, ca_key)
    _write_owned(key_file, key.private_bytes(pem, pkcs8, plain))
    _write_owned(cert_file, cert.public_bytes(pem))
    return ca_file, cert_file, key_file


def server_context(cert_file: Path, key_file: Path) -> ssl.SSLContext:
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(str(cert_file), str(key_file))
    return ctx
