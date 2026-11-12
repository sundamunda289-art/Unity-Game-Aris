using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Offline gameplay mixer with dedicated player feedback and positional world sounds.
    /// Uses the existing movement, drop, combat and vehicle authorities.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(900)]
    public sealed class AarisAudioDirector : MonoBehaviour
    {
        private const int FeedbackVoiceCount = 4;
        private const int WorldVoiceCount = 8;
        private const int TransitionPriority = 32;
        private const int ActionPriority = 72;
        private const int DetailPriority = 150;
        // Fixed-size timed cue queue: zero allocations when automatic guns fire.
        private struct TimedCue
        {
            public string Key;
            public Vector3 Position;
            public float At, Volume;
        }
        private readonly TimedCue[] scheduled = new TimedCue[16];
        private static AarisAudioDirector instance;
        private static readonly string[] Keys =
        {
            "plane_engine_loop", "jump_whoosh", "wind_rush_loop", "canopy_glide_loop",
            "parachute_open", "parachute_land", "step_dirt", "step_road",
            "step_water", "jump_start", "land_thud", "smg_shot",
            "ar_shot", "battle_rifle_shot", "sniper_shot", "shotgun_shot",
            "bolt_reload", "shell_drop", "bullet_impact", "punch_hit",
            "grenade_pin", "grenade_throw", "grenade_bounce", "grenade_boom",
            "car_start", "car_engine_loop", "tire_skid", "car_crash", "gloo_deploy"
        };
        public static IReadOnlyList<string> RequiredCueKeys => Keys;
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(32);
        private readonly AudioSource[] feedbackVoices = new AudioSource[FeedbackVoiceCount];
        private readonly AudioSource[] worldVoices = new AudioSource[WorldVoiceCount];
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
        private AudioSource planeLoop, windLoop, canopyLoop, engineLoop;
        private AarisOfflineDrop.DropPhase lastDropPhase;
        private bool hasDropPhase, wasDriving;
        private float previousY, windDuckUntil;
        private float stepDistance, lastSkidAt = -10f, lastCrashAt = -10f;
        private int surfaceMask;
        private Vector3 previousPosition;

        private static AudioSource MakeSource(Transform parent, string name, bool local)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            var source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = local ? 0f : 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 5f;
            source.maxDistance = 65f;
            source.dopplerLevel = 0f;
            source.priority = DetailPriority;
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
            foreach (string key in Keys)
            {
                var clip = Resources.Load<AudioClip>("SFX/" + key);
                clips[key] = clip;
                if (clip == null) Debug.LogWarning("Missing gameplay AudioClip: SFX/" + key, this);
            }
            // Kenney Impact Sounds (publisher CC0); eagerly load once,
            // then choose real varied footfalls without per-step allocations.
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
            audioRoot = new GameObject("Aaris Audio Voices (12 pooled)");
            for (int i = 0; i < feedbackVoices.Length; i++)
                feedbackVoices[i] = MakeSource(audioRoot.transform, "Player Feedback " + i, true);
            for (int i = 0; i < worldVoices.Length; i++)
                worldVoices[i] = MakeSource(audioRoot.transform, "World SFX " + i, false);
            planeLoop = MakeSource(audioRoot.transform, "Plane Engine Loop", true);
            windLoop = MakeSource(audioRoot.transform, "Drop Wind Loop", true);
            canopyLoop = MakeSource(audioRoot.transform, "Parachute Canopy Loop", true);
            engineLoop = MakeSource(audioRoot.transform, "Vehicle Engine Loop", true);
            planeLoop.loop = windLoop.loop = canopyLoop.loop = engineLoop.loop = true;
            planeLoop.volume = windLoop.volume = canopyLoop.volume = engineLoop.volume = 0f;
            int playerLayer = LayerMask.NameToLayer("Player");
            surfaceMask = playerLayer < 0 ? Physics.DefaultRaycastLayers :
                Physics.DefaultRaycastLayers & ~(1 << playerLayer);
            previousY = transform.position.y;
            previousPosition = transform.position;
        }

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
            if (name == "grenade_bounce" && metalImpactFoley != null)
                return metalImpactFoley;
            if (clips.TryGetValue(name, out AudioClip value) && value != null) return value;
            return name == "land_thud" ? softLandingFoley : null;
        }

        private void Feedback(string name, Vector3 point, float volume,
            int priority = ActionPriority, float pitch = 1f)
        {
            PlayCue(feedbackVoices, name, point, volume, priority, pitch);
        }

        private void WorldCue(string name, Vector3 point, float volume, int priority = DetailPriority)
        {
            PlayCue(worldVoices, name, point, volume, priority, 1f);
        }

        private void PlayCue(AudioSource[] pool, string name, Vector3 point, float volume, int priority, float pitch)
        {
            if (!isActiveAndEnabled) return;
            AudioClip clip = GetClip(name);
            if (clip == null) return;
            AudioSource voice = PickVoice(pool, priority);
            if (voice == null) return;
            if (voice.isPlaying) voice.Stop();
            voice.transform.position = point;
            voice.pitch = Mathf.Clamp(pitch, 0.75f, 1.3f);
            voice.priority = priority;
            voice.clip = clip;
            voice.volume = Mathf.Clamp01(volume);
            voice.Play();
        }

        private static AudioSource PickVoice(AudioSource[] pool, int priority)
        {
            AudioSource replacement = null;
            float shortest = float.PositiveInfinity;
            foreach (var voice in pool)
            {
                if (!voice.isPlaying) return voice;
                if (voice.priority < priority) continue;
                float remaining = (voice.clip.length - voice.time) / voice.pitch;
                if (replacement == null || voice.priority > replacement.priority ||
                    (voice.priority == replacement.priority && remaining < shortest))
                {
                    replacement = voice;
                    shortest = remaining;
                }
            }
            return replacement;
        }

        internal void PlayJump()
        {
            Feedback("jump_start", transform.position, 0.78f, TransitionPriority);
        }

        internal void PlayLanding(float impactSpeed)
        {
            float volume = Mathf.Lerp(0.58f, 0.88f, Mathf.InverseLerp(2f, 10f, impactSpeed));
            Feedback("land_thud", transform.position, volume, TransitionPriority);
        }

        private void Schedule(string key, Vector3 point, float seconds, float volume)
        {
            int empty = -1;
            for (int i = 0; i < scheduled.Length; i++)
                if (scheduled[i].Key == null) { empty = i; break; }
            // At high rates, drop only the oldest noncritical tail cue.
            if (empty < 0)
            {
                empty = 0;
                for (int i = 1; i < scheduled.Length; i++)
                    if (scheduled[i].At < scheduled[empty].At) empty = i;
            }
            scheduled[empty] = new TimedCue
            {
                Key = key, Position = point, At = Time.time + seconds,
                Volume = volume
            };
        }

        private void PlayScheduled()
        {
            for (int i = 0; i < scheduled.Length; i++)
            {
                if (scheduled[i].Key == null || Time.time < scheduled[i].At) continue;
                TimedCue cue = scheduled[i];
                scheduled[i].Key = null;
                Feedback(cue.Key, cue.Position, cue.Volume,
                    cue.Key == "grenade_throw" ? ActionPriority : DetailPriority);
            }
        }

        /// <summary>Only successful weapon shots call this, never a held Fire button.</summary>
        public static void GunShot(string category, string id, Vector3 position)
        {
            if (instance == null || !instance.isActiveAndEnabled) return;
            string sound = category == "SMG" ? "smg_shot" :
                category == "Sniper" ? "sniper_shot" :
                category == "Shotgun" ? "shotgun_shot" :
                id == "BattleRifle" ? "battle_rifle_shot" : "ar_shot";
            instance.Feedback(sound, position, 0.63f, ActionPriority,
                UnityEngine.Random.Range(0.97f, 1.035f));
            instance.Schedule("shell_drop", position, 0.14f, 0.16f);
            if (category == "Sniper" || category == "Shotgun")
                instance.Schedule("bolt_reload", position, 0.36f, 0.44f);
        }

        public static void BulletImpact(Vector3 position)
        {
            if (instance != null) instance.WorldCue("bullet_impact", position, 0.42f);
        }

        /// <summary>Called only after the actual weapon accepts a reload.</summary>
        public static void ReloadStarted(Vector3 position)
        {
            if (instance != null)
                instance.Feedback("bolt_reload", position, 0.55f);
        }

        public static void Punch(Vector3 position)
        {
            if (instance != null) instance.Feedback("punch_hit", position, 0.60f);
        }

        public static void GrenadeThrow(Vector3 position)
        {
            if (instance == null || !instance.isActiveAndEnabled) return;
            instance.Feedback("grenade_pin", position, 0.48f);
            instance.Schedule("grenade_throw", position, 0.14f, 0.65f);
        }

        public static void GrenadeBounce(Vector3 position, float speed)
        {
            if (instance != null && speed > 1.3f)
                instance.WorldCue("grenade_bounce", position, Mathf.Clamp01(speed / 12f) * 0.65f);
        }

        public static void GrenadeExplosion(Vector3 position)
        {
            if (instance != null) instance.WorldCue("grenade_boom", position, 0.92f, TransitionPriority);
        }

        public static void GlooDeployed(Vector3 position)
        {
            if (instance != null && instance.isActiveAndEnabled)
                instance.Feedback("gloo_deploy", position, 0.69f);
        }

        public static void VehicleCrash(Vector3 position, float relativeSpeed)
        {
            if (instance == null || relativeSpeed < 4f ||
                Time.time - instance.lastCrashAt < 0.5f) return;
            instance.lastCrashAt = Time.time;
            instance.WorldCue("car_crash", position, Mathf.Clamp01(relativeSpeed / 17f), ActionPriority);
        }

        private void DriveLoop(AudioSource source, string key, float volume, float pitch, float dt)
        {
            if (volume > 0f && !source.isPlaying && !AudioListener.pause)
            {
                if (source.clip == null) source.clip = GetClip(key);
                if (source.clip == null) return;
                source.volume = 0f;
                source.pitch = pitch;
                source.Play();
            }
            float fadeRate = volume < source.volume ? 3.4f : 1.55f;
            source.volume = Mathf.MoveTowards(source.volume, volume, dt * fadeRate);
            source.pitch = Mathf.MoveTowards(source.pitch, pitch, dt * 1.8f);
            if (source.volume <= 0.001f && volume <= 0f && source.isPlaying) source.Stop();
        }

        private void LateUpdate()
        {
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.15f);
            Vector3 pos = transform.position;
            float fallSpeed = dt > 0.001f
                ? Mathf.Clamp((previousY - pos.y) / dt, 0f, 65f) : 0f;
            previousY = pos.y;
            PlayScheduled();
            if (drop == null) drop = GetComponent<AarisOfflineDrop>();
            if (vehicle == null) vehicle = GetComponent<AarisVehicleSession>();
            if (combat == null) combat = GetComponent<AarisBattleActions>();
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
            var phase = activeDrop ? drop.Phase : AarisOfflineDrop.DropPhase.Landed;
            if (!activeDrop)
            {
                if (hasDropPhase && drop != null && drop.HasLanded)
                    Feedback("parachute_land", pos, 0.88f, TransitionPriority);
                hasDropPhase = false;
            }
            else if (!hasDropPhase || phase != lastDropPhase)
            {
                if (phase == AarisOfflineDrop.DropPhase.Freefall)
                    Feedback("jump_whoosh", pos, 0.77f, TransitionPriority);
                if (phase == AarisOfflineDrop.DropPhase.Opening ||
                    (phase == AarisOfflineDrop.DropPhase.Gliding &&
                     (!hasDropPhase || lastDropPhase == AarisOfflineDrop.DropPhase.Freefall)))
                {
                    Feedback("parachute_open", pos, 0.88f, TransitionPriority);
                    windDuckUntil = Time.unscaledTime + 0.42f;
                }
                lastDropPhase = phase;
                hasDropPhase = true;
            }

            bool inPlane = phase == AarisOfflineDrop.DropPhase.InPlane;
            bool freefall = phase == AarisOfflineDrop.DropPhase.Freefall;
            bool underCanopy = phase == AarisOfflineDrop.DropPhase.Opening || phase == AarisOfflineDrop.DropPhase.Gliding;
            float windSpeed = Mathf.Clamp01(fallSpeed / 54f);
            float windVolume = freefall ? Mathf.Lerp(0.24f, 0.49f, windSpeed) : underCanopy ? 0.12f : 0f;
            float canopyVolume = underCanopy ? 0.24f : 0f;
            if (Time.unscaledTime < windDuckUntil)
            {
                windVolume *= 0.45f;
                canopyVolume *= 0.5f;
            }
            DriveLoop(planeLoop, "plane_engine_loop", inPlane ? 0.54f : 0f, 1f, dt);
            DriveLoop(windLoop, "wind_rush_loop", windVolume,
                freefall ? Mathf.Lerp(0.91f, 1.19f, windSpeed) : 0.85f, dt);
            DriveLoop(canopyLoop, "canopy_glide_loop", canopyVolume, 1f, dt);
        }

        private void TickVehicle(bool driving, float dt)
        {
            var car = driving ? vehicle.DrivenCar : null;
            if (car == null)
            {
                if (wasDriving) wasDriving = false;
                DriveLoop(engineLoop, "car_engine_loop", 0f, 0.85f, dt);
                return;
            }
            if (!wasDriving)
            {
                Feedback("car_start", car.transform.position, 0.63f);
                wasDriving = true;
            }
            float speed = Mathf.Abs(car.SpeedMetresPerSecond);
            float normalized = Mathf.Clamp01(speed /
                Mathf.Max(1f, car.TopSpeedMetresPerSecond));
            // Four smooth synthesized gear bands. Audio only: real physics
            // still owns RPM, WheelColliders, traction and torque.
            float gearPhase = Mathf.Repeat(normalized * 4f, 1f);
            float targetPitch = Mathf.Clamp(0.84f + normalized * 0.62f +
                gearPhase * 0.31f + Mathf.Abs(car.ThrottleInput) * 0.09f,
                0.8f, 2f);
            DriveLoop(engineLoop, "car_engine_loop",
                Mathf.Lerp(0.35f, 0.68f, normalized), targetPitch, dt);
            bool hardBrake = speed > 9f &&
                (car.ThrottleInput < -0.8f || car.LateralSlipMetresPerSecond > 3.5f);
            if (hardBrake && Time.time - lastSkidAt >= 0.95f)
            {
                lastSkidAt = Time.time;
                Feedback("tire_skid", car.transform.position, 0.49f);
            }
        }

        private void TickFootsteps(Vector3 pos)
        {
            if (capsule == null || !capsule.enabled) return;
            if (!capsule.isGrounded) { stepDistance = 0f; return; }
            Vector3 delta = pos - previousPosition;
            delta.y = 0f;
            float moved = delta.magnitude;
            if (moved > 1.5f) { stepDistance = 0f; return; }
            if (moved < 0.008f) return;
            float speed = moved / Mathf.Max(Time.deltaTime, 0.001f);
            if (speed < 0.4f) return;
            float stride = combat != null && combat.Stance == AarisBattleActions.PlayerStance.Prone
                ? 0.65f : combat != null && combat.Stance == AarisBattleActions.PlayerStance.Crouching
                ? 0.92f : speed > 5.4f ? 1.65f : 1.25f;
            stepDistance += moved;
            if (stepDistance < stride) return;
            stepDistance %= stride;
            string surface = "step_dirt";
            if (Physics.Raycast(pos + Vector3.up * 0.4f, Vector3.down,
                    out RaycastHit hit, 2.0f, surfaceMask,
                    QueryTriggerInteraction.Ignore))
            {
                string label = hit.collider.name;
                if (label.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("river", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("stream", StringComparison.OrdinalIgnoreCase) >= 0) surface = "step_water";
                else if (!(hit.collider is TerrainCollider)) surface = "step_road";
            }
            Feedback(surface, pos, speed > 5.4f ? 0.53f : 0.42f, DetailPriority,
                UnityEngine.Random.Range(0.92f, 1.08f));
        }

        private void OnDisable()
        {
            if (instance != this) return;
            foreach (var voice in feedbackVoices)
                if (voice != null) voice.Stop();
            foreach (var voice in worldVoices)
                if (voice != null) voice.Stop();
            if (planeLoop != null) planeLoop.Stop();
            if (windLoop != null) windLoop.Stop();
            if (canopyLoop != null) canopyLoop.Stop();
            if (engineLoop != null) engineLoop.Stop();
            Array.Clear(scheduled, 0, scheduled.Length);
            hasDropPhase = wasDriving = false;
            windDuckUntil = 0f;
            stepDistance = 0f;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            if (audioRoot != null) Destroy(audioRoot);
        }
    }
}
