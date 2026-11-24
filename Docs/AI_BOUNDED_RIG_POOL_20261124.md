# Bounded 100-bot humanoid reuse (2026-11-24)

Unity 6000.3.26f1 / Android URP. Scope is visual lifecycle only. Existing 100 logical opponents, physical gun damage, 350ms distant AI cadence, kill feed, two-player-attacker cap, scope receivers, map quality and 155m detailed visual policy are unchanged.

## Reason
RetireHiddenRig previously destroyed the instantiated FBX after a 5s hidden dwell beyond 190m. Returning to a settlement required re-instantiation, nested material/collider traversal, skinning resource admission and skeleton fitting. On phones this can cause camera/vehicle traversal hitches.

## Policy
- Never retain dormant rigs on devices reporting under 3GB RAM (keeps existing Destroy path).
- Allow at most one passive dormant FBX on 3-6GB and two on 6GB+; no additional rigs are pre-created.
- Only passive SkinnedMeshRenderer roots with no MonoBehaviour, Rigidbody or Collider are eligible. Other source prefabs keep the original teardown behavior.
- Destroy PlayableGraph before retiring. Restore original forceRenderingOff and Animator.enabled flags so an unseen rig never remains permanently invisible after reuse.
- Retain original fitted local pose/scale and rebind Animator on reuse, avoiding duplicate skeleton scaling and asset hierarchy reconstruction.
- An Application.lowMemory signal destroys all dormant rigs and permanently disables the optional cache for the match.
- Reuse/render admission still goes through the existing Android TryClaimOptionalModelBuild frame gate. All unchanged authored materials and model quality are retained.

## Validation boundaries
The companion Python source-contract and native Unity EditMode test cover bounded memory policy, hierarchy reuse, renderer flag recovery, scale stability and low-memory eviction. Source-only Unity compile does not prove that an APK's 1%-low FPS or GPU cost improved; an actual Snapdragon 680 profile with repeated >190m departure/return and low-memory signals remains required.
