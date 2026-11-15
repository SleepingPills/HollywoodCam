using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     The tight space camera's state and decisions. In cramped spaces the configured camera position often cannot see what
///     the character is facing (a doorway walked through with the camera still behind the wall, a corner, a stairwell)
///     although nothing is between the camera and the character, so the collision solver is happy with it. This shortens
///     the boom (the camera's distance behind, in the view frame) in a few fixed levels until enough of the character's
///     view is visible again, and lengthens it back when the space opens up.
///     The caller probes each level's camera position for visibility (IntentProbe) and hands the levels' results to
///     Propose; Step then eases the applied boom toward the decided level and Shape turns it into the offset the collision
///     solver is given. Boom changes move the camera along its own centre ray, so they do not move the aim; only the
///     lateral shrink that goes with a short boom does, which is why it is frozen while aiming down sights.
///     Kept free of game types so it can be reasoned about on its own.
/// </summary>
public class BoomModulator
{
    // Boom levels probed, evenly spaced from 1 (the configured offset) down to the shortest allowed. Four is enough to
    // get through a doorway in steps the smoothing hides; every level costs a collision cast and up to a full set of
    // sight casts when the longer ones fail.
    public const int LevelCount = 4;

    // Visibility is counted in missed samples (IntentProbe: the centre sample counts 2, every other 1), not as a share,
    // so the rules below mean the same however many samples there are (the near samples drop out close to a wall).
    // A level at or shorter than the current target may miss this many and still be taken. 2 lets one clipped vicinity
    // sample or two (a shelf edge, a railing) through; in a doorway the camera is still behind the wall, the jamb hides
    // the shoulder-side inner sample and the three outer ones, 4.
    public const int AllowedMisses = 2;

    // A level longer than the current target may miss only this many. Hysteresis: a level that just failed at 3 misses
    // has to come back to 1, not 2, before the boom goes back out, so one sample flickering does not pump it.
    public const int LengthenAllowedMisses = 1;

    // When no level is good enough, the longest level that misses at most this many more than the best one is taken:
    // shortening has to buy at least two samples, or the boom would collapse whenever something no boom can fix is in
    // the way.
    public const int FallbackSlackMisses = 1;

    // With the surface under the crosshair closer to the eye than this there is nothing to see past it, so no level is
    // probed and the target is held where it is: facing a wall up close never moves the boom.
    public const float FacingSurfaceDistance = 0.8f;

    // Lateral offset at the shortest boom, as a fraction of the configured one. A 0.5 m lateral offset is half a doorway:
    // at full lateral the camera sits on the jamb's line and the wall fills half the screen however short the boom is.
    public const float LateralAtShortest = 0.6f;

    // Shortening is quick (a doorway is passed in about a second) but only after a shorter level has been wanted
    // without a break for ShortenDwell, so a one-frame dip does not move the camera; 0.1 s is 0.15 m of walking and
    // 0.5 m of sprinting. Lengthening is slow and only after the longer level has been wanted for LengthenDwell, so a
    // passing obstruction does not pump the camera. The dwells are game time; the easing times are divided by Cam
    // Movement Speed.
    public const float ShortenTime = 0.12f;
    public const float ShortenDwell = 0.1f;
    public const float LengthenTime = 0.6f;
    public const float LengthenDwell = 0.4f;
    // The lateral factor follows the boom with this smoothing, and catches up at this pace after aiming down sights
    public const float LateralTime = 0.15f;

    // The shaped offset is never closer to the pivot than this (unless the configured one is): closer, the camera would
    // be inside the head and helmet
    public const float MinCameraDistance = 0.35f;

    // Below this the applied boom is snapped onto its target, so at rest the offset is exactly the configured one
    private const float SettleEpsilon = 1e-4f;

