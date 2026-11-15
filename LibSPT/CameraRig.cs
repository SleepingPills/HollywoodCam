using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     Owns the third person camera's orientation (yaw/pitch, no roll) and a pivot transform that the camera offset and
///     collision solver are expressed against. The pivot lives at the scene root so nothing the game does to the player
///     hierarchy can rotate it behind our back.
/// </summary>
public class CameraRig
{
    // Below this horizontal forward length the yaw is numerically meaningless (looking straight up/down)
    private const float VerticalForwardEpsilon = 1e-6f;
    public const float MaxPitch = 90f;

    public float Yaw;
    public float Pitch;

    private Transform _pivot;

    public Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

    // Recreated on demand, in case something (e.g. a scene unload) destroyed it under us
    public Transform Pivot
    {
        get
        {
            if (_pivot != null) return _pivot;
            
            var go = new GameObject("HollywoodCamPivot") { hideFlags = HideFlags.HideInHierarchy };
            _pivot = go.transform;

            return _pivot;
        }
    }

    public void SyncFrom(Quaternion rotation)
    {
        // Derived from the forward vector rather than eulerAngles, which wrap and can swap yaw/roll near the poles
        var forward = rotation * Vector3.forward;
        Pitch = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;

        if (forward.x * forward.x + forward.z * forward.z > VerticalForwardEpsilon)
            Yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }

    // Yaw/pitch as the game's MovementContext.Rotation lays them out: x = yaw, y = pitch
    public Vector2 Angles => new(Yaw, Pitch);

    public void SetAngles(float yaw, float pitch)
    {
        Yaw = Mathf.DeltaAngle(0f, yaw);
        Pitch = Mathf.Clamp(pitch, -MaxPitch, MaxPitch);
    }

    public void AddAngles(float yaw, float pitch)
    {
        SetAngles(Yaw + yaw, Pitch + pitch);
    }

    /// <summary>
    ///     The rotation that takes one yaw/pitch pair (x = yaw, y = pitch, degrees) to another, yaw the short way round,
    ///     so a ±360 wrap on either side does not read as a full turn.
    /// </summary>
    public static Vector2 AngleDelta(Vector2 from, Vector2 to)
    {
        return new Vector2(Mathf.DeltaAngle(from.x, to.x), to.y - from.y);
    }

    public static bool IsFinite(Vector2 v)
    {
        return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y);
    }

    public void UpdatePivot(Vector3 worldPosition)
    {
        Pivot.SetPositionAndRotation(worldPosition, Rotation);
    }

    public void Destroy()
    {
        if (_pivot != null)
            Object.Destroy(_pivot.gameObject);

        _pivot = null;
    }
}
