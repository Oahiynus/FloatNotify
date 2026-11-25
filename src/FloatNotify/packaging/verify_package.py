"""Verify a solid LZMA NSIS release against the build's explicit x64 payload inventory."""
import argparse
import hashlib
import json
import lzma
import os
from pathlib import Path
import struct
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument("installer", type=Path)
parser.add_argument("build", type=Path)
parser.add_argument("sevenzip", type=Path)
parser.add_argument("--forbid", action="append", default=[], help="Private marker checked locally; not stored in the release")
args = parser.parse_args()
here = Path(__file__).resolve().parent
repo = here.parents[2]
notices = {"LICENSE", "THIRD-PARTY-NOTICES.md"} | {
    p.relative_to(repo).as_posix() for p in (repo / "Notices").glob("*.txt")
}
expected = set((here / "files.txt").read_text(encoding="utf-8-sig").splitlines())
archive = args.installer.read_bytes()
inventory = dict(line.split("  ", 1)[::-1] for line in (args.build / "inventory.txt").read_text(encoding="utf-8-sig").splitlines())
if set(inventory) != expected:
    raise ValueError("Build inventory does not match the release file list")

markers = [str(Path.home()), os.environ.get("COMPUTERNAME"), str(args.build.resolve()), str(here.parent), *args.forbid]
needles = set()
for marker in filter(None, markers):
    for variant in {marker, marker.replace("\\", "/"), marker.replace("\\", "\\\\")}:
        for encoding in ("utf-8", "utf-16le", "utf-16be", "gb18030"):
            needles.add(variant.encode(encoding).lower())

def privacy(data, label):
    contents = data.lower()
    if any(word in contents for word in needles):
        raise ValueError(f"Private build or user marker found in {label}")

def amd64(data):
    pe = struct.unpack_from("<I", data, 60)[0]
    return data[pe:pe+4] == b"PE\x00\x00" and struct.unpack_from("<H", data, pe+4)[0] == 0x8664

privacy(archive, "setup stub")
# Also inspect the decompressed NSIS instructions/string table and solid data,
# including items which archive tools treat as installer metadata.
magic = bytes.fromhex("efbeadde") + b"NullsoftInst"
pos = archive.index(magic) + 24
prop = archive[pos]
remainder, lc = divmod(prop, 9)
pb, lp = divmod(remainder, 5)
dictionary = struct.unpack_from("<I", archive, pos+1)[0]
decoder = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[
    {"id":lzma.FILTER_LZMA1,"dict_size":dictionary,"lc":lc,"lp":lp,"pb":pb}])
solid = decoder.decompress(archive[pos+5:-4])
privacy(solid, "expanded installer instructions and data")

with tempfile.TemporaryDirectory(prefix="FloatNotify-audit-") as temp:
    subprocess.run([str(args.sevenzip), "x", str(args.installer.resolve()), "-o"+temp, "-y"], check=True,
                   stdout=subprocess.DEVNULL)
    root = Path(temp)
    plugin = root / "$PLUGINSDIR"
    payload = plugin / "payload"
    actual = {p.relative_to(payload).as_posix() for p in payload.rglob("*") if p.is_file()}
    if actual != expected:
        raise ValueError(f"Unexpected archive contents: {actual ^ expected}")
    if not notices.issubset(actual):
        raise ValueError(f"Missing distribution notices: {notices - actual}")
    support = {"InstallSupport.ps1", "inventory.txt", "modern-wizard.bmp", "nsDialogs.dll", "nsExec.dll", "System.dll"}
    wrapper = {p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file() and not p.is_relative_to(payload)}
    if wrapper != {"$PLUGINSDIR/" + s for s in support}:
        raise ValueError(f"Unexpected setup resources: {wrapper}")
    for part in root.rglob("*"):
        if part.is_file():
            privacy(part.read_bytes(), str(part.relative_to(root)))
    for rel in sorted(actual):
        data = (payload / rel).read_bytes()
        if rel in notices and data != (repo / rel).read_bytes():
            raise ValueError(f"Packaged notice differs from repository: {rel}")
        if hashlib.sha256(data).hexdigest().upper() != inventory[rel] or data != (args.build / "payload" / rel).read_bytes():
            raise ValueError(f"File differs from clean staged build: {rel}")
    for rel in ("FloatNotify.exe", "FloatNotify.dll", "GlassBackdrop.dll", "WebView2Loader.dll"):
        if not amd64((payload / rel).read_bytes()):
            raise ValueError(f"Not an x64 binary: {rel}")
    deps = json.loads((payload / "FloatNotify.deps.json").read_text(encoding="utf-8-sig"))
    if not deps["runtimeTarget"]["name"].endswith("/win-x64"):
        raise ValueError("Wrong .NET runtime target")
    for name in ("InstallSupport.ps1", "inventory.txt"):
        if (plugin / name).read_bytes() != (args.build / name).read_bytes():
            raise ValueError(f"Installer support mismatch: {name}")
print(f"PASS {len(expected)} application files, {len(support)} setup resources, x64 headers, hashes, private markers")
print("SHA256", hashlib.sha256(archive).hexdigest())

