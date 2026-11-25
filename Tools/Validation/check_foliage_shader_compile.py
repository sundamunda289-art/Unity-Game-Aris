"""Compile the custom SpeedTree foliage passes against pinned Unity URP headers.

This is a license-free HLSL compiler gate. It checks the real custom include
integration with Unity 6 / URP 17.3 headers for representative mobile Vulkan
variants. It is not a Unity ShaderLab import, Android build, or device benchmark.
"""
import argparse
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path

URP_COMMIT = "4064d5bb6a1e3748a7cdddc244b89934ad1c3145"
SHADER = Path("Assets/TerrainDemoScene_URP/ShaderGraphs/AarisSpeedTree8Mobile.shader")
SHADER_DIR = SHADER.parent
PASS_NAMES = ("ForwardLit", "SceneSelectionPass", "GBuffer", "ShadowCaster", "DepthOnly", "DepthNormals")
MOBILE_PASSES = ("ForwardLit", "SceneSelectionPass", "ShadowCaster", "DepthOnly", "DepthNormals")


def extract_programs(source: str):
    programs = re.findall(r"HLSLPROGRAM(.*?)ENDHLSL", source, re.S)
    if len(programs) != len(PASS_NAMES):
        raise AssertionError(f"expected {len(PASS_NAMES)} HLSL programs, found {len(programs)}")
    return dict(zip(PASS_NAMES, programs))


def prepare_program(program: str):
    vertex = re.search(r"(?m)^\s*#pragma\s+vertex\s+(\w+)", program)
    fragment = re.search(r"(?m)^\s*#pragma\s+fragment\s+(\w+)", program)
    if not vertex or not fragment:
        raise AssertionError("missing vertex/fragment entry pragma")
    # DXC does not know Unity's include_with_pragmas directive. For a compiler
    # syntax gate the included declarations are what matter; Unity owns the
    # variant pragma expansion.
    program = re.sub(r"(?m)^\s*#include_with_pragmas\s+", "#include ", program)
    program = re.sub(r"(?m)^\s*#pragma.*$", "", program)
    # Unity's ShaderLab front-end supplies compatibility for legacy combined
    # sampler2D declarations that pure DXC intentionally does not support.
    # Sampling behavior is not under test here, so neutralize that legacy type
    # while keeping the exact Aaris Fast/Fastest wind + subsurface math parsed.
    prelude = """
#define sampler2D float4
#define tex2D(textureName, uv) float4(1.0, 1.0, 1.0, 1.0)
"""
    return vertex.group(1), fragment.group(1), prelude + program


