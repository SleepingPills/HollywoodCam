using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     Stage 2 of the tight space camera: positions other than the boom levels, for where shortening the boom is the wrong
///     tool. The case it is for: the character pressed against a wall on the camera's side, where the collision solver can
///     only keep the camera out of the wall by sliding it in behind the head, and the character then covers the middle of
///     the view. The answer there is up, not in: the camera lifts over the head, where the wall does not reach.
///     The configured offset as Stage 1 shapes it is "home". An alternative is taken only with a reason (the character
///     covers the middle of the view where the solver left the camera: a collapse), only when it is clearly better
///     (SwitchMargin, in misses), and only after the reason has persisted (SwitchDwell). Every boom level failing is not a
///     reason: in doorways, where that happens, raising the camera meets the lintel and bought nothing in simulation, and
///     probing for it only cost casts. Home is returned to
///     only once it is clean on its own terms (ReturnDwell), never because an alternative merely got a little worse, so a
///     camera that has moved stays put rather than hunting between two positions of similar score.
///     The caller probes; this class holds the state and the rules, and blends between home and the alternative.
///     Kept free of game types so it can be reasoned about on its own.
/// </summary>
public class TightSpaceCandidates
{
    public readonly struct Alternative(string name, int boomLevel, float lateral, float raise)
    {
        public readonly string Name = name;
        // Boom level (BoomModulator.LevelBoom) and lateral factor, as Stage 1's levels have them
        public readonly int BoomLevel = boomLevel;
        public readonly float Lateral = lateral;
        // Metres up in the pivot frame, on top of the configured offset's own height
        public readonly float Raise = raise;
    }

    // Over the head: centred behind the character and lifted clear of the head. Centred, because against a wall on the
    // camera's side there is no lateral room at all and a half-way lateral position is slid back behind the head by the
    // solver; at the first shortened boom level, because the full boom puts the camera far enough back to meet the wall
    // a character walking along it has just passed. 0.3 m over the configured height puts the centre ray about 0.45 m
    // above the eye, clear of the head and helmet (CoverRadius) at any pitch.
    // A single alternative: in simulated wall-hug, corridor and corner scenes a half-way lateral position and a centred
    // unraised one were both slid back behind the head by the solver, and a raised over-the-shoulder one did the same
    // job as this with less clearance.
    public static readonly Alternative[] Alternatives =
    [
        new("Over head", 1, 0f, 0.3f)
    ];

    // The character covers the middle of the view when the camera's centre ray passes within this of the line from the
    // head to the ribcage. Over the shoulder the ray passes 0.5 m from it (0.3 m at Stage 1's shortest boom), so this
    // cannot fire there; the head is about 0.15 m in radius, so at 0.2 the crosshair is on the head.
    public const float CoverRadius = 0.2f;
    // Only the part of the centre ray this far from the camera, where the character is
    public const float CoverReach = 3f;
    // How long the character must cover the middle of the view before that counts, and be clear of it before it no longer
    // does: a step along a wall with a doorway in it, or a frame of the solver settling, is not a collapse
    public const float CollapseDwell = 0.25f;
    public const float CollapseReleaseDwell = 0.5f;

    // A collapsed camera counts as missing this many samples on top of its real misses: more than AllowedMisses, so a
    // collapse always weighs as a failed position
    public const int CoverPenalty = 3;
    // An alternative is taken only if it misses at least this many fewer (cover penalty included) than home: a real
    // improvement, not one sample
    public const int SwitchMargin = 2;
    // How long a switch must be wanted without a break before it is taken: to an alternative, and back home. Home is
    // slower to come back to, so leaving a wall for a step does not swing the camera down and up again.
    public const float SwitchDwell = 0.3f;
    public const float ReturnDwell = 0.6f;
    // Home is clean only when its predicted camera (Stage 1's clamped candidate) keeps the centre ray at least this far
    // from the character, well over CoverRadius: the prediction is not exactly where the solver will put the camera, and
    // returning into a collapse would pump
    public const float ReturnCoverDistance = 0.3f;
    // The move between home and an alternative, eased; divided by Cam Movement Speed
    public const float BlendTime = 0.4f;

    private const float SettleEpsilon = 1e-4f;
    private const int NoPending = int.MinValue;
    public const int Home = -1;

    // The alternative in use, Home for none
    public int Active { get; private set; } = Home;
    // The alternative blended toward or away from: Active, or the last one while easing back home
    public int Shown { get; private set; } = Home;
    // 0 = home, 1 = the shown alternative
    public float Blend { get; private set; }
    public bool Blending => Mathf.Abs(Blend - (Active == Home ? 0f : 1f)) > SettleEpsilon;

    // Whether the centre ray is covered right now, and whether it has been for CollapseDwell
    public bool Covered { get; private set; }
    public bool Collapsed { get; private set; }
    // A reason to leave home: collapsed, and still covered this frame. Collapsed alone lingers for CollapseReleaseDwell,
    // which a transient (the camera squeezed against a wall for half a second while turning a corner) would otherwise
    // carry through the switch dwell.
    public bool Collapsing => Collapsed && Covered;
    public float CoverDistance { get; private set; } = float.PositiveInfinity;

