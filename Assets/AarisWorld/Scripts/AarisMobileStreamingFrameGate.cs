using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Coordinates optional 3D model and nonessential Foley audio imports across streamers.
    /// Per-streamer budgets alone can still create several heavy hierarchies in
    /// the same rendered frame. This adds one shared budget-device Android admission
    /// slot; essential gameplay and existing collision/visibility stay live.
    /// </summary>
    internal static class AarisMobileStreamingFrameGate
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private static int claimedFrame = -1;
        private static bool devicePolicyReady, constrainedDevice;
        private static int importIntegrationFrame = -1;
        private static int pressureDecisionFrame = -1, consecutivePressureDeferrals;
        private static bool pressureBlockedThisFrame;
        // Defer at most two attempt-frames while frame time exceeds 50 ms.
        // Still admit one optional item on the third during sustained load,
        // so a slow device cannot starve its nearby models indefinitely.
        private const float SlowFrameSeconds = 0.05f;
        private const int MaximumPressureDeferralFrames = 2;
        internal static int ImportSettleDeferredAttempts { get; private set; }
        internal static int PressureDeferredFrames { get; private set; }
        // Essential player rig construction is one high-cost main-thread
        // operation (Instantiate + PlayableGraph + validation BakeMesh).
        // Reserve its NEXT frame before yielding; optional models/materials
        // must not begin another import or hierarchy build on that frame.
        private static int essentialRigFrame = -1;
        // The high-detail cargo plane has its own unavoidable native
        // Instantiate + material/renderer setup. A separate reservation
        // preserves a simultaneous character-rig fence rather than replacing it.
        private static int essentialAircraftFrame = -1;
        internal static int RigReservedDeferredAttempts { get; private set; }
        // One shared native import slot across optional model prewarmers.
        // Completed requests release the slot even when a coroutine stops.
        private static ResourceRequest optionalLoadInFlight;
        // Unity's critical-memory asset cleanup can run for many rendered
        // frames. Starting optional imports/hierarchies while it is still
        // unloading competes for native memory and the same main-thread time.
        // The cleanup itself is never blocked; essential scene/gameplay work
        // never enters this optional admission gate.
        private static AsyncOperation unusedAssetCleanup;
        // Fence the native asset cleanup completion frame before optional integration.
        private static int cleanupCompletionFrame = -1;
        // Monotonic attempted-admission counters. Reading these ints only
        // during existing 15-second diagnostics does not allocate, poll native
        // hierarchies, or add logging to the model-streaming hot path.
        internal static int CleanupDeferredAttempts { get; private set; }
        internal static int SameFrameDeferredAttempts { get; private set; }
        internal static int ImportDeferredAttempts { get; private set; }

        internal static void DeferOptionalWorkDuringAssetCleanup(AsyncOperation cleanup)
        {
            if (cleanup == null) return;
            // Unity can finish UnloadUnusedAssets synchronously on the frame
            // that starts it. That frame still belongs to native cleanup and
            // must not accept an optional FBX/material import or hierarchy.
            if (cleanup.isDone)
            {
                unusedAssetCleanup = null;
                cleanupCompletionFrame = Time.frameCount;
                return;
            }
            unusedAssetCleanup = cleanup;
        }

        private static bool IsAssetCleanupPending()
        {
            if (unusedAssetCleanup != null)
            {
                if (!unusedAssetCleanup.isDone) return true;
                // The native completion frame may already be saturated.
                unusedAssetCleanup = null;
                cleanupCompletionFrame = Time.frameCount;
            }
            return cleanupCompletionFrame == Time.frameCount;
        }

        private static bool IsOptionalImportBusy()
        {
            int frame = Time.frameCount;
            if (optionalLoadInFlight != null)
            {
                if (!optionalLoadInFlight.isDone)
                {
                    ImportDeferredAttempts++;
                    return true;
                }
                optionalLoadInFlight = null;
                // isDone includes native main-thread integration. Keep its
                // observed completion frame free of another hierarchy/import.
                importIntegrationFrame = frame;
            }
            if (importIntegrationFrame != frame) return false;
            ImportSettleDeferredAttempts++;
            return true;
        }

        private static bool ShouldDeferForFramePressure(int frame)
        {
            // Several streamers can ask in one frame. They must share one
            // decision, not consume two recovery frames in the same Update.
            if (pressureDecisionFrame == frame) return pressureBlockedThisFrame;
            pressureDecisionFrame = frame;
            pressureBlockedThisFrame = Time.unscaledDeltaTime > SlowFrameSeconds &&
                consecutivePressureDeferrals < MaximumPressureDeferralFrames;
            if (pressureBlockedThisFrame)
            {
                consecutivePressureDeferrals++;
                PressureDeferredFrames++;
            }
            else consecutivePressureDeferrals = 0;
            return pressureBlockedThisFrame;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            claimedFrame = -1;
            devicePolicyReady = constrainedDevice = false;
            importIntegrationFrame = pressureDecisionFrame = -1;
            consecutivePressureDeferrals = 0;
            pressureBlockedThisFrame = false;
            ImportSettleDeferredAttempts = PressureDeferredFrames = 0;
            essentialRigFrame = -1;
            essentialAircraftFrame = -1;
            RigReservedDeferredAttempts = 0;
            optionalLoadInFlight = null;
            unusedAssetCleanup = null;
            cleanupCompletionFrame = -1;
            CleanupDeferredAttempts = SameFrameDeferredAttempts = ImportDeferredAttempts = 0;
        }
