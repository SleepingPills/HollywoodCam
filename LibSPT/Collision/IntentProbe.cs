using System;
using UnityEngine;

namespace HollywoodCam.Collision;

/// <summary>
///     The local character's body as simple shapes, for telling whether a sight line passes through it. The camera's hit
///     masks leave the character's own colliders out, so physics never reports it.
/// </summary>
public struct BodyShape
{
    public bool Valid;
    public Vector3 Head;
    public float HeadRadius;
    public Vector3 TorsoTop;
    public Vector3 TorsoBottom;
    public float TorsoRadius;
}

public enum TargetView : byte
{
    NotChecked,
    // Nothing in the way
    Visible,
    // Blocked by geometry near the character or the camera: the only result that counts as a miss
    Geometry,
    // Behind the character's own body: normal 3rd person framing, counted as seen
    Character,
    // Nothing in the way within the near range, or the first thing in the way is beyond it: parallax, counted as seen
    Far,
    // The sight line only touched the sample's own surface on the way (a wall or floor seen at a shallow angle, a pipe on
    // it): counted as seen
    Graze
}

/// <summary>
///     One level's sight test: the misses that decide it, and how every sample checked was classified, for the debug UI.
/// </summary>
public struct SightResult
{
    public int Misses;
    public int Geometry;
    public int Character;
    public int Far;
    public int Graze;
    public int Unchecked;
}

/// <summary>
///     What the character sees, sampled as points, and whether a given camera position is stuck behind something near the
///     character that hides it. The samples are cast from the eye along the aim, so they are what the character would see
///     in 1st person whatever the camera is doing; a sample is the first surface the ray meets (a few centimetres short of
///     it). That keeps the measure independent of the camera: the aim point itself would not do, it is the first hit of
///     the camera's own centre ray and so is always visible from the camera, a door jamb included.
///     Samples: the centre ray, two points along it short of its hit (the space directly ahead), an inner ring of four rays
///     close around it (the target's vicinity) and six outer rays well to either side (the two halves of the screen, which
///     is where a doorway's wall shows when the camera is still behind it).
///     Only near obstructions count. The camera sits half a metre to the side and a metre and a half back from the eye, so
///     by plain parallax it cannot see past every edge the eye sees past; that is not a tight space. A sight line counts
///     as blocked only by something within NearRadius of the line from the camera to the eye: the cast is not even made
///     past that distance, and a hit further from that line is ignored.
/// </summary>
public class IntentProbe
{
    public const int InnerCount = 4;
    public const int OuterCount = 6;
    public const int NearCount = 2;
    public const int MaxTargets = 1 + NearCount + InnerCount + OuterCount;

    // The inner ring's angle from the centre ray: at 5 m about 0.6 m around the target
    public const float InnerAngle = 7f;
    // The outer rays: this far to either side, at three heights. 20 degrees is well into each half of the screen but
    // still inside a doorway's opening seen from a metre or so before it, where the doorway case has to start.
    public const float OuterYaw = 20f;
    private static readonly float[] OuterPitches = [-8f, 0f, 8f];
    // Far enough for any room; open ground is visible either way
    public const float MaxDistance = 30f;
    // Samples are pulled back this far from the surface they hit, so the camera's sight line ends in front of it
    public const float SurfaceBackoff = 0.05f;
    // A sight line blocked this close to its sample still counts as reaching it
    public const float ReachTolerance = 0.1f;
    // Points along the centre ray, in metres from the eye, used only when the centre hit is NearMargin beyond them
    private static readonly float[] NearDistances = [1f, 2f];
    private const float NearMargin = 0.2f;

    // An obstruction counts only within this of the line from the camera to the eye. A doorway's jamb is within half a
    // metre of it from the moment the eye is in the opening until the camera is through; furniture across a room is
    // metres away. Sight casts stop at the camera-to-eye distance plus this, beyond which nothing can be that near.
    public const float NearRadius = 1.5f;

    // A hit within this of the sample's own surface plane, on a surface facing the same way, is that surface itself
    // (seen at a shallow angle, or a trim or pipe on it), not something in front of it
    public const float SameSurfaceTolerance = 0.15f;
    public const float SameSurfaceNormalDot = 0.7f;

    // Misses per sample: what is under the crosshair counts double
    public const int CentreWeight = 2;
    public const int OtherWeight = 1;