    // The boom as applied (1 = the configured offset), eased toward Target
    public float Boom { get; private set; } = 1f;
    // The lateral factor as applied
    public float Lateral { get; private set; } = 1f;
    // The decided level
    public float Target { get; private set; } = 1f;
    // The last level the probes asked for, which Target follows after ShortenDwell when shorter, LengthenDwell when longer
    public float Proposed { get; private set; } = 1f;
    // How long a longer level has been asked for, negative when none is
    public float LengthenWanted { get; private set; } = -1f;
    // How long a shorter level has been asked for, negative when none is
    public float ShortenWanted { get; private set; } = -1f;

    private float _boomVelocity;
    private float _lateralVelocity;
    private float _lengthenSince = -1f;
    private float _shortenSince = -1f;
    // The longest of the shorter levels asked for since _shortenSince: the least shortening the whole dwell agreed on
    private float _shortenLongest;

    public static float LevelBoom(int level, float shortestBoom)
    {
        return level <= 0 ? 1f : Mathf.Lerp(1f, shortestBoom, level / (float)(LevelCount - 1));
    }

    /// <summary>
    ///     0 at full boom, 1 at the shortest allowed.
    /// </summary>
    public static float Shortening(float boom, float shortestBoom)
    {
        return shortestBoom < 1f ? Mathf.Clamp01((1f - boom) / (1f - shortestBoom)) : 0f;
    }

    public static float LateralFor(float boom, float shortestBoom)
    {
        return Mathf.Lerp(1f, LateralAtShortest, Shortening(boom, shortestBoom));
    }

    /// <summary>
    ///     The lateral factor a level would be applied with: its own, or the frozen one while aiming down sights.
    /// </summary>
    public float CandidateLateral(float boom, float shortestBoom, bool aiming)
    {
        return aiming ? Lateral : LateralFor(boom, shortestBoom);
    }

    /// <summary>
    ///     The misses a level may have and still be taken: fewer for one longer than the current target.
    /// </summary>
    public int AllowedMissesFor(float levelBoom)
    {
        return levelBoom > Target + SettleEpsilon ? LengthenAllowedMisses : AllowedMisses;
    }

    /// <summary>
    ///     The boom to ask for when no level was good enough: the fallback level's, but never longer than the current
    ///     target. Lengthening only ever happens through a level passing LengthenAllowedMisses; otherwise, in a doorway
    ///     where every level is blocked for a moment, the fallback would ask for the longest level and the boom would start
    ///     back out in the middle of the doorway.
    /// </summary>
    public float FallbackBoom(int[] misses, int count, float shortestBoom, out int level)
    {
        level = ChooseFallback(misses, count);
        return Mathf.Min(LevelBoom(level, shortestBoom), Target);
    }

    /// <summary>
    ///     When no level was good enough: the longest level within FallbackSlackMisses of the fewest misses.
    /// </summary>
    public static int ChooseFallback(int[] misses, int count)
    {
        var best = int.MaxValue;
        for (var i = 0; i < count; i++)
            best = Mathf.Min(best, misses[i]);

        for (var i = 0; i < count; i++)
        {
            if (misses[i] <= best + FallbackSlackMisses)
                return i;
        }

        return 0;
    }

    /// <summary>
    ///     The offset for the collision solver: the configured one with the boom (z) and lateral (x) scaled, plus the given
    ///     share of trailCancel (the pivot's trail behind the eye, in the pivot frame) so a short boom is measured from the
    ///     eye rather than from a pivot trailing up to a third of a metre behind it. Exactly desired at a boom of 1.
    /// </summary>
    public static Vector3 Shape(Vector3 desired, float boom, float lateral, Vector3 trailCancel, float shortening)
    {
        if (boom >= 1f && lateral >= 1f && shortening <= 0f)
            return desired;

        var shaped = new Vector3(desired.x * lateral, desired.y, desired.z * boom) + trailCancel * shortening;

        // Only ever pushed back along the boom, and never past the configured offset
        if (desired.z < 0f && desired.sqrMagnitude > MinCameraDistance * MinCameraDistance && shaped.sqrMagnitude < MinCameraDistance * MinCameraDistance)
        {
            var side = shaped.x * shaped.x + shaped.y * shaped.y;
            var back = -Mathf.Sqrt(Mathf.Max(0f, MinCameraDistance * MinCameraDistance - side));
            shaped.z = Mathf.Max(Mathf.Min(shaped.z, back), desired.z);
        }

        return shaped;
    }

