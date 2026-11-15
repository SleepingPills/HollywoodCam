using System;
using EFT;
using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     Puts GaitWeight on the local player's skeleton: reads the feet and movement, moves Spine1 (dip, sway, sag) and
///     rotates the neck (head lag), and puts both bones back before the next animator pass.
///
///     Applied right after Player.FBBIKUpdate on VisualPass's full path (see Patches/Camera.cs): after the full body
///     IK and grounder have run, so they neither undo nor reshape it, and before the hands object is placed at the
///     ribcage, so the weapon, shoulders and arm IK targets follow the chest. Only the spine from Spine1 up moves; the
///     pelvis and legs are left alone because the grounder would not keep the feet on the ground under a lowered pelvis.
///
///     Each touched bone's local pose is cached right before it is written and written back in ThirdPersonView.Update,
///     which runs before the animator (body update mode Auto, Update queue). A bone the clips do not key would otherwise
///     keep the offset into the next frame and accumulate it.
/// </summary>
public class GaitPoser
{
    // How fast the master weight and the intensity ease, per second
    public const float WeightRate = 4f;
    // Horizontal speed (m/s) below which the stride is not driven at all, and from which it is driven fully. A wide
    // idle or crouch stance must not sag, the slowest walks are a little under 1 m/s.
    public const float MoveSpeedMin = 0.3f;
    public const float MoveSpeedFull = 1f;
    // Master weight scales: aiming (the weapon and sight dip with the chest) and full crouch
    public const float AimingScale = 0.3f;
    public const float FullCrouchScale = 0.5f;
    // Same as the pivot frame's minimum for reading a yaw off the body's forward vector
    private const float MinForwardSqr = 1e-8f;

    private readonly GaitWeight _gait = new();
    private readonly Action<float, float> _onGrounded;
    private MovementContext _groundedSource;
    private float _pendingFall;

    private float _master;
    private float _intensity;
    private int _lastStepFrame = -1;

    // The pose put back by Restore
    private bool _restorePending;
    private Transform _spine;
    private Transform _neck;
    private Vector3 _spineLocalPosition;
    private Quaternion _spineLocalRotation;
    private Quaternion _neckLocalRotation;

    private Quaternion _lastBodyYaw = Quaternion.identity;

    // Debug UI
    public float FootSeparation { get; private set; }
    public float MovementWeight { get; private set; }
    public float Master => _master;
    public float Intensity => _intensity;
    public bool Airborne { get; private set; }
    // Why the master weight is held or easing to 0, null when it is not
    public string Suppression { get; private set; } = "Not run";
    public GaitWeight Gait => _gait;

    public GaitPoser(float intensity)
    {
        _intensity = Mathf.Clamp(intensity, 0f, 2f);
        _onGrounded = OnGrounded;
    }

    /// <summary>
    ///     Puts back the pose the last Apply cached. ThirdPersonView.Update, before the animator; also the backstop at the
    ///     start of Apply and on destroy.
    /// </summary>
    public void Restore()
    {
        if (!_restorePending)
            return;

        _restorePending = false;

        if (_spine != null)
        {
            _spine.localPosition = _spineLocalPosition;
            _spine.localRotation = _spineLocalRotation;
        }

        if (_neck != null)
            _neck.localRotation = _neckLocalRotation;
    }

    public void Dispose()
    {
        Restore();
        Subscribe(null);
    }

    /// <summary>
    ///     A frame on which the motion cannot be applied at all (1st person, the game skipping the body pass): no bone
    ///     writes, and the next applied frame fades in from rest.
    /// </summary>
    public void Hold(MovementContext movementContext, string reason)
    {
        Restore();
        Subscribe(movementContext);
        _pendingFall = 0f;
        _master = 0f;
        _gait.Reset();
        FootSeparation = MovementWeight = 0f;
        Suppression = reason;
    }