def compile_foliage(dxc, graphics_root, cgincludes):
    graphics_root = Path(graphics_root)
    cgincludes = Path(cgincludes)
    speedtree_wind = cgincludes / "SpeedTreeWind.cginc"
    if not speedtree_wind.is_file():
        raise AssertionError(f"missing Fast/Fastest SpeedTree include stub: {speedtree_wind}")
    wind_text = speedtree_wind.read_text(encoding="utf-8", errors="strict")
    if "AARIS_FAST_PATH_SPEEDTREE_STUB" not in wind_text:
        raise AssertionError("unexpected SpeedTree compiler stub")
    # The target mobile Fast/Fastest variants must not depend on Unity's
    # legacy GlobalWind/LeafWind implementation. The intentionally empty
    # include makes any accidental dependency a hard compiler error.
    universal = graphics_root / "Packages/com.unity.render-pipelines.universal/package.json"
    shadergraph = graphics_root / "Packages/com.unity.shadergraph/package.json"
    package = json.loads(universal.read_text())
    sg_package = json.loads(shadergraph.read_text())
    if package["version"] != "17.3.0":
        raise AssertionError(package["version"])
    source_commit = subprocess.check_output(
        ["git", "-C", str(graphics_root), "rev-parse", "HEAD"], text=True
    ).strip()
    if source_commit != URP_COMMIT:
        raise AssertionError(source_commit)

    source = SHADER.read_text(encoding="utf-8")
    programs = extract_programs(source)
    results = []

    # Fast exercises the custom close leaf micro-flutter; Fastest exercises
    # the cheaper no-quality-keyword path used by authored distant 3D LODs.
    wind_variants = (
        ("fast", ("_WINDQUALITY_FAST",)),
        ("fastest", ()),
    )

    platform_variants = (
        ("vulkan", "SHADER_API_VULKAN"),
        ("gles3", "SHADER_API_GLES3"),
    )

    with tempfile.TemporaryDirectory(prefix="aaris-foliage-hlsl-") as workdir:
        work = Path(workdir)
        for pass_name in MOBILE_PASSES:
            vertex, fragment, hlsl = prepare_program(programs[pass_name])
            src = work / f"{pass_name}.hlsl"
            src.write_text(hlsl, encoding="utf-8")
            for wind_name, wind_defines in wind_variants:
                # Depth/selection passes intentionally do not sample leaf PBR,
                # while ForwardLit compiles the full normal/subsurface path.
                feature_defines = list(wind_defines)
                if pass_name in ("ForwardLit", "DepthNormals"):
                    feature_defines += ["EFFECT_BUMP", "EFFECT_SUBSURFACE"]
                if pass_name == "ForwardLit":
                    feature_defines += [
                        "EFFECT_HUE_VARIATION",
                        "_MAIN_LIGHT_SHADOWS",
                        "_SHADOWS_SOFT_MEDIUM",
                    ]

                for platform_name, platform_define in platform_variants:
                    for stage, entry, profile in (
                        ("vertex", vertex, "vs_6_0"),
                        ("fragment", fragment, "ps_6_0"),
                    ):
                        output = work / f"{pass_name}-{wind_name}-{platform_name}-{stage}.spv"
                        defines = [
                            platform_define,
                            "SHADER_API_MOBILE",
                            "UNITY_COMPILER_HLSL",
                            "UNITY_VERSION=600030",
                            "SHADER_TARGET=35",
                            "UNITY_HARDWARE_TIER3",
                            f"SHADER_STAGE_{stage.upper()}",
                            *feature_defines,
                        ]
                        cmd = [
                            str(dxc),
                            "-E", entry,
                            "-T", profile,
                            "-HV", "2018",
                            "-I", str(graphics_root),
                            "-I", str(SHADER_DIR),
                            "-I", str(cgincludes),
                            "-spirv",
                            "-fspv-target-env=vulkan1.1",
                            "-fvk-use-dx-layout",
                            "-Fo", str(output),
                        ]
                        for define in defines:
                            cmd += ["-D", define]
                        done = subprocess.run(
                            [*cmd, str(src)],
                            capture_output=True,
                            text=True,
                        )
                        if done.returncode != 0:
                            raise AssertionError(
                                f"{pass_name} {wind_name} {platform_name} {stage}\n"
                                f"stdout:\n{done.stdout}\nstderr:\n{done.stderr}"
                            )
                        if not output.is_file() or output.stat().st_size == 0:
                            raise AssertionError(f"empty compiler output: {output}")
                        results.append(
                            {
                                "pass": pass_name,
                                "wind": wind_name,
                                "platform_macro": platform_name,
                                "stage": stage,
                                "status": "compiled",
                            }
                        )

    # Shader Graph custom functions are compiled separately because Unity's
    # generated graph shader is Editor-owned. These wrappers exercise the real
    # player/vehicle bush bend and shrub transmission functions against the same
    # pinned URP headers and mobile Vulkan target.
    repo_root = Path.cwd()
    custom_sources = {
        "ground_interaction_vertex": (
            "vs_6_0",
            "AarisGroundInteractionProbe",
            """#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Assets/TerrainDemoScene_URP/ShaderGraphs/CustomFunctions/AarisGrassInteraction.hlsl"
struct AarisProbeAttributes { float3 positionOS : POSITION; };
float4 AarisGroundInteractionProbe(AarisProbeAttributes input) : SV_POSITION
{
    float3 outPosition;
    float3 bendSlope;
    AarisGrassInteraction_float(input.positionOS, input.positionOS, 2.2,
        float4(1.0, 0.0, 0.0, 1.0), outPosition, bendSlope);
    return TransformObjectToHClip(outPosition + bendSlope * 0.0);
}
""",
            ("SHADER_STAGE_VERTEX",),
        ),
        "shrub_transmission_fragment": (
            "ps_6_0",
            "AarisShrubTransmissionProbe",
            """#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Assets/TerrainDemoScene_URP/ShaderGraphs/CustomFunctions/PseudoSubsurface.hlsl"
half4 AarisShrubTransmissionProbe(float4 positionCS : SV_POSITION) : SV_Target
{
    half3 transmission;
    PseudoSubsurface_half(float3(0.0, 0.0, 0.0), normalize(half3(0.2h, 0.9h, 0.3h)),
        half3(2.0h, 2.0h, 2.0h), 1.0h, transmission);
    half3 rooted;
    half3 transmissionColor;
    half height01;
    AarisDetailGround_half(half3(0.25h, 0.55h, 0.18h),
        half4(0.28h, 0.24h, 0.16h, 0.35h), 0.0h, 0.63h,
        half4(0.35h, 0.0h, 0.0h, 1.0h), rooted, transmissionColor, height01);
    return half4(transmission + rooted * 0.0h + transmissionColor * 0.0h + height01 * 0.0h, 1.0h);
}
""",
            ("SHADER_STAGE_FRAGMENT",),
        ),
    }
    with tempfile.TemporaryDirectory(prefix="aaris-ground-foliage-hlsl-") as custom_workdir:
        custom_work = Path(custom_workdir)
        for probe_name, (profile, entry, source_text, stage_defines) in custom_sources.items():
            src = custom_work / f"{probe_name}.hlsl"
            src.write_text(source_text, encoding="utf-8")
            for platform_name, platform_define in platform_variants:
                output = custom_work / f"{probe_name}-{platform_name}.spv"
                cmd = [
                    str(dxc),
                    "-E", entry,
                    "-T", profile,
                    "-HV", "2018",
                    "-I", str(graphics_root),
                    "-I", str(repo_root),
                    "-spirv",
                    "-fspv-target-env=vulkan1.1",
                    "-fvk-use-dx-layout",
                    "-Fo", str(output),
                ]
                defines = [
                    platform_define,
                    "SHADER_API_MOBILE",
                    "UNITY_COMPILER_HLSL",
                    "UNITY_VERSION=600030",
                    "SHADER_TARGET=35",
                    "UNITY_HARDWARE_TIER3",
                    *stage_defines,
                ]
                for define in defines:
                    cmd += ["-D", define]
                done = subprocess.run([*cmd, str(src)], capture_output=True, text=True)
                if done.returncode != 0:
                    raise AssertionError(
                        f"{probe_name} {platform_name}\n"
                        f"stdout:\n{done.stdout}\nstderr:\n{done.stderr}"
                    )
                if not output.is_file() or output.stat().st_size == 0:
                    raise AssertionError(f"empty compiler output: {output}")
                results.append({
                    "pass": probe_name,
                    "wind": "n/a",
                    "platform_macro": platform_name,
                    "stage": "vertex" if profile.startswith("vs_") else "fragment",
                    "status": "compiled",
                })

    report = {
        "status": "dxc_foliage_hlsl_checks_passed",
        "unity_version_target": "6000.3.26f1",
        "urp_version": package["version"],
        "shadergraph_version": sg_package["version"],
        "graphics_source_commit": source_commit,
        "speedtree_fast_path_stub_sha256": hashlib.sha256(speedtree_wind.read_bytes()).hexdigest(),
        "legacy_speedtree_wind_required": False,
        "compiler_version": subprocess.check_output([str(dxc), "--version"], text=True).strip(),
        "hlsl_language_version": 2018,
        "platform_macro_paths": [name for name, _ in platform_variants],
        "spirv_parser_backend": "vulkan1.1",
        "compilations_passed": len(results),
        "variants": results,
        "unity_shaderlab_import_tested": False,
        "android_device_tested": False,
    }
    print(json.dumps({k: v for k, v in report.items() if k != "variants"}, sort_keys=True))
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--dxc", required=True)
    parser.add_argument("--graphics", required=True)
    parser.add_argument("--cgincludes", required=True)
    args = parser.parse_args()
    compile_foliage(args.dxc, args.graphics, args.cgincludes)
