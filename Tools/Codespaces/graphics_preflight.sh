#!/usr/bin/env bash
# Read-only software OpenGL probe. No Unity licence bypass.
set -Eeuo pipefail
cd "$(dirname "$0")/../.."
if ! grep -q '^ID=ubuntu' /etc/os-release; then
  echo SNAP_GRAPHICS_UNSUPPORTED_OS; exit 11
fi
if ! command -v xdpyinfo >/dev/null || ! command -v glxinfo >/dev/null; then
  echo SNAP_GRAPHICS_MISSING_X11_OR_MESA_UTILS; exit 12
fi
export DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe
if ! timeout 8 xdpyinfo >/dev/null 2>&1; then
  echo SNAP_GRAPHICS_XVFB_NOT_READY; exit 13
fi
mkdir -p /tmp/aaris-codespaces
if ! timeout 15 glxinfo -B >/tmp/aaris-codespaces/graphics-preflight.private.log 2>&1; then
  echo SNAP_GRAPHICS_GLX_FAILED; exit 14
fi
if grep -q -E 'OpenGL (core profile )?version string: ([0-3]\.|4\.[0-2]\.)' /tmp/aaris-codespaces/graphics-preflight.private.log; then
  echo SNAP_GRAPHICS_GL_TOO_OLD; exit 15
fi
if ! grep -q -E 'OpenGL (core profile )?version string:' /tmp/aaris-codespaces/graphics-preflight.private.log; then
  echo SNAP_GRAPHICS_VERSION_UNKNOWN; exit 16
fi
echo SNAP_SOFTWARE_GL_AVAILABLE_NOT_UNITY_RENDER_VERIFIED
if grep -qi llvmpipe /tmp/aaris-codespaces/graphics-preflight.private.log; then
  echo SNAP_RENDERER_LLVMPIPE
else
  echo SNAP_RENDERER_OTHER_CHECK_ENVIRONMENT
fi
if test -f Assets/TerrainDemoScene_URP/VFXGraphs/Fog.vfx; then
  echo SNAP_VFX_FOG_PRESENT_DO_NOT_REMOVE_WITHOUT_AB_TEST
fi
