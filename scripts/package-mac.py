"""Create portable .app ZIPs with Unix executable permissions from publish output."""
import argparse
import pathlib
import plistlib
import shutil
import struct
import json
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--arch", choices=["arm64", "x64"], required=True)
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parent.parent
source = root / "artifacts" / f"Watchroom-Mac-{args.arch}"
if not (source / "Watchroom.Mac").is_file():
    raise SystemExit("Publish the selected macOS architecture first.")
header = (source / "Watchroom.Mac").read_bytes()[:8]
expected = 0x0100000C if args.arch == "arm64" else 0x01000007
if struct.unpack("<II", header) != (0xFEEDFACF, expected):
    raise SystemExit("App executable does not match the selected Mac architecture.")
bundle = root / "artifacts" / f"Watchroom-{args.arch}" / "Watchroom.app"
contents = bundle / "Contents"
macos = contents / "MacOS"
resources = contents / "Resources"
macos.mkdir(parents=True, exist_ok=True)
resources.mkdir(parents=True, exist_ok=True)
for file in source.iterdir():
    if file.name == "libvlc":
        continue  # Native VLC is installed separately; never ship Windows VLC.
    if file.is_dir():
        shutil.copytree(file, macos / file.name, dirs_exist_ok=True)
    else:
        shutil.copy2(file, macos / file.name)
with (contents / "Info.plist").open("wb") as file:
    plistlib.dump({
        "CFBundleExecutable": "Watchroom.Mac",
        "CFBundleName": "Watchroom",
        "CFBundleDisplayName": "Watchroom",
        "CFBundleIdentifier": "app.watchroom.desktop",
        "CFBundlePackageType": "APPL",
        "CFBundleVersion": "0.2.0",
        "CFBundleShortVersionString": "0.2.0",
        "LSMinimumSystemVersion": "14.0",
        "NSHighResolutionCapable": True,
        "NSLocalNetworkUsageDescription": "Watchroom connects to friends and streams your selected movie over an encrypted connection.",
        "NSPrincipalClass": "NSApplication"
    }, file)
for filename in ["MAC-CLIENT.md", "THIRD-PARTY-NOTICES.md"]:
    if (root / filename).exists():
        shutil.copy2(root / filename, resources / filename)
lock = json.loads((root / "src/Watchroom.Mac/packages.lock.json").read_text())
for dependencies in lock["dependencies"].values():
    for package, details in dependencies.items():
        version = details.get("resolved")
        if not version:
            continue
        package_dir = root / ".nuget/packages" / package.lower() / version
        if not package_dir.is_dir():
            continue
        for file in package_dir.iterdir():
            if file.is_file() and (any(word in file.name.lower() for word in ["license", "copying", "notice"]) or file.suffix == ".nuspec"):
                destination = resources / "licenses" / f"{package}-{version}"
                destination.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, destination / file.name)
archive = root / "artifacts" / f"Watchroom-Mac-{args.arch}-0.2.0.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zip:
    for file in bundle.rglob("*"):
        relative = file.relative_to(bundle.parent).as_posix()
        if file.is_dir():
            info = zipfile.ZipInfo(relative + "/")
            info.create_system = 3
            info.external_attr = (0o40755 << 16) | 0x10
            zip.writestr(info, b"")
        else:
            info = zipfile.ZipInfo.from_file(file, relative)
            info.create_system = 3
            executable = file.name == "Watchroom.Mac" or file.suffix == ".dylib"
            info.external_attr = (0o100755 if executable else 0o100644) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            zip.writestr(info, file.read_bytes())
with zipfile.ZipFile(archive) as zip:
    executable = zip.getinfo("Watchroom.app/Contents/MacOS/Watchroom.Mac")
    assert executable.external_attr >> 16 & 0o111
    plist = plistlib.loads(zip.read("Watchroom.app/Contents/Info.plist"))
    assert plist["CFBundleExecutable"] == "Watchroom.Mac"
print(archive)