    /// <summary>
    ///     The level the probes asked for this frame. A shorter level than the target is taken once shorter ones have been
    ///     asked for without a break for ShortenDwell (the longest of them, so one deep dip in the window does not take the
    ///     camera all the way in), a longer one once it has been asked for without a break for LengthenDwell. Asking for the
    ///     target itself cancels both.
    /// </summary>
    public void Propose(float boom, float time)
    {
        Proposed = boom;

        if (boom < Target - SettleEpsilon)
        {
            _lengthenSince = -1f;

            if (_shortenSince < 0f)
            {
                _shortenSince = time;
                _shortenLongest = boom;
            }
            else
            {
                _shortenLongest = Mathf.Max(_shortenLongest, boom);
            }

            if (time - _shortenSince >= ShortenDwell)
            {
                Target = _shortenLongest;
                _shortenSince = -1f;
            }
        }
        else if (boom > Target + SettleEpsilon)
        {
            _shortenSince = -1f;

            if (_lengthenSince < 0f)
                _lengthenSince = time;

            if (time - _lengthenSince >= LengthenDwell)
            {
                Target = boom;
                _lengthenSince = -1f;
            }
        }
        else
        {
            _lengthenSince = -1f;
            _shortenSince = -1f;
        }

        LengthenWanted = _lengthenSince < 0f ? -1f : time - _lengthenSince;
        ShortenWanted = _shortenSince < 0f ? -1f : time - _shortenSince;
    }

    /// <summary>
    ///     Eases the applied boom toward the target and the lateral factor toward the boom's, except while aiming down
    ///     sights, when the lateral factor holds still (a lateral camera move shifts the point under the crosshair).
    ///     speedScale is Cam Movement Speed.
    /// </summary>
    public void Step(bool aiming, float shortestBoom, float speedScale, float deltaTime)
    {
        if (!(deltaTime > 0f))
            return;

        var boomTime = (Target < Boom ? ShortenTime : LengthenTime) / speedScale;
        Boom = Mathf.SmoothDamp(Boom, Target, ref _boomVelocity, boomTime, Mathf.Infinity, deltaTime);
        if (Mathf.Abs(Boom - Target) < SettleEpsilon && Mathf.Abs(_boomVelocity) < SettleEpsilon)
        {
            Boom = Target;
            _boomVelocity = 0f;
        }

        if (aiming)
        {
            _lateralVelocity = 0f;
            return;
        }

        var lateralTarget = LateralFor(Boom, shortestBoom);
        Lateral = Mathf.SmoothDamp(Lateral, lateralTarget, ref _lateralVelocity, LateralTime / speedScale, Mathf.Infinity, deltaTime);
        if (Mathf.Abs(Lateral - lateralTarget) < SettleEpsilon && Mathf.Abs(_lateralVelocity) < SettleEpsilon)
        {
            Lateral = lateralTarget;
            _lateralVelocity = 0f;
        }
    }

    /// <summary>
    ///     Straight to a level with nothing in motion: the first frame of 3rd person, so the camera eases out from the eye
    ///     only as far as the space allows.
    /// </summary>
    public void Snap(float boom, float shortestBoom)
    {
        Boom = Target = Proposed = boom;
        Lateral = LateralFor(boom, shortestBoom);
        _boomVelocity = _lateralVelocity = 0f;
        _lengthenSince = _shortenSince = -1f;
        LengthenWanted = ShortenWanted = -1f;
    }

    /// <summary>
    ///     Back to the configured offset: modulation is off, or the game is posing the body.
    /// </summary>
    public void Reset()
    {
        Snap(1f, 1f);
        Lateral = 1f;
    }
}
