"""Read-only SteamVR inventory. Never loads DLLs, opens processes or grants admission."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct

ROOT = Path(__file__).resolve().parents[1]
INTERFACES = ("IServerTrackedDeviceProvider", "IVRServerDriverHost", "IVRDriverInput",
              "IVRProperties", "IVRDriverLog", "IVRSettings")
TYPES = INTERFACES + ("DriverPose_t", "HmdQuaternion_t", "HmdMatrix34_t", "VRBoneTransform_t")
RUNTIME_FILES = ("bin/win64/vrserver.exe", "bin/vrclient_x64.dll", "bin/win64/openvr_api.dll")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_bounded(path, limit):
    # The only outputs are the requested report; these inputs are never modified.
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError(f"Input exceeds {limit} byte inspection limit: {path.name}")
    return data


def manifest_field(text, name):
    values = set(re.findall(r'"' + re.escape(name) + r'"\s*"([^"\r\n]*)"', text))
    if len(values) != 1:
        return None
    value = values.pop()
    return value if re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", value) else None


def pe_summary(data):
    if len(data) < 64 or data[:2] != b"MZ":
        raise ValueError("Missing DOS header")
    offset = struct.unpack_from("<I", data, 60)[0]
    if offset > len(data) - 26 or data[offset:offset + 4] != b"PE\0\0":
        raise ValueError("Invalid PE header")
    machine, sections = struct.unpack_from("<HH", data, offset + 4)
    optional_size = struct.unpack_from("<H", data, offset + 20)[0]
    magic = struct.unpack_from("<H", data, offset + 24)[0]
    if sections < 1 or sections > 96 or optional_size < 2 or offset + 24 + optional_size + 40 * sections > len(data):
        raise ValueError("Invalid section table bounds")
    return {"machine": hex(machine), "peMagic": hex(magic), "sections": sections,
            "windowsX64": machine == 0x8664 and magic == 0x20B}


def versions(header):
    result = {}
    for name in INTERFACES:
        matches = re.findall(r'\b' + name + r'_Version\s*=\s*"(' + name + r'_\d+)"', header)
        if len(matches) != 1:
            raise ValueError(f"Missing or ambiguous public interface declaration: {name}")
        result[name] = matches[0]
    return result


def declaration(header, name):
    # Source comparison only, not a C++ parser or binary ABI certification.
    clean = re.sub(r"/\*.*?\*/|//[^\n]*", "", header, flags=re.S)
    match = re.search(r"\b(?:class|struct)\s+" + re.escape(name) + r"\s*\{", clean)
    if not match:
        raise ValueError(f"Missing public declaration: {name}")
    depth, end = 1, match.end()
    while depth and end < len(clean):
        depth += (clean[end] == "{") - (clean[end] == "}")
        end += 1
    if depth:
        raise ValueError("Unclosed declaration")
    return re.sub(r"\s+", "", clean[match.start():end])


def audit(runtime, manifest, pinned_header, admission_header, reference_header=None):
    pinned_bytes = read_bounded(pinned_header, 2_000_000)
    pinned = pinned_bytes.decode("utf-8-sig")
    expected = versions(pinned)
    manifest_bytes = read_bounded(manifest, 262144)
    metadata = manifest_bytes.decode("utf-8-sig")
    build = manifest_field(metadata, "buildid")
    if build is not None and (not build.isascii() or not build.isdigit()):
        build = None
    approved = re.findall(r'SupportedRuntimeBuild\s*=\s*"(\d+)"', read_bounded(admission_header, 262144).decode())
    if len(approved) != 1:
        raise ValueError("Production admission declaration is ambiguous")
    files = []
    for relative in RUNTIME_FILES:
        data = read_bounded(runtime / relative, 64_000_000)
        files.append({"file": relative, "bytes": len(data), "sha256": digest(data), "pe": pe_summary(data),
                      "interfaceStringOccurrences": {key: data.count(value.encode() + b"\0") for key, value in expected.items()}})
    reference = None
    if reference_header:
        reference_bytes = read_bounded(reference_header, 2_000_000)
        source = reference_bytes.decode("utf-8-sig")
        reference = {"sha256": digest(reference_bytes), "interfaces": versions(source),
                     "headerTextMatchesIgnoringLineEndings": pinned.replace("\r\n", "\n") == source.replace("\r\n", "\n"),
                     "normalizedDeclarationMatches": {name: declaration(pinned, name) == declaration(source, name) for name in TYPES}}
    return {"schema": 1, "method": "Static file bytes and public source declarations only; no DLL loading or process access",
            "build": build, "branch": manifest_field(metadata, "BetaKey"), "manifestSha256": digest(manifest_bytes),
            "admittedBuild": approved[0], "buildMatchesGate": build == approved[0],
            "compatibilityEstablished": False,
            "limitations": ["A matching string does not prove interface availability, method ownership, vtable layout or hook patchability.",
                            "Source declaration matches do not prove the installed binary implements that source or behaves equivalently.",
                            "No vendor transport, companion hook, device input, teardown or live headset acceptance was exercised.",
                            "This report never changes or grants runtime admission."],
            "pinnedHeaderSha256": digest(pinned_bytes), "expectedInterfaces": expected,
            "files": files, "referenceHeader": reference}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--steamvr", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--reference-header", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.steamvr, args.manifest, ROOT / "third_party/openvr/headers/openvr_driver.h",
                   ROOT / "native/runtime_admission.hpp", args.reference_header)
    # Refuse an output within the installation or over one of our inputs.
    output = args.output.resolve()
    if output.is_relative_to(args.steamvr.resolve()) or output == args.manifest.resolve() or output.exists():
        raise ValueError("Report must be a new file outside the runtime installation")
    with output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
        stream.write("\n")
    print(json.dumps({"build": result["build"], "branch": result["branch"],
                      "buildMatchesGate": result["buildMatchesGate"], "compatibilityEstablished": False,
                      "report": str(output)}))


if __name__ == "__main__":
    main()
