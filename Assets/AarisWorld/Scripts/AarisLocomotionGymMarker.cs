using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Marker for the movement-only APK. The regular motor, stance capsule,
    /// ground checks, and character presentation remain authoritative.
    /// No world systems are instantiated for this scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisLocomotionGymMarker : MonoBehaviour
    {
        private AarisBattleActions stance;

        private void Awake()
        {
            stance = GetComponent<AarisBattleActions>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.R) && stance != null)
                stance.TryStand();
        }

        private void OnGUI()
        {
            if (stance == null) return;

            float scale = Mathf.Clamp(Mathf.Min(Screen.width / 960f,
                Screen.height / 540f), 0.55f, 1.5f);
            float x = Screen.width / scale - 120f;
            float y = (Screen.height - Screen.safeArea.yMax) / scale + 12f;
            Matrix4x4 before = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            GUI.Box(new Rect(x - 250f, y, 230f, 111f),
                "LOCOMOTION GYM\n" +
                "Stance: " + stance.Stance + "\n" +
                "C / Z / R : crouch / prone / stand\n" +
                "WASD move  Space jump  T sprint");

            if (GUI.Button(new Rect(x, y, 116f, 40f), "CROUCH"))
                stance.ToggleCrouch();
            if (GUI.Button(new Rect(x, y + 44f, 116f, 40f), "PRONE"))
                stance.ToggleProne();
            if (GUI.Button(new Rect(x, y + 88f, 116f, 40f), "STAND"))
                stance.TryStand();

            GUI.matrix = before;
        }
    }
}
