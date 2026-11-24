#!/usr/bin/env python3
"""Disabled or destroyed vehicle sessions must not accept stale UI input.

Static integration contract plus explicit state-transition decision checks.
Does not replace live Unity lifecycle or physics tests.
"""
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
SESSION = (SCRIPTS / "AarisVehicleSession.cs").read_text("utf-8")
HUD = (SCRIPTS / "AarisVehicleHUD.cs").read_text("utf-8")


class VehicleSessionSuspension(unittest.TestCase):
    def test_disabled_session_rejects_stale_throttle_and_steering(self):
        setter = SESSION[SESSION.index("public void SetSteering(float move, float steer)"):
                         SESSION.index("public bool TryEnterOrExit()")]
        self.assertIn("!isActiveAndEnabled || Time.timeScale <= 0f", setter)
        self.assertIn("car == null || !car.isActiveAndEnabled", setter)
        self.assertIn("ClearHeldDrivingInput();", setter)
        self.assertLess(setter.index("!isActiveAndEnabled || Time.timeScale <= 0f"),
                        setter.index("touchMove = Mathf.Clamp("))
        self.assertIn("car.SetInput(touchMove, touchSteer);", setter)

    def test_disabled_session_rejects_stale_drive_exit_clicks(self):
        handler = SESSION[SESSION.index("public bool TryEnterOrExit()"):
                          SESSION.index("private bool TryEnter()")]
        self.assertIn("if (!isActiveAndEnabled || Time.timeScale <= 0f)", handler)
        self.assertIn("CancelDrivingInput();", handler)
        self.assertIn("return IsDriving ? TryExit() : TryEnter();", handler)
        self.assertLess(handler.index("if (!isActiveAndEnabled || Time.timeScale <= 0f)"),
                        handler.index("return IsDriving ? TryExit() : TryEnter();"))
        self.assertIn("if (Input.GetKeyDown(KeyCode.X) && IsDriving) TryEnterOrExit();",
                      SESSION)

    def test_hud_hides_both_controls_on_session_loss_or_disable(self):
        late = HUD[HUD.index("private void LateUpdate()",
                             HUD.index("public sealed class AarisVehicleHUD")):
                   HUD.index("private void OnApplicationPause(",
                             HUD.index("public sealed class AarisVehicleHUD"))]
        self.assertIn("if (session == null || !session.isActiveAndEnabled || Time.timeScale <= 0f)", late)
        self.assertIn("ClearArrows();", late)
        self.assertIn("arrowPanel.SetActive(false);", late)
        self.assertIn("driveButton.SetActive(false);", late)
        self.assertLess(late.index("if (session == null || !session.isActiveAndEnabled || Time.timeScale <= 0f)"),
                        late.index("bool driving = session.IsDriving;"))
        self.assertIn("bool show = driving || session.CanEnter;", late)

    def test_suspend_releases_held_input_without_unsafe_exit(self):
        end = SESSION[SESSION.index("private void ClearHeldDrivingInput()"):]
        # Accept either the legacy one-liner or a structured lifecycle body.
        # The new foliage cleanup must not bypass driving input cancellation.
        disabled = end.split("private void OnDisable()", 1)[1]
        self.assertIn("CancelDrivingInput();", disabled)
        if "GrassInteractorId" in SESSION:
            self.assertIn("Shader.SetGlobalVector(GrassInteractorId, Vector4.zero);", disabled)
            self.assertLess(disabled.index("Shader.SetGlobalVector(GrassInteractorId, Vector4.zero);"),
                            disabled.index("CancelDrivingInput();"))
        self.assertIn("if (car != null) car.SetInput(0f, 0f);", end)
        self.assertNotIn("ReleaseDriver(", end)
        self.assertNotIn("TryExit()", end)
        for active in (False, True):
            for car_present in (False, True):
                for button_down in (False, True):
                    can_dispatch = active and car_present
                    can_change_mode = active
                    if not active:
                        self.assertFalse(can_dispatch)
                        self.assertFalse(can_change_mode)
                    if active and car_present and button_down:
                        self.assertTrue(can_dispatch)


    def test_disabled_hud_cannot_leave_drive_ui_interactive(self):
        click = HUD.split("button.onClick.AddListener(() =>", 1)[1].split("});", 1)[0]
        self.assertIn("if (isActiveAndEnabled && session != null)", click)
        arrow = HUD.split("public void ArrowState(int dir, bool on)", 1)[1].split(
            "private void ApplyInput()", 1)[0]
        self.assertIn("(on && !isActiveAndEnabled)", arrow)
        # A release must still be accepted while shutting down the HUD.
        self.assertIn("pressed[dir] = on;", arrow)
        disabled = HUD.rsplit("private void OnDisable()", 1)[1]
        required = (
            "ClearArrows();",
            "if (arrowPanel != null) arrowPanel.SetActive(false);",
            "if (driveButton != null) driveButton.SetActive(false);",
            "if (speedPanel != null) speedPanel.SetActive(false);",
        )
        for guard in required:
            self.assertIn(guard, disabled)
        self.assertEqual([disabled.index(v) for v in required],
                         sorted(disabled.index(v) for v in required))

    def test_motor_suspension_flushes_the_bound_hud_immediately(self):
        link = SESSION.split("internal void OnVehicleSuspended(", 1)[1].split(
            "private void ClearHeldDrivingInput()", 1)[0]
        self.assertIn("if (car == vehicle) CancelDrivingInput();", link)
        self.assertIn("vehicleHud.CancelInput(this);", link)
        self.assertIn("session.BindVehicleHud(this);", HUD)
        self.assertIn("if (session == owner) ClearArrows();", HUD)
        arrow = HUD.split("public void ArrowState(int dir, bool on)", 1)[1].split(
            "private void ApplyInput()", 1)[0]
        self.assertIn("if (on && !CanDrive)", arrow)
        self.assertLess(arrow.index("if (on && !CanDrive)"), arrow.index("pressed[dir] = on;"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