    // Own colliders a ray may step past before giving up (as AimSolver), and how far past each it restarts
    private const int MaxOwnHits = 4;
    private const float OwnHitSkip = 0.01f;

    private const float MinSightLength = 1e-3f;

    private readonly int _mask;
    private readonly Func<Collider, bool> _isOwn;
    private readonly Func<Collider, bool> _isSeeThrough;

    // Sample order: centre, near points, inner ring, outer rays. Evaluate checks them in this order, most important first.
    public readonly Vector3[] Targets = new Vector3[MaxTargets];
    private readonly int[] _weights = new int[MaxTargets];
    private readonly bool[] _onSurface = new bool[MaxTargets];
    private readonly Vector3[] _surfacePoints = new Vector3[MaxTargets];
    private readonly Vector3[] _surfaceNormals = new Vector3[MaxTargets];
    public int Count { get; private set; }
    public int NearSamples { get; private set; }
    public int TotalWeight { get; private set; }
    // Distance from the eye to the centre sample's surface (MaxDistance when the ray hit nothing)
    public float CentreDistance { get; private set; }

    /// <param name="mask">Layers that block sight</param>
    /// <param name="isOwn">The local player's own colliders, stepped past</param>
    /// <param name="isSeeThrough">Colliders a sight line passes (chain link, grates, glass); the sample counts as seen</param>
    public IntentProbe(int mask, Func<Collider, bool> isOwn, Func<Collider, bool> isSeeThrough)
    {
        _mask = mask;
        _isOwn = isOwn;
        _isSeeThrough = isSeeThrough;
    }

    /// <summary>
    ///     Casts the samples from the eye along the aim rotation. The centre first: if its surface is closer than
    ///     stopIfCloser, returns false without casting the rest (facing a surface, there is nothing to see past). Otherwise
    ///     1 + InnerCount + OuterCount casts, plus one per own collider stepped past.
    /// </summary>
    public bool Sample(Vector3 eye, Quaternion aim, float stopIfCloser)
    {
        Count = 0;
        NearSamples = 0;
        TotalWeight = 0;

        var forward = aim * Vector3.forward;
        var centreHit = Cast(eye, forward, MaxDistance, out var hit);
        CentreDistance = centreHit ? hit.distance : MaxDistance;
        Add(eye, forward, centreHit, hit, CentreWeight);

        if (CentreDistance < stopIfCloser)
            return false;

        for (var i = 0; i < NearDistances.Length; i++)
        {
            if (!(CentreDistance > NearDistances[i] + NearMargin))
                continue;

            AddPoint(eye + forward * NearDistances[i], OtherWeight);
            NearSamples++;
        }

        for (var i = 0; i < InnerCount; i++)
        {
            var around = i * (360f / InnerCount) * Mathf.Deg2Rad;
            CastAndAdd(eye, aim, InnerAngle * Mathf.Sin(around), InnerAngle * Mathf.Cos(around));
        }

        for (var side = -1; side <= 1; side += 2)
        {
            for (var i = 0; i < OuterPitches.Length; i++)
                CastAndAdd(eye, aim, OuterPitches[i], side * OuterYaw);
        }

        return true;
    }

    /// <summary>
    ///     How many samples (weighted) the camera misses because of something near the character or the camera. Stops as
    ///     soon as the level can no longer miss more than allowedMisses (the rest are left NotChecked), since the caller
    ///     then only needs to know it passed; otherwise every sample is checked and the count is exact, unless it passes
    ///     abortAbove first (the caller has no use for the exact count of a position that has already lost). One cast per
    ///     sample not behind the character, plus one per own collider stepped past. views, when given, receives each
    ///     sample's classification; only the debug UI draws it, so callers pass null otherwise.
    /// </summary>
    public SightResult Evaluate(Vector3 camera, Vector3 eye, int allowedMisses, in BodyShape body, TargetView[] views, int abortAbove = int.MaxValue)
    {
        var result = new SightResult();

        if (views != null)
        {
            for (var i = 0; i < MaxTargets; i++)
                views[i] = TargetView.NotChecked;
        }

        var remaining = TotalWeight;
        var nearLimit = (eye - camera).magnitude + NearRadius;

        for (var i = 0; i < Count; i++)
        {
            if (result.Misses + remaining <= allowedMisses || result.Misses > abortAbove)
            {
                result.Unchecked = Count - i;
                break;
            }

            var view = Check(camera, eye, i, nearLimit, body);
            if (views != null)
                views[i] = view;
            remaining -= _weights[i];

            switch (view)
            {
                case TargetView.Geometry:
                    result.Geometry++;
                    result.Misses += _weights[i];
                    break;
                case TargetView.Character:
                    result.Character++;
                    break;
                case TargetView.Far:
                    result.Far++;
                    break;
                case TargetView.Graze:
                    result.Graze++;
                    break;
            }
        }

        return result;
    }

