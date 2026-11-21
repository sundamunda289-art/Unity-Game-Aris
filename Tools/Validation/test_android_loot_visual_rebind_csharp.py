#!/usr/bin/env python3
"""Compile/exercise the *actual* AarisWeaponPickup rebind method with Mono.

Uses minimal UnityEngine stubs to prove same-gun type/state invariants.
Not a Unity player compile, rendering or Android device FPS test.
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisWeaponPickup.cs").read_text()


class PickupRebindCSharpContract(unittest.TestCase):
    def test_production_method_type_safe_and_compatible(self):
        first = SOURCE.index("internal bool RebindForStreaming(")
        last = SOURCE.index("        private void Update()", first)
        actual_method = SOURCE[first:last]
        editor = os.getenv("AARIS_UNITY_EDITOR_DATA")
        if editor:
            bin_dir = Path(editor) / "MonoBleedingEdge/bin"
            mcs = str(bin_dir / "mcs")
            mono = str(bin_dir / "mono")
        else:
            mcs = shutil.which("mcs")
            mono = shutil.which("mono")
        if not mcs or not mono or not Path(mcs).is_file() or not Path(mono).is_file():
            self.skipTest("Mono C# compiler unavailable")

        prefix = r'''
using System;
namespace UnityEngine {
    public class GameObject { public bool activeSelf; }
    public struct Quaternion {
        public int marker;
        public static Quaternion identity { get { return new Quaternion(); } }
    }
    public class Transform { public Quaternion localRotation; }
    public class Renderer {}
    public class Camera {}
}
namespace Aaris.MapReview {
    using UnityEngine;
    public sealed class AarisWeaponSpec {
        public string id;
        public AarisWeaponSpec(string name) { id = name; }
    }
    public sealed class AarisWeaponState {
        public AarisWeaponSpec Spec;
        public AarisWeaponState(AarisWeaponSpec spec) { Spec = spec; }
    }
    public sealed class AarisWeaponPickup {
        public GameObject gameObject = new GameObject();
        public bool consumed, reserved, modelWasVisible;
        public AarisWeaponState state;
        public int candidateIndex;
        public Transform model;
        public Renderer[] modelRenderers;
        public float pendingSpinDegrees;
        public float nextModelVisibilityCheck, nextBeaconVisibilityCheck;
        public Camera beaconObserver;
'''
        suffix = r'''
    }
    internal static class Harness {
        static int Main() {
            var ar = new AarisWeaponSpec("rifle");
            var sniper = new AarisWeaponSpec("sniper");
            var original = new AarisWeaponState(ar);
            var replacement = new AarisWeaponState(ar);
            var pickup = new AarisWeaponPickup {
                state = original,
                candidateIndex = 11,
                model = new Transform {localRotation = new Quaternion {marker = 13}},
                modelRenderers = new Renderer[] { new Renderer() },
                pendingSpinDegrees = 71,
                nextModelVisibilityCheck = 12,
                nextBeaconVisibilityCheck = 10,
                beaconObserver = new Camera(),
                modelWasVisible = true,
            };
            if (pickup.RebindForStreaming(new AarisWeaponState(sniper), 21)) return 1;
            if (pickup.candidateIndex != 11 || pickup.state != original) return 2;
            pickup.reserved = true;
            if (pickup.RebindForStreaming(replacement, 22)) return 3;
            pickup.reserved = false;
            pickup.consumed = true;
            if (pickup.RebindForStreaming(replacement, 22)) return 4;
            pickup.consumed = false;
            pickup.gameObject.activeSelf = true;
            if (pickup.RebindForStreaming(replacement, 22)) return 5;
            pickup.gameObject.activeSelf = false;
            if (!pickup.RebindForStreaming(replacement, 22)) return 6;
            if (pickup.state != replacement || pickup.candidateIndex != 22) return 7;
            if (pickup.consumed || pickup.reserved) return 8;
            if (pickup.pendingSpinDegrees != 0 ||
                pickup.model.localRotation.marker != 0) return 9;
            if (pickup.modelWasVisible || pickup.beaconObserver != null ||
                pickup.nextBeaconVisibilityCheck != 0 ||
                pickup.nextModelVisibilityCheck != 0) return 10;
            pickup.modelRenderers = null;
            if (pickup.RebindForStreaming(original, 23)) return 11;
            return 0;
        }
    }
}
'''
        fixture = prefix + actual_method + suffix
        with tempfile.TemporaryDirectory(prefix="aaris-gun-rebind-") as td:
            path = Path(td) / "fixture.cs"
            output = Path(td) / "test.exe"
            path.write_text(fixture, encoding="utf-8")
            subprocess.run([mcs, "-warnaserror", "-out:" + str(output), str(path)],
                           check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           timeout=45)
            subprocess.run([mono, str(output)], check=True, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, timeout=45)


if __name__ == "__main__":
    unittest.main(verbosity=2)
