"""One-time: makes TaskMaster's update signing key (ECDSA P-256). Prints the public key to paste into Updater.cs.
The private key stays in %USERPROFILE%\\.taskmaster\\updater-key.pem and is never committed; back it up."""
import base64, os, sys
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import serialization

path = os.path.join(os.path.expanduser("~"), ".taskmaster", "updater-key.pem")
if os.path.exists(path):
    sys.exit(f"{path} already exists; not overwriting it.")
os.makedirs(os.path.dirname(path), exist_ok=True)
key = ec.generate_private_key(ec.SECP256R1())
with open(path, "wb") as f:
    f.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
pub = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
print(base64.b64encode(pub).decode())
