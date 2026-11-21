#!/usr/bin/env python3
"""Basic source and visual-safety contracts for opt-in chunk streamer."""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
scripts = root / 'Assets/AarisWorld/Scripts'
streamer = (scripts / 'AarisAdditiveChunkStreamer.cs').read_text()
policy = (scripts / 'AarisChunkStreamingPolicy.cs').read_text()
for token in ('LoadSceneAsync(path, LoadSceneMode.Additive)',
              'UnloadSceneAsync(path)', 'SceneUtility.GetBuildIndexByScenePath',
              'TryClaimOptionalModelBuild()', 'ReserveNativeTeardownFrame()',
              'states[i].externallyOwned', 'inFlight != null',
              'AarisChunkStreamingPolicy.Predict',
              'distance > exitSquared', 'predicted > entrySquared'):
    assert token in streamer, token
for disallowed in ('Resources.UnloadUnusedAssets(', 'WaitForCompletion(',
                   'TerrainData.treeInstances', 'QualitySettings.',
                   'transform.Find(', 'GetComponentsInChildren(', 'FindObjectsOfType('):
    assert disallowed not in streamer, disallowed
for token in ('Math.Abs(x - centerX)', 'Math.Abs(z - centerZ)',
              'speedSquared > teleportSpeed * teleportSpeed',
              'elapsed > 1.5f'):
    assert token in policy, token
assert 'RuntimeInitializeOnLoadMethod' not in streamer  # no auto-bootstrap
assert 'maxSpeculativeResidentCells = 2;' in streamer
assert 'speculativeResidentCount >= maxSpeculativeResidentCells' in streamer
assert 'bool loadIsUrgent = false;' in streamer
assert 'loadIsUrgent = distance <= entrySquared;' in streamer
assert 'if (unloadCell >= 0 && (loadCell < 0 || !loadIsUrgent))' in streamer
assert 'if (!loadIsUrgent &&' in streamer  # even predicted-inside priority ties
assert 'nearestLoad > entrySquared' not in streamer
assert 'AarisMobileStreamingFrameGate.IsConstrainedDevice()' in streamer
assert 'AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();' in streamer
assert streamer.count('AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();') == 2
print('PASS: opt-in additive streaming safety/source contracts')
