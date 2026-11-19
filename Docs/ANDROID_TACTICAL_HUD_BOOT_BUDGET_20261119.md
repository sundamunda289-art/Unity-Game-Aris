# Android first-frame Tactical HUD budget — 2026-11-19

## Audited startup collision

The real scene boot contains multiple UI/asset creation paths: `AarisBattleHUD`, `AarisWeaponHUD`, `AarisOfflineTacticalHUD`, the player's runtime boot and the optional 3D assets. The original `AarisOfflineTacticalHUD.Start()` built its full Canvas, graphics, action buttons, text and event components in the same initial frame, even though the offline battle royale starts inside the aircraft where tactical actions are unavailable.

This is a **plausible CPU initialization burst**, not a measured explanation of a 3–30 second stall or system freeze. No Unity Profiler trace or Android proof has been obtained.

## Narrow Android-only optimization

When `AarisOfflineDrop` is present and the player has not landed, `AarisOfflineTacticalHUD` keeps the same component and gameplay state but defers construction of its **optional** uGUI hierarchy by two frames, then hides its Canvas and GraphicRaycaster before enabling tactical updates if flight/driving is still active. This avoids its one-time UI object/Canvas build in the first render frame. Original labels, layout, controls, colours, graphics, default material and physics are not modified. Medical site streaming and grenades are unchanged.

When already on foot, no `AarisOfflineDrop` is present, or running in the Unity Editor or non-Android player, the original immediate build remains. The existing air-drop HUD control-handoff remains untouched: `AarisOfflineDrop` caches `AarisWeaponHUD`'s Canvas at flight setup, so deferring weapon HUD creation independently could leave input enabled during flight; this optimization deliberately does **not** do that.

The grenade resource prewarm retains its previous Android schedule. No terrain, water, URP, tree, texture or 3D model source changes are necessary.

## Validation and remaining limits

Source regression tests verify Android-only flight gating, two yields, immediate nonflight/editor initialization, no click interception during flight, preserved controls and existing WeaponHUD flight ownership. Existing mobile HUD/physics/asset streaming source tests should still pass. These checks **cannot** prove a specific FPS gain or compilation: the Codespace Unity Editor license remains unavailable. Real Android logcat, Unity Profiler and long-match comparison are needed for attributable freeze/crash conclusions.

Review source: `Assets/AarisWorld/Scripts/AarisOfflineTacticalHUD.cs`.
