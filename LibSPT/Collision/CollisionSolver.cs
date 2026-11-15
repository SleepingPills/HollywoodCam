using UnityEngine;

namespace HollywoodCam.Collision;

public enum ScanOutcome
{
    // Not run yet
    None,
    // No gradient and the line from the cast origin to the camera point is clear: the point as requested
    Clear,
    // No gradient but the line is blocked (all round or symmetrically): pulled in toward the cast origin
    PulledIn,
    // Slid along the clearance gradient to the first good enough probe
    SlideBail,
    // Slid along the gradient, no probe was good enough, the best one taken
    SlideBest
}

public class AdvectionSolver(CircleScan circleScan, LineScan lineScan, float? lineScanRadius = null)
{
    public readonly CircleScan CircleScan = circleScan;
    public readonly LineScan LineScan = lineScan;
    public readonly float LineScanRadius = lineScanRadius ?? circleScan.CenterSphereCastRadius;

    // How the last solve ended, for the debug UI
    public ScanOutcome LastOutcome { get; private set; }

    public Vector3 Solve(
        Transform eyeTransform, Vector3 cameraOffset, Vector3 objectivePos, LayerMask layerMask
    )
    {
        CircleScan.Update(eyeTransform, cameraOffset, objectivePos, layerMask);

        // There's either no collision or no gradient. We grab the tangent to the central raycast as the desired position.
        if (CircleScan.Success)
        {
            LastOutcome = CircleScan.CenterScore >= 1f ? ScanOutcome.Clear : ScanOutcome.PulledIn;
            return CircleScan.CenterPoint;
        }

        // var scanDir = CircleScan.AdvectionVector.normalized;
        var scanOffset = CircleScan.AdvectionVector;

        var result = LineScan.FindBestPosition(eyeTransform, cameraOffset, scanOffset, objectivePos, LineScanRadius, layerMask);
        LastOutcome = LineScan.LastBailed ? ScanOutcome.SlideBail : ScanOutcome.SlideBest;
        return result;
    }

    /// <summary>
    ///     One line for the debug UI: the outcome and its numbers.
    /// </summary>
    public string Describe()
    {
        return LastOutcome switch
        {
            ScanOutcome.Clear => "Clear",
            ScanOutcome.PulledIn => $"Pull {CircleScan.CenterScore:F2}",
            ScanOutcome.SlideBail => $"Slide {LineScan.LastStep}/{LineScan.LastSteps}",
            ScanOutcome.SlideBest => $"Best {LineScan.LastStep}/{LineScan.LastSteps} {LineScan.LastScore:F2}",
            _ => "-"
        };
    }
}

