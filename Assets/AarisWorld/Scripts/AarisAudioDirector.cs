using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Small, offline, event-aware 3D mixer for the saved player.
    /// Uses the EXISTING movement, drop, combat and vehicle authorities.
    /// Missing clips are silent; no downloads, Update-time Resources.Load,
    /// duplicate controllers, networking or per-shot GameObject allocations.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(900)]
    public sealed class AarisAudioDirector : MonoBehaviour
    {
        private const int VoiceCount = 12;
        private const int LocalVoiceCount = 4;
        // Fixed-size timed cue queue: zero allocations when automatic guns fire.
        private struct TimedCue
        {
            public string Key;
            public Vector3 Position;
            public float At, Volume;
            public bool Local;
        }
        private readonly TimedCue[] scheduled = new TimedCue[16];
        // Most frames have no due audio cue. Track the next deadline so a
        // quiet player does not poll all 16 slots at every display refresh.
        private float nextScheduledAt = float.PositiveInfinity;
        private static AarisAudioDirector instance;
        private static readonly string[] Keys =
        {
            "plane_engine_loop", "jump_whoosh", "wind_rush_loop",
            "canopy_glide_loop", "parachute_open", "parachute_land", "step_dirt", "step_road",
            "step_water", "jump_start", "land_thud", "smg_shot",
            "ar_shot", "battle_rifle_shot", "sniper_shot", "shotgun_shot",
            "bolt_reload", "shell_drop", "bullet_impact", "punch_hit",
            "grenade_pin", "grenade_throw", "grenade_bounce", "grenade_boom",
            "car_start", "car_engine_loop", "tire_skid", "car_crash", "gloo_deploy",
            "door_swing"
        };
        public static IReadOnlyList<string> RequiredCueKeys => Keys;
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(32);
        private readonly AudioSource[] voices = new AudioSource[VoiceCount];
        private readonly AudioClip[] grassFoley = new AudioClip[5];
        private readonly AudioClip[] concreteFoley = new AudioClip[5];
        private readonly AudioClip[] punchFoley = new AudioClip[3];
        private AudioClip softLandingFoley, metalImpactFoley;
        private GameObject audioRoot;
        private AarisThirdPersonPlayer player;
        private AarisBattleActions combat;
        private AarisVehicleSession vehicle;
        private AarisOfflineDrop drop;
        private CharacterController capsule;
        private AudioSource planeLoop, windLoop, engineLoop;
        private AarisOfflineDrop.DropPhase lastDropPhase;
        private bool hasDropPhase, wasDriving;
        // These three loop sources are owned only by this director. Once
        // a silent fade has fully settled, avoid native getters every frame.
        // Entering flight/driving invalidates the matching settled state.
        private bool dropLoopsSettled = true, engineLoopSettled = true;
        private float previousY, previousJump = -10f, previousLand = -10f;
        private float stepDistance, lastSkidAt = -10f, lastCrashAt = -10f;
        private AarisBattleActions.PlayerStance lastFootstepStance = AarisBattleActions.PlayerStance.Standing;
        private int nextLocalVoice, nextWorldVoice = LocalVoiceCount;
        // Gunshot pitch variation must not advance Unity's global gameplay RNG.
        // This stream only affects audio presentation, not loot or AI choices.
        private uint gunshotPitchSeed = 0xC2B2AE35u;

        private float NextGunshotPitch()
        {
            gunshotPitchSeed = unchecked(
                gunshotPitchSeed * 1664525u + 1013904223u);
            return .97f + (gunshotPitchSeed & 0x00FFFFFFu) *
                (1f / 16777216f) * .065f;
        }
        private int vehicleGear = 1;
        private Vector3 previousPosition;
        private float nextOptionalComponentRefresh;

        private static AudioSource MakeSource(Transform parent, string name)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            var source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 3f;
            source.maxDistance = 65f;
            source.dopplerLevel = 0f;
            source.priority = 180;
            return source;
        }

        private void Awake()
        {
            if (instance != null && instance != this) { enabled = false; return; }
            instance = this;
            player = GetComponent<AarisThirdPersonPlayer>();
            combat = GetComponent<AarisBattleActions>();
            vehicle = GetComponent<AarisVehicleSession>();
            drop = GetComponent<AarisOfflineDrop>();
            capsule = GetComponent<CharacterController>();
            // Cold-start Android only needs the airborne/drop cues immediately.
            // Keep their original clips ready before the first playable frame.
            // Everything else remains identical but is loaded one request per
            // frame after the first render, avoiding dozens of synchronous
            // decompressions during an already busy scene boot.
            foreach (string key in Keys)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!IsImmediateStartupCue(key)) continue;
