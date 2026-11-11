#!/usr/bin/env bash
# Read-only diagnostics for the ORIGINAL Codespace /workspaces Unity Editor.
# No editor process, new project, package install, secret or raw log export.
set -Eeuo pipefail
python3 - <<'PY'
from pathlib import Path
import re,subprocess
base=Path('/tmp/aaris-codespaces')
paths=[
base/'Editor.log',
base/'editor-launch-stderr.log',
base/'v3-editor-gui-private.log',
base/'v3-editor-stderr.private.log',
base/'v3-editor-stdout.private.log',
base/'v3-version-error-private.log',
]
files=[p for p in paths if p.is_file() and p.stat().st_size]
print('AARIS_GUI_DIAG_PRIVATE_LOGS='+str(len(files)))
logs=[]
for p in files:
    try:logs.append(p.read_text(encoding='utf-8',errors='replace')[-400000:])
    except OSError:pass
s='\n'.join(logs)
kinds={
 'UNITY_NATIVE_SEGFAULT':r'segmentation fault|SIGSEGV|signal 11|fatal signal',
 'GL_GRAPHICS':r'GfxDevice|OpenGL|OpenGLCore|libGL|libEGL|llvmpipe|Mesa|libgallium|libLLVM',
 'VULKAN_GRAPHICS':r'vulkan|libvulkan|VkInstance',
 'PROJECT_IMPORT':r'Importing|ImportAsset|AssetDatabase|Refresh completed',
 'SCRIPT_COMPILATION':r'Compilation|compil|assembly|Mono',
 'EDITOR_LICENSE':r'[Ll]icen[cs]e|[Aa]ctivat',
 'OOM':r'out.of.memory|std::bad_alloc|Cannot allocate memory',
 'DISPLAY_ERROR':r'cannot open display|x11 error|failed to create window',
 'UPM_ERROR':r'package manager|could not resolve package',
 'STACK_TRACE':r'stacktrace|native stack|stack trace|backtrace',
}
for name,pat in kinds.items():
    if re.search(pat,s,re.I):print('AARIS_GUI_DIAG_MARKER_'+name)
for filename in ['v3-editor-exit-code.private','editor-exit-code.txt']:
    p=base/filename
    if p.is_file():
        try:
            code=int(p.read_text().strip())
            if 0<=code<=255:
                print('AARIS_GUI_DIAG_'+('GLCORE' if filename.startswith('v3') else 'DEFAULT')+'_EXIT_'+str(code))
        except (OSError,ValueError):pass
# Only boolean classifications of module *names*; never paths or stack lines.
modules={
 'MESA':r'lib(?:gallium|EGL_mesa|GLX_mesa|glapi|OSMesa)',
 'LLVM':r'libLLVM',
 'UNITY_ENGINE':r'UnityPlayer|Unity\.so|Editor/Unity',
 'MONO':r'libmono|MonoBleedingEdge',
 'CPP_ABI':r'libstdc\+\+|GLIBCXX|libgcc_s',
 'GDK_GTK':r'libgdk|libgtk',
 'DISPLAY':r'libX11|libxcb|XOpenDisplay',
}
for name,pattern in modules.items():
    if re.search(pattern,s,re.I):print('AARIS_GUI_DIAG_MODULE_'+name)
# Shared library missing categories, without logging absolute paths.
binary=next((p for p in [
Path.home()/'.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity',
Path('/workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity'),
Path('/tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity')] if p.is_file()),None)
if binary:
    run=subprocess.run(['ldd',str(binary)],capture_output=True,text=True,timeout=15)
    misses=len(re.findall(r'not found',run.stdout+run.stderr))
    print('AARIS_GUI_DIAG_MISSING_LIB_COUNT='+str(misses))
else:print('AARIS_GUI_DIAG_EDITOR_BINARY_NOT_FOUND')
run=subprocess.run(['bash','-c','DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 glxinfo -B'],
                    capture_output=True,text=True,timeout=16)
if run.returncode!=0:
    print('AARIS_GUI_DIAG_GLXINFO_FAILED_OR_UNAVAILABLE')
else:
    t=run.stdout
    if re.search('llvmpipe|softpipe|Software Rasterizer|Mesa/X.org',t,re.I):
        print('AARIS_GUI_DIAG_SOFTWARE_GL_PRESENT')
    else:print('AARIS_GUI_DIAG_SOFTWARE_GL_UNCLASSIFIED')
    ver=re.search(r'OpenGL core profile version string:\s*(\d+)\.(\d+)',t)
    if not ver:ver=re.search(r'OpenGL version string:\s*(\d+)\.(\d+)',t)
    if ver:
        major,minor=map(int,ver.groups())
        print('AARIS_GUI_DIAG_GL_VERSION_CLASS='+('GL45_PLUS' if (major,minor)>=(4,5) else 'BELOW_GL45'))
PY
