using UnityEngine;

namespace HollywoodCam.Collision;

public struct SphereCastResult(float score, Vector3 tangentPoint)
{
    public readonly float Score = score;
    public readonly Vector3 TangentPoint = tangentPoint;
}

public static class CollisionUtils
{
    /// <summary>
    ///     Every physics query the camera solve makes goes through this class and bumps this counter, so the debug UI can show
    ///     the measured cost of a frame by taking the difference across a section. A plain increment, so it is kept with the
    ///     debug UI off; it wraps after 2^31 queries, which a difference taken in the same frame does not notice.
    /// </summary>
    public static int CastCount;

    public static SphereCastResult SphereCast(Vector3 originPos, Vector3 targetPos, float radius, LayerMask layerMask)
    {
        var aimVector = targetPos - originPos;
        var aimVectorMagnitude = aimVector.magnitude;

        if (aimVectorMagnitude == 0)
            return new SphereCastResult(0f, originPos);

        CastCount++;

        // Triggers ignored: a trigger volume is not something the camera can be occluded by or clip into, and the default
        // (UseGlobal) would follow the project's queriesHitTriggers setting, which we do not control
        if (!Physics.SphereCast(originPos, radius, aimVector.normalized, out var hitInfo, aimVectorMagnitude, layerMask, QueryTriggerInteraction.Ignore))
            return new SphereCastResult(1f, targetPos);

        var tangentPoint = Geometry.ClosestPointOnLine(originPos, targetPos, hitInfo.point);

        // Square root to concentrate most of the effect to low values and react less to differences between higher values
        var score = Mathf.Sqrt((tangentPoint - originPos).magnitude / aimVectorMagnitude);

        return new SphereCastResult(score, tangentPoint);
    }


    /// <summary>
    ///     A counted sphere cast that never hits triggers. Like every cast it does not see a collider it starts inside.
    /// </summary>
    public static bool SphereCastHit(Vector3 origin, float radius, Vector3 direction, out RaycastHit hit, float maxDistance, int layerMask)
    {
        CastCount++;
        return Physics.SphereCast(origin, radius, direction, out hit, maxDistance, layerMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    ///     A counted overlap test that never sees triggers. It finds geometry the sphere touches, not a sphere wholly inside
    ///     a closed mesh, which has no surface near it.
    /// </summary>
    public static bool CheckSphere(Vector3 position, float radius, int layerMask)
    {
        CastCount++;
        return Physics.CheckSphere(position, radius, layerMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    ///     A counted raycast that never hits triggers.
    /// </summary>
    public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance, int layerMask)
    {
        CastCount++;
        return Physics.Raycast(origin, direction, out hit, maxDistance, layerMask, QueryTriggerInteraction.Ignore);
    }
}
