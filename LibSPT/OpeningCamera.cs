using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     What the caller measured this frame for the opening camera. Distances in metres.
/// </summary>
public struct OpeningCheck
{
    // |SmoothedTilt|, the lean as animated
    public float Lean;
    // Where the ordinary solve wants the camera (its phase 1 target): how far its centre ray passes from the character, and
    // whether the camera could get there from where it is without going through anything
    public float OrdinaryCover;
    public bool OrdinaryReachable;
    // The opening camera's position: how far its centre ray passes from the character, whether a cast from the head to it
    // is blocked (it is round a corner from the head), how many of the eye's view samples have a clear line back to it
    // (out of how many there are), whether it overlaps geometry it could not be moved out of (for a position not yet in
    // use: whether it overlaps at all), and its distance from the eye
    public float CameraCover;
    public bool HiddenFromHead;
    public int Seen;
    public int Samples;
    public bool Overlap;
    public float Distance;
    public float MaxDistance;
}

public enum OpeningAction
{
    Stay,
    // Back to the ordinary solve, from where the camera is: the solver continues from this position, nothing jumps
    Handover,
    // The position is no longer good: move the camera back toward the head until the head can see it, then hand over
    Retreat,
    // Straight to the ordinary solve's position: the camera cannot get there by moving
    Cut
}

/// <summary>
///     The camera for a head leaned through an opening (a doorway leaned into from beside it) with the body behind the wall.
///     The ordinary solve keeps the camera where a line from the head reaches it; with the head in the opening and the view
///     turned toward the wall, every such position is in the opening behind the head, and the head fills the screen. This
///     mode instead keeps the camera on the far side of the opening, moving it like a small sphere from its own last
///     position toward where the offset wants it and sliding along what it meets, so it comes to rest against the far face
///     of the wall. The character is then hidden behind the wall, which is accepted: the camera sees what the leaning eye
///     sees.
///     The camera gets to the far side either because the ordinary solve put it there (looking along the wall, the solve
///     slides it through the opening) and the mode just keeps it, or by moving out through the opening in the lean's
///     direction and then back toward the offset (Bootstrap). It is proved to be in real free space each frame by the eye's
///     view samples: points the eye sees, so in free space, which must have a clear line back to the camera. A cast that
///     starts inside a mesh does not see its back faces, so the camera seeing them would prove nothing; a cast from outside
///     to a camera inside a wall hits the wall.
///     Kept free of game types: the caller measures, this class holds the state and applies the rules.
/// </summary>
public class OpeningCamera
{
    public enum Phase
    {
        Off,
        Active,
        Retreating
    }

    // Lean (|SmoothedTilt|, the game's range is 0 to 5) the mode may start at, and below which it counts as no longer
    // leaning: the opening case is a lean, and a walk past a doorway or a corner must never start it. Near the full lean
    // to start: half way in, the eye still sees the room the body is in, and a camera left behind the body passes the
    // view sample test, only to fail it a moment later (simulated: kept, retreated, bootstrapped in turn).
    public const float LeanEnter = 4f;
    public const float LeanExit = 1f;
    // How long the lean must have been under LeanExit before the mode lets go: a lean flicking between sides passes zero
    public const float LeanExitDwell = 0.2f;

    // The mode may start when the ordinary solve's position keeps the centre ray less than this from the character (a
    // poor view, not necessarily a full collapse: its target can sit just outside Stage 2's trigger while the camera it
    // produces is covered), and only for a position that keeps the ray at least CameraCoverMin from the character and
    // CoverGain further than the ordinary one: a clearly better view, not a sideways step
    public const float EnterOrdinaryCover = TightSpaceCandidates.ReturnCoverDistance;
    public const float CameraCoverMin = TightSpaceCandidates.ReturnCoverDistance;
    public const float CoverGain = 0.15f;
    // The ordinary solve must keep the ray this far from the character to take back over: more than EnterOrdinaryCover,
    // so a solve hovering at the threshold does not hand back and forth
    public const float HandoverCover = 0.4f;

    // View samples that must have a clear line back to the camera. To start: half of them (at least two), so the camera
    // sees what the leaning eye sees; a camera left behind the body while the eye leans out sees little of it. To stay:
    // one, so the mode holds while the view changes, and lets go once the eye and the camera no longer share any view
    // (fewer when the eye has fewer samples, facing a surface up close).
    public const float SeenShareToEnter = 0.5f;
    public const int SeenToEnter = 2;
    public const int SeenToStay = 1;

