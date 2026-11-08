"""Builds a TaskMaster release: publishes the app, zips it, signs the zip and writes latest.json.

    python -I tools/release.py            (version from TaskManager.csproj; notes from release/<v>/notes.md)

Output in release/<v>/: TaskMaster-<v>.zip, TaskMaster-<v>.zip.sig, latest.json. Upload all three to the GitHub
release tagged v<v>; the app reads releases/latest/download/latest.json and checks the signature before installing.
The private key is %USERPROFILE%\\.taskmaster\\updater-key.pem (tools/keygen.py), never in the repo.
"""
import base64, json, os, re, subprocess, sys, zipfile
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(ROOT, "src", "TaskManager", "TaskManager.csproj")
REPO = "SteelArchadian/TaskMaster"
KEY = os.path.join(os.path.expanduser("~"), ".taskmaster", "updater-key.pem")

version = re.search(r"<Version>([^<]+)</Version>", open(PROJ, encoding="utf-8").read()).group(1)
out = os.path.join(ROOT, "release", version)
notes_path = os.path.join(out, "notes.md")
if not os.path.exists(notes_path):
    sys.exit(f"Write the release notes first: {notes_path}")
notes = open(notes_path, encoding="utf-8").read().strip()

app = os.path.join(out, "app")
subprocess.run(["dotnet", "publish", PROJ, "-c", "Release", "-o", app, "--nologo", "-v", "q"], check=True)
if not os.path.exists(os.path.join(app, "TaskMaster.exe")):
    sys.exit("publish produced no TaskMaster.exe")

zip_name = f"TaskMaster-{version}.zip"
zip_path = os.path.join(out, zip_name)
with zipfile.ZipFile(zip_path + ".tmp", "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for base, _, files in os.walk(app):
        for f in files:
            if f.endswith(".pdb"):
                continue
            full = os.path.join(base, f)
            z.write(full, os.path.relpath(full, app))
os.replace(zip_path + ".tmp", zip_path)

key = serialization.load_pem_private_key(open(KEY, "rb").read(), password=None)
data = open(zip_path, "rb").read()
sig = base64.b64encode(key.sign(data, ec.ECDSA(hashes.SHA256()))).decode()
key.public_key().verify(base64.b64decode(sig), data, ec.ECDSA(hashes.SHA256()))
open(zip_path + ".sig", "w").write(sig)

latest = {
    "version": version,
    "notes": notes,
    "url": f"https://github.com/{REPO}/releases/download/v{version}/{zip_name}",
    "sig": sig,
}
json.dump(latest, open(os.path.join(out, "latest.json"), "w", encoding="utf-8"), indent=2)
print(f"release {version}: {zip_path} ({len(data) / 1e6:.1f} MB), signed")