public class PositionSolver(
    AdvectionSolver phase1,
    AdvectionSolver phase2,
    AdvectionSolver phase3,
    float phase1SmoothTime,
    float phase2SmoothTime
)
{
    // A phase whose result is closer than this to what it was given counts as not having changed the offset
    private const float DecidedThreshold = 0.01f;

    public readonly AdvectionSolver Phase1 = phase1;
    public readonly AdvectionSolver Phase2 = phase2;
    public readonly AdvectionSolver Phase3 = phase3;

    // Slow speed solver with large radius. The chode solver. It's all about the girth.
    private Vector3 _phase1Velocity;

    // Fast speed solver with narrow radius. It's all about the technique.
    private Vector3 _phase2Velocity;

    // Whether the solves record the distances below. Set by the caller from the debug UI setting: nothing else reads them.
    public bool RecordDiagnostics;

    // Last solve, for the debug UI. Distances in metres, in the pivot frame.
    // Phase 1: its solved target from the desired offset; phase 2: its solved target from the smoothed offset; phase 3:
    // its hard correction of the smoothed offset
    public float Phase1Moved { get; private set; }
    public float Phase2Moved { get; private set; }
    public float Phase3Moved { get; private set; }
    // Whether the phase teleported the current camera across an obstacle onto the solved side
    public bool Phase1Shifted { get; private set; }
    public bool Phase2Shifted { get; private set; }
    // The result's distance from the desired offset
    public float DesiredError { get; private set; }

    /// <summary>
    ///     The phase that set the offset this frame: the last one that changed what it was given by more than a centimetre,
    ///     "Desired" when the result is the desired offset, "Smoothing" when it is still easing toward it.
    /// </summary>
    public string DecidedBy
    {
        get
        {
            if (Phase3Moved > DecidedThreshold) return "P3";
            if (Phase2Moved > DecidedThreshold) return "P2";
            if (Phase1Moved > DecidedThreshold) return "P1";
            return DesiredError > DecidedThreshold ? "Smoothing" : "Desired";
        }
    }

    public Vector3 Solve(Transform eyeTransform, Vector3 currentCameraOffset, Vector3 desiredCameraOffset, Vector3 eyePos, LayerMask eyeMask)
    {
        var speed = Plugin.CameraMoveSpeedScale;

        var phase1Offset = ApplySolver(Phase1, eyeTransform, ref currentCameraOffset, desiredCameraOffset, eyePos, eyeMask, out var shifted1);
        LastPhase1Target = phase1Offset;
        if (RecordDiagnostics)
            Phase1Moved = (phase1Offset - desiredCameraOffset).magnitude;
        Phase1Shifted = shifted1;
        currentCameraOffset = Vector3.SmoothDamp(currentCameraOffset, phase1Offset, ref _phase1Velocity, phase1SmoothTime / speed);

        var phase2Offset = ApplySolver(Phase2, eyeTransform, ref currentCameraOffset, currentCameraOffset, eyePos, eyeMask, out var shifted2);
        if (RecordDiagnostics)
            Phase2Moved = (phase2Offset - currentCameraOffset).magnitude;
        Phase2Shifted = shifted2;
        currentCameraOffset = Vector3.SmoothDamp(currentCameraOffset, phase2Offset, ref _phase2Velocity, phase2SmoothTime / speed);

        var result = Enforce(eyeTransform, currentCameraOffset, eyePos, eyeMask);
        if (RecordDiagnostics)
            DesiredError = (result - desiredCameraOffset).magnitude;
        return result;
    }

    /// <summary>
    ///     Where phase 1 wanted the camera in the last Solve (its target before smoothing), in the pivot frame: where the
    ///     solve is heading, ahead of where its smoothing has got to.
    /// </summary>
    public Vector3 LastPhase1Target { get; private set; }

    /// <summary>
    ///     Phase 1's target for an offset without solving: no smoothing, no state touched. For asking where the ordinary
    ///     solve would go while it is not the one placing the camera.
    /// </summary>
    public Vector3 Phase1Target(Transform eyeTransform, Vector3 desiredCameraOffset, Vector3 eyePos, LayerMask eyeMask)
    {
        return eyeTransform.InverseTransformPoint(Phase1.Solve(eyeTransform, desiredCameraOffset, eyePos, eyeMask));
    }

    /// <summary>
    ///     Drops the smoothing velocities: the solve takes over from a camera something else was moving.
    /// </summary>
    public void ResetVelocity()
    {
        _phase1Velocity = _phase2Velocity = Vector3.zero;
    }

    /// <summary>
    ///     Phase 3 alone: the unsmoothed hard correction that keeps the offset out of geometry. Also for re-checking an offset
    ///     after the pivot frame was re-rotated past the full solve (the convergence hold's re-point).
    /// </summary>
    public Vector3 Enforce(Transform eyeTransform, Vector3 cameraOffset, Vector3 eyePos, LayerMask eyeMask)
    {
        var finalCameraPos = Phase3.Solve(eyeTransform, cameraOffset, eyePos, eyeMask);
        var result = eyeTransform.InverseTransformPoint(finalCameraPos);
        if (RecordDiagnostics)
            Phase3Moved = (result - cameraOffset).magnitude;
        return result;
    }

    private static Vector3 ApplySolver(
        AdvectionSolver solver, Transform eyeTransform, ref Vector3 currentCameraOffset, Vector3 desiredCameraOffset, Vector3 objectivePos,
        LayerMask layerMask, out bool shifted
    )
    {
        var solvedPosition = solver.Solve(eyeTransform, desiredCameraOffset, objectivePos, layerMask);

        // Handle possible colliders between the current camera position and the desired target and position ourselves on the target side.
        var cameraPos = eyeTransform.TransformPoint(currentCameraOffset);
        shifted = CameraShiftCollisionAvoidance(ref cameraPos, solvedPosition, layerMask);
        if (shifted)
        {
            currentCameraOffset = eyeTransform.InverseTransformPoint(cameraPos);
        }

        return eyeTransform.InverseTransformPoint(solvedPosition);
    }

    private static bool CameraShiftCollisionAvoidance(ref Vector3 currentCameraPos, Vector3 targetCameraPos, LayerMask eyeMask)
    {
        var movementVector = currentCameraPos - targetCameraPos;
        var distance = movementVector.magnitude;

        // Nothing to cross. The old code cast a zero-length ray here, which never hits.
        if (!(distance > 0f))
            return false;

        var result = CollisionUtils.Raycast(targetCameraPos, movementVector / distance, out var hitInfo, distance, eyeMask);

        if (result)
            currentCameraPos = hitInfo.point;

        return result;
    }
}
