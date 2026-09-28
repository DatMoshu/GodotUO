"""LAN mode for the web client: a phone on the same network plays from this PC.

Off unless UO_WEB_LAN=1 (config.local.bat) or --lan is given. Then:

- the web server (tools/web serve) and the shard's WebSocket bridge
  (tools/ws_bridge serve) listen on this PC's private LAN address only, never
  on every interface, and refuse any connection that does not come from a
  private or loopback address, so a router port-forward still reaches nothing;
- both speak TLS (https, wss): a phone's browser gives cross-origin isolation,
  which a Godot web export needs, only to a secure origin, and anything but
  localhost is only secure over HTTPS;
- the certificate is signed by a local CA made here, once, into
  build/web/lan_certs (gitignored). The phone trusts that CA after it is
  installed from /guo-ca.crt (tools/web/README.md, "Play on a phone").

The address is found at run time (UO_WEB_LAN_HOST overrides it, in
config.local.bat) and is written nowhere in the repository: no LAN address is
ever committed.
"""
from __future__ import annotations

import datetime
import ipaddress
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


def ensure_certificates(folder: Path, address: str) -> tuple[Path, Path, Path]:
    """(CA certificate, server certificate, server key) under folder, made or renewed as needed.

    The CA is made once and kept, so the phone installs it once. The server
    certificate names this address, the PC's name and localhost; it is made
    again when the address changes or it is within a month of expiring.
    """
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

    folder.mkdir(parents=True, exist_ok=True)
    ca_key_file, ca_file = folder / "guo-ca.key", folder / "guo-ca.crt"
    key_file, cert_file = folder / "server.key", folder / "server.crt"
    now = datetime.datetime.now(datetime.timezone.utc)
    pem = serialization.Encoding.PEM
    plain = serialization.NoEncryption()
    pkcs8 = serialization.PrivateFormat.PKCS8

    ca = None
    if ca_file.exists() and ca_key_file.exists():
        ca_key = serialization.load_pem_private_key(ca_key_file.read_bytes(), None)
        ca = x509.load_pem_x509_certificate(ca_file.read_bytes())
        # One made before the CA had its present name is made again.
        if not ca.subject.rfc4514_string().startswith(f"CN={CA_NAME}"):
            ca = None
    if ca is None:
        ca_key = ec.generate_private_key(ec.SECP256R1())
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, f"{CA_NAME} ({socket.gethostname()})")])
        ca = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
              .public_key(ca_key.public_key()).serial_number(x509.random_serial_number())
              .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=3650))
              .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
              .add_extension(x509.KeyUsage(digital_signature=True, key_cert_sign=True, crl_sign=True,
                                           content_commitment=False, key_encipherment=False, data_encipherment=False,
                                           key_agreement=False, encipher_only=False, decipher_only=False), critical=True)
              .sign(ca_key, hashes.SHA256()))
        ca_key_file.write_bytes(ca_key.private_bytes(pem, pkcs8, plain))
        ca_file.write_bytes(ca.public_bytes(pem))

    host = socket.gethostname()
    wanted = {address, "127.0.0.1", host.lower(), f"{host.lower()}.local", "localhost"}
    if cert_file.exists() and key_file.exists():
        cert = x509.load_pem_x509_certificate(cert_file.read_bytes())
        san = cert.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
        have = {str(v) for v in san.get_values_for_type(x509.IPAddress)} | \
               {v.lower() for v in san.get_values_for_type(x509.DNSName)}
        fresh = cert.not_valid_after_utc - now > datetime.timedelta(days=30)
        if have >= wanted and fresh and cert.issuer == ca.subject:
            return ca_file, cert_file, key_file

    key = ec.generate_private_key(ec.SECP256R1())
    names = [x509.IPAddress(ipaddress.ip_address(a)) for a in (address, "127.0.0.1")]
    names += [x509.DNSName(n) for n in (host, f"{host}.local", "localhost")]
    # 397 days: the longest a browser accepts for a server certificate.
    cert = (x509.CertificateBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, host)]))
            .issuer_name(ca.subject).public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=397))
            .add_extension(x509.SubjectAlternativeName(names), critical=False)
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .sign(ca_key, hashes.SHA256()))
    key_file.write_bytes(key.private_bytes(pem, pkcs8, plain))
    cert_file.write_bytes(cert.public_bytes(pem))
    return ca_file, cert_file, key_file


def server_context(cert_file: Path, key_file: Path) -> ssl.SSLContext:
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(str(cert_file), str(key_file))
    return ctx
