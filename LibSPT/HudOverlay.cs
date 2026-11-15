using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     The crosshair and the debug overlay, drawn by ThirdPersonView.DrawHud. A separate component so that ThirdPersonView
///     defines no OnGUI: Unity dispatches IMGUI events to every enabled component that has one, whether it draws or not.
///     ThirdPersonView enables this only on frames where one of the two can draw.
/// </summary>
public class HudOverlay : MonoBehaviour
{
    public ThirdPersonView View;

    public void Awake()
    {
        // Only GUI.* calls are made, never GUILayout, so the layout event is pure overhead
        useGUILayout = false;
    }

    public void OnGUI()
    {
        // Labels and textures only show in the repaint pass; the other events would repeat the crosshair's cast for nothing
        if (Event.current.type != EventType.Repaint)
            return;

        // Unity null: the view is destroyed immediately on death and dispose, this component only at the end of that frame
        if (View == null)
            return;

        View.DrawHud();
    }
}
