using System;

// API doubles deliberately count/guard native access. Production guards are
// inserted by run_mobile_diagnostics_csharp.py before compilation.
enum ProfilerMarkerDataUnit { Bytes, Nanoseconds, Count }
struct ProfilerRecorder
{
    public bool Valid;
    public ProfilerMarkerDataUnit UnitType;
    public int Count;
    public long Value;
    public long LastValue
    {
        get
        {
            if (!Valid || Count == 0) throw new Exception("Invalid native sample read");
            return Value;
        }
    }
}

static class AarisMobileStreamingFrameGate
{
    public static int RetiredAdditiveSceneCount;
    public static bool Constrained;
    public static bool IsConstrainedDevice() => Constrained;
}

static class AarisAndroidMemoryPressure
{
    public static int Queries;
    public static bool Due, Pressure;
    public static bool TrySample(out bool pressure)
    {
        Queries++;
        pressure = Pressure;
        return Due;
    }
}

sealed class RuntimeFixture
{
    public bool proactiveAssetCleanupPending;
    public void Tick()
    {
        /* MEMORY_GUARD */
    }
    /* BYTE_READER */
}

static class MobileDiagnosticsTests
{
    private static int assertions;
    private static void Require(bool passed, string message)
    {
        assertions++;
        if (!passed) throw new Exception(message);
    }

    public static void Main()
    {
        var runtime = new RuntimeFixture();
        AarisMobileStreamingFrameGate.Constrained = true;
        AarisAndroidMemoryPressure.Due = true;
        AarisAndroidMemoryPressure.Pressure = true;
        // Cold start and arbitrarily long stationary play have no retired
        // scene assets: not even one Android memory-query call is useful.
        for (int i = 0; i < 1000; i++) runtime.Tick();
        Require(AarisAndroidMemoryPressure.Queries == 0, "No retirement must issue zero memory queries");
        Require(!runtime.proactiveAssetCleanupPending, "No retirement cannot request proactive cleanup");

        AarisMobileStreamingFrameGate.RetiredAdditiveSceneCount = 1;
        runtime.Tick();
        Require(AarisAndroidMemoryPressure.Queries == 1, "First actual retirement must query memory pressure");
        Require(runtime.proactiveAssetCleanupPending, "Pressure plus retired scene must request cleanup");
        AarisAndroidMemoryPressure.Due = false;
        AarisAndroidMemoryPressure.Pressure = false;
        runtime.Tick();
        Require(runtime.proactiveAssetCleanupPending, "No fresh sample must preserve the pending decision");
        AarisAndroidMemoryPressure.Due = true;
        runtime.Tick();
        Require(!runtime.proactiveAssetCleanupPending, "Fresh recovery signal must cancel proactive cleanup");

        int queries = AarisAndroidMemoryPressure.Queries;
        AarisMobileStreamingFrameGate.Constrained = false;
        runtime.Tick();
        Require(AarisAndroidMemoryPressure.Queries == queries, "Unconstrained devices must not gain new memory polls");
        AarisMobileStreamingFrameGate.Constrained = true;
        AarisMobileStreamingFrameGate.RetiredAdditiveSceneCount = 0;
        runtime.Tick();
        Require(AarisAndroidMemoryPressure.Queries == queries, "Clearing reclamation debt must stop queries again");

        Require(RuntimeFixture.ReadGcAllocatedBytes(default(ProfilerRecorder)) == -1L,
            "Missing counter must stay unavailable");
        var counter = new ProfilerRecorder { Valid = true, UnitType = ProfilerMarkerDataUnit.Bytes };
        Require(RuntimeFixture.ReadGcAllocatedBytes(counter) == -1L,
            "No completed sample must stay unavailable");
        counter.Count = 1;
        Require(RuntimeFixture.ReadGcAllocatedBytes(counter) == 0L,
            "Valid measured zero allocations must remain zero");
        counter.Value = 65536L;
        Require(RuntimeFixture.ReadGcAllocatedBytes(counter) == 65536L,
            "Measured allocation bytes must retain their exact value");
        counter.UnitType = ProfilerMarkerDataUnit.Nanoseconds;
        Require(RuntimeFixture.ReadGcAllocatedBytes(counter) == -1L,
            "Timing units cannot be misreported as bytes");
        counter.UnitType = ProfilerMarkerDataUnit.Count;
        Require(RuntimeFixture.ReadGcAllocatedBytes(counter) == -1L,
            "Allocation event counts cannot be misreported as bytes");
        counter.UnitType = ProfilerMarkerDataUnit.Bytes;
        counter.Value = -4L;
        Require(RuntimeFixture.ReadGcAllocatedBytes(counter) == -1L,
            "Negative counter data must stay unavailable");
        Console.WriteLine("MOBILE_DIAGNOSTICS_CSHARP_PASS assertions=" + assertions);
    }
}
