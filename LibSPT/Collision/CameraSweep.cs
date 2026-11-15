using UnityEngine;

namespace HollywoodCam.Collision;

/// <summary>
///     Moves the camera as a sphere: from where it is toward a goal, stopping at what it meets and sliding along it, the way
///     a character controller moves. A camera moved only like this from a position in free space stays in free space: a
///     sweep that starts outside a collider sees its surface.
/// </summary>
public static class CameraSweep
{
    // Slides per move: a corner takes two, a third covers a crease
    private const int MaxSlides = 3;
    // Kept off the surface it stopped at, so the next sweep does not start touching it (and then would not see it)
    private const float Skin = 0.005f;
    private const float MinMove = 1e-5f;

    public static Vector3 Move(Vector3 from, Vector3 to, float radius, int layerMask)
    {
        var position = from;

        for (var i = 0; i < MaxSlides; i++)
        {
            var move = to - position;
            var distance = move.magnitude;
            if (distance < MinMove)
                break;

            var direction = move / distance;
            if (!CollisionUtils.SphereCastHit(position, radius, direction, out var hit, distance, layerMask))
                return to;

            position += direction * Mathf.Max(0f, hit.distance - Skin);

            // What is left of the move, with the part into the surface taken out
            var rest = to - position;
            to = position + (rest - hit.normal * Vector3.Dot(rest, hit.normal));
        }

        return position;
    }
}
