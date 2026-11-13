import struct
import tempfile
import unittest
from pathlib import Path
from review_runtime import ROOT, RUNTIME_FILES, audit, declaration, manifest_field, pe_summary, versions


class RuntimeReviewTests(unittest.TestCase):
    def test_manifest_ambiguity(self):
        self.assertEqual(manifest_field('"buildid" "26496310"', "buildid"), "26496310")
        self.assertIsNone(manifest_field('"buildid" "26496310" "buildid" "25330290"', "buildid"))
        self.assertIsNone(manifest_field('"buildid" "26496310 beta"', "buildid"))
        self.assertIsNone(manifest_field('', "buildid"))
        self.assertEqual(manifest_field('"BetaKey" "beta" "BetaKey" "beta"', "BetaKey"), "beta")

    def test_truncated_or_hostile_pe(self):
        for data in (b"", b"MZ", bytes(64), b"MZ" + bytes(58) + b"\xff" * 4):
            with self.assertRaises(ValueError):
                pe_summary(data)

    def test_architecture_is_inventory_only(self):
        data = bytearray(256)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 60, 64)
        data[64:68] = b"PE\0\0"
        struct.pack_into("<HH", data, 68, 0x8664, 1)
        struct.pack_into("<H", data, 84, 2)
        struct.pack_into("<H", data, 88, 0x20B)
        self.assertTrue(pe_summary(data)["windowsX64"])
        struct.pack_into("<H", data, 68, 0x14C)
        self.assertFalse(pe_summary(data)["windowsX64"])
        struct.pack_into("<H", data, 70, 97)
        with self.assertRaises(ValueError):
            pe_summary(data)

    def test_declaration_comparison_preserves_signatures_and_order(self):
        first = 'class Example { /* comment */ virtual void A(int x) = 0; };'
        self.assertEqual(declaration(first, "Example"), declaration('class Example { virtual void A( int x )=0; };', "Example"))
        self.assertNotEqual(declaration(first, "Example"), declaration('class Example { virtual void A(float x)=0; };', "Example"))
        with self.assertRaises(ValueError):
            declaration('class Example {', "Example")
        with self.assertRaises(ValueError):
            versions('IVRServerDriverHost_Version = "IVRServerDriverHost_006";')

    def test_matching_static_evidence_never_certifies_runtime(self):
        fixture_parent = ROOT / "reports"
        fixture_parent.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="switcheroonie-runtime-review-", dir=fixture_parent) as temporary:
            root = Path(temporary).resolve()
            self.assertTrue(root.is_relative_to(fixture_parent.resolve()))
            self.assertTrue(root.name.startswith("switcheroonie-runtime-review-"))
            pinned = ROOT / "third_party/openvr/headers/openvr_driver.h"
            data = bytearray(256)
            data[:2] = b"MZ"
            struct.pack_into("<I", data, 60, 64)
            data[64:68] = b"PE\0\0"
            struct.pack_into("<HH", data, 68, 0x8664, 1)
            struct.pack_into("<H", data, 84, 2)
            struct.pack_into("<H", data, 88, 0x20B)
            for version in versions(pinned.read_text()).values():
                data.extend(version.encode() + b"\0")
            for relative in RUNTIME_FILES:
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
            manifest = root / "fixture.acf"
            for build, matches in (("26496310", False), ("25330290", True)):
                manifest.write_text(f'"buildid" "{build}" "BetaKey" "fixture"')
                result = audit(root, manifest, pinned, ROOT / "native/runtime_admission.hpp", pinned)
                self.assertEqual(result["buildMatchesGate"], matches)
                self.assertFalse(result["compatibilityEstablished"])
                self.assertTrue(result["referenceHeader"]["headerTextMatchesIgnoringLineEndings"])
                self.assertTrue(all(all(file["interfaceStringOccurrences"].values()) for file in result["files"]))


if __name__ == "__main__":
    unittest.main()
