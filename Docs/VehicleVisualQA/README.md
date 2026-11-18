# Aaris native vehicle source photographs (18 November 2026)

This contact sheet is captured in **real Unity 6000.3.26f1 URP** from seven imported source models under a temporary neutral-lit studio: sedan, SUV, sports, premium GT, hatchback, pickup, and van.

This is **not an in-game or Android benchmark**: it cannot prove spawn visibility, the runtime paint selection, correct wheel motion, game lighting, vehicle physics, GPU efficiency, or FPS. The imported source art visibly retains low-poly, older-model silhouettes; do not claim photorealism before licensed higher-detail art and in-game footage are reviewed.

To reproduce, with a working Unity Editor and off-screen OpenGL display (never simultaneously with another agent's editor):

```bash
out=/tmp/aaris-vehicle-native-photos; mkdir -p "$out"
AARIS_VEHICLE_AUDIT_DIR="$out" DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 \
  /home/vscode/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity \
  -batchmode -quit -force-glcore -projectPath /workspaces/Unity-Game-Aris \
  -executeMethod Aaris.MapReview.Editor.AarisVehiclePhotoStudio.Capture \
  -logFile "$out/unity.log"
```

The editor method creates an **unsaved isolated scene**, writes seven full-size PNGs and a local README under the temporary output directory, and does not modify the native map or persist materials. Respect active Unity processes and all other agents' dirty work.