    // The ordinary solve has to be good again (uncovered and reachable) for this long before the mode hands back to it
    public const float HandoverDwell = 0.15f;
    // Longest the camera may take to get back to where the head can see it before the mode gives up and cuts
    public const float RetreatMaxTime = 1f;
    // Longest the camera may take to get out through the opening to its waypoint when bootstrapping
    public const float TravelMaxTime = 0.6f;
    // The caller moves a camera found overlapping geometry back out (depenetration, or its last clear position). Only if
    // that keeps failing for this long is the camera stuck inside something, and the mode cuts: a cut shows the collapsed
    // view the mode exists to avoid, so a single bad frame (a rough mesh, a door leaf brushing past) must not cause one
    public const float StuckCutTime = 0.25f;
    // A first cut lets the mode start again at once (by its bootstrap, if the camera is behind the head). A second cut within
    // CutRepeatWindow of the last one means the same conditions keep ending it badly, and the mode then stays off for
    // CutCooldown, so it cannot cut and re-enter frame after frame (simulated before overlaps were resolved: 54 cuts in a
    // second, each re-entry flagging the same overlap)
    public const float CutRepeatWindow = 2f;
    public const float CutCooldown = 1f;

    // The camera is a sphere of this radius, the solver's middle phase radius: the same clearance from walls
    public const float CameraRadius = 0.09f;
    // How far out through the opening the bootstrap goes, in the lean's direction, before turning back toward the offset.
    // A wall is 0.2 to 0.4 m thick and the leaned eye is just inside it.
    public const float BootstrapReach = 0.6f;
    // How far beyond the offset's own length the camera may be from the eye before the position is given up (snagged on
    // something while the character walks away)
    public const float DistanceSlack = 1f;
    // Moving toward its goal the camera eases like the solver's phase 1; retreating to the head (a longer way, round the
    // jamb) it eases slower, so it does not swoop. Divided by Cam Movement Speed.
    public const float SmoothTime = 0.2f;
    public const float RetreatSmoothTime = 0.35f;

    public Phase State { get; private set; } = Phase.Off;
    public bool Active => State != Phase.Off;
    // Whether the camera is still on its way out through the opening (bootstrap), toward Waypoint
    public bool Travelling { get; private set; }
    public bool Bootstrapped { get; private set; }
    public Vector3 Waypoint { get; private set; }
    public float Since { get; private set; }
    // Why the last decision went the way it did, for the debug UI
    public string Reason { get; private set; } = "-";

    private float _handoverSince = -1f;
    private float _leanLowSince = -1f;
    private float _travelSince;
    private float _retreatSince;
    private float _stuckSince = -1f;
    private float _cutTime = float.NegativeInfinity;
    private float _cooldownUntil = float.NegativeInfinity;

    /// <summary>
    ///     The view samples a position must be seen by to start the mode, out of how many the eye has.
    /// </summary>
    public static int SeenNeededToEnter(int samples)
    {
        return Mathf.Min(samples, Mathf.Max(SeenToEnter, Mathf.CeilToInt(samples * SeenShareToEnter)));
    }

    /// <summary>
    ///     Whether a position may start the mode. Checked first for the camera continuing from where it is, then for a
    ///     bootstrap position. It must be clearly better than the ordinary solve's position and see what the leaning eye
    ///     sees. Whether the head sees the position does not matter: the ordinary solve misses positions the head can see as
    ///     well (its first phase casts a 0.18 m sphere and slides greedily), and requiring them to be hidden refused good
    ///     bootstraps in simulation.
    /// </summary>
    public static bool CanEnter(in OpeningCheck check)
    {
        return check.Lean >= LeanEnter
               && check.OrdinaryCover < EnterOrdinaryCover
               && !check.Overlap
               && check.CameraCover >= Mathf.Max(CameraCoverMin, check.OrdinaryCover + CoverGain)
               && check.Seen >= SeenNeededToEnter(check.Samples)
               && check.Distance <= check.MaxDistance;
    }

    /// <summary>
    ///     Which of CanEnter's rules refused a position, for the debug UI. Builds a string, and a refusal repeats every frame
    ///     while leaning, so only for when someone is reading it.
    /// </summary>
    public static string DescribeRefusal(in OpeningCheck check)
    {
        if (!(check.Lean >= LeanEnter))
            return $"not leaning ({check.Lean:F1} < {LeanEnter:F1})";
        if (!(check.OrdinaryCover < EnterOrdinaryCover))
            return $"ordinary solve fine (cover {check.OrdinaryCover:F2})";
        if (check.Overlap)
            return "position overlaps geometry";
        if (!(check.CameraCover >= Mathf.Max(CameraCoverMin, check.OrdinaryCover + CoverGain)))
            return $"position not clearly better ({check.CameraCover:F2} vs {check.OrdinaryCover:F2})";
        if (check.Seen < SeenNeededToEnter(check.Samples))
            return $"view samples see it {check.Seen}/{check.Samples}";
        if (!(check.Distance <= check.MaxDistance))
            return $"too far ({check.Distance:F2} > {check.MaxDistance:F2})";

        return "-";
    }

