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
              'AarisChunkStreamingPolicy.SampleVelocity', 'AarisChunkStreamingPolicy.FirstApproach',
              'distance > exitSquared', 'float.IsPositiveInfinity(priority)'):
    assert token in streamer, token
for disallowed in ('Resources.UnloadUnusedAssets(', 'WaitForCompletion(',
                   'TerrainData.treeInstances', 'QualitySettings.',
                   'transform.Find(', 'GetComponentsInChildren(', 'FindObjectsOfType('):
    assert disallowed not in streamer, disallowed
for token in ('Math.Abs(x - centerX)', 'Math.Abs(z - centerZ)',
              'speedSquared > teleportSpeed * teleportSpeed',
              'elapsed > 1.5f'):
    assert token in policy, token
# Separate streamers must not stack asynchronous scene activation/retirement
# in a budget Android frame. Non-Android Editor behavior stays unchanged.
assert 'private static AsyncOperation sharedSceneOperation;' in streamer
assert 'private static int sharedSceneCompletionFrame = -1;' in streamer
assert 'if (sharedSceneOperation != null)' in streamer
assert 'if (sharedSceneCompletionFrame == Time.frameCount) return;' in streamer
assert 'sharedSceneOperation = operation;' in streamer
assert 'ReferenceEquals(sharedSceneOperation, inFlight)' in streamer
assert streamer.index('if (sharedSceneOperation != null)') < streamer.index('if (now < nextPollAt) return;')
assert streamer.index('SampleMovement(now);') < streamer.index('if (inFlight != null)')
assert 'nextPollAt = nextTransitionAt;' in streamer
assert 'RuntimeInitializeOnLoadMethod' not in streamer  # no auto-bootstrap
assert 'maxSpeculativeResidentCells = 2;' in streamer
assert 'speculativeResidentCount >= maxSpeculativeResidentCells' in streamer
assert 'bool loadIsUrgent = false;' in streamer
assert 'bool urgent = distance <= entrySquared;' in streamer
assert 'loadIsUrgent = urgent;' in streamer
assert 'if (unloadCell >= 0 && (loadCell < 0 || !loadIsUrgent))' in streamer
assert 'if (!loadIsUrgent &&' in streamer  # projection cannot bypass the optional budget
assert 'nearestLoad > entrySquared' not in streamer
assert 'AarisMobileStreamingFrameGate.IsConstrainedDevice()' in streamer
assert 'AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();' in streamer
assert streamer.count('AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();') == 2
print('PASS: opt-in additive streaming safety/source contracts')
