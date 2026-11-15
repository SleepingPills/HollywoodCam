using System;
using UnityEngine;

namespace HollywoodCam.Collision;

/// <summary>
///     Moves the camera as a sphere: from where it is toward a goal, stopping at what it meets and sliding along it, the way
///     a character controller moves, and moves it back out when it is found overlapping something.
///     A sphere cast does not see a collider it starts touching, and a camera that starts a move touching a surface passes
///     into it. So a move first checks it starts clear (the cast reports touching colliders at distance zero, which stops the
///     move), it keeps a skin between the sphere and what it stopped at, and overlaps are resolved rather than moved through.
///     The local player's own colliders (body, hands, weapon) are never obstacles.
/// </summary>
public sealed class CameraSweep
{
    // Slides per move: a surface takes one, a crease (two surfaces) a second along the line where they meet, a third
    // covers a corner
    private const int MaxSlides = 3;
    // Kept off the surface a move stopped at. 5 mm let the sphere end up touching rough game meshes (brick, rubble), after
    // which the next move started blind; 2 cm leaves room for a mesh's bumps and for float error.
    public const float Skin = 0.02f;
    private const float MinMove = 1e-5f;
    private const float MinCreaseSqr = 1e-6f;
    // Hits and overlaps looked at per query: the nearest that is not our own is used, the player's own colliders can fill
    // several entries
    private const int BufferSize = 16;
    // Depenetration rounds: each pushes out of every overlapping collider by its own penetration, which can push into a
    // neighbour; three settle a crease
    private const int DepenetrationRounds = 3;

    private readonly Func<Collider, bool> _isOwn;
    private readonly RaycastHit[] _hits = new RaycastHit[BufferSize];
    private readonly Collider[] _overlaps = new Collider[BufferSize];
    // A sphere of the camera's size for ComputePenetration, which needs a collider; parked far below the world as a trigger
    // on the Ignore Raycast layer, so nothing ever meets it. The pose given to ComputePenetration is what counts.
    private SphereCollider _probe;

    // What the last move stopped at, and the first collider the last overlap test found: references, recorded for the
    // debug UI and logging when something goes wrong
    public Collider LastBlocker { get; private set; }
    public Collider LastOverlap { get; private set; }

    public CameraSweep(Func<Collider, bool> isOwn)
    {
        _isOwn = isOwn;
    }

    public void Destroy()
    {
        if (_probe != null)
            UnityEngine.Object.Destroy(_probe.gameObject);
        _probe = null;
    }

    /// <summary>
    ///     Toward to, sliding along what it meets. Stops where it is if it starts touching something (the caller resolves
    ///     that). On a second surface it slides only along the line where the two meet, so a camera pushed into a crease
    ///     (wall and floor) stays in it without creeping into either.
    /// </summary>
    public Vector3 Move(Vector3 from, Vector3 to, float radius, int layerMask)
    {
        LastBlocker = null;
        var position = from;
        var firstNormal = Vector3.zero;

        for (var i = 0; i < MaxSlides; i++)
        {
            var move = to - position;
            var distance = move.magnitude;
            if (distance < MinMove)
                break;

            var direction = move / distance;
            if (!Cast(position, radius, direction, distance, layerMask, out var hit))
                return to;

            LastBlocker = hit.collider;
            // Touching at the start: a move from here would not see it
            if (hit.distance <= 0f)
                return position;

            position += direction * Mathf.Max(0f, hit.distance - Skin);

            var rest = to - position;
            if (i == 0)
            {
                firstNormal = hit.normal;
                rest -= hit.normal * Vector3.Dot(rest, hit.normal);
            }
            else
            {
                var crease = Vector3.Cross(firstNormal, hit.normal);
                if (crease.sqrMagnitude < MinCreaseSqr)
                    break;

                crease.Normalize();
                rest = crease * Vector3.Dot(rest, crease);
            }

            to = position + rest;
        }

        return position;
    }

    /// <summary>
    ///     How many colliders, not our own, the sphere overlaps; the first is kept in LastOverlap.
    /// </summary>
    public int Overlap(Vector3 position, float radius, int layerMask)
    {
        CollisionUtils.CastCount++;
        var count = Physics.OverlapSphereNonAlloc(position, radius, _overlaps, layerMask, QueryTriggerInteraction.Ignore);
        var foreign = 0;
        LastOverlap = null;
        for (var i = 0; i < count; i++)
        {
            if (_isOwn(_overlaps[i]))
                continue;

            if (foreign == 0)
                LastOverlap = _overlaps[i];
            foreign++;
        }

        return foreign;
    }

    /// <summary>
    ///     Pushes the sphere out of whatever it overlaps, by the penetration Unity computes against each collider, plus the
    ///     skin. True if it ends clear having moved no more than maxPush; otherwise position is left as it was. A sphere
    ///     touching a surface needs at most its radius plus the skin; a longer push means its centre was inside a solid,
    ///     and the nearest way out can be the far side of a wall. ComputePenetration does not support every collider
    ///     (terrain); those are left to the caller's fallback.
    /// </summary>
    public bool Depenetrate(ref Vector3 position, float radius, int layerMask, float maxPush)
    {
        var start = position;
        if (DepenetrateUnbounded(ref position, radius, layerMask) && (position - start).sqrMagnitude <= maxPush * maxPush)
            return true;

        position = start;
        return false;
    }

    private bool DepenetrateUnbounded(ref Vector3 position, float radius, int layerMask)
    {
        var probe = GetProbe(radius);

        for (var round = 0; round < DepenetrationRounds; round++)
        {
            CollisionUtils.CastCount++;
            var count = Physics.OverlapSphereNonAlloc(position, radius, _overlaps, layerMask, QueryTriggerInteraction.Ignore);
            var moved = false;
            var foreign = false;

            for (var i = 0; i < count; i++)
            {
                var collider = _overlaps[i];
                if (_isOwn(collider))
                    continue;

                foreign = true;
                var other = collider.transform;
                if (Physics.ComputePenetration(probe, position, Quaternion.identity, collider, other.position, other.rotation, out var direction, out var depth))
                {
                    position += direction * (depth + Skin);
                    moved = true;
                }
            }

            if (!foreign)
                return true;
            if (!moved)
                return false;
        }

        return Overlap(position, radius, layerMask) == 0;
    }

    /// <summary>
    ///     The nearest hit along the cast that is not our own. Colliders the sphere starts touching come back at distance
    ///     zero, which is what tells Move it started blind.
    /// </summary>
    private bool Cast(Vector3 origin, float radius, Vector3 direction, float distance, int layerMask, out RaycastHit nearest)
    {
        CollisionUtils.CastCount++;
        var count = Physics.SphereCastNonAlloc(origin, radius, direction, _hits, distance, layerMask, QueryTriggerInteraction.Ignore);
        nearest = default;
        var found = false;

        for (var i = 0; i < count; i++)
        {
            var hit = _hits[i];
            if (_isOwn(hit.collider))
                continue;

            if (!found || hit.distance < nearest.distance)
            {
                nearest = hit;
                found = true;
            }
        }

        return found;
    }

    private SphereCollider GetProbe(float radius)
    {
        if (_probe == null)
        {
            var go = new GameObject("HollywoodCamSweepProbe") { hideFlags = HideFlags.HideAndDontSave, layer = IgnoreRaycastLayer };
            go.transform.position = new Vector3(0f, -10000f, 0f);
            _probe = go.AddComponent<SphereCollider>();
            _probe.isTrigger = true;
        }

        _probe.radius = radius;
        return _probe;
    }

    // Unity's built-in layer that queries skip by default
    private const int IgnoreRaycastLayer = 2;
}
