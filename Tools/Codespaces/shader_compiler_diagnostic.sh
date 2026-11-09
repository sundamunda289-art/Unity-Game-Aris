#!/usr/bin/env bash
# Read-only, sanitized native shader compiler diagnosis on the approved Codespace.
# No raw machine paths, environment, license state, file contents or source edits.
set -Eeuo pipefail
python3 - <<'PY'
from pathlib import Path
import os
import re
import shutil
import subprocess

def emit(label, flag):
    print("AARIS_SHADER_"+label+"="+("yes" if flag else "no"))
def free_mib(path):
    v=os.statvfs(path)
    return v.f_bavail*v.f_frsize//1048576

print("AARIS_SHADER_DIAG_V1")
print("AARIS_SHADER_SCRATCH_FREE_MIB="+str(free_mib("/tmp")))
roots=[
    Path.home()/".local/share/aaris-unity-editors/6000.3.26f1",
    Path("/workspaces/.aaris-unity-editors/6000.3.26f1"),
    Path("/tmp/aaris-unity-runtime/editors/6000.3.26f1"),
]
compiler=next((r/"Editor/Data/Tools/UnityShaderCompiler" for r in roots
               if (r/"Editor/Data/Tools/UnityShaderCompiler").is_file()),None)
emit("COMPILER_FOUND",compiler is not None)
libdirs=("/usr/lib/x86_64-linux-gnu/libtinfo.so.6",
         "/lib/x86_64-linux-gnu/libtinfo.so.6")
emit("LIBTINFO6_PRESENT",any(Path(p).is_file() for p in libdirs))
emit("GLXINFO_AVAILABLE",shutil.which("glxinfo") is not None)
if compiler:
    try:
        result=subprocess.run(["ldd",str(compiler)],capture_output=True,text=True,timeout=25)
        lines=result.stdout+"\n"+result.stderr
        missing=re.findall(r"\b([A-Za-z0-9_.+\-]+\.so(?:\.\d+)*)\s+=>\s+not found",lines)
        print("AARIS_SHADER_COMPILER_LDD_EXIT="+str(max(0,min(255,result.returncode))))
        print("AARIS_SHADER_COMPILER_MISSING_COUNT="+str(min(99,len(missing))))
        names=("libtinfo","libncurses","libstdc++","libGL","libX11","libxcb","libc")
        for name in names:
            emit("MISSING_"+re.sub(r"[^A-Z0-9]","_",name.upper()),
                 any(m.startswith(name+".") for m in missing))
        emit("MISSING_OTHER",any(not any(m.startswith(n+".") for n in names) for m in missing))
    except (subprocess.TimeoutExpired,OSError):
        print("AARIS_SHADER_COMPILER_LDD_UNAVAILABLE")
log=Path("/tmp/aaris-codespaces/scene-probe/unity-probe-editor.log")
emit("SCENE_CRASH_LOG_FOUND",log.is_file())
if log.is_file():
    with log.open("rb") as f:
        f.seek(max(0,log.stat().st_size-4*1024*1024))
        s=f.read().decode("utf-8","replace")
    # Fixed public error families only, never a line from an Editor log.
    signatures={
        "IPC_CONNECTION":r"shader compiler.+(?:ipc|not connected)|Failed to get ipc connection",
        "SHADER_COMPILER_PROCESS":r"UnityShaderCompiler|shader compiler initialization",
        "MISSING_LIBTINFO":r"libtinfo[.]so[.]6",
        "MISSING_NCURSES":r"libncurses[.]so",
        "SHADER_COMPILATION_ERROR":r"shader.*(?:compile.*fail|error|unsupported)|failed to compile shader",
        "MESA_WARNING":r"mesa.*(?:error|failed|unsupported)",
        "GRAPHICS_ERROR":r"GfxDevice.*fail|Failed to create GL|framebuffer incomplete",
        "NATIVE_CRASH":r"SIGSEGV|segmentation fault|signal 11",
        "MEMORY_PRESSURE":r"No space left on device|out of memory|failed to allocate",
        "CSHARP_COMPILER_ERROR":r"error CS[0-9]{4}",
        "EDITOR_EXIT":r"Exiting batchmode|EditorApplication.Exit",
    }
    for name,pattern in signatures.items():
        emit("SCENE_LOG_"+name, bool(re.search(pattern,s,re.I)))
# Identify tightly allowlisted shader families near compiler errors; never emit raw
# shader names, file paths, message bodies, stack traces, or lines.
if log.is_file():
    error_lines=[line for line in s.splitlines()
                 if re.search(r"Shader error|Shader warning|shader.*(?:compile.*fail|unsupported)|ShaderCompiler",line,re.I)]
    print("AARIS_SHADER_CANDIDATE_LINES="+str(min(9999,len(error_lines))))
    nearby="\\n".join(error_lines[-150:])
    names={
        "SPEEDTREE":r"speedtree|pine[_ ]|conifer|cypress",
        "URP":r"Universal Render Pipeline|Universal\\/|\\bURP\\b",
        "TERRAIN":r"terrain|\\bNature\\/",
        "SHADER_GRAPH":r"shadergraph|shader graph",
        "CUSTOM":r"\\bAaris\\b|\\bAarisWorld\\b",
        "ERROR_TOKEN":r"shader error in|ShaderCompiler.*(?:error|failed)",
        "UNSUPPORTED_BACKEND":r"not supported|unsupported|not available",
        "SHADER_COMPILE_TIMEOUT":r"timeout|timed out",
        "GLSL":r"glsl|GLSLPROGRAM|glcore|OpenGL",
        "VULKAN":r"vulkan|spir-?v",
        "COMPILER_EXCEPTION":r"exception|segmentation|signal 11",
    }
    for label,pattern in names.items():
        emit("CANDIDATE_"+label,bool(re.search(pattern,nearby,re.I)))
    # Shader errors are distinct from mere mentions of compiler initialization.
    emit("EXPLICIT_SHADER_ERROR",bool(re.search(r"Shader error in|failed to compile shader",nearby,re.I)))
    emit("SHADER_WARNING",bool(re.search(r"Shader warning in",nearby,re.I)))
print("AARIS_SHADER_READONLY_COMPLETE")
PY