    // The switch wanted but not yet taken (Home or an alternative), and for how long; Pending is NoPending when none
    public bool HasPending => _pending != NoPending;
    public int Pending => _pending;
    public float PendingFor { get; private set; }

    private int _pending = NoPending;
    private float _pendingSince;
    private float _coveredSince = -1f;
    private float _clearSince = -1f;
    private float _blendVelocity;
    private bool _switched;

    public static string NameOf(int alternative)
    {
        return alternative == Home ? "Home" : Alternatives[alternative].Name;
    }

    /// <summary>
    ///     How far the camera's centre ray passes from the character (the head to ribcage line), over CoverReach.
    /// </summary>
    public static float RayCoverDistance(Vector3 camera, Vector3 forward, Vector3 head, Vector3 ribcage)
    {
        return Geometry.SegmentSegmentDistance(camera, camera + forward * CoverReach, head, ribcage);
    }

    /// <summary>
    ///     The offset for an alternative: Stage 1's shape at its boom level and lateral factor, raised.
    /// </summary>
    public static Vector3 Shape(int alternative, Vector3 desired, Vector3 trailCancel, float shortestBoom)
    {
        var candidate = Alternatives[alternative];
        var boom = BoomModulator.LevelBoom(candidate.BoomLevel, shortestBoom);
        var shaped = BoomModulator.Shape(desired, boom, candidate.Lateral, trailCancel, BoomModulator.Shortening(boom, shortestBoom));
        shaped.y += candidate.Raise;
        return shaped;
    }

    /// <summary>
    ///     Once per frame with the cover distance of the camera where the solver left it.
    /// </summary>
    public void UpdateCollapse(float coverDistance, float time)
    {
        CoverDistance = coverDistance;
        Covered = coverDistance < CoverRadius;

        if (Covered)
        {
            _clearSince = -1f;
            if (_coveredSince < 0f)
                _coveredSince = time;
            if (time - _coveredSince >= CollapseDwell)
                Collapsed = true;
        }
        else
        {
            _coveredSince = -1f;
            if (_clearSince < 0f)
                _clearSince = time;
            if (time - _clearSince >= CollapseReleaseDwell)
                Collapsed = false;
        }
    }

    /// <summary>
    ///     The cost home is compared with, in misses: its own plus CoverPenalty when collapsed.
    /// </summary>
    public static int Cost(int misses, bool covered)
    {
        return misses + (covered ? CoverPenalty : 0);
    }

    /// <summary>
    ///     The position wanted this probe (Home or an alternative). Taken once it has been wanted without a break for its
    ///     dwell; wanting the active one cancels any pending switch.
    /// </summary>
    public void Request(int wanted, float time)
    {
        if (wanted == Active)
        {
            ClearPending();
            return;
        }

        if (wanted != _pending)
        {
            _pending = wanted;
            _pendingSince = time;
        }

        PendingFor = time - _pendingSince;
        if (PendingFor < (wanted == Home ? ReturnDwell : SwitchDwell))
            return;

        Active = wanted;
        if (wanted != Home)
            Shown = wanted;
        _switched = true;
        ClearPending();
    }

    /// <summary>
    ///     No decision this frame (aiming down sights, an orbit, facing a surface): nothing pending survives it.
    /// </summary>
    public void Hold()
    {
        ClearPending();
    }

    /// <summary>
    ///     Whether a switch was taken since the last call: the caller holds the aim point while the camera travels.
    /// </summary>
    public bool ConsumeSwitch()
    {
        var switched = _switched;
        _switched = false;
        return switched;
    }

    /// <summary>
    ///     Eases the blend toward the active position. Frozen (aiming down sights) it holds still: a lateral or vertical
    ///     camera move shifts the point under the crosshair.
    /// </summary>
    public void Step(bool frozen, float speedScale, float deltaTime)
    {
        if (frozen || !(deltaTime > 0f))
        {
            _blendVelocity = 0f;
            return;
        }

        var target = Active == Home ? 0f : 1f;
        Blend = Mathf.SmoothDamp(Blend, target, ref _blendVelocity, BlendTime / speedScale, Mathf.Infinity, deltaTime);
        if (Mathf.Abs(Blend - target) < SettleEpsilon && Mathf.Abs(_blendVelocity) < SettleEpsilon)
        {
            Blend = target;
            _blendVelocity = 0f;
            if (Active == Home)
                Shown = Home;
        }
    }

    /// <summary>
    ///     Home blended toward the shown alternative's offset. Exactly home when nothing is shown.
    /// </summary>
    public Vector3 Offset(Vector3 home, Vector3 alternative)
    {
        return Shown == Home || Blend <= 0f ? home : Vector3.Lerp(home, alternative, Blend);
    }

    public void Reset()
    {
        Active = Shown = Home;
        Blend = _blendVelocity = 0f;
        Covered = Collapsed = false;
        CoverDistance = float.PositiveInfinity;
        _coveredSince = _clearSince = -1f;
        _switched = false;
        ClearPending();
    }

    private void ClearPending()
    {
        _pending = NoPending;
        PendingFor = 0f;
    }
}
