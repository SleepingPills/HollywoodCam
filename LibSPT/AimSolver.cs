using System.Collections.Generic;
using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     Aim point convergence: finds the world point under the centre of the screen and the yaw/pitch that puts the
///     weapon's line of fire through it. The barrel is modelled feed-forward (rest direction = look forward, muzzle at a
///     low-passed offset from the ribcage), so recoil and sway are never solved against.
/// </summary>
public class AimSolver
{
    public const float FarDistance = 1000f;

    // Long enough that recoil kick and sway do not feed back into the aim, short enough to follow ADS and stance changes
    private const float MuzzleOffsetTimeConstant = 0.25f;
    // Keeps 1/distance finite if the camera ends up right at the aim origin
    private const float MinRayDistance = 0.05f;
    private const float DegenerateSqrLength = 1e-8f;
    private const int SolveIterations = 2;
    private const int MaxOwnHits = 8;
    private const float OwnHitSkip = 0.01f;

    private readonly int _hitMask;

    // What counts as the local player's own geometry, refreshed by the caller before casting
    private HashSet<Collider> _ignoredColliders;
    private Transform _ignoredRootA;
    private Transform _ignoredRootB;

    private bool _hasMuzzleOffset;
    private Vector3 _muzzleOffset;

    private bool _hasSmoothed;
    private float _smoothedInverseDistance;

    // Last results, for the debug UI
    public float RawDistance { get; private set; }
    public float SmoothedDistance { get; private set; }
    public Vector3 AimPoint { get; private set; }
    public Vector3 MuzzleOffset => _muzzleOffset;

    public AimSolver(int hitMask)
    {
        _hitMask = hitMask;
    }

    public void SetIgnored(HashSet<Collider> colliders, Transform rootA, Transform rootB)
    {
        _ignoredColliders = colliders;
        _ignoredRootA = rootA;
        _ignoredRootB = rootB;
    }

    /// <summary>
    ///     Feeds one target for the muzzle offset (the fireport in the hands root's rotation frame, or zero without a
    ///     firearm). Snaps on the very first sample only; afterwards always low-passes, weapon swaps included, so a
    ///     controller change turns the character smoothly instead of in one frame.
    /// </summary>
    public void SampleMuzzleOffset(Vector3 target, float deltaTime)
    {
        if (!IsFinite(target))
            return;

        if (!_hasMuzzleOffset)
        {
            _hasMuzzleOffset = true;
            _muzzleOffset = target;
            return;
        }

        _muzzleOffset = Vector3.Lerp(_muzzleOffset, target, 1f - Mathf.Exp(-deltaTime / MuzzleOffsetTimeConstant));
    }

    /// <summary>
    ///     The next UpdateAimPoint snaps to the raw distance instead of smoothing from a stale one.
    /// </summary>
    public void ResetSmoothing()
    {
        _hasSmoothed = false;
    }

    public void SnapDistance(float distance)
    {
        distance = Mathf.Max(distance, MinRayDistance);
        _smoothedInverseDistance = 1f / distance;
        _hasSmoothed = true;
        RawDistance = SmoothedDistance = distance;
    }

    /// <summary>
    ///     The aim point on the camera's centre ray: nearest foreign hit beyond the aim origin, pushed out to the minimum
    ///     convergence distance and smoothed in 1/distance (the parallax correction is proportional to it).
    /// </summary>
    public Vector3 UpdateAimPoint(Vector3 cameraPosition, Vector3 cameraForward, Vector3 aimOrigin, float minDistance, float speed, float deltaTime)
    {
        // Start level with the character, geometry between the camera and the character is never what we aim at
        var start = Mathf.Max(0f, Vector3.Dot(aimOrigin - cameraPosition, cameraForward));
        var raw = start + Cast(cameraPosition + cameraForward * start, cameraForward, FarDistance - start);

        var clamp = Mathf.Max(DistanceAlongRayAtRange(cameraPosition, cameraForward, aimOrigin, minDistance), MinRayDistance);
        raw = Mathf.Max(raw, clamp);
        RawDistance = raw;

        var rawInverse = 1f / raw;
        if (!_hasSmoothed || float.IsNaN(_smoothedInverseDistance))
        {
            _smoothedInverseDistance = rawInverse;
            _hasSmoothed = true;
        }
        else
        {
            _smoothedInverseDistance += (rawInverse - _smoothedInverseDistance) * (1f - Mathf.Exp(-speed * deltaTime));
        }

        // Re-clamped: interpolating between two clamped distances can still pass closer than the minimum
        SmoothedDistance = Mathf.Max(1f / _smoothedInverseDistance, clamp);
        AimPoint = cameraPosition + cameraForward * SmoothedDistance;

        return AimPoint;
    }

