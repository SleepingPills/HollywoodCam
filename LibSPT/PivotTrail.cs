using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     Lets the camera pivot trail the character's travel, so starts, stops and turns show as the character moving in
///     the frame rather than the world moving around a pinned character. Horizontal only, see Step.
///     The state is the lag itself (pivot minus character, world space), not a world position: the aim solve in Update
///     adds it to that frame's live character position, which a stored position would put a frame of travel behind.
///     Inside the deadzone radius the pivot holds still. Beyond it the excess x closes at f(x) = tan(a x) / (a T), with
///     T the smoothing time and a = pi / (2 MaxTrail): x / T for small x, a plain trail, rising without bound toward
///     MaxTrail. At travel speed v the excess settles where f(x) = v, so the lag is bounded by deadzone + MaxTrail at any
///     speed and approaches it smoothly, with no edge where the behaviour switches.
///     Kept free of game types so it can be reasoned about on its own.
/// </summary>
public class PivotTrail
{
    // The furthest the pivot may trail beyond the deadzone. The trail law only reaches it at infinite speed: with the
    // default smoothing a sprint (about 5 m/s) settles 0.22 m past the deadzone, the last few percent take over 10 m/s.
    public const float MaxTrail = 0.25f;

    // More travel than this in one frame is a teleport or a long hitch, not something to trail
    public const float SnapDistance = 2f;

    // Travel per sub-step: the trail law is exact for a character held still over a step, so a frame's travel is fed
    // in slices to keep the result the same at any frame rate
    private const float SubstepDistance = 0.025f;
    private const int MaxSubsteps = 32;

    private const float TrailAngleScale = Mathf.PI / (2f * MaxTrail);

    public enum TrailState
    {
        // Both settings at zero: the pivot is rigid on the character
        Off,
        // Reset to no lag this step
        Snapped,
        Inside,
        Trailing,
        // The hard limit at MaxTrail clipped the excess this step; only a frame's travel big enough to overshoot the
        // trail law's asymptote does that
        Capped
    }

    private bool _seeded;
    private Vector2 _lag;
    private Vector2 _lastTarget;

    // Pivot minus character, horizontal, world space
    public Vector3 Lag => new(_lag.x, 0f, _lag.y);

    // How far the lag is past the deadzone it was last stepped with
    public float Excess { get; private set; }

    public TrailState State { get; private set; } = TrailState.Snapped;

    /// <summary>
    ///     No lag, the pivot on the character at target; trailing resumes from there on the next Step.
    /// </summary>
    public void Snap(Vector3 target)
    {
        _lag = Vector2.zero;
        _lastTarget = new Vector2(target.x, target.z);
        _seeded = true;
        Excess = 0f;
        State = TrailState.Snapped;
    }

    /// <summary>
    ///     The next Step snaps instead of trailing from a stale position.
    /// </summary>
    public void Reset()
    {
        _seeded = false;
        Excess = 0f;
        State = TrailState.Snapped;
    }

    /// <summary>
    ///     Advances the lag behind target, the character's position. Only the horizontal travel is trailed: height changes
    ///     are steps (already smoothed by the game's height interpolation), jumps and drops, and the eye's own vertical
    ///     offset is low-passed by PivotFilter, so a vertical trail would stack a third lag on the same motion and move
    ///     the collision origin toward floors and ceilings.
    /// </summary>
    public void Step(Vector3 target, float deadzone, float smoothing, float deltaTime)
    {
        var flat = new Vector2(target.x, target.z);

        if (!_seeded || !CameraRig.IsFinite(_lag) || !CameraRig.IsFinite(_lastTarget) || !CameraRig.IsFinite(flat))
        {
            Snap(target);
            return;
        }

        var moved = flat - _lastTarget;
        _lastTarget = flat;

        if (!(deadzone > 0f) && !(smoothing > 0f))
        {
            _lag = Vector2.zero;
            Excess = 0f;
            State = TrailState.Off;
            return;
        }

        var movedDistance = moved.magnitude;
        if (movedDistance > SnapDistance)
        {
            Snap(target);
            return;
        }

        deadzone = Mathf.Max(0f, deadzone);

        // Paused: the pivot keeps its lag and moves with whatever the character does
        if (!(deltaTime > 0f))
            return;

        var steps = Mathf.Clamp(Mathf.CeilToInt(movedDistance / SubstepDistance), 1, MaxSubsteps);
        var stepMoved = moved / steps;
        var stepTime = deltaTime / steps;
        // exp(-t / T) per sub-step, zero for no smoothing: the excess is dropped outright and the deadzone edge is hard
        var keep = smoothing > 0f ? Mathf.Exp(-stepTime / smoothing) : 0f;

        var capped = false;
        for (var i = 0; i < steps; i++)
        {
            _lag -= stepMoved;
            Relax(deadzone, keep, ref capped);
        }

        Excess = Mathf.Max(0f, _lag.magnitude - deadzone);
        State = capped ? TrailState.Capped : Excess > 0f ? TrailState.Trailing : TrailState.Inside;
    }

    /// <summary>
    ///     With the character held still the excess follows dx/dt = -f(x), whose exact solution is
    ///     sin(a x(t)) = sin(a x0) exp(-t / T). The pivot moves straight toward the character, so a curving path is cut
    ///     rather than retraced.
    /// </summary>
    private void Relax(float deadzone, float keep, ref bool capped)
    {
        var distance = _lag.magnitude;
        var excess = distance - deadzone;
        if (!(excess > 0f))
            return;

        if (excess > MaxTrail)
        {
            excess = MaxTrail;
            capped = true;
        }

        var kept = Mathf.Asin(Mathf.Sin(TrailAngleScale * excess) * keep) / TrailAngleScale;
        _lag *= (deadzone + kept) / distance;
    }
}
