# NEW movement-only motion sources (separate from legacy Aris animations)

This experimental source pipeline is **not a substitute for tested Humanoid clips**.
It downloads 9 new 100STYLE motion-only skeleton GLBs to temporary CI storage.
It does not add them to Assets/Resources, launch Unity, or change the main game.

The 100STYLE CC BY 4.0 data comes from Ian Mason, Sebastian Starke and Taku
Komura; the 100style-glb dataset is a third-party derivative. Source motion
includes forward/backward/side/idle in both upright and crouched gaits.

ACCAD's Male1 BVH archive is probed separately for candidate stand/lie/crawl
transitions. The original ACCAD recordings are CC BY 3.0 and require their
own verified skeletal mapping. Raw C3D/marker data is **not** a usable Unity
Humanoid clip without rigging; do not silently swap in old crawl animations.

Run `python3 Tools/CI/fetch_gym_mocap.py --inspect-accad` from an internet
connected Linux environment to reproduce. The GitHub Action emits hash and
clip metadata only; large motion files never enter the canonical repo.
The next stage requires trimming/loop-contact review, humanoid retarget,
and a new 2D blend tree before the first actual Gym APK.

References:
- https://www.ianxmason.com/100style/
- https://huggingface.co/datasets/codepawl/100style-glb
- https://accad.osu.edu/research/motion-lab/mocap-system-and-data
- https://github.com/schyro/ALS-Unity
