using UnityEngine;

namespace HollywoodCam;

public static class Geometry
{
    public static Vector3 ClosestPointOnLine(Vector3 origin, Vector3 target, Vector3 point)
    {
        var vec1 = point - origin;
        var vec2 = (target - origin).normalized;

        var d = Vector3.Distance(origin, target);
        var t = Vector3.Dot(vec2, vec1);

        if (t <= 0)
            return origin;

        if (t >= d)
            return target;

        var vec3 = vec2 * t;

        return origin + vec3;
    }

    public static float SegmentPointDistance(Vector3 a, Vector3 b, Vector3 point)
    {
        var ab = b - a;
        var lengthSqr = ab.sqrMagnitude;
        var t = lengthSqr > 0f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSqr) : 0f;
        return (a + ab * t - point).magnitude;
    }

    /// <summary>
    ///     Closest distance between segments p1-q1 and p2-q2 (the standard clamped solution).
    /// </summary>
    public static float SegmentSegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        const float epsilon = 1e-8f;

        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = Vector3.Dot(d1, d1);
        var e = Vector3.Dot(d2, d2);
        var f = Vector3.Dot(d2, r);

        float s, t;
        if (a <= epsilon && e <= epsilon)
            return r.magnitude;

        if (a <= epsilon)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= epsilon)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denominator = a * e - b * b;
                s = denominator > epsilon ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                t = (b * s + f) / e;

                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }

        return (p1 + d1 * s - (p2 + d2 * t)).magnitude;
    }
}
