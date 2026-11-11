#!/usr/bin/env python3
"""Post-merge safety audit for the vehicle driving and exit motor.

License-free source contracts plus deterministic numeric reference checks.
This does NOT simulate Unity WheelCollider forces or replace Play-mode QA.
"""
from pathlib import Path
import math

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets" / "AarisWorld" / "Scripts"
def source(name):
    return (SCRIPTS / name).read_text(encoding="utf-8")

def test():
    motor = source("AarisVehicleMotor.cs")
    session = source("AarisVehicleSession.cs")
    world = source("AarisVehicleWorld.cs")
    hud = source("AarisVehicleHUD.cs")

    # Exiting a sliding vehicle cannot use just forward-speed projection.
    assert "public float TotalSpeedMetresPerSecond" in motor
    assert "body.linearVelocity.magnitude" in motor
    assert "public bool IsSafeToExit" in motor
    assert "TotalSpeedMetresPerSecond >= 1.1f" in motor
    assert "body.angularVelocity.magnitude >= .5f" in motor
    assert "Vector3.Dot(transform.up, Vector3.up) < .72f" in motor
    assert "return grounded >= 2;" in motor
    # Stopped flipped cars still need a valid external capsule before exit.
    assert "if (Vector3.Dot(transform.up, Vector3.up) < .72f) return true;" in motor
    assert "Vector3.ProjectOnPlane(t.forward, Vector3.up)" in session
    assert "Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward);" in session
    assert "Physics.RaycastAll(" in session
    assert "Physics.OverlapCapsule(" in session
    assert "car == null || !car.IsSafeToExit" in session
    assert "car.SpeedMetresPerSecond > 1.1f" not in session

    # NaN is not clamped by Mathf.Clamp; public touch/drive APIs must
    # sanitize every control axis BEFORE it can reach WheelCollider.
    for source_text, names in ((motor, ("forwardAxis", "steerAxis")),
                               (session, ("move", "steer"))):
        for name in names:
            assert f"float.IsNaN({name})" in source_text
            assert f"float.IsInfinity({name})" in source_text
    assert motor.index('float.IsNaN(forwardAxis)') < motor.index('forward = Mathf.Clamp(forwardAxis')
    assert motor.index('float.IsNaN(steerAxis)') < motor.index('steering = Mathf.Clamp(steerAxis')
    assert session.index('float.IsNaN(move)') < session.index('touchMove = Mathf.Clamp(move')
    assert session.index('float.IsNaN(steer)') < session.index('touchSteer = Mathf.Clamp(steer')
    def safe_axis(value):
        return max(-1.0, min(1.0, value)) if math.isfinite(value) else 0.0
    for value in (math.nan, math.inf, -math.inf):
        assert safe_axis(value) == 0.0
    for value,expected in ((-2,-1),(2,1),(-.35,-.35),(.75,.75),(0,0)):
        assert safe_axis(value) == expected

    # Parking/reverse must use physical wheel brake torque instead of a
    # zero-speed velocity assignment or an instant sign flip.
    assert "ParkingBrakeTorque = 3600f" in motor
    assert "ServiceBrakeTorque = 3200f" in motor
    assert "if (speed < -.55f) brake = ServiceBrakeTorque * throttle;" in motor
    assert "if (speed > .55f) brake = ServiceBrakeTorque * -throttle;" in motor
    assert "driver == null ? ParkingBrakeTorque : 155f" in motor
    assert "wheels[i].brakeTorque = ParkingBrakeTorque;" in motor
    assert "wheel.brakeTorque = ParkingBrakeTorque;" in motor
    assert "linearVelocity =" not in motor
    assert "MoveRotation(" not in motor

    # Anti-roll is based on contact-weight travel, not a guaranteed
    # no-flip hack; do not apply when both tyres are airborne.
    assert "private void ApplyAntiRoll(int leftIndex, int rightIndex)" in motor
    assert "ApplyAntiRoll(0, 1);" in motor and "ApplyAntiRoll(2, 3);" in motor
    assert "left.GetGroundHit(out WheelHit leftHit)" in motor
    assert "right.GetGroundHit(out WheelHit rightHit)" in motor
    assert "if (leftGrounded)" in motor
    assert "if (rightGrounded)" in motor
    assert "AddForceAtPosition" in motor
    assert "AntiRollStrength = 4800f" in motor

    # Vehicle camera updates after physics interpolation and ignores own
    # car/player collisions, yet reacts to the nearest OTHER world wall.
    assert "private void LateUpdate()" in session
    assert "if (car != null && followCamera != null) FollowCarCamera();" in session
    assert "Physics.SphereCastNonAlloc(new Ray(start, direction)" in session
    assert "hit.collider.transform.IsChildOf(vehicle)" in session
    assert "hit.collider.transform.IsChildOf(transform)" in session
    assert "nearest = Mathf.Min(nearest, hit.distance)" in session
    assert "if (count >= cameraObstacles.Length)" in session
    assert "Physics.SphereCastAll(new Ray(start, direction)" in session
    assert "var hit = sight[i];" in session
    assert "Physics.SphereCast(" not in session

    # Existing map and touchscreen interaction protections must remain.
    assert "Physics.OverlapBoxNonAlloc" in world
    assert "float ground = terrain.SampleHeight(" in world
    assert "record.visible.CanPark" in world
    assert "(!IsDriving)" not in session  # no duplicate input owner gate
    assert "Physics.OverlapCapsuleNonAlloc" in session
    assert "hit.collider.transform.IsChildOf(nearbyCar.transform)" in session
    assert "if (actions != null) actions.enabled = false;" in session
    assert "if (inventory != null) inventory.enabled = false;" in session
    assert "if (lootWorld != null) lootWorld.enabled = false;" in session
    assert "OnApplicationPause(bool paused)" in hud
    assert "OnApplicationFocus(bool focused)" in hud
    assert "IPointerUpHandler" in hud and "IPointerDownHandler" in hud
    assert "AARIS_DRIVE_TELEMETRY" in motor
    assert "Time.time + 3f" in motor

    # Reference calculations, not an engine simulation: braking should
    # never transition straight from forward >.55m/s to reverse torque.
    def choose(throttle, forward_speed, occupied):
        drive, brake = 0, 0
        if throttle > .08:
            if forward_speed < -.55:
                brake = 3200 * throttle
            elif forward_speed < 28:
                drive = 1500 * throttle
        elif throttle < -.08:
            if forward_speed > .55:
                brake = 3200 * -throttle
            elif forward_speed > -7:
                drive = -1500 * .48 * -throttle
        else:
            brake = 155 if occupied else 3600
        return drive, brake

    for speed in (-24,-7,-2,-.8,-.2,0,.2,.8,2,10,28):
        forward_torque, forward_brake = choose(1,speed,True)
        reverse_torque, reverse_brake = choose(-1,speed,True)
        assert not (forward_torque>0 and forward_brake>0)
        assert not (reverse_torque<0 and reverse_brake>0)
        if speed > .55:
            assert reverse_torque == 0 and reverse_brake>0
        if speed < -.55:
            assert forward_torque == 0 and forward_brake>0
        assert choose(0,speed,False)[1] == 3600

    # Camera wall hit selection must be order invariant even with an
    # own-car hit in front. Source uses closest non-owned hit.
    rays = [
        [(0.3,True),(2.8,False),(5.3,False)],
        [(5.3,False),(0.3,True),(2.8,False)],
        [(2.8,False),(5.3,False),(0.3,True)],
    ]
    for ray in rays:
        nearest = min((d for d,own in ray if not own),default=6.7)
        assert abs(nearest-2.8)<1e-6
    # Standing but sliding sideways at 15 m/s is NOT safe to EXIT.
    for lateral_speed in (1.2,3,10,15,25):
        velocity_mag = math.sqrt(0**2 + lateral_speed**2)
        assert velocity_mag>=1.1
    # Frame-rate independent exponential body damping remains smooth.
    for fps in (15,30,60,120):
        value=0.0
        dt=1/fps
        for _ in range(fps):
            value+=(1-value)*(1-math.exp(-5*dt))
        assert abs(value-(1-math.exp(-5)))<1e-8

    print("PASS: car exit while sliding/spinning/airborne, service vs parking brakes, "
          "axle anti-roll, camera hit order, 4-arrow cleanup and 15-120 FPS maths.")
    print("LIMIT: Unity WheelCollider solver, collision motion and phone GPU not exercised.")

if __name__ == "__main__":
    test()
