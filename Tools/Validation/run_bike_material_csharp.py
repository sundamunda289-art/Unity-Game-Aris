#!/usr/bin/env python3
"""Compile/test the production bike material coroutine with Mono stubs.

This isolates the actual C# method text without modifying Unity Assets or
claiming that a full Unity Editor assembly has compiled.
"""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs").read_text(encoding="utf-8")


def method(signature):
    start = SOURCE.index(signature)
    brace = SOURCE.index("{", start)
    depth = 0
    for end in range(brace, len(SOURCE)):
        if SOURCE[end] == "{":
            depth += 1
        elif SOURCE[end] == "}":
            depth -= 1
            if depth == 0:
                return SOURCE[start:end + 1]
    raise AssertionError("Unterminated production method: " + signature)


WRAPPER = method("private void SetupMaterials()")
BUILDER = method("private System.Collections.IEnumerator BuildMaterialsAcrossFrames(bool stagger)")
HARNESS = """using System;
namespace Aaris.MapReview
{
    internal sealed class Shader
    {
        public static Shader Find(string name) { return new Shader(); }
    }

    internal struct Color
    {
        public float r, g, b;
        public Color(float red, float green, float blue)
        { r = red; g = green; b = blue; }
        public static Color operator *(Color c, float scale)
        { return new Color(c.r * scale, c.g * scale, c.b * scale); }
    }

    internal sealed class Material
    {
        internal static int created;
        public string name;
        public bool enableInstancing;
        public Material(Shader shader) { created++; }
        public Material(Material original) { created++; }
        public void SetColor(string key, Color value) { }
        public void SetFloat(string key, float value) { }
        public bool HasProperty(string key) { return true; }
        public void EnableKeyword(string name) { }
    }

    internal static class AarisMobileStreamingFrameGate
    {
        internal static int claims;
        public static bool TryClaimOptionalModelBuild()
        { claims++; return true; }
    }

    internal sealed class BikeBuildFixture
    {
        private bool enabled = true;
        private bool isActiveAndEnabled { get { return enabled; } }
        private readonly Material[] surfaces = new Material[8];
        private readonly Material[] paintVariants = new Material[4];
        private bool bikeMaterialsReady;

        __WRAPPER__

        __BUILDER__

        public static void Main()
        {
            var bike = new BikeBuildFixture();
#if UNITY_ANDROID && !UNITY_EDITOR
            var work = bike.BuildMaterialsAcrossFrames(true);
            int[] expected = { 2, 4, 6, 8, 11 };
            for (int i = 0; i < expected.Length; i++)
            {
                bool hasMore = work.MoveNext();
                if (Material.created != expected[i])
                    throw new Exception("Material burst at slice " + i
                        + " actual=" + Material.created);
                if (hasMore != (i < 4))
                    throw new Exception("Incorrect yield boundary at slice " + i);
                if (bike.bikeMaterialsReady != (i == 4))
                    throw new Exception("Half-built palette became visible");
            }
            if (AarisMobileStreamingFrameGate.claims != 4)
                throw new Exception("Shared gate must admit four later slices");

            // Other Android GPUs must keep exact original one-shot behavior.
            var fast = new BikeBuildFixture();
            Material.created = 0;
            AarisMobileStreamingFrameGate.claims = 0;
            fast.SetupMaterials();
            if (!fast.bikeMaterialsReady || Material.created != 11 ||
                AarisMobileStreamingFrameGate.claims != 0)
                throw new Exception("Fast Android setup was altered");
#else
            bike.SetupMaterials();
            if (!bike.bikeMaterialsReady || Material.created != 11 ||
                AarisMobileStreamingFrameGate.claims != 0)
                throw new Exception("Editor/desktop original setup was altered");
#endif
            if (bike.paintVariants[0] != bike.surfaces[0])
                throw new Exception("Original paint slot alias changed");
            Console.WriteLine("BIKE_MATERIAL_PRODUCTION_CSHARP_PASS");
        }
    }
}
""".replace("__WRAPPER__", WRAPPER).replace("__BUILDER__", BUILDER)


def main():
    mcs = shutil.which("mcs")
    mono = shutil.which("mono")
    if not mcs or not mono:
        raise SystemExit("Mono mcs + mono required for isolated production C# method test")
    with tempfile.TemporaryDirectory(dir="/tmp", prefix="aaris-bike-material-csharp-") as tmp:
        root = Path(tmp)
        program = root / "BikeMaterialHarness.cs"
        program.write_text(HARNESS, encoding="utf-8")
        for suffix, flags in (
            ("desktop", []),
            ("android", ["-define:UNITY_ANDROID"]),
        ):
            exe = root / (suffix + ".exe")
            subprocess.run(
                [mcs, "-langversion:latest", "-out:" + str(exe), *flags, str(program)],
                check=True, timeout=45,
            )
            result = subprocess.run([mono, str(exe)], check=True,
                                    text=True, capture_output=True, timeout=20)
            assert "BIKE_MATERIAL_PRODUCTION_CSHARP_PASS" in result.stdout, result.stdout
            print(suffix + ": " + result.stdout.strip())


if __name__ == "__main__":
    main()