    public void Enter(float time, bool bootstrapped, Vector3 waypoint)
    {
        State = Phase.Active;
        Since = time;
        Bootstrapped = bootstrapped;
        Travelling = bootstrapped;
        Waypoint = waypoint;
        _travelSince = time;
        _handoverSince = -1f;
        _leanLowSince = -1f;
        Reason = bootstrapped ? "entered through the opening" : "entered, kept the far side";
    }

    /// <summary>
    ///     The camera reached its waypoint, or gave up on it: from here on it heads for the offset.
    /// </summary>
    public void Arrived()
    {
        Travelling = false;
    }

    public bool TravelExpired(float time) => Travelling && time - _travelSince > TravelMaxTime;

    /// <summary>
    ///     The rules while active. While still travelling out through the opening the camera passes behind the head and is
    ///     seen by the head, so only the hard checks apply then. describe builds the reasons that carry numbers, which
    ///     allocate; only the debug UI shows them.
    /// </summary>
    public OpeningAction Update(in OpeningCheck check, float time, bool describe)
    {
        if (check.Overlap)
        {
            if (_stuckSince < 0f)
                _stuckSince = time;
            if (time - _stuckSince >= StuckCutTime)
                return Leave(OpeningAction.Cut, "cut: stuck in geometry", time);
            Reason = "overlapping, could not move out yet";
            return OpeningAction.Stay;
        }

        _stuckSince = -1f;

        if (check.Seen < Mathf.Min(SeenToStay, check.Samples))
            return Retreat(time, "retreating: view samples no longer see it");

        if (check.Lean < LeanExit)
        {
            if (_leanLowSince < 0f)
                _leanLowSince = time;
            if (time - _leanLowSince >= LeanExitDwell)
                return Retreat(time, "retreating: lean ended");
        }
        else
        {
            _leanLowSince = -1f;
        }

        if (Travelling)
        {
            Reason = "travelling out through the opening";
            return OpeningAction.Stay;
        }

        if (check.Distance > check.MaxDistance)
            return Retreat(time, describe ? $"retreating: too far ({check.Distance:F2})" : "retreating: too far");

        if (check.CameraCover < TightSpaceCandidates.CoverRadius)
            return Retreat(time, describe ? $"retreating: character covers the view ({check.CameraCover:F2})" : "retreating: character covers the view");

        if (check.OrdinaryCover >= HandoverCover && check.OrdinaryReachable)
        {
            if (_handoverSince < 0f)
                _handoverSince = time;
            if (time - _handoverSince >= HandoverDwell)
                return Leave(OpeningAction.Handover, "handed over: ordinary solve good again", time);
            Reason = "ordinary solve good, handing over";
            return OpeningAction.Stay;
        }

        _handoverSince = -1f;
        Reason = "holding the far side";
        return OpeningAction.Stay;
    }

    /// <summary>
    ///     The rules while retreating toward the head: hand over as soon as the head can see the camera.
    /// </summary>
    public OpeningAction UpdateRetreat(bool hiddenFromHead, float time)
    {
        if (!hiddenFromHead)
            return Leave(OpeningAction.Handover, "handed over: retreated, head sees the camera", time);

        if (time - _retreatSince > RetreatMaxTime)
            return Leave(OpeningAction.Cut, "cut: retreat took too long", time);

        return OpeningAction.Stay;
    }

    public void Reset(string reason = "-")
    {
        State = Phase.Off;
        Travelling = Bootstrapped = false;
        _handoverSince = _leanLowSince = _stuckSince = -1f;
        Reason = reason;
    }

    // The reasons come whole from the callers, as literals where they can be, so a transition does not allocate
    private OpeningAction Retreat(float time, string reason)
    {
        State = Phase.Retreating;
        Travelling = false;
        _retreatSince = time;
        Reason = reason;
        return OpeningAction.Retreat;
    }

    private OpeningAction Leave(OpeningAction action, string reason, float time)
    {
        Reset(reason);
        if (action == OpeningAction.Cut)
        {
            if (time - _cutTime < CutRepeatWindow)
                _cooldownUntil = time + CutCooldown;
            _cutTime = time;
        }

        return action;
    }

    /// <summary>
    ///     Whether the mode may not start again yet: only after a repeated cut (see CutRepeatWindow).
    /// </summary>
    public bool CoolingDown(float time) => time < _cooldownUntil;
}
