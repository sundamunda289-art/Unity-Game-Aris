#!/usr/bin/env python3
"""Audit the hydrated URP assets that actually back Android foliage rendering.

Source-level evidence only. This does not replace Unity Editor import, RenderDoc,
Android GPU profiling, or physical-device frame timing.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
URP_DIR = ROOT / "Assets/TerrainDemoScene_URP/Settings/URP"
PIPELINE = URP_DIR / "URP-HighQuality Pipeline Asset.asset"
RENDERER = URP_DIR / "Universal Render Pipeline Asset_Renderer.asset"
RENDERER_META = RENDERER.with_suffix(RENDERER.suffix + ".meta")
RUNTIME = ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs"

EXPECTED_RENDERER_GUID = "e16a06fa1e6a5b34eb28ed4924ae327d"


def hydrated_text(path: Path) -> str:
    text = path.read_text(encoding="utf-8")
    assert not text.startswith("version https://git-lfs.github.com/spec/"), (
        f"{path} is still a Git LFS pointer"
    )
    return text


def scalar(text: str, key: str, cast=float):
    match = re.search(rf"(?m)^\s*{re.escape(key)}:\s*([^\n#]+)", text)
    assert match, f"missing {key}"
    raw = match.group(1).strip()
    return cast(raw)


def integer(text: str, key: str) -> int:
    return int(scalar(text, key, str))


def main() -> None:
    pipeline = hydrated_text(PIPELINE)
    renderer = hydrated_text(RENDERER)
    renderer_meta = RENDERER_META.read_text(encoding="utf-8")
    runtime = RUNTIME.read_text(encoding="utf-8")

    guid_match = re.search(r"(?m)^guid:\s*([0-9a-f]{32})\s*$", renderer_meta)
    assert guid_match and guid_match.group(1) == EXPECTED_RENDERER_GUID
    assert EXPECTED_RENDERER_GUID in pipeline, "pipeline no longer points at audited renderer"

    data = {
        "renderer_guid": EXPECTED_RENDERER_GUID,
        "rendering_mode": integer(renderer, "m_RenderingMode"),
        "depth_priming_mode": integer(renderer, "m_DepthPrimingMode"),
        "native_render_pass": integer(renderer, "m_UseNativeRenderPass"),
        "intermediate_texture_mode": integer(renderer, "m_IntermediateTextureMode"),
        "renderer_features_empty": "m_RendererFeatures: []" in renderer,
        "require_depth_texture": integer(pipeline, "m_RequireDepthTexture"),
        "require_opaque_texture": integer(pipeline, "m_RequireOpaqueTexture"),
        "supports_hdr": integer(pipeline, "m_SupportsHDR"),
        "msaa_samples": integer(pipeline, "m_MSAA"),
        "render_scale": scalar(pipeline, "m_RenderScale", float),
        "lod_crossfade_enabled": integer(pipeline, "m_EnableLODCrossFade"),
        "main_light_mode": integer(pipeline, "m_MainLightRenderingMode"),
        "main_shadow_resolution": integer(pipeline, "m_MainLightShadowmapResolution"),
        "additional_lights_mode": integer(pipeline, "m_AdditionalLightsRenderingMode"),
        "additional_lights_per_object": integer(pipeline, "m_AdditionalLightsPerObjectLimit"),
        "additional_light_shadows": integer(pipeline, "m_AdditionalLightShadowsSupported"),
        "shadow_distance_m": scalar(pipeline, "m_ShadowDistance", float),
        "shadow_cascades": integer(pipeline, "m_ShadowCascadeCount"),
        "srp_batcher": integer(pipeline, "m_UseSRPBatcher"),
    }

    # Stable mobile architecture invariants. These are quality-neutral guards:
    # Forward avoids mobile G-buffer bandwidth, extra per-object lights are off,
    # and the SRP Batcher remains available for compatible renderers.
    assert data["rendering_mode"] == 0, data
    assert data["additional_lights_mode"] == 0, data
    assert data["srp_batcher"] == 1, data
    assert data["renderer_features_empty"], data
    assert data["depth_priming_mode"] in (0, 1, 2), data

    # Cross-check the real Android fallback that protects the authored desktop
    # settings on Adreno 610 without lowering source texture/mesh quality.
    for contract in (
        'HasExactGpuModel(gpu, "Adreno 610")',
        'HasExactGpuModel(gpu, "Adreno (TM) 610")',
        "appliedMsaaSamples = Mathf.Min(originalMsaaSamples, 2);",
        "float mobileShadowDistance = constrainedGraphics ? 120f : 240f;",
        "appliedShadowCascades = Mathf.Min(originalShadowCascades, 2);",
        "Mathf.Min(originalDetailDistances[i], 80f)",
        "Mathf.Min(originalBillboardDistances[i], 85f)",
        "private const int LowMemoryFullLodTreeLimit = 50;",
    ):
        assert contract in runtime, contract

    data.update({
        "adreno_610_runtime_budget_path": True,
        "budget_msaa_cap": 2,
        "budget_shadow_distance_m": 120,
        "budget_shadow_cascade_cap": 2,
        "budget_detail_distance_m": 80,
        "budget_tree_billboard_distance_m": 85,
        "budget_full_lod_tree_cap": 50,
        "depth_priming_review_required": data["depth_priming_mode"] == 2,
        "unity_editor_tested": False,
        "android_device_tested": False,
        "status": "mobile_urp_renderer_source_audit_passed",
    })
    print(json.dumps(data, sort_keys=True))


if __name__ == "__main__":
    main()