    /// <summary>
    ///     Once per frame, right after the full body IK on VisualPass's full path. fadeReason non-null eases the master weight to 0;
    ///     scale is the master weight's target otherwise (aiming, crouch).
    /// </summary>
    public void Apply(Player player, string fadeReason, float scale, float intensitySetting)
    {
        var frame = Time.frameCount;
        if (frame == _lastStepFrame)
            return;

        // Backstop: should Update not have run since the last Apply, the offsets must not stack
        Restore();

        var movementContext = player.MovementContext;
        Subscribe(movementContext);

        // A gap (frames on which this did not run) means the springs and weight are stale; fade in from rest
        if (frame - _lastStepFrame > 1)
        {
            _master = 0f;
            _gait.Reset();
        }

        _lastStepFrame = frame;

        var deltaTime = Time.deltaTime;
        var step = WeightRate * deltaTime;
        _intensity = Mathf.MoveTowards(_intensity, Mathf.Clamp(intensitySetting, 0f, 2f), step);

        // Off: not even measured, so nothing is touched
        if (!(_intensity > 0f))
        {
            _pendingFall = 0f;
            _master = 0f;
            _gait.Reset();
            FootSeparation = MovementWeight = 0f;
            Suppression = "Off";
            return;
        }

        var bones = player.PlayerBones;
        var spine = bones != null ? bones.Spine1 : null;
        var neck = bones != null ? bones.Neck : null;
        var body = bones != null ? bones.BodyTransform?.Original : null;
        if (spine == null || neck == null || body == null)
        {
            Hold(movementContext, "Missing bones");
            return;
        }

        var hasFeet = TryGetFeet(player, out var leftFoot, out var rightFoot);
        if (fadeReason == null && !hasFeet)
            fadeReason = "Missing feet";

        Suppression = fadeReason;
        _master = Mathf.MoveTowards(_master, fadeReason == null ? Mathf.Clamp01(scale) : 0f, step);

        var separation = 0f;
        var heightDifference = 0f;
        if (hasFeet)
        {
            var leftPosition = leftFoot.position;
            var rightPosition = rightFoot.position;
            // A distance, so the body's yaw frame does not change it
            var dx = rightPosition.x - leftPosition.x;
            var dz = rightPosition.z - leftPosition.z;
            separation = Mathf.Sqrt(dx * dx + dz * dz);
            heightDifference = rightPosition.y - leftPosition.y;
        }

        FootSeparation = separation;

        var state = movementContext.CurrentState;
        var stateName = state != null ? state.Name : EPlayerState.None;
        Airborne = !movementContext.IsGrounded || stateName == EPlayerState.Jump || stateName == EPlayerState.FallDown;

        // In the air or with nothing to measure, the stride targets go to rest and the springs settle on their own,
        // still live for the landing kick
        var velocity = movementContext.Velocity;
        var speed = Mathf.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z);
        MovementWeight = Airborne || fadeReason != null ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(MoveSpeedMin, MoveSpeedFull, speed));

        if (_pendingFall > 0f && !Airborne)
        {
            _gait.AddLanding(_pendingFall);
            _pendingFall = 0f;
        }

        _gait.Step(separation, heightDifference, MovementWeight, deltaTime);

        var weight = _master * _intensity;
        if (!(weight > 0f))
            return;

        _spine = spine;
        _neck = neck;
        _spineLocalPosition = spine.localPosition;
        _spineLocalRotation = spine.localRotation;
        _neckLocalRotation = neck.localRotation;
        _restorePending = true;

        // Body yaw frame, as the pivot frame reads it: only the heading, whatever the body's pitch
        var forward = body.rotation * Vector3.forward;
        if (forward.x * forward.x + forward.z * forward.z > MinForwardSqr)
            _lastBodyYaw = Quaternion.Euler(0f, Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 0f);

        var right = _lastBodyYaw * Vector3.right;

        // World space, so the bones' local axes do not matter. Spine1 first: the neck's world rotation then includes the sag.
        spine.SetPositionAndRotation(
            spine.position + Vector3.down * (_gait.Dip * weight) + right * (_gait.Sway * weight),
            Quaternion.AngleAxis(_gait.Sag * weight, right) * spine.rotation
        );
        neck.rotation = Quaternion.AngleAxis(_gait.Head * weight, right) * neck.rotation;
    }

    private static bool TryGetFeet(Player player, out Transform leftFoot, out Transform rightFoot)
    {
        leftFoot = rightFoot = null;

        var grounder = player.Grounder;
        var ik = grounder != null ? grounder.ik : null;
        var references = ik != null ? ik.references : null;
        if (references == null)
            return false;

        leftFoot = references.leftFoot;
        rightFoot = references.rightFoot;
        return leftFoot != null && rightFoot != null;
    }

    private void Subscribe(MovementContext movementContext)
    {
        if (movementContext == _groundedSource)
            return;

        if (_groundedSource != null)
            _groundedSource.OnGrounded -= _onGrounded;

        _groundedSource = movementContext;

        if (movementContext != null)
            movementContext.OnGrounded += _onGrounded;
    }

    /// <summary>
    ///     MovementContext.CheckFlying (Update), on the first grounded frame after being in the air. fallHeight is from
    ///     the highest point of the flight.
    /// </summary>
    private void OnGrounded(float fallHeight, float jumpHeight)
    {
        if (fallHeight > _pendingFall)
            _pendingFall = fallHeight;
    }
}
