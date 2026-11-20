#!/usr/bin/env python3
"""Compile and execute actual production cache methods with Unity-like stubs.

Tests source-material identity verification and four original finish layouts.
This is not a native Unity Player or Android GPU test.
"""
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs").read_text(encoding="utf-8")


def method(signature):
    start = SOURCE.index(signature)
    brace = SOURCE.index("{", start)
    count = 0
    for i in range(brace, len(SOURCE)):
        if SOURCE[i] == "{":
            count += 1
        if SOURCE[i] == "}":
            count -= 1
            if count == 0:
                return SOURCE[start:i + 1]
    raise AssertionError("Unterminated production method: " + signature)


MATCH = method("private bool MatchesCachedBikeSource(Renderer[] renderers)")
BUILD = method("private Material[][][] GetBikePaintLayouts(Renderer[] renderers)")

HARNESS = r"""using System;
using System.Collections.Generic;
namespace Aaris.MapReview
{
    internal sealed class Material
    {
        internal readonly int kind;
        internal Material(int kind) { this.kind = kind; }
    }
    internal static class Mathf
    {
        internal static int Min(int a, int b) { return Math.Min(a, b); }
    }
    internal sealed class Renderer
    {
        internal readonly Material[] source;
        internal int arrayGetterCalls, listGetterCalls;
        internal Renderer(Material[] source) { this.source = source; }
        internal Material[] sharedMaterials
        {
            get { arrayGetterCalls++; return (Material[])source.Clone(); }
        }
        internal void GetSharedMaterials(List<Material> scratch)
        {
            listGetterCalls++;
            scratch.Clear();
            scratch.AddRange(source);
        }
    }

    internal sealed class Fixture
    {
        private Material[][][] cachedBikePaintLayouts;
        private Material[][] cachedBikePrefabMaterials;
        private readonly List<Material> bikeSourceScratch = new List<Material>(8);
        private readonly Material[] surfaces = new Material[8];
        private readonly Material[] paintVariants = new Material[4];

        internal Fixture()
        {
            for (int i = 0; i < surfaces.Length; i++)
                surfaces[i] = new Material(i);
            paintVariants[0] = surfaces[0];
            for (int i = 1; i < paintVariants.Length; i++)
                paintVariants[i] = new Material(8 + i);
        }

        private static int SurfaceIndex(Material material, int fallback)
        {
            return material == null ? fallback : material.kind;
        }

        __MATCH__
        __BUILD__

        private void ExpectPalette(Material[][][] palette, int sourceCount)
        {
            if (palette.Length != 4) throw new Exception("Lost paint variant");
            for (int paint = 0; paint < 4; paint++)
            {
                if (palette[paint][0].Length != sourceCount)
                    throw new Exception("Source slots changed");
                int[] kinds = {1,2,3,4,0,7,5,6};
                for (int slot = 0; slot < sourceCount; slot++)
                {
                    Material expected = kinds[slot] == 0
                        ? paintVariants[paint] : surfaces[kinds[slot]];
                    if (!ReferenceEquals(palette[paint][0][slot], expected))
                        throw new Exception("Paint or body material mapping changed");
                }
            }
        }

        public static void Main()
        {
            var fixture = new Fixture();
            int[] kinds = {1,2,3,4,0,7,5,6};
            Material[] original = new Material[kinds.Length];
            for (int i = 0; i < kinds.Length; i++)
                original[i] = new Material(kinds[i]);
            var first = new Renderer(original);
            var baseline = fixture.GetBikePaintLayouts(new[] { first });
            fixture.ExpectPalette(baseline, 8);
            if (first.arrayGetterCalls != 1)
                throw new Exception("Unexpected source array reads");

            var matchingClone = new Renderer(original);
            var hit = fixture.GetBikePaintLayouts(new[] { matchingClone });
            if (!ReferenceEquals(hit, baseline))
                throw new Exception("Identical clone did not reuse layouts");
            if (matchingClone.arrayGetterCalls != 0 || matchingClone.listGetterCalls != 1)
                throw new Exception("Cache hit allocated sharedMaterials array");

            Material[] changed = (Material[])original.Clone();
            changed[0] = new Material(1); // Same surface kind, different asset identity
            var variant = new Renderer(changed);
            var fallback = fixture.GetBikePaintLayouts(new[] { variant });
            if (ReferenceEquals(fallback, baseline))
                throw new Exception("Different imported prefab wrongly reused cache");
            fixture.ExpectPalette(fallback, 8);
            if (ReferenceEquals(fixture.GetBikePaintLayouts(
                new[] { new Renderer(original) }), fallback))
                throw new Exception("Divergent clone poisoned original cache");

            // Different renderer/material slot count must rebuild, not crash.
            var variantCount = new Renderer(new[] { original[0] });
            var smaller = fixture.GetBikePaintLayouts(new[] { variantCount });
            if (ReferenceEquals(smaller, baseline) ||
                smaller[0][0].Length != 1)
                throw new Exception("Slot-count mismatch reused an invalid palette");

            Console.WriteLine("BIKE_SOURCE_LAYOUT_CACHE_CSHARP_PASS");
        }
    }
}
""".replace("__MATCH__", MATCH).replace("__BUILD__", BUILD)


def main():
    compiler = shutil.which("mcs")
    runtime = shutil.which("mono")
    if not compiler or not runtime:
        raise SystemExit("Mono mcs and mono are required")
    with tempfile.TemporaryDirectory(prefix="aaris-bike-layout-", dir="/tmp") as folder:
        source = Path(folder) / "Test.cs"
        executable = Path(folder) / "Test.exe"
        source.write_text(HARNESS, encoding="utf-8")
        subprocess.run([compiler, "-langversion:latest", "-out:" + str(executable),
                        str(source)], check=True, timeout=50)
        result = subprocess.run([runtime, str(executable)], capture_output=True,
                                text=True, check=True, timeout=25)
        if "BIKE_SOURCE_LAYOUT_CACHE_CSHARP_PASS" not in result.stdout:
            raise RuntimeError("No success marker: " + result.stdout)
        print(result.stdout.strip())


if __name__ == "__main__":
    main()