    private void CastAndAdd(Vector3 eye, Quaternion aim, float pitch, float yaw)
    {
        var direction = aim * (Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);
        var surface = Cast(eye, direction, MaxDistance, out var hit);
        Add(eye, direction, surface, hit, OtherWeight);
    }

    private void Add(Vector3 eye, Vector3 direction, bool surface, RaycastHit hit, int weight)
    {
        var distance = surface ? hit.distance : MaxDistance;
        Targets[Count] = eye + direction * Mathf.Max(0f, distance - SurfaceBackoff);
        _weights[Count] = weight;
        _onSurface[Count] = surface;
        _surfacePoints[Count] = surface ? hit.point : Vector3.zero;
        _surfaceNormals[Count] = surface ? hit.normal : Vector3.zero;
        TotalWeight += weight;
        Count++;
    }

    private void AddPoint(Vector3 point, int weight)
    {
        Targets[Count] = point;
        _weights[Count] = weight;
        _onSurface[Count] = false;
        TotalWeight += weight;
        Count++;
    }

    private TargetView Check(Vector3 camera, Vector3 eye, int index, float nearLimit, in BodyShape body)
    {
        var target = Targets[index];

        if (body.Valid && (Geometry.SegmentPointDistance(camera, target, body.Head) < body.HeadRadius
                           || Geometry.SegmentSegmentDistance(camera, target, body.TorsoTop, body.TorsoBottom) < body.TorsoRadius))
            return TargetView.Character;

        var sight = target - camera;
        var length = sight.magnitude;
        var reach = length - ReachTolerance;
        if (!(length > MinSightLength) || !(reach > 0f))
            return TargetView.Visible;

        var castLength = Mathf.Min(reach, nearLimit);
        if (!Cast(camera, sight / length, castLength, out var hit))
            return castLength < reach ? TargetView.Far : TargetView.Visible;

        if (Geometry.SegmentPointDistance(camera, eye, hit.point) > NearRadius)
            return TargetView.Far;

        if (_isSeeThrough(hit.collider))
            return TargetView.Visible;

        if (_onSurface[index]
            && Mathf.Abs(Vector3.Dot(hit.point - _surfacePoints[index], _surfaceNormals[index])) <= SameSurfaceTolerance
            && Vector3.Dot(hit.normal, _surfaceNormals[index]) >= SameSurfaceNormalDot)
            return TargetView.Graze;

        return TargetView.Geometry;
    }

    /// <summary>
    ///     How many samples have a clear line to a point (up to its radius short of it), counting until need is reached. The
    ///     samples are points the eye sees, so in free space: a point they see is in that same free space, which a cast
    ///     from the point itself could not prove (it does not see the back faces of a mesh it starts inside). mask is what
    ///     blocks (the camera's collision mask, so glass counts). One cast per sample checked.
    /// </summary>
    public int CountSeenBy(Vector3 point, float radius, int mask, int need)
    {
        var seen = 0;
        for (var i = 0; i < Count && seen < need; i++)
        {
            var line = point - Targets[i];
            var length = line.magnitude;
            var reach = length - radius;
            if (!(reach > 0f) || !Cast(Targets[i], line / length, reach, mask, out _))
                seen++;
        }

        return seen;
    }

    private bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit hit)
    {
        return Cast(origin, direction, maxDistance, _mask, out hit);
    }

    /// <summary>
    ///     Nearest hit that is not our own, with its distance from origin.
    /// </summary>
    private bool Cast(Vector3 origin, Vector3 direction, float maxDistance, int mask, out RaycastHit hit)
    {
        var travelled = 0f;

        for (var i = 0; i < MaxOwnHits && travelled < maxDistance; i++)
        {
            if (!CollisionUtils.Raycast(origin + direction * travelled, direction, out hit, maxDistance - travelled, mask))
                return false;

            if (!_isOwn(hit.collider))
            {
                hit.distance += travelled;
                return true;
            }

            travelled += hit.distance + OwnHitSkip;
        }

        hit = default;
        return false;
    }
}
