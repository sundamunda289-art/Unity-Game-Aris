# Android medical candidate scan pacing — 2026-11-19

## Source-level observation

The map's 205,956-byte local candidate JSON contains 1,577 entries.
AarisOfflineTacticalHUD selects approximately 284 outdoor medical/helmet
sites. The existing MaxMedicalSpawnProbesPerFrame=2 cap applied ONLY to
expensive nearby placement queries, not to the full list scan. When
every candidate was distant or unavailable, one Update could visit all
284 sites before returning.

The underlying AarisLootDocument.Load() already has a shared cached
document. Repeated JSON parsing is NOT established as an ongoing cost.

## Targeted, reversible fix

On Android only, limit StreamMedicalSites to 48 candidate visits each
rendered frame, counting distant, taken and retry-pending candidates.
Keep the existing two expensive physics/visual spawn probes per frame,
eight active visuals, pickup check every 0.08 seconds, refresh ordering,
and cursor resume exactly as before. In an all-distant worst case the
284 candidate checks take six frames instead of a single frame.

Desktop/Editor scan behavior remains unchanged. No gameplay sites, loot
amounts, models, materials, textures, foliage, map geometry or graphics
quality are removed or reduced.

## Validation limits

Source and deterministic tests:
python3 Tools/Validation/test_android_medical_candidate_scan.py
python3 Tools/Validation/check_battle_hardening.py

Real Android Unity compilation, device p95/p99 frame measurements, native
crash traces and visual comparison remain necessary. This targeted
iteration is not a verified cure for 3–30 second stalls or crashes.
