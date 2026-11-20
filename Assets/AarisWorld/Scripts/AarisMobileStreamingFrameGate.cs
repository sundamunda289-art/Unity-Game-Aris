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
        // 3+ FixedUpdate ticks before one render indicate a physics catch-up
        // burst. Reserve optional work for at most two such frames in a row
        // so persistent slow physics never starves nearby authored assets.
        private const int PhysicsCatchupStepThreshold = 3;
        private const int MaximumPhysicsCatchupDeferrals = 2;
        private static int lastPhysicsCatchupFrame = -1;
        private static int consecutivePhysicsCatchupDeferrals;
        internal static int PhysicsCatchupDeferredFrames { get; private set; }
        // Optional native imports also contend with 30 FPS frame pacing:
        // a 34-50 ms frame has already missed the 30 FPS budget, even before
        // reaching the old 50 ms severe-stall threshold. Defer one attempt
        // at moderate pressure and at most two at severe pressure. Always
        // admit a later attempt so nearby original-detail models are not lost.
        private const float FallbackFrameBudgetSeconds = 1f / 30f;
        private const int FrameBudgetRefreshFrames = 120;
        private static float effectiveFrameBudgetSeconds = FallbackFrameBudgetSeconds;
        private static int nextFrameBudgetRefresh = -1;
        private static int cachedTargetFrameRate = int.MinValue;
        private const int ModeratePressureDeferralFrames = 1;
        private const int MaximumPressureDeferralFrames = 2;
        // A multi-hundred-millisecond stall (often Android app resume or
        // native upload) needs a few clean render frames before optional
        // hierarchy construction resumes. Essential gameplay is never gated.
        private const float LongHitchThresholdSeconds = 0.25f;
        private const int LongHitchRecoveryFrames = 3;
        private static int longHitchRecoveryThroughFrame = -1;
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
        // Android can signal critical memory between unrelated script Update calls.
        // Reserve the notification frame and the next one even before this
        // component starts Unity's nonblocking unused-asset reclamation.
        private static int lowMemoryNotificationFrame = -1;
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

        internal static void ReserveLowMemoryNotificationFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            lowMemoryNotificationFrame = Time.frameCount;
