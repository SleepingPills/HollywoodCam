using UnityEngine;

namespace HollywoodCam;

public enum MovingDeadzoneMode
{
    // Inside the deadzone, the legs held where they are (re-centre speed 0)
    Hold,
    // Inside the deadzone, the legs drifting toward the aim
    Recentre,
    // Outside the deadzone, the legs turning to bring the aim back to its edge
    Chase,
    // Past the hard ceiling, the lag clamped to it this step
    Ceiling,
    // Trying to walk without getting anywhere, the legs squaring up to the aim
    Blocked
}

/// <summary>
///     Advances the legs' yaw toward the look yaw while walking. The walking state normally glues the legs to the look
///     within a few frames; this lets the upper body take small aim changes instead. Travel is aim-relative regardless
///     (the walking state's local-to-global conversion uses the look yaw while this is in control), so the lag only
///     shows as the walk animation blending toward a strafe. Kept free of game types so it can be reasoned about on its
///     own; all angles are in degrees.
/// </summary>
public class MovingDeadzone
{
    // Past this the legs are dragged along instantly: travel follows the aim, so legs far behind it would moonwalk
    public const float MaxLag = 45f;

    public MovingDeadzoneMode Mode { get; private set; }

    /// <summary>
    ///     Signed shortest angle from the legs to the look after the last step (positive: the look is to the right).
    /// </summary>
    public float Lag { get; private set; }

    /// <summary>
    ///     Returns the new leg yaw, in legYaw's own continuity. turnSpeed is how fast the legs turn while the aim is
    ///     outside the deadzone or the walk is blocked, 0 meaning at once (the walking state's own follow is close to a
    ///     snap); recentreSpeed is how fast they drift inside it, 0 meaning hold.
    /// </summary>
    public float Step(float legYaw, float lookYaw, float deadzone, float turnSpeed, float recentreSpeed, bool blocked, float deltaTime)
    {
        deadzone = Mathf.Clamp(deadzone, 0f, MaxLag);
        deltaTime = Mathf.Max(0f, deltaTime);

        var lag = Mathf.DeltaAngle(legYaw, lookYaw);
        var magnitude = Mathf.Abs(lag);
        var sign = lag < 0f ? -1f : 1f;

        var ceiling = magnitude > MaxLag;
        if (ceiling)
            magnitude = MaxLag;

        if (blocked)
        {
            Mode = MovingDeadzoneMode.Blocked;
            magnitude = turnSpeed > 0f ? Mathf.Max(0f, magnitude - turnSpeed * deltaTime) : 0f;
        }
        else
        {
            var remaining = deltaTime;

            if (magnitude > deadzone)
            {
                Mode = MovingDeadzoneMode.Chase;

                // Whatever part of the step is left once the edge is reached drifts at the re-centre speed, so the
                // outcome does not depend on how the time is cut into frames
                if (turnSpeed > 0f)
                {
                    var toEdge = (magnitude - deadzone) / turnSpeed;
                    if (toEdge >= remaining)
                    {
                        magnitude -= turnSpeed * remaining;
                        remaining = 0f;
                    }
                    else
                    {
                        magnitude = deadzone;
                        remaining -= toEdge;
                    }
                }
                else
                {
                    magnitude = deadzone;
                }
            }
            else
            {
                Mode = recentreSpeed > 0f ? MovingDeadzoneMode.Recentre : MovingDeadzoneMode.Hold;
            }

            if (recentreSpeed > 0f && remaining > 0f)
                magnitude = Mathf.Max(0f, magnitude - recentreSpeed * remaining);
        }

        if (ceiling)
            Mode = MovingDeadzoneMode.Ceiling;

        var newLag = sign * magnitude;
        Lag = newLag;

        // Moved by the change in lag rather than rebuilt from the look yaw, so the leg yaw keeps its own range
        return legYaw + (lag - newLag);
    }
}
