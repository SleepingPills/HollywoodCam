using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     The procedural weight added to the walk and sprint cycle: how far the upper body dips, sways and sags, and how far
///     the head lags it. Driven by the pose itself rather than footstep events or a clock: the body is lowest when the
///     feet are furthest apart (around contact) and highest as they pass, so a dip that grows with the feet's separation
///     is in phase with the stride whatever the direction, and grows with the stride length. Springs toward those
///     targets give the lag and slight overshoot that read as weight.
///     All values at intensity 1; the caller scales the output. Kept free of game types so it can be reasoned about on
///     its own.
/// </summary>
public class GaitWeight
{
    // Dip (metres, positive down) = DipPerSquareMetre * separation^2, capped: about 2 cm at a 0.6 m walking stride.
    // The square keeps a narrow stance (feet passing, about 0.2 m apart) at a couple of millimetres.
    public const float DipPerSquareMetre = 0.02f / (0.6f * 0.6f);
    public const float MaxDipTarget = 0.045f;
    public const float DipFrequency = 4.5f;
    public const float DipDamping = 0.5f;

    // Forward pitch of the lower spine, proportional to the dip: 2.5 degrees at 3 cm
    public const float SagDegreesPerMetre = 2.5f / 0.03f;

    // The neck takes back this share of the sag on its own, slower spring, so the head settles after the chest
    public const float HeadCounterShare = 0.5f;
    public const float HeadFrequency = 3f;
    public const float HeadDamping = 0.7f;

    // Toward the lower (planted) foot: metres of sway per metre of foot height difference, capped under a centimetre
    public const float SwayPerMetre = 0.08f;
    public const float MaxSwayTarget = 0.008f;
    public const float SwayFrequency = 2.5f;
    public const float SwayDamping = 0.8f;

    // Landing: a downward kick sized so the spring's first excursion is about this many metres of dip per metre fallen
    // past the deadzone (the deadzone keeps small steps down from registering), capped
    public const float LandingDeadzone = 0.1f;
    public const float LandingDipPerMetre = 0.045f;
    public const float MaxLandingDip = 0.06f;

    // A long frame (hitch, load) is stepped as this long. The springs are stepped exactly, so this only bounds how much
    // motion one frame can carry, not stability.
    public const float MaxDeltaTime = 0.1f;

    // Hard limits on the output, overshoot and landing included
    public const float MinDip = -0.03f;
    public const float MaxDip = 0.09f;
    public const float MaxSway = 0.015f;
    public const float MaxSagDegrees = 8f;
    public const float MaxHeadDegrees = 5f;

    private Spring _dip = new(DipFrequency, DipDamping);
    private Spring _sway = new(SwayFrequency, SwayDamping);
    private Spring _head = new(HeadFrequency, HeadDamping);

    public float DipTarget { get; private set; }
    // Metres, positive down
    public float Dip { get; private set; }
    // Metres along the body's right axis
    public float Sway { get; private set; }
    // Degrees about the body's right axis, positive pitches forward
    public float Sag { get; private set; }
    // Degrees about the body's right axis applied to the neck, negative counters the sag
    public float Head { get; private set; }

    /// <summary>
    ///     footSeparation is the horizontal distance between the feet (metres); footHeightDifference is the right foot's
    ///     height minus the left's (metres). movementWeight is 0 standing still or in the air, 1 once properly moving.
    /// </summary>
    public void Step(float footSeparation, float footHeightDifference, float movementWeight, float deltaTime)
    {
        movementWeight = Mathf.Clamp01(movementWeight);
        if (!IsFinite(footSeparation) || !IsFinite(footHeightDifference))
            footSeparation = footHeightDifference = movementWeight = 0f;

        deltaTime = Mathf.Min(deltaTime, MaxDeltaTime);

        DipTarget = Mathf.Min(DipPerSquareMetre * footSeparation * footSeparation, MaxDipTarget) * movementWeight;
        var swayTarget = Mathf.Clamp(-SwayPerMetre * footHeightDifference, -MaxSwayTarget, MaxSwayTarget) * movementWeight;

        if (deltaTime > 0f)
        {
            _dip.Step(DipTarget, deltaTime);
            _sway.Step(swayTarget, deltaTime);
        }

        // Clamped into the state as well, so a clamp cannot leave a wound-up spring behind it
        Dip = _dip.Clamp(MinDip, MaxDip);
        Sway = _sway.Clamp(-MaxSway, MaxSway);
        Sag = Mathf.Clamp(Dip * SagDegreesPerMetre, -MaxSagDegrees, MaxSagDegrees);

        if (deltaTime > 0f)
            _head.Step(-HeadCounterShare * Sag, deltaTime);

        Head = _head.Clamp(-MaxHeadDegrees, MaxHeadDegrees);
    }

