#!/usr/bin/env python3
"""Compile and run the production deferred collider release coroutine with Mono.

No Unity engine/device is simulated: the harness proves method control flow,
native-claim counts, and preserved collision behavior under deterministic stubs.
"""
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text(encoding="utf-8")


def extract_method(signature):
    start = SOURCE.index(signature)
    brace = SOURCE.index("{", start)
    depth = 0
    for i in range(brace, len(SOURCE)):
        if SOURCE[i] == "{":
            depth += 1
        elif SOURCE[i] == "}":
            depth -= 1
            if depth == 0:
                return SOURCE[start:i + 1]
    raise AssertionError("Source method had no closing brace")


HARNESS = r"""using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float xx,float yy,float zz) { x=xx;y=yy;z=zz; }
    }
    public struct Bounds
    {
        public Vector3 cached;
        public Vector3 ClosestPoint(Vector3 input) { return cached; }
    }
    public class Transform { public Vector3 position; }
    public class Collider { public bool enabled = true; }
    public static class Mathf
    {
        public static float Max(float a,float b) { return Math.Max(a,b); }
    }
    public static class Time
    {
        public static double realtimeSinceStartupAsDouble { get { return 0d; } }
    }
}
namespace Aaris.MapReview
{
    internal static class AarisMobileStreamingFrameGate
    {
        internal static int claims;
        public static void ReservePhysicsStreamingFrame() { claims++; }
    }

    internal sealed class ReleaseFixture
    {
        private readonly List<int> pendingColliderRelease = new List<int>();
        private readonly bool[] colliderReleaseQueued;
        private readonly bool[] disabledByStreaming;
        private readonly Collider[] obstacles;
        private readonly int[] wanted;
        private readonly Bounds[] worldBounds;
        private Transform target = new Transform();
        private int colliderReleaseCursor;
        private int query = 1;
        private float activationDistance = 55f;
        private object deferredColliderRelease = new object();
        private const float ColliderReleaseMargin = 6f;
        private const int AndroidReleaseUpdatesPerFrame = 12;
        private const int AndroidReleaseClockCheckInterval = 4;
        private const double AndroidReleaseFrameBudgetSeconds = .001;

        public ReleaseFixture(int n, int actualRetirements)
        {
            colliderReleaseQueued = new bool[n];
            disabledByStreaming = new bool[n];
            obstacles = new Collider[n];
            wanted = new int[n];
            worldBounds = new Bounds[n];
            for (int i=0; i<n; i++)
            {
                pendingColliderRelease.Add(i);
                colliderReleaseQueued[i] = true;
                obstacles[i] = new Collider();
                wanted[i] = i < actualRetirements ? 0 : query;
                worldBounds[i] = new Bounds { cached = new Vector3(300f,0f,0f) };
            }
        }
        __PRODUCTION_METHOD__

        private int DisabledCount()
        {
            int count=0;
            for (int i = 0; i < obstacles.Length; i++)
            {
                bool disabled = !obstacles[i].enabled;
                Assert(disabledByStreaming[i] == disabled,
                    "only actual streaming retirements should become inactive cover");
                if (disabled) count++;
            }
            return count;
        }
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        private static void CheckReacquiredQueueNoClaims()
        {
            var fixture = new ReleaseFixture(21, 0);
            var work=fixture.ReleaseDistantAcrossFrames();
            AarisMobileStreamingFrameGate.claims=0;
            Assert(work.MoveNext(), "initial coroutine yield missing");
            Assert(AarisMobileStreamingFrameGate.claims==0, "claimed before native work");
            Assert(work.MoveNext(), "first queued retirement budget yield missing");
            Assert(AarisMobileStreamingFrameGate.claims==0, "stale queue stole admission");
            Assert(fixture.DisabledCount()==0, "reentered collider disabled");
            Assert(!work.MoveNext(), "unexpected extra batch");
            Assert(fixture.DisabledCount()==0 &&
                   AarisMobileStreamingFrameGate.claims==0,
                   "no-op batch should not reserve");
        }
        private static void CheckRealTwoFrames()
        {
            var fixture = new ReleaseFixture(20, 20);
            var work=fixture.ReleaseDistantAcrossFrames();
            AarisMobileStreamingFrameGate.claims=0;
            Assert(work.MoveNext(), "initial yield missing");
            Assert(AarisMobileStreamingFrameGate.claims==0,"claim before work");
            Assert(work.MoveNext(),"expected first twelve batch");
            Assert(fixture.DisabledCount()==12, "first frame must retire exactly 12");
            Assert(AarisMobileStreamingFrameGate.claims==1, "one claim per actual frame");
            Assert(!work.MoveNext(),"second batch must finish");
            Assert(fixture.DisabledCount()==20, "remaining colliders not released");
            Assert(AarisMobileStreamingFrameGate.claims==2, "second frame must claim once");
        }
        private static void CheckMixedNoopAndReal()
        {
            var fixture = new ReleaseFixture(12, 2);
            var work=fixture.ReleaseDistantAcrossFrames();
            AarisMobileStreamingFrameGate.claims=0;
            Assert(work.MoveNext(), "initial yield missing");
            Assert(work.MoveNext(), "first budget yield missing");
            Assert(fixture.DisabledCount()==2, "must release only two eligible entries");
            Assert(AarisMobileStreamingFrameGate.claims==1,
                   "real physics batch should claim once");
            Assert(!work.MoveNext(), "extra work after queue drained");
            Assert(AarisMobileStreamingFrameGate.claims==1, "extra no-op claimed frame");
        }
        public static void Main()
        {
            CheckReacquiredQueueNoClaims();
            CheckRealTwoFrames();
            CheckMixedNoopAndReal();
            Console.WriteLine("AARIS_COLLIDER_RELEASE_PRODUCTION_CSHARP_PASS");
        }
    }
}
""".replace("__PRODUCTION_METHOD__", extract_method(
    "private IEnumerator ReleaseDistantAcrossFrames()"))


def main():
    mcs, mono = shutil.which("mcs"), shutil.which("mono")
    if not mcs or not mono:
        raise SystemExit("Requires mono-mcs / mono runtime to compile source")
    with tempfile.TemporaryDirectory(prefix="aaris-collider-release-csharp-",
                                     dir="/tmp") as d:
        source = Path(d) / "ReleaseFixture.cs"
        exe = Path(d) / "ReleaseFixture.exe"
        source.write_text(HARNESS, encoding="utf-8")
        subprocess.run([mcs, "-langversion:latest", "-out:" + str(exe), str(source)],
                       check=True, timeout=45)
        result = subprocess.run([mono, str(exe)], check=True, capture_output=True,
                                text=True, timeout=20)
        assert "AARIS_COLLIDER_RELEASE_PRODUCTION_CSHARP_PASS" in result.stdout
        print(result.stdout.strip())


if __name__ == "__main__":
    main()