#endif
        }

        private static bool IsAssetCleanupPending()
        {
            int frame = Time.frameCount;
            // Notification callbacks may precede this runtime's Update. Hold
            // optional loads/instantiation for two render frames, with no wait
            // or change to essential gameplay and no permanent throttling.
            if (frame == lowMemoryNotificationFrame ||
                (lowMemoryNotificationFrame >= 0 &&
                 frame == lowMemoryNotificationFrame + 1))
                return true;
            if (unusedAssetCleanup != null)
            {
                if (!unusedAssetCleanup.isDone) return true;
                // The native completion frame may already be saturated.
                unusedAssetCleanup = null;
                cleanupCompletionFrame = Time.frameCount;
            }
            // Native texture/mesh resource retirement can spill into the next
            // rendered frame after Unity reports the cleanup as complete.
            // On constrained Android only, avoid combining that aftershock
            // with another optional import or hierarchy build. Essential
            // gameplay and the original assets remain untouched.
            if (cleanupCompletionFrame >= 0 &&
                Time.frameCount == cleanupCompletionFrame + 1 &&
                IsConstrainedDevice())
                return true;
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

        // Unity's Android frame pacing uses integer divisors of display refresh.
        // A 60-FPS request on 90 Hz is effectively 45 FPS, not 60 FPS.
        // Sample display timing only every 120 frames or on a target-FPS change;
        // do not query Screen.currentResolution for every streaming request.
        private static float CurrentFrameBudgetSeconds(int frame)
        {
            int requestedFps = Application.targetFrameRate;
            if (requestedFps == cachedTargetFrameRate &&
                frame < nextFrameBudgetRefresh)
                return effectiveFrameBudgetSeconds;

            cachedTargetFrameRate = requestedFps;
            nextFrameBudgetRefresh = frame + FrameBudgetRefreshFrames;
            if (requestedFps <= 0) requestedFps = 30;
            double refreshHz = Screen.currentResolution.refreshRateRatio.value;
            double effectiveFps = requestedFps;
            if (refreshHz >= 1d &&
                !double.IsNaN(refreshHz) && !double.IsInfinity(refreshHz))
            {
                double divisor = System.Math.Max(1d,
                    System.Math.Ceiling(refreshHz / requestedFps));
                // Match the runtime quality governor: reject tiny display
                // clock rounding errors from exact requested-FPS divisors.
                // For example, 90.0001 / 30 must use three display refreshes,
                // not four; preserve 59.94-Hz and genuine 90/60 -> 45 FPS.
                double nearestDivisor = System.Math.Round(refreshHz / requestedFps);
                if (nearestDivisor >= 1d &&
                    System.Math.Abs(refreshHz - nearestDivisor * requestedFps) <= 0.05d)
                    divisor = nearestDivisor;
                effectiveFps = refreshHz / divisor;
            }
            effectiveFrameBudgetSeconds = 1f /
                (float)System.Math.Max(1d, effectiveFps);
            return effectiveFrameBudgetSeconds;
        }

        // Share the display-paced Android budget with vehicle spawn admission.
        // A fixed 45 FPS threshold would treat a healthy 30 FPS device as
        // perpetually overloaded, or mistake normal 90 Hz/45 FPS pacing for a
        // hitch. Reuse the cached, nonalloc 120-frame refresh policy.
        internal static float CurrentPacedFrameBudgetSeconds()
        {
            return CurrentFrameBudgetSeconds(Time.frameCount);
        }

        private static bool ShouldDeferForFramePressure(int frame)
        {
            // All optional streamers in the same frame share one decision.
            // Adapt the pressure thresholds to 30/45/60 FPS without changing
            // the authored model, tree, terrain, texture or visibility settings.
            if (pressureDecisionFrame == frame) return pressureBlockedThisFrame;
            pressureDecisionFrame = frame;
            float frameSeconds = Time.unscaledDeltaTime;
            // Do not start cosmetic native integrations immediately after a
            // resume/long hitch. This is a bounded admission delay, not a
            // reduction in map detail, car counts or draw distances.
            if (frameSeconds >= LongHitchThresholdSeconds)
                longHitchRecoveryThroughFrame = frame + LongHitchRecoveryFrames;
            if (frame <= longHitchRecoveryThroughFrame)
            {
                pressureBlockedThisFrame = true;
                consecutivePressureDeferrals = 0;
                PressureDeferredFrames++;
                return true;
            }
            float budget = CurrentFrameBudgetSeconds(frame);
            // Allow 8% pacing jitter, but protect the next frame when work has
            // already missed the real hardware-specific presentation budget.
            int maxDeferrals = frameSeconds > budget * 1.5f
                ? MaximumPressureDeferralFrames
                : frameSeconds > budget * 1.08f
                    ? ModeratePressureDeferralFrames : 0;
            pressureBlockedThisFrame = maxDeferrals > 0 &&
                consecutivePressureDeferrals < maxDeferrals;
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
            lastPhysicsCatchupFrame = -1;
            consecutivePhysicsCatchupDeferrals = 0;
            PhysicsCatchupDeferredFrames = 0;
            longHitchRecoveryThroughFrame = -1;
            effectiveFrameBudgetSeconds = FallbackFrameBudgetSeconds;
            nextFrameBudgetRefresh = -1;
            cachedTargetFrameRate = int.MinValue;
            ImportSettleDeferredAttempts = PressureDeferredFrames = 0;
            essentialRigFrame = -1;
            essentialAircraftFrame = -1;
            RigReservedDeferredAttempts = 0;
            optionalLoadInFlight = null;
            unusedAssetCleanup = null;
            lowMemoryNotificationFrame = -1;
            cleanupCompletionFrame = -1;
            CleanupDeferredAttempts = SameFrameDeferredAttempts = ImportDeferredAttempts = 0;
        }
#endif

        /// <summary>
        /// The authored collision grid is essential world physics, not an
        /// optional visual import. A grid/toggle batch can consume costly native
        /// CPU work even when its own item/time budget is respected. Reserve
        /// that rendered frame against additional optional model construction,
        /// imports and diagnostics; preserve exact colliders and every visual.
        /// </summary>
        // Called once from AarisMobileRuntime's early (-300) Update, after
        // Unity has run this rendered frame's physics catch-up FixedUpdates.
        // Avoid coincident optional imports, new model hierarchies and expensive
        // diagnostic snapshots on two busy frames; allow the third so visual
        // streaming continues under sustained load. No physics ticks are skipped.
        internal static void ReservePhysicsCatchupFrame(int fixedSteps)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!IsConstrainedDevice()) return;
            int frame = Time.frameCount;
            if (lastPhysicsCatchupFrame == frame) return;
            lastPhysicsCatchupFrame = frame;
            if (fixedSteps < PhysicsCatchupStepThreshold)
            {
                consecutivePhysicsCatchupDeferrals = 0;
                return;
            }
            if (consecutivePhysicsCatchupDeferrals >= MaximumPhysicsCatchupDeferrals)
            {
                consecutivePhysicsCatchupDeferrals = 0;
                return;
            }
            consecutivePhysicsCatchupDeferrals++;
            PhysicsCatchupDeferredFrames++;
            claimedFrame = frame;
#endif
        }

        internal static void ReservePhysicsStreamingFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsConstrainedDevice())
                claimedFrame = Time.frameCount;
#endif
        }

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

        /// <summary>
        /// A nonessential profiler/JSON snapshot must compete with the same
        /// native-main-thread slot as optional FBX integration. Called only
        /// when a report is actually due, not on every rendered frame.
        /// This does not defer memory cleanup, gameplay or Android visuals.
        /// </summary>
        internal static bool TryClaimNonessentialDiagnosticFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsEssentialRigFrame() || IsAssetCleanupPending()) return false;
            if (!IsConstrainedDevice()) return true;
            int frame = Time.frameCount;
            // An admitted hierarchy/import or an observed native completion
            // owns this frame. Conversely, a diagnostic claim reserves it
            // against any streamer that runs later in the Unity Update order.
            if (claimedFrame == frame || importIntegrationFrame == frame)
                return false;
            // A pending ResourceRequest can still consume background I/O and
            // native main-thread integration. A nonessential JSON/profiler
            // snapshot must wait for both the import and its completion frame.
            // Reuse the same fence as optional builds (no new hot-path poll).
            if (IsOptionalImportBusy()) return false;
            // Use the existing bounded 2-frame pressure recovery. A long
            // sequence of slow frames must not suppress telemetry forever.
            if (ShouldDeferForFramePressure(frame)) return false;
            claimedFrame = frame;
#endif
            return true;
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
