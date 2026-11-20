# Real Unity pose capture — 20 November 2026

Source: running Codespace improved-space-capybara-wv4pp6757gx6295w4, original dirty working tree local HEAD 3490edf1 (30 locally modified tracked files). Unity Editor 6000.3.26f1, Play Mode capture AarisPoseFrames.CaptureMultiAnglePortrait, captured 12 actual JPEG frames (standing/crouch/prone, four views each). This is NOT the combined PR #786 revision.

QA result: FAILED `SETTLED_PRONE_KNEE_TOO_HIGH`. Measured prone lower-leg bone above terrain approximately 0.406m / 0.343m; settled knee offsets 0.338m / 0.294m. Standing shoe alignment and low stance appearance require further review. Image pixels were not recolored; bright-magenta signature measured 0% in sampled pixels. A magenta shader issue was not reproduced in this capture.

Contact sheet: 20261120_native_12_pose_views.jpg. Each cell is one original native Unity screenshot, resized for side-by-side comparison and labeled by pose and view. Native scene screenshots were generated in the game project's isolated Editor pose camera, not a synthetic depiction.
