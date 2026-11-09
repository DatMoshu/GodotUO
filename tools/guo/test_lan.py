"""LAN CA key identifiers and strict TLS verification. No client data.

The legacy fixture is the pre-fix CA: basic constraints and key usage only.
OpenSSL strict mode rejects it. ensure_certificates must reissue inside the
folder it was given, keep a matching private key, and still verify the
hostname.
"""
import datetime
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

from guo import lan


def _pem():
    return serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()


def _write_legacy_pair(folder: Path) -> None:
    """A CA and server certificate shaped like GUO before key identifiers."""
    folder.mkdir(parents=True, exist_ok=True)
    now = datetime.datetime.now(datetime.timezone.utc)
    ca_key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, f"{lan.CA_NAME} (legacy)")])
    ca = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
          .public_key(ca_key.public_key()).serial_number(x509.random_serial_number())
          .not_valid_before(now - datetime.timedelta(days=1))
          .not_valid_after(now + datetime.timedelta(days=3650))
          .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
          .add_extension(x509.KeyUsage(
              digital_signature=True, key_cert_sign=True, crl_sign=True,
              content_commitment=False, key_encipherment=False, data_encipherment=False,
              key_agreement=False, encipher_only=False, decipher_only=False), critical=True)
          .sign(ca_key, hashes.SHA256()))
    host = socket.gethostname()
    key = ec.generate_private_key(ec.SECP256R1())
    names = [x509.IPAddress(__import__("ipaddress").ip_address("127.0.0.1")), x509.DNSName("localhost"),
             x509.DNSName(host), x509.DNSName(f"{host}.local")]
    cert = (x509.CertificateBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, host)]))
            .issuer_name(ca.subject).public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=1))
            .not_valid_after(now + datetime.timedelta(days=397))
            .add_extension(x509.SubjectAlternativeName(names), critical=False)
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .sign(ca_key, hashes.SHA256()))
    encoding, fmt, encryption = _pem()
    (folder / "guo-ca.key").write_bytes(ca_key.private_bytes(encoding, fmt, encryption))
    (folder / "guo-ca.crt").write_bytes(ca.public_bytes(encoding))
    (folder / "server.key").write_bytes(key.private_bytes(encoding, fmt, encryption))
    (folder / "server.crt").write_bytes(cert.public_bytes(encoding))


def _handshake(ca: Path, cert: Path, key: Path, hostname: str, *, strict: bool, cafile: bool):
    server = lan.server_context(cert, key)
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    sock.listen(1)
    port = sock.getsockname()[1]

    def serve():
        conn, _ = sock.accept()
        try:
            tls = server.wrap_socket(conn, server_side=True)
            tls.recv(16)
            tls.close()
        except Exception:
            pass
        finally:
            sock.close()

    threading.Thread(target=serve, daemon=True).start()
    trust = ssl.create_default_context(cafile=str(ca) if cafile else None)
    if strict:
        trust.verify_flags |= ssl.VERIFY_X509_STRICT
    if not trust.check_hostname or trust.verify_mode != ssl.CERT_REQUIRED:
        raise AssertionError("hostname or certificate verification was turned off")
    with trust.wrap_socket(socket.create_connection(("127.0.0.1", port), timeout=5), server_hostname=hostname) as tls:
        tls.send(b"ping")


class LanCertificateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="guo-lan-test-")
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)

    def test_legacy_ca_fails_strict_verification_and_is_reissued_in_place(self):
        _write_legacy_pair(self.folder)
        note = self.folder / "user-note.txt"
        note.write_text("leave me", encoding="utf-8")
        key_before = (self.folder / "guo-ca.key").read_bytes()
        with self.assertRaises(ssl.SSLCertVerificationError) as caught:
            _handshake(self.folder / "guo-ca.crt", self.folder / "server.crt", self.folder / "server.key",
                       "127.0.0.1", strict=True, cafile=True)
        self.assertIn("Authority Key Identifier", str(caught.exception))

        ca, cert, key = lan.ensure_certificates(self.folder, "127.0.0.1")
        self.assertEqual((self.folder / "guo-ca.key").read_bytes(), key_before)
        self.assertEqual(note.read_text(encoding="utf-8"), "leave me")
        ca_cert = x509.load_pem_x509_certificate(ca.read_bytes())
        server = x509.load_pem_x509_certificate(cert.read_bytes())
        subject_key = ca_cert.extensions.get_extension_for_class(x509.SubjectKeyIdentifier).value
        authority_key = server.extensions.get_extension_for_class(x509.AuthorityKeyIdentifier).value
        self.assertEqual(authority_key.key_identifier, subject_key.digest)
        server.verify_directly_issued_by(ca_cert)
        _handshake(ca, cert, key, "127.0.0.1", strict=True, cafile=True)
        openssl = __import__("shutil").which("openssl")
        if openssl:
            checked = subprocess.run(
                [openssl, "verify", "-x509_strict", "-CAfile", str(ca), "-verify_ip", "127.0.0.1", str(cert)],
                capture_output=True, text=True, check=False)
            self.assertEqual(checked.returncode, 0, checked.stderr)

    def test_conformant_ca_is_kept_and_a_leaf_without_aki_is_replaced(self):
        ca, cert, key = lan.ensure_certificates(self.folder, "127.0.0.1")
        first = ca.read_bytes(), (self.folder / "guo-ca.key").read_bytes(), cert.read_bytes()
        again, _, _ = lan.ensure_certificates(self.folder, "127.0.0.1")
        self.assertEqual(again.read_bytes(), first[0])
        self.assertEqual((self.folder / "guo-ca.key").read_bytes(), first[1])
        self.assertEqual(cert.read_bytes(), first[2])

        # Re-sign the leaf with the same CA key but drop the authority key identifier.
        now = datetime.datetime.now(datetime.timezone.utc)
        ca_key = serialization.load_pem_private_key((self.folder / "guo-ca.key").read_bytes(), None)
        ca_cert = x509.load_pem_x509_certificate(ca.read_bytes())
        leaf_key = serialization.load_pem_private_key(key.read_bytes(), None)
        host = socket.gethostname()
        bare = (x509.CertificateBuilder()
                .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, host)]))
                .issuer_name(ca_cert.subject).public_key(leaf_key.public_key())
                .serial_number(x509.random_serial_number())
                .not_valid_before(now - datetime.timedelta(days=1))
                .not_valid_after(now + datetime.timedelta(days=200))
                .add_extension(x509.SubjectAlternativeName([
                    x509.IPAddress(__import__("ipaddress").ip_address("127.0.0.1")),
                    x509.DNSName("localhost"), x509.DNSName(host), x509.DNSName(f"{host}.local")]), critical=False)
                .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
                .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
                .sign(ca_key, hashes.SHA256()))
        cert.write_bytes(bare.public_bytes(serialization.Encoding.PEM))
        lan.ensure_certificates(self.folder, "127.0.0.1")
        self.assertEqual(ca.read_bytes(), first[0])
        self.assertEqual((self.folder / "guo-ca.key").read_bytes(), first[1])
        self.assertNotEqual(cert.read_bytes(), bare.public_bytes(serialization.Encoding.PEM))
        _handshake(ca, cert, key, "127.0.0.1", strict=True, cafile=True)

    def test_wrong_name_and_unreadable_key_and_links_stay_put(self):
        now = datetime.datetime.now(datetime.timezone.utc)
        ca_key = ec.generate_private_key(ec.SECP256R1())
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Other CA")])
        ca = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
              .public_key(ca_key.public_key()).serial_number(x509.random_serial_number())
              .not_valid_before(now - datetime.timedelta(days=1))
              .not_valid_after(now + datetime.timedelta(days=30))
              .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
              .sign(ca_key, hashes.SHA256()))
        encoding, fmt, encryption = _pem()
        other = self.folder / "other"
        other.mkdir()
        note = other / "note.txt"
        note.write_text("outside", encoding="utf-8")
        (other / "guo-ca.key").write_bytes(ca_key.private_bytes(encoding, fmt, encryption))
        (other / "guo-ca.crt").write_bytes(ca.public_bytes(encoding))
        lan.ensure_certificates(other, "127.0.0.1")
        self.assertEqual(note.read_text(encoding="utf-8"), "outside")
        self.assertNotEqual((other / "guo-ca.key").read_bytes(), ca_key.private_bytes(encoding, fmt, encryption))

        locked = self.folder / "locked"
        locked.mkdir()
        secret = ca_key.private_bytes(
            encoding, fmt, serialization.BestAvailableEncryption(b"not-a-real-password"))
        (locked / "guo-ca.key").write_bytes(secret)
        (locked / "guo-ca.crt").write_bytes(ca.public_bytes(encoding))
        with self.assertRaises(TypeError):
            lan.ensure_certificates(locked, "127.0.0.1")
        self.assertEqual((locked / "guo-ca.key").read_bytes(), secret)

        linked = self.folder / "linked"
        linked.mkdir()
        target = self.folder / "outside.crt"
        target.write_bytes(b"do not replace")
        (linked / "guo-ca.key").write_bytes(ca_key.private_bytes(encoding, fmt, encryption))
        try:
            (linked / "guo-ca.crt").symlink_to(target)
        except OSError:
            self.skipTest("this platform refused a symlink")
        with self.assertRaises(OSError):
            lan.ensure_certificates(linked, "127.0.0.1")
        self.assertEqual(target.read_bytes(), b"do not replace")

    def test_strict_context_still_rejects_the_wrong_name_and_an_unknown_ca(self):
        ca, cert, key = lan.ensure_certificates(self.folder, "127.0.0.1")
        with self.assertRaises(ssl.SSLCertVerificationError):
            _handshake(ca, cert, key, "not-this-host.example", strict=True, cafile=True)
        with self.assertRaises(ssl.SSLCertVerificationError):
            _handshake(ca, cert, key, "127.0.0.1", strict=True, cafile=False)


if __name__ == "__main__":
    unittest.main()
