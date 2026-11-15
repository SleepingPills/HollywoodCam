using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     Picks the trunk rotation limit (Player.TrunkRotationLimit) each frame. While standing still the game twists the
///     torso freely within the limit and drags the body along once the twist exceeds it. Holding the limit at the
///     deadzone gives the "aim with the upper body, legs planted" part; when the twist leaves the deadzone, ramping the
///     limit down lets that same drag turn the legs toward the aim, after which the limit goes back to the deadzone.
///     Kept free of game types so the state machine can be reasoned about on its own.
///     The deadzone cannot usefully exceed 45: the same twist angle is fed to the animator, whose stand layer enters
///     Turn_Left_90_Aim / Turn_Right_90_Aim past about that angle, and their root rotation turns the body whatever the
///     limit says (observed in game; the threshold is in the animator asset, not in code).
/// </summary>
public class TorsoDeadzone
{
    // Where a re-centring turn stops. Chasing an exact zero would only add frames of the game's exponential drag.
    public const float SettleAngle = 2f;

    // How close to the settle angle the twist has to get for the turn to count as done; the drag closes the rest of
    // the excess at 30/s, so this is a few frames after the limit bottoms out.
    private const float SettleMargin = 1f;

    public bool IsTurning { get; private set; }

    public float Limit { get; private set; }

    /// <summary>
    ///     Returns the limit to write. handsToBodyAngle is MovementContext.HandsToBodyAngle (signed, degrees), as left by
    ///     the last animator pass.
    /// </summary>
    public float Update(float handsToBodyAngle, float deadzone, float turnSpeed, float deltaTime)
    {
        deadzone = Mathf.Max(0f, deadzone);

        // No re-centring: the game's native drag at the edge of the deadzone. A deadzone at or under the settle angle
        // is already as tight as a turn would leave it.
        if (turnSpeed <= 0f || deadzone <= SettleAngle)
        {
            IsTurning = false;
            return Limit = deadzone;
        }

        var twist = Mathf.Abs(handsToBodyAngle);

        if (!IsTurning)
        {
            if (!(twist > deadzone))
                return Limit = deadzone;

            IsTurning = true;
            Limit = deadzone;
        }

        // Min with the deadzone: it can be lowered mid-turn, and the limit should never sit above it
        Limit = Mathf.Max(SettleAngle, Mathf.Min(Limit, deadzone) - turnSpeed * deltaTime);

        if (Limit <= SettleAngle && twist <= SettleAngle + SettleMargin)
        {
            IsTurning = false;
            Limit = deadzone;
        }

        return Limit;
    }

    /// <summary>
    ///     Drops a turn in progress, for when the limit is left to the game (prone). The next Update starts fresh.
    /// </summary>
    public void Abort()
    {
        IsTurning = false;
    }

    /// <summary>
    ///     Drops a turn in progress and returns the plain deadzone as the limit, for while the walking legs are driven by
    ///     MovingDeadzone. Coming to a stop then starts from a clean state with the lag the walk left.
    /// </summary>
    public float Hold(float deadzone)
    {
        IsTurning = false;
        return Limit = Mathf.Max(0f, deadzone);
    }
}
