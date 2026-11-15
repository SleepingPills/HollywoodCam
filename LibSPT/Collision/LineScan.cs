using UnityEngine;

namespace HollywoodCam.Collision;

public class LineScan(float length, float resolution)
{
    // Last scan, for the debug UI: the probe index returned, the probes on the line, whether it bailed on a good enough
    // probe (otherwise it returned the best one) and that probe's score
    public int LastStep { get; private set; }
    public int LastSteps { get; private set; }
    public bool LastBailed { get; private set; }
    public float LastScore { get; private set; }

    public Vector3 FindBestPosition(
        Transform eyeTransform, Vector3 cameraOffset, Vector3 endOffset, Vector3 objectivePos, float sphereCastRadius, LayerMask layerMask
        )
    {
        var epsilon = sphereCastRadius / 2;
        var endOffsetSized = length * endOffset;

        var lineStart = eyeTransform.TransformPoint(cameraOffset);
        var lineEnd = eyeTransform.TransformPoint(cameraOffset + endOffsetSized);

        var bestScore = -1f;
        var bestPosition = Vector3.zero;
        var bestStep = -1;

        var steps = (int)(endOffsetSized.magnitude / resolution);

        var normFactor = Mathf.Max(steps - 1f, 1f);

        LastSteps = steps;
        LastBailed = false;

        for (var i = 0; i < steps; i++)
        {
            var probePosition = Vector3.Lerp(lineStart, lineEnd, i / normFactor);
            var result = CollisionUtils.SphereCast(objectivePos, probePosition, sphereCastRadius, layerMask);

            // We found a good enough position, bail out immediately
            if (1 - result.Score <= epsilon)
            {
                LastStep = i;
                LastBailed = true;
                LastScore = result.Score;
                return result.TangentPoint;
            }

            if (result.Score <= bestScore) continue;

            bestScore = result.Score;
            bestPosition = result.TangentPoint;
            bestStep = i;
        }

        LastStep = bestStep;
        LastScore = bestScore;
        return bestPosition;
    }
}