    /// <summary>
    ///     fallHeight in metres, from the highest point of the fall to the ground. Takes effect on the next Step.
    /// </summary>
    public void AddLanding(float fallHeight)
    {
        if (!IsFinite(fallHeight))
            return;

        var dip = Mathf.Min(Mathf.Max(0f, fallHeight - LandingDeadzone) * LandingDipPerMetre, MaxLandingDip);
        if (dip > 0f)
            _dip.Kick(dip);
    }

    /// <summary>
    ///     Back to rest, for when the motion stops being applied (1st person, the game posing the body).
    /// </summary>
    public void Reset()
    {
        _dip.Reset();
        _sway.Reset();
        _head.Reset();
        DipTarget = Dip = Sway = Sag = Head = 0f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    ///     Underdamped second order spring, x'' = w^2 (target - x) - 2 z w x', stepped with its exact solution for a target
    ///     held over the step, so the motion does not depend on the frame rate and cannot blow up.
    /// </summary>
    private struct Spring
    {
        private readonly float _omega;
        private readonly float _decayRate;
        private readonly float _dampedOmega;
        private float _value;
        private float _velocity;

        public Spring(float frequency, float damping)
        {
            // Underdamped only: the closed form below divides by the damped frequency
            damping = Mathf.Clamp(damping, 0.05f, 0.95f);
            _omega = 2f * Mathf.PI * frequency;
            _decayRate = damping * _omega;
            _dampedOmega = _omega * Mathf.Sqrt(1f - damping * damping);
            _value = _velocity = 0f;
        }

        public void Step(float target, float deltaTime)
        {
            if (!IsFinite(_value) || !IsFinite(_velocity))
                Reset();

            var error = _value - target;
            var decay = Mathf.Exp(-_decayRate * deltaTime);
            var cos = Mathf.Cos(_dampedOmega * deltaTime);
            var sin = Mathf.Sin(_dampedOmega * deltaTime);

            var newError = decay * (error * cos + (_velocity + _decayRate * error) / _dampedOmega * sin);
            _velocity = decay * (_velocity * cos - (_decayRate * _velocity + _omega * _omega * error) / _dampedOmega * sin);
            _value = target + newError;
        }

        /// <summary>
        ///     Adds the velocity whose first excursion from rest is about the given distance (positive along the value).
        /// </summary>
        public void Kick(float distance)
        {
            // From rest, x(t) = v0 / wd * exp(-a t) sin(wd t), which peaks at wd t = atan(wd / a)
            var peakTime = Mathf.Atan2(_dampedOmega, _decayRate) / _dampedOmega;
            var peakPerVelocity = Mathf.Exp(-_decayRate * peakTime) * Mathf.Sin(_dampedOmega * peakTime) / _dampedOmega;
            _velocity += distance / peakPerVelocity;
        }

        public float Clamp(float min, float max)
        {
            if (_value > max)
            {
                _value = max;
                _velocity = Mathf.Min(_velocity, 0f);
            }
            else if (_value < min)
            {
                _value = min;
                _velocity = Mathf.Max(_velocity, 0f);
            }

            return _value;
        }

        public void Reset()
        {
            _value = _velocity = 0f;
        }
    }
}
