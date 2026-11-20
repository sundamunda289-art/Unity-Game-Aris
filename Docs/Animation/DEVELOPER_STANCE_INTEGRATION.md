# MIT developer-authored stance integration

This branch consumes third-party implementations instead of attempting to recreate all humanoid poses procedurally.

- **EasyLoco** (Pue-Tsua Workshop), MIT: unmodified Humanoid muscle-curve animations `IdleStandDefault.anim`, `IdleCrouchDefault.anim`, `IdleProneDefault.anim`, with original Unity meta files. Source: https://github.com/puetsua/VRCEasyLoco ; license included at `Docs/ThirdParty/VRCEasyLoco_MIT.txt`.
- **ALS-Unity** (Serkan Yalnız), MIT: the original `ALSMath.cs` stance-weight interpolation helper imported unmodified. Source: https://github.com/schyro/ALS-Unity ; license and third-party notices under `Docs/ThirdParty/`.

### Actual wiring

The PlayableGraph loads EasyLoco Crouch/Prone poses when both are present, have valid Humanoid motion, and the calibrated primary avatar is active. The game's current crouch-walk/crawl movement clips remain in place, and physical camera/capsule authority is unchanged. The MIT ALS interpolation helper replaces the custom stance-weight smoothing equation. The imported Crouch pose bypasses old custom X/Z targets, while imported Prone uses the existing soldier-specific terrain-foot IK and root calibration as a compatibility adapter: the original developer clip supplies animation muscles, but its VR-avatar foot coordinates do not retarget to this soldier without geometric support. Unity-native QA must verify both. If a clip is incompatible, the old authored FBX fallback stays available and is explicitly logged.

### Validation

Static tests do not prove retargeting. The acceptance gate is a real Unity 6000.3.26f1 Play Mode pass with `AARIS_MIT_STANCE_CLIPS_ACTIVE` in the log, zero compiler errors, 16 pose-angle captures, grounded foot/knee clearance, and moving/transition state checks. A merely successful process exit is not enough. Device FPS/GC remains a separate check.

Third-party source code, clips and licenses have been kept in dedicated paths for auditing and future replacement.

### Native QA record

Unity 6000.3.26f1 actually loaded both EasyLoco clips and compiled with zero errors. Crouch knee-height delta was 0.044–0.046 m (pass), but two direct VR-avatar prone poses failed on the soldier rig: default left lower leg ~0.568 m above terrain; lying-down alternative ~0.527 m; prone knee delta ~0.394 m. The foot spread collapsed to ~0.006 m. The subsequent branch update re-enables the existing proven terrain-foot IK for prone only; **its native QA result must be checked separately and cannot be inferred from static tests**.
