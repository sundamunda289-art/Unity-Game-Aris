# Android: allocation-controlled authored wheel-name lookup (2026-11-21)

## Source audit

An imported real car with separately authored tyre meshes invokes `AarisVehicleWorld.FindNamedWheel` for each of four wheel labels while correcting model forward and again during axle-ground alignment. Previously every lookup called `GetComponentsInChildren<Transform>(true)` and allocated a fresh `Transform[]`, even when the same body was examined eight times in one integration. It was a spawn-time allocation risk, not a proven measured cause of Adreno 610 frame stalls.

## Narrow change

The exact existing first-match and case-insensitive semantics are preserved. Use Unity 6000.3 `Transform.GetComponentsInChildren<Transform>(true, List<Transform>)` into a single thread-local lazily created list, with `finally` cleanup to prevent retained imported objects on all return paths or native query exceptions. Authored car body orientation, wheel positions, real WheelCollider sizes, tyre-ground contact thresholds, fallback models, map and graphics remain unchanged. No new worktree or modification of the Codespace checked-out weapon branch.

## Checks and limitations

The new C# Mono fixture injects the actual production `FindNamedWheel` method and proves first matching wheel identity, mixed-case ASCII labels, repeated queries using the same list, no lingering object references, null inputs and recovery from an exceptional child query. The existing native-geometry source contracts and parser are also checked in CI.

No assertion of native Unity Editor compilation, Android IL2CPP gameplay acceptance or measured FPS improvement. Capture matched car spawn frame times, GC alloc bytes and actual vehicle screenshot equivalence on Snapdragon 680 before claiming measurable gains.

Unity API: https://docs.unity.com/ja-jp/engine/6000.3/script-reference/unityengine/component/getcomponentsinchildren
