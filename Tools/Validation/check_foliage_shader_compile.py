"""Compile the custom SpeedTree foliage passes against pinned Unity URP headers.

This is a license-free HLSL compiler gate. It checks the real custom include
integration with Unity 6 / URP 17.3 headers for representative mobile Vulkan
variants. It is not a Unity ShaderLab import, Android build, or device benchmark.
"""
import argparse
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
    return vertex.group(1), fragment.group(1), program


def compile_foliage(dxc, graphics_root, cgincludes):
    graphics_root = Path(graphics_root)
    cgincludes = Path(cgincludes)
    speedtree_wind = cgincludes / "SpeedTreeWind.cginc"
    if not speedtree_wind.is_file() or speedtree_wind.stat().st_size < 1000:
        raise AssertionError(f"missing pinned Unity built-in include: {speedtree_wind}")
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
                        "EFFECT_EXTRA_TEX",
                        "_MAIN_LIGHT_SHADOWS",
                        "_SHADOWS_SOFT_MEDIUM",
                    ]

                for stage, entry, profile in (
                    ("vertex", vertex, "vs_6_0"),
                    ("fragment", fragment, "ps_6_0"),
                ):
                    output = work / f"{pass_name}-{wind_name}-{stage}.spv"
                    defines = [
                        "SHADER_API_VULKAN",
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
                            f"{pass_name} {wind_name} {stage}\n"
                            f"stdout:\n{done.stdout}\nstderr:\n{done.stderr}"
                        )
                    if not output.is_file() or output.stat().st_size == 0:
                        raise AssertionError(f"empty compiler output: {output}")
                    results.append(
                        {
                            "pass": pass_name,
                            "wind": wind_name,
                            "stage": stage,
                            "status": "compiled",
                        }
                    )

    report = {
        "status": "dxc_foliage_hlsl_checks_passed",
        "unity_version_target": "6000.3.26f1",
        "urp_version": package["version"],
        "shadergraph_version": sg_package["version"],
        "graphics_source_commit": source_commit,
        "compiler_version": subprocess.check_output([str(dxc), "--version"], text=True).strip(),
        "hlsl_language_version": 2018,
        "platform": "vulkan_mobile",
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