    /// <summary>
    ///     The point the barrel currently points at, cast from the modelled muzzle along the look direction, with the same
    ///     ignore rules, far fallback and minimum distance as the camera's aim point.
    /// </summary>
    public Vector3 CastAimPoint(Vector3 muzzle, Vector3 direction, float minDistance)
    {
        var distance = Mathf.Max(Cast(muzzle, direction, FarDistance), minDistance, MinRayDistance);
        return muzzle + direction * distance;
    }

    /// <summary>
    ///     Where a ray actually lands: nearest hit that is not the local player, or FarDistance along it. No minimum
    ///     distance, so it shows a close wall where it is (the crosshair).
    /// </summary>
    public Vector3 CastHitPoint(Vector3 origin, Vector3 direction)
    {
        return origin + direction * Cast(origin, direction, FarDistance);
    }

    /// <summary>
    ///     Yaw/pitch (x = yaw, y = pitch, degrees) such that the line from (ribcage + rotation * muzzleOffset) along
    ///     rotation * forward passes through target. Fixed-point iteration from start; it contracts by roughly
    ///     |muzzleOffset| / distance per step, and the minimum distance keeps that well below 1.
    /// </summary>
    public static bool SolveConvergence(Vector3 ribcage, Vector3 muzzleOffset, Vector3 target, Vector2 start, out Vector2 result)
    {
        result = start;

        for (var i = 0; i < SolveIterations; i++)
        {
            var muzzle = ribcage + Quaternion.Euler(result.y, result.x, 0f) * muzzleOffset;
            if (!TryYawPitch(target - muzzle, result.x, out result))
                return false;
        }

        return CameraRig.IsFinite(result);
    }

    /// <summary>
    ///     Yaw/pitch of a direction in the layout of Quaternion.Euler(pitch, yaw, 0) * forward. Keeps fallbackYaw when the
    ///     direction is vertical.
    /// </summary>
    public static bool TryYawPitch(Vector3 direction, float fallbackYaw, out Vector2 yawPitch)
    {
        yawPitch = default;

        var sqrLength = direction.sqrMagnitude;
        if (!(sqrLength > DegenerateSqrLength) || float.IsInfinity(sqrLength))
            return false;

        direction /= Mathf.Sqrt(sqrLength);

        var pitch = -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
        var yaw = direction.x * direction.x + direction.z * direction.z > DegenerateSqrLength
            ? Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg
            : fallbackYaw;

        yawPitch = new Vector2(yaw, pitch);
        return CameraRig.IsFinite(yawPitch);
    }

    /// <summary>
    ///     Distance along the ray from origin to the nearest hit that is not the local player, or maxDistance.
    /// </summary>
    private float Cast(Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (!(maxDistance > 0f))
            return 0f;

        // Nearest-hit casts, stepping past our own colliders. A multi-hit cast into a fixed buffer returns an arbitrary
        // subset once the buffer is full, and a long ray across a dense map can cross more colliders than any sane buffer.
        var travelled = 0f;

        for (var i = 0; i < MaxOwnHits && travelled < maxDistance; i++)
        {
            if (!Physics.Raycast(origin + direction * travelled, direction, out var hit, maxDistance - travelled, _hitMask, QueryTriggerInteraction.Ignore))
                return maxDistance;

            travelled += hit.distance;
            if (!IsIgnored(hit.collider))
                return travelled;

            // Restart just inside the collider we own: a ray does not hit a collider it starts inside
            travelled += OwnHitSkip;
        }

        return maxDistance;
    }

    private bool IsIgnored(Collider collider)
    {
        if (collider == null)
            return true;

        if (_ignoredColliders != null && _ignoredColliders.Contains(collider))
            return true;

        if (_ignoredRootA == null && _ignoredRootB == null)
            return false;

        var root = collider.transform.root;
        return root == _ignoredRootA || root == _ignoredRootB;
    }

    /// <summary>
    ///     How far along the ray it has to go to be at least range away from point (0 if it never comes closer).
    /// </summary>
    private static float DistanceAlongRayAtRange(Vector3 origin, Vector3 direction, Vector3 point, float range)
    {
        // |origin + direction * t - point| = range, the far root
        var w = origin - point;
        var b = Vector3.Dot(w, direction);
        var discriminant = b * b - (w.sqrMagnitude - range * range);

        return discriminant > 0f ? -b + Mathf.Sqrt(discriminant) : 0f;
    }

    private static bool IsFinite(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
               && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }
}