#endif

        internal static bool IsConstrainedDevice()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!devicePolicyReady)
            {
                int ramMb = SystemInfo.systemMemorySize;
                // More RAM does not make an Adreno 610 or Mali-G52 faster.
                // Reuse the exact driver-aware policy and cache it once,
                // avoiding native device queries/string work in hot paths.
                constrainedDevice = (ramMb > 0 && ramMb <= 4608) ||
                    AarisMobileRuntime.UseConstrainedGpuStreamingBudget();
                devicePolicyReady = true;
            }
            return constrainedDevice;
#else
            return false;
#endif
        }

        // Called by the essential player visual AFTER animation imports,
        // immediately before its final one-frame yield. Fast devices retain
        // their current full-speed behavior.
        internal static bool ReserveNextFrameForEssentialRig()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsConstrainedDevice())
            {
                essentialRigFrame = Time.frameCount + 1;
                return true;
            }
#endif
            return false;
        }

        // Called before the existing aircraft integration yield, so optional
        // streamers can check the reservation during the NEXT render frame.
        // Does not delay flight, airplane input or the essential model load.
        internal static bool ReserveNextFrameForEssentialAircraft()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsConstrainedDevice())
            {
                essentialAircraftFrame = Time.frameCount + 1;
                return true;
            }
#endif
            return false;
        }

        private static bool IsEssentialRigFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Time.frameCount == essentialRigFrame ||
                   Time.frameCount == essentialAircraftFrame;
#else
            return false;
#endif
        }

        internal static bool TryClaimOptionalModelBuild()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsEssentialRigFrame())
            {
                // Essential player/aircraft frames have priority over optional
                // houses, model imports and Foley. The counter counts both.
                RigReservedDeferredAttempts++;
                return false;
            }
            // Even a larger phone can report critical memory: keep all optional
            // native hierarchy construction off the unused-asset cleanup pass.
            if (IsAssetCleanupPending())
            {
                CleanupDeferredAttempts++;
                return false;
            }
            if (IsConstrainedDevice())
            {
                // Background IO can still finalize native objects on the
                // main thread. Hierarchy builds share its import fence too.
                if (IsOptionalImportBusy()) return false;
                int frame = Time.frameCount;
                if (claimedFrame == frame)
                {
                    SameFrameDeferredAttempts++;
                    return false;
                }
                if (ShouldDeferForFramePressure(frame)) return false;
                claimedFrame = frame;
            }
#endif
            return true;
        }

        // Native terrain, tree collision, essential loot and first-use sound
        // cues are never delayed. Optional 3D models, clips and Foley variants
        // share the same admission budget on constrained Android hardware.
        internal static ResourceRequest TryStartOptionalLoadAsync<T>(string path)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path)) return null;
#if UNITY_ANDROID && !UNITY_EDITOR
            bool constrained = IsConstrainedDevice();
            if (IsAssetCleanupPending())
            {
                CleanupDeferredAttempts++;
                return null;
            }
            if (IsEssentialRigFrame())
            {
                RigReservedDeferredAttempts++;
                return null;
            }
            if (constrained)
            {
                // Model imports and optional hierarchy/material integration
                // share in-flight, completion-frame and pressure admission.
                if (!TryClaimOptionalModelBuild()) return null;
            }
#endif
            ResourceRequest request = Resources.LoadAsync<T>(path);
            request.priority = -1;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (constrained) optionalLoadInFlight = request;
#endif
            return request;
        }
    }
}