#endif
                clips[key] = Resources.Load<AudioClip>("SFX/" + key);
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            StartCoroutine(LoadAndroidAudioAcrossFrames());
#else
            LoadOptionalFoleySynchronously();
#endif
            audioRoot = new GameObject("Aaris Audio Voices (12 pooled)");
            for (int i = 0; i < voices.Length; i++)
            {
                bool local = i < LocalVoiceCount;
                voices[i] = MakeSource(audioRoot.transform,
                    (local ? "Local SFX Voice " : "World SFX Voice ") + i);
                voices[i].spatialBlend = local ? 0f : 1f;
            }
            planeLoop = MakeSource(audioRoot.transform, "Plane Engine Loop");
            windLoop = MakeSource(audioRoot.transform, "Drop Wind Loop");
            engineLoop = MakeSource(audioRoot.transform, "Vehicle Engine Loop");
            planeLoop.loop = windLoop.loop = engineLoop.loop = true;
            // These loops represent the local player's occupied vehicle or canopy.
            // Keep them audible at the different camera offsets for each mode.
            planeLoop.spatialBlend = windLoop.spatialBlend = engineLoop.spatialBlend = 0f;
            previousY = transform.position.y;
            previousPosition = transform.position;
        }

        private void LoadOptionalFoleySynchronously()
        {
            for (int i = 0; i < grassFoley.Length; i++)
            {
                string number = i.ToString("000");
                grassFoley[i] = Resources.Load<AudioClip>("SFX/CC0/footstep_grass_" + number);
                concreteFoley[i] = Resources.Load<AudioClip>("SFX/CC0/footstep_concrete_" + number);
            }
            for (int i = 0; i < punchFoley.Length; i++)
                punchFoley[i] = Resources.Load<AudioClip>(
                    "SFX/CC0/impactPunch_medium_" + i.ToString("000"));
            softLandingFoley = Resources.Load<AudioClip>("SFX/CC0/impactSoft_heavy_000");
            metalImpactFoley = Resources.Load<AudioClip>("SFX/CC0/impactMetal_light_000");
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool IsImmediateStartupCue(string key)
        {
            return key == "plane_engine_loop" || key == "jump_whoosh" ||
                   key == "wind_rush_loop" || key == "canopy_glide_loop" ||
                   key == "parachute_open" || key == "parachute_land";
        }

        // One Android audio staging pipeline. Both base cues and optional
        // variants retain their original source, codec and volume. Waiting
        // for each request before starting the next prevents memory/I/O bursts.
        private IEnumerator LoadAndroidAudioAcrossFrames()
        {
            yield return null; // let URP render the native terrain first
            foreach (string key in Keys)
            {
                if (IsImmediateStartupCue(key)) continue;
                ResourceRequest request = Resources.LoadAsync<AudioClip>(
                    "SFX/" + key);
                // Gameplay cues start without waiting for optional assets.
                // Only cosmetic native integration yields to this import.
                AarisMobileStreamingFrameGate.TrackEssentialAudioLoad(request);
                yield return request;
                clips[key] = request.asset as AudioClip;
                yield return null; // bound native audio integration per frame
            }
            // All base sounds are now ready; load extra Foley variants last.
            yield return LoadOptionalFoleyAcrossFrames();
        }

        // The thirty authored base cues remain essential and are prewarmed
        // in their original order. Only OPTIONAL CC0 Foley variants compete
        // with car, bike, rival and house native imports on low-RAM Android.
        // A blocked request is retried rather than deleting the sound.
        private enum FoleyGroup { Grass, Concrete, Punch, SoftLanding, MetalImpact }

        private IEnumerator LoadOptionalFoleyAsset(
            string path, FoleyGroup group, int index = 0)
        {
            ResourceRequest request = null;
            while (isActiveAndEnabled && request == null)
            {
                request = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<AudioClip>(
                    path);
                if (request == null) yield return null;
            }
            if (!isActiveAndEnabled) yield break;
            yield return request;
            if (!isActiveAndEnabled) yield break;
            AudioClip clip = request.asset as AudioClip;
            switch (group)
            {
                case FoleyGroup.Grass: grassFoley[index] = clip; break;
                case FoleyGroup.Concrete: concreteFoley[index] = clip; break;
                case FoleyGroup.Punch: punchFoley[index] = clip; break;
                case FoleyGroup.SoftLanding: softLandingFoley = clip; break;
                case FoleyGroup.MetalImpact: metalImpactFoley = clip; break;
            }
            // Even a completed native audio request must not be followed by
            // another optional import during the same rendered frame.
            yield return null;
        }

        private IEnumerator LoadOptionalFoleyAcrossFrames()
        {
            yield return null;
            for (int i = 0; i < grassFoley.Length; i++)
            {
                string number = i.ToString("000");
                yield return LoadOptionalFoleyAsset(
                    "SFX/CC0/footstep_grass_" + number, FoleyGroup.Grass, i);
                yield return LoadOptionalFoleyAsset(
                    "SFX/CC0/footstep_concrete_" + number, FoleyGroup.Concrete, i);
            }
            for (int i = 0; i < punchFoley.Length; i++)
                yield return LoadOptionalFoleyAsset(
                    "SFX/CC0/impactPunch_medium_" + i.ToString("000"),
                    FoleyGroup.Punch, i);
            yield return LoadOptionalFoleyAsset(
                "SFX/CC0/impactSoft_heavy_000", FoleyGroup.SoftLanding);
            yield return LoadOptionalFoleyAsset(
                "SFX/CC0/impactMetal_light_000", FoleyGroup.MetalImpact);
        }
#endif

        private AudioClip GetClip(string name)
        {
            AudioClip[] variants = name == "step_dirt" ? grassFoley :
                name == "step_road" ? concreteFoley :
                name == "punch_hit" ? punchFoley : null;
            if (variants != null)
            {
                AudioClip selected = variants[UnityEngine.Random.Range(0, variants.Length)];
                if (selected != null) return selected;
            }
            if (name == "land_thud" && softLandingFoley != null)
                return softLandingFoley;
            if (name == "grenade_bounce" && metalImpactFoley != null)
                return metalImpactFoley;
            return clips.TryGetValue(name, out AudioClip value) ? value : null;
        }

        // Under dense firefights, incidental footsteps/shells should never
        // cut an important shot or explosion when all pooled voices are busy.
        // Exact string keys and a fixed voice pool keep this branch allocation-free.
        private static bool IsLowPriorityCue(string key) =>
            key == "shell_drop" || key == "step_dirt" ||
            key == "step_road" || key == "step_water" ||
            key == "grenade_bounce";

        // Own weapon, movement and equipment feedback should not fade with
        // the shoulder camera offset. World impacts and explosions stay 3D.
        private static bool IsLocalCue(string key) =>
            key == "smg_shot" || key == "ar_shot" || key == "battle_rifle_shot" ||
            key == "sniper_shot" || key == "shotgun_shot" ||
            key == "bolt_reload" || key == "shell_drop" || key == "punch_hit" ||
            key == "step_dirt" || key == "step_road" || key == "step_water" ||
            key == "jump_start" || key == "land_thud" || key == "jump_whoosh" ||
            key == "parachute_open" || key == "parachute_land" ||
            key == "grenade_pin" || key == "grenade_throw" ||
            key == "car_start" || key == "gloo_deploy";

        private void OneShot(string name, Vector3 point, float volume = 1f,
            float pitch = 1f, bool forceWorld = false)
        {
            AudioClip clip = GetClip(name);
            if (!isActiveAndEnabled || clip == null) return;
            // Reuse an idle pooled voice before cutting off an ongoing sound.
            // A shotgun/impact cue must not be stolen by round-robin wrapping
            // while another of the 12 existing channels has already finished.
            bool local = IsLocalCue(name);
            // Player shots use the dedicated close-mix; nearby rivals remain
            // directional 3D voices, even when sharing the exact same clip.
            if (forceWorld) local = false;
            int start = local ? 0 : LocalVoiceCount;
            int count = local ? LocalVoiceCount : VoiceCount - LocalVoiceCount;
            int cursor = local ? nextLocalVoice : nextWorldVoice;
            int selected = cursor;
            // Editor domain reload can transiently clear nonserialized pooled AudioSource references.
            if (selected < start || selected >= start + count || voices[selected] == null) return;
            bool idleVoice = !voices[selected].isPlaying;
            if (!idleVoice)
            {
                for (int offset = 1; offset < count; offset++)
                {
                    int candidate = start + (cursor - start + offset) % count;
                    AudioSource candidateVoice = voices[candidate];
                    if (candidateVoice == null || candidateVoice.isPlaying) continue;
                    selected = candidate;
                    idleVoice = true;
                    break;
                }
            }
            // Preserve the cursor and currently playing channels if a minor
            // cue cannot find an idle voice. Critical cues retain the original
            // all-busy round-robin preemption behaviour.
            if (!idleVoice && IsLowPriorityCue(name)) return;
            AudioSource voice = voices[selected];
            if (voice == null) return;
            if (local)
                nextLocalVoice = start + (selected - start + 1) % count;
            else
                nextWorldVoice = start + (selected - start + 1) % count;
            // When all voices are busy, preserve the old round-robin fallback.
            if (voice.isPlaying) voice.Stop();
            voice.transform.position = point;
            voice.pitch = Mathf.Clamp(pitch, 0.75f, 1.3f);
            voice.clip = clip;
            voice.volume = Mathf.Clamp01(volume);
            voice.Play();
        }

        // Reserve a saturated cue queue for mechanically meaningful sounds:
        // grenade release > bolt/pump cycle > incidental cartridge landing.
        // Do not drop a cue that is about to play just to accept a later,
        // equal-priority shell. A tiny integer comparison, no allocations.
        private static int ScheduledCuePriority(string key)
        {
            return key == "grenade_throw" ? 2 :
                key == "bolt_reload" ? 1 : 0;
        }

        private void Schedule(string key, Vector3 point, float seconds,
            float volume, bool local = true)
        {
            int empty = -1;
            for (int i = 0; i < scheduled.Length; i++)
                if (scheduled[i].Key == null) { empty = i; break; }
            bool replacedOldest = empty < 0;
            float at = Time.time + seconds;
            if (replacedOldest)
            {
                // Full queue: select the least-important cue, breaking ties
                // by FARTHEST future deadline. Earlier sounds keep playing.
                int lowestPriority = int.MaxValue;
                float latestAt = float.NegativeInfinity;
                for (int i = 0; i < scheduled.Length; i++)
                {
                    int priority = ScheduledCuePriority(scheduled[i].Key);
                    if (priority < lowestPriority ||
                        (priority == lowestPriority && scheduled[i].At > latestAt))
                    {
                        lowestPriority = priority;
                        latestAt = scheduled[i].At;
                        empty = i;
                    }
                }
                int incomingPriority = ScheduledCuePriority(key);
                // Never evict a more important cue. For equal priority,
                // retain the cue scheduled sooner instead of starving it.
                if (incomingPriority < lowestPriority ||
                    (incomingPriority == lowestPriority && at >= latestAt))
                    return;
            }
            scheduled[empty] = new TimedCue
            {
                Key = key, Position = point, At = at, Volume = volume,
                Local = local
            };
            if (replacedOldest)
            {
                nextScheduledAt = float.PositiveInfinity;
                for (int i = 0; i < scheduled.Length; i++)
                    if (scheduled[i].Key != null && scheduled[i].At < nextScheduledAt)
                        nextScheduledAt = scheduled[i].At;
            }
            else
                nextScheduledAt = Mathf.Min(nextScheduledAt, at);
        }

        private void PlayScheduled()
        {
            float now = Time.time;
            if (now < nextScheduledAt) return;
            float next = float.PositiveInfinity;
            for (int i = 0; i < scheduled.Length; i++)
            {
                if (scheduled[i].Key == null) continue;
                if (now < scheduled[i].At)
                {
                    next = Mathf.Min(next, scheduled[i].At);
                    continue;
                }
                TimedCue cue = scheduled[i];
                scheduled[i].Key = null;
                OneShot(cue.Key, cue.Position, cue.Volume,
                    forceWorld: !cue.Local);
            }
            nextScheduledAt = next;
        }

        /// <summary>Only successful weapon shots call this, never a held Fire button.</summary>
        public static void GunShot(string category, string id, Vector3 position,
            bool isPlayerShot = true)
        {
            if (instance == null || !instance.isActiveAndEnabled) return;
            // Real bot shots farther than the spatial voice range cannot be
            // heard. Do not spend an 8-channel world voice, pitch RNG or two
            // timed-cue slots on inaudible combat across the 2km map.
            if (!isPlayerShot &&
                (position - instance.transform.position).sqrMagnitude > 70f * 70f)
                return;
            string sound = category == "SMG" ? "smg_shot" :
                category == "Sniper" ? "sniper_shot" :
                category == "Shotgun" ? "shotgun_shot" :
                id == "BattleRifle" ? "battle_rifle_shot" : "ar_shot";
            float pitch = category == "Pistol" ? 1.24f : 1f;
            instance.OneShot(sound, position, category == "Pistol" ? 0.68f : 0.85f,
                pitch * instance.NextGunshotPitch(), forceWorld: !isPlayerShot);
            // The shell cannot hit the floor before the bolt/pump releases it.
            // From a ~1.3m receiver with upward ejection, a ~0.63s flight
            // is a restrained ground-contact approximation (no extra PhysX).
            float casingDelay = category == "Shotgun" ? .31f :
                category == "Sniper" ? .29f : .025f;
            instance.Schedule("shell_drop", position, casingDelay + .63f,
                .16f, local: isPlayerShot);
            if (category == "Sniper" || category == "Shotgun")
                instance.Schedule("bolt_reload", position, 0.36f, 0.44f,
                    local: isPlayerShot);
        }

        public static void BulletImpact(Vector3 position)
        {
            if (instance != null) instance.OneShot("bullet_impact", position, 0.36f);
        }

        /// <summary>Called only after the actual weapon accepts a reload.</summary>
        public static void ReloadStarted(Vector3 position)
        {
            if (instance != null)
                instance.OneShot("bolt_reload", position, 0.46f);
        }

        public static void Punch(Vector3 position)
        {
            if (instance != null) instance.OneShot("punch_hit", position, 0.65f);
        }

        public static void GrenadeThrow(Vector3 position)
        {
            if (instance == null || !instance.isActiveAndEnabled) return;
            instance.OneShot("grenade_pin", position, 0.5f);
            instance.Schedule("grenade_throw", position, 0.14f, 0.65f);
        }

        public static void GrenadeBounce(Vector3 position, float speed)
        {
            if (instance != null && speed > 1.3f)
                instance.OneShot("grenade_bounce", position, Mathf.Clamp01(speed / 12f) * 0.65f);
        }

        public static void GrenadeExplosion(Vector3 position)
        {
            if (instance != null) instance.OneShot("grenade_boom", position, 1f);
        }

        public static void GlooDeployed(Vector3 position)
        {
            if (instance != null && instance.isActiveAndEnabled)
                instance.OneShot("gloo_deploy", position, 0.88f);
        }

        /// <summary>One shared 3D wood-hinge creak, dispatched only on door motion transitions.</summary>
        public static void DoorSwing(Vector3 position, bool closing)
        {
            if (instance != null && instance.isActiveAndEnabled)
                instance.OneShot("door_swing", position, closing ? 0.36f : 0.52f,
                    closing ? 1.11f : 0.94f);
        }

        public static void VehicleCrash(Vector3 position, float relativeSpeed)
        {
            if (instance == null || relativeSpeed < 4f ||
                Time.time - instance.lastCrashAt < 0.5f) return;
            instance.lastCrashAt = Time.time;
            instance.OneShot("car_crash", position, Mathf.Clamp01(relativeSpeed / 17f));
        }

        private static void BeginLoop(AudioSource source, AudioClip clip)
        {
            if (clip == null || source.clip == clip && source.isPlaying) return;
            source.clip = clip;
            source.volume = 0f;
            source.Play();
        }

        private static bool AdjustLoop(AudioSource source, float volume, float pitch, float dt)
        {
            if (source == null || source.clip == null) return true;
            // A steady loop already at its target should not issue two
            // managed-to-native AudioSource property writes every frame.
            // Preserve the exact MoveTowards result, including the final snap.
            float currentVolume = source.volume;
            float nextVolume = Mathf.MoveTowards(currentVolume, volume, dt * 1.6f);
            if (nextVolume != currentVolume) source.volume = nextVolume;
            float currentPitch = source.pitch;
            float nextPitch = Mathf.MoveTowards(currentPitch, pitch, dt * 1.8f);
            if (nextPitch != currentPitch) source.pitch = nextPitch;
            if (nextVolume <= 0.001f && volume <= 0f && source.isPlaying)
                source.Stop();
            // Do not retire a fade at the audible-stop threshold: finish
            // the exact zero-volume and pitch targets before caching silence.
            return volume <= 0f && nextVolume == 0f && nextPitch == pitch;
        }

        private void LateUpdate()
        {
            // Use real simulation time for velocity measurement. Only cap the
            // audio smoothing step; otherwise a long frame divides the same
            // displacement by a shorter dt and invents an exaggerated wind speed.
            float motionDt = Time.deltaTime;
            float dt = Mathf.Clamp(motionDt, 0f, 0.15f);
            Vector3 pos = transform.position;
            float fallSpeed = motionDt > 0.001f
                ? Mathf.Clamp((previousY - pos.y) / motionDt, 0f, 65f) : 0f;
            previousY = pos.y;
            PlayScheduled();
            // Optional gameplay components can be attached after Awake.
            // If one is absent, retry at most once per second rather than
            // repeating up to three native GetComponent searches every frame.
            if ((drop == null || vehicle == null || combat == null) &&
                Time.unscaledTime >= nextOptionalComponentRefresh)
            {
                nextOptionalComponentRefresh = Time.unscaledTime + 1f;
                if (drop == null) drop = GetComponent<AarisOfflineDrop>();
                if (vehicle == null) vehicle = GetComponent<AarisVehicleSession>();
                if (combat == null) combat = GetComponent<AarisBattleActions>();
            }
            bool airborneDrop = drop != null && drop.enabled && !drop.HasLanded;
            bool driving = vehicle != null && vehicle.IsDriving;

            TickDrop(airborneDrop, fallSpeed, pos, dt);
            TickVehicle(driving, dt);

            if (!airborneDrop && !driving && player != null && player.enabled)
                TickFootsteps(pos);
            else stepDistance = 0f;

            previousPosition = pos;
        }

        private void TickDrop(bool activeDrop, float fallSpeed, Vector3 pos, float dt)
        {
            if (!activeDrop)
            {
                if (hasDropPhase && drop != null && drop.HasLanded)
                {
                    OneShot("parachute_land", pos, 0.85f);
                    // ResumeFromAerialDrop intentionally sets LastLandingTime.
                    // The parachute touchdown already has its own cue:
                    // prevent a second generic walking landing in this frame.
                    if (player != null) previousLand = player.LastLandingTime;
                }
                if (!dropLoopsSettled)
                {
                    // Both calls must run: short-circuit && would starve
                    // the second loop's fade while the first is settling.
                    bool planeSettled = AdjustLoop(planeLoop, 0f, 1f, dt);
                    bool windSettled = AdjustLoop(windLoop, 0f, 1f, dt);
                    dropLoopsSettled = planeSettled && windSettled;
                }
                hasDropPhase = false;
                return;
            }
            dropLoopsSettled = false;
            var phase = drop.Phase;
            if (!hasDropPhase || phase != lastDropPhase)
            {
                if (phase == AarisOfflineDrop.DropPhase.InPlane)
                    BeginLoop(planeLoop, GetClip("plane_engine_loop"));
                else
                {
                    if (phase == AarisOfflineDrop.DropPhase.Freefall)
                        OneShot("jump_whoosh", pos, 0.8f);
                    if (phase == AarisOfflineDrop.DropPhase.Opening ||
                        (phase == AarisOfflineDrop.DropPhase.Gliding &&
                         (!hasDropPhase || lastDropPhase == AarisOfflineDrop.DropPhase.Freefall)))
                        OneShot("parachute_open", pos, 0.9f);
                    if (phase != AarisOfflineDrop.DropPhase.Landing)
                    {
                        // Reuse the same pooled 2D loop source: audible canopy
                        // flutter during slow glide, full wind rush in freefall.
                        bool underCanopy = phase == AarisOfflineDrop.DropPhase.Opening ||
                            phase == AarisOfflineDrop.DropPhase.Gliding;
                        BeginLoop(windLoop, GetClip(underCanopy ?
                            "canopy_glide_loop" : "wind_rush_loop"));
                    }
                }
                lastDropPhase = phase;
                hasDropPhase = true;
            }
            float planeVol = phase == AarisOfflineDrop.DropPhase.InPlane ? 0.65f : 0f;
            float windVol = phase == AarisOfflineDrop.DropPhase.Freefall
                ? Mathf.Lerp(0.35f, 0.85f, fallSpeed / 54f) :
                phase == AarisOfflineDrop.DropPhase.Opening ||
                phase == AarisOfflineDrop.DropPhase.Gliding ? 0.30f : 0f;
            float windPitch = phase == AarisOfflineDrop.DropPhase.Freefall
                ? Mathf.Lerp(0.9f, 1.25f, fallSpeed / 54f) : 0.85f;
            AdjustLoop(planeLoop, planeVol, 1f, dt);
            AdjustLoop(windLoop, windVol, windPitch, dt);
        }

        private void TickVehicle(bool driving, float dt)
        {
            var car = driving ? vehicle.DrivenCar : null;
            if (car == null)
            {
                if (wasDriving) wasDriving = false;
                vehicleGear = 1;
                if (!engineLoopSettled)
                    engineLoopSettled = AdjustLoop(engineLoop, 0f, 0.85f, dt);
                return;
            }
            engineLoopSettled = false;
            if (!wasDriving)
            {
                OneShot("car_start", car.transform.position, 0.8f);
                BeginLoop(engineLoop, GetClip("car_engine_loop"));
                vehicleGear = 1;
                wasDriving = true;
            }
            float speed = Mathf.Abs(car.SpeedMetresPerSecond);
            float normalized = Mathf.Clamp01(speed /
                Mathf.Max(1f, car.TopSpeedMetresPerSecond));
            // Four synthesized forward gears with hysteresis. The former
            // Mathf.Repeat band snapped between low/high pitch whenever road
            // speed hovered on a 25% boundary, producing audible gear chatter.
            // Audio only: WheelColliders/torque remain the physics authority.
            float signedSpeed = car.SpeedMetresPerSecond;
            if (signedSpeed < -0.2f)
                vehicleGear = 1;
            else
            {
                float upshift = vehicleGear * 0.25f + 0.015f;
                float downshift = (vehicleGear - 1) * 0.25f - 0.055f;
                if (vehicleGear < 4 && normalized > upshift)
                    vehicleGear++;
                else if (vehicleGear > 1 && normalized < downshift)
                    vehicleGear--;
            }
            float gearLow = (vehicleGear - 1) * 0.25f;
            float gearHigh = vehicleGear * 0.25f;
            float gearPhase = signedSpeed < -0.2f
                ? Mathf.Clamp01(speed / 7f)
                : Mathf.InverseLerp(gearLow, gearHigh, normalized);
            float engineLoad = Mathf.Abs(car.ThrottleInput);
            // Use the existing single engine loop. Damage makes a worn
            // motor audibly laboured, without adding more AudioSources or
            // altering the physical WheelCollider gearbox.
            float wear = car.Damage01;
            float targetPitch = Mathf.Clamp(0.82f + gearPhase * 0.58f +
                engineLoad * 0.11f - wear * .12f, 0.78f, 1.58f);
            float engineVolume = Mathf.Clamp01(0.36f + normalized * 0.42f +
                engineLoad * 0.13f);
            AdjustLoop(engineLoop, engineVolume, targetPitch, dt);
            // WheelCollider already calculates a slip-based tyre model.
            // Use its real contact slip rather than chassis lateral velocity:
            // a fast lane change with gripping tyres should not sound like a
            // drift, while a locked/braking or sideways-sliding tyre should.
            float tyreSlip = car.TyreSlip;
            float slipBlend = Mathf.InverseLerp(0.24f, 0.90f, tyreSlip);
            // The RPM/gear simulation correctly follows *forward* speed,
            // but skid audio must follow TOTAL chassis speed. A car sliding
            // sideways at road speed has near-zero longitudinal velocity
            // even while tyres are audibly scrubbing the asphalt.
            float roadSpeed = car.TotalSpeedMetresPerSecond;
            bool tyreSkidding = roadSpeed > 4f && tyreSlip > 0.24f;
            float skidCadence = Mathf.Lerp(0.82f, 0.38f, slipBlend);
            if (tyreSkidding && Time.time - lastSkidAt >= skidCadence)
            {
                lastSkidAt = Time.time;
                OneShot("tire_skid", car.transform.position,
                    Mathf.Lerp(0.28f, 0.68f, slipBlend),
                    Mathf.Lerp(0.94f, 1.06f, slipBlend));
            }
        }

        // One sound per alternate foot contact, not one sound per arbitrary
        // elapsed second. Mirror AarisCharacterVisual's distance-driven clip
        // cycles: each forward/crouch/crawl cycle contains two ground contacts.
        // Read the same authoritative motor ceilings as the animation system;
        // stale 4.4/4.8 m/s thresholds made sprint audio drift from planted feet.
        private float FootfallSpacing(float metresPerSecond, bool armed,
            AarisBattleActions.PlayerStance stance)
        {
            if (stance == AarisBattleActions.PlayerStance.Prone) return 0.45f;
            if (stance == AarisBattleActions.PlayerStance.Crouching) return 0.70f;
            float jogCeiling = player != null
                ? player.JogSpeedMetresPerSecond : 4.2f;
            float sprintCeiling = player != null
                ? (armed ? player.ArmedSprintSpeedMetresPerSecond :
                    player.UnarmedSprintSpeedMetresPerSecond)
                : (armed ? 5.7f : 6.5f);
            float runStart = Mathf.Max(1.8f, jogCeiling * 0.55f);
            float sprintStart = Mathf.Max(jogCeiling * 0.95f,
                sprintCeiling * 0.72f);
            float walkRunBlend = Mathf.InverseLerp(
                runStart, jogCeiling, metresPerSecond);
            float sprintBlend = Mathf.InverseLerp(
                sprintStart, sprintCeiling, metresPerSecond);
            float cycleDistance = Mathf.Lerp(1.25f,
                Mathf.Lerp(2.55f, 3.65f, sprintBlend), walkRunBlend);
            return cycleDistance * 0.5f;
        }

        private void TickFootsteps(Vector3 pos)
        {
            if (capsule == null || !capsule.enabled) return;
            if (player.LastJumpStartedTime > previousJump &&
                Time.unscaledTime - player.LastJumpStartedTime < 0.3f)
            {
                previousJump = player.LastJumpStartedTime;
                OneShot("jump_start", pos, 0.58f);
            }
            if (player.LastLandingTime > previousLand &&
                Time.unscaledTime - player.LastLandingTime < 0.3f)
            {
                previousLand = player.LastLandingTime;
                OneShot("land_thud", pos, 0.72f);
            }
            if (!capsule.isGrounded) { stepDistance = 0f; return; }
            var stance = combat != null ? combat.Stance : AarisBattleActions.PlayerStance.Standing;
            // The crouch and crawl clips keep independent gait clocks. Avoid
            // carrying a half-finished standing step into a new stance.
            if (stance != lastFootstepStance)
            {
                stepDistance = 0f;
                lastFootstepStance = stance;
            }
            Vector3 delta = pos - previousPosition;
            delta.y = 0f;
            float moved = delta.magnitude;
            if (moved > 1.5f) { stepDistance = 0f; return; }
            if (moved < 0.008f) return;
            float speed = moved / Mathf.Max(Time.deltaTime, 0.001f);
            if (speed < 0.4f) return;
            float stride = FootfallSpacing(speed, combat != null && combat.IsArmed, stance);
            stepDistance += moved;
            if (stepDistance < stride) return;
            stepDistance %= stride;
            string surface = "step_dirt";
            if (Physics.Raycast(pos + Vector3.up * 0.4f, Vector3.down,
                    out RaycastHit hit, 2.0f, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                // Case-insensitive comparisons avoid allocating a lowercased
                // string on every footstep while keeping material detection.
                string label = hit.collider.name;
                if (label.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("river", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("stream", StringComparison.OrdinalIgnoreCase) >= 0)
                    surface = "step_water";
                else if (label.IndexOf("road", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("asphalt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("concrete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("bridge", StringComparison.OrdinalIgnoreCase) >= 0)
                    surface = "step_road";
            }
            float volume = stance == AarisBattleActions.PlayerStance.Prone
                ? 0.30f : speed >= 4.0f ? 0.61f : 0.48f;
            OneShot(surface, pos, volume,
                UnityEngine.Random.Range(0.92f, 1.08f));
        }

        private void OnDisable()
        {
            if (instance != this) return;
            foreach (var voice in voices)
                if (voice != null) voice.Stop();
            if (planeLoop != null) planeLoop.Stop();
            if (windLoop != null) windLoop.Stop();
            if (engineLoop != null) engineLoop.Stop();
            Array.Clear(scheduled, 0, scheduled.Length);
            nextScheduledAt = float.PositiveInfinity;
            hasDropPhase = wasDriving = false;
            vehicleGear = 1;
            stepDistance = 0f;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            if (audioRoot != null) Destroy(audioRoot);
        }
    }
}
