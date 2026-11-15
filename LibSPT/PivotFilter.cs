using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     Low-passes the eye's offset from the character's root (in the root's yaw frame), so the camera pivot can sit on
///     the body's average pose rather than on the bobbing head. Each axis is a critically damped filter stepped with its
///     exact solution for a target held over the step, so the result does not depend on the frame rate.
///     Kept free of game types so it can be reasoned about on its own.
/// </summary>
public class PivotFilter
{
    // Vertical: has to take out a gait bob of a few Hz while still following crouch and pose changes
    public const float VerticalTimeConstant = 0.25f;
    // Lateral and forward: short, so leaning and step-out leans still track
    public const float HorizontalTimeConstant = 0.1f;

    // The furthest the filter may trail the raw offset. Prone, standing up and leaning move the eye far faster than
    // the filter follows; past this the filter is dragged along with the eye instead of leaving the camera behind.
    public const float VerticalMaxLag = 0.1f;
    public const float HorizontalMaxLag = 0.05f;

    private bool _seeded;
    private Vector3 _offset;
    private Vector3 _velocity;

    public Vector3 Offset => _offset;

    // Whether the last update snapped to the raw offset instead of filtering it
    public bool Bypassed { get; private set; } = true;

    /// <summary>
    ///     Sets the filter to the raw offset at rest. Filtering resumes from there on the next Step, so leaving a
    ///     bypass does not jump.
    /// </summary>
    public void Snap(Vector3 raw)
    {
        _offset = raw;
        _velocity = Vector3.zero;
        _seeded = true;
        Bypassed = true;
    }

    /// <summary>
    ///     The next Step snaps instead of filtering from a stale offset.
    /// </summary>
    public void Reset()
    {
        _seeded = false;
        Bypassed = true;
    }

    public void Step(Vector3 raw, float deltaTime)
    {
        if (!_seeded || !IsFinite(_offset) || !IsFinite(_velocity))
        {
            Snap(raw);
            Bypassed = false;
            return;
        }

        Bypassed = false;

        if (!(deltaTime > 0f))
            return;

        StepAxis(ref _offset.x, ref _velocity.x, raw.x, HorizontalTimeConstant, HorizontalMaxLag, deltaTime);
        StepAxis(ref _offset.y, ref _velocity.y, raw.y, VerticalTimeConstant, VerticalMaxLag, deltaTime);
        StepAxis(ref _offset.z, ref _velocity.z, raw.z, HorizontalTimeConstant, HorizontalMaxLag, deltaTime);
    }

    /// <summary>
    ///     With e the error from the target and w = 1 / timeConstant: e(t) = (e0 + (v0 + w e0) t) exp(-w t). At the lag
    ///     cap the velocity toward the target is limited to w * maxLag, below which a critically damped return cannot
    ///     overshoot; at that limit the return is a plain exponential with the same time constant.
    /// </summary>
    private static void StepAxis(ref float value, ref float velocity, float target, float timeConstant, float maxLag, float deltaTime)
    {
        var omega = 1f / timeConstant;
        var error = value - target;
        var decay = Mathf.Exp(-omega * deltaTime);
        var k = velocity + omega * error;

        error = (error + k * deltaTime) * decay;
        velocity = (velocity - omega * k * deltaTime) * decay;

        if (error > maxLag)
        {
            error = maxLag;
            velocity = Mathf.Clamp(velocity, -omega * maxLag, 0f);
        }
        else if (error < -maxLag)
        {
            error = -maxLag;
            velocity = Mathf.Clamp(velocity, 0f, omega * maxLag);
        }

        value = target + error;
    }

    private static bool IsFinite(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
               && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }
}
