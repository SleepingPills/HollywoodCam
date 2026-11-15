using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     The cosmetic recoil push of the upper body when a shot goes off: one spring, kicked once per shot, read as metres
///     of travel of the firing shoulder back along the barrel. The caller decides how hard each shot is (kick, 1 = an
///     unmodded assault rifle) and turns the push into bone rotations; this only holds the motion. Kept free of game types so it can be
///     reasoned about on its own.
/// </summary>
public class ShoulderRecoil
{
    // Shoulder travel at the first peak for kick 1 at strength 1
    public const float PushPerKick = 0.03f;
    public const float MinKick = 0.25f;
    public const float MaxKick = 3f;

    // About 40 ms to the peak, a forward rebound of about a tenth, 90% settled after about a quarter of a second: quick
    // enough to read as a shot, slower than the hands' own recoil so the torso reads as the heavier mass
    public const float Frequency = 4.5f;
    public const float Damping = 0.6f;

    // Soft clip at strength 1 (scaled with strength): linear up to the knee, then eased into the limit, so a single heavy
    // shot passes nearly whole while sustained automatic fire cannot walk the shoulder back without bound
    public const float KneeAtStrengthOne = 0.06f;
    public const float LimitAtStrengthOne = 0.09f;
    // The forward rebound is never clipped by the knee but is bounded to this share of it
    public const float MaxReboundShare = 0.3f;

    public const float MaxDeltaTime = 0.1f;

    private readonly float _omega;
    private readonly float _decayRate;
    private readonly float _dampedOmega;
    private readonly float _peakPerVelocity;

    private float _value;
    private float _velocity;
    private float _pendingVelocity;
    private float _strength = 1f;

    // Metres of shoulder travel along the barrel, positive back, soft clipped
    public float Push { get; private set; }
    // The kick of the last shot, after clamping, for the debug UI
    public float LastKick { get; private set; }

    public ShoulderRecoil()
    {
        _omega = 2f * Mathf.PI * Frequency;
        _decayRate = Damping * _omega;
        _dampedOmega = _omega * Mathf.Sqrt(1f - Damping * Damping);

        // From rest, x(t) = v0 / wd * exp(-a t) sin(wd t), which peaks at wd t = atan(wd / a)
        var peakTime = Mathf.Atan2(_dampedOmega, _decayRate) / _dampedOmega;
        _peakPerVelocity = Mathf.Exp(-_decayRate * peakTime) * Mathf.Sin(_dampedOmega * peakTime) / _dampedOmega;
    }

    /// <summary>
    ///     A shot. Takes effect on the next Step; several shots before it add up.
    /// </summary>
    public void AddShot(float kick, float strength)
    {
        if (!IsFinite(kick) || !IsFinite(strength) || !(strength > 0f))
            return;

        LastKick = Mathf.Clamp(kick, MinKick, MaxKick);
        _pendingVelocity += LastKick * strength * PushPerKick / _peakPerVelocity;
    }

    /// <summary>
    ///     Once per frame. strength is the setting as applied; it also scales the soft clip, so the shape is the same at
    ///     any strength.
    /// </summary>
    public void Step(float deltaTime, float strength)
    {
        if (!IsFinite(_value) || !IsFinite(_velocity) || !IsFinite(_pendingVelocity))
            Reset();

        _strength = IsFinite(strength) ? Mathf.Max(0f, strength) : 0f;

        _velocity += _pendingVelocity;
        _pendingVelocity = 0f;

        deltaTime = Mathf.Min(deltaTime, MaxDeltaTime);
        if (deltaTime > 0f)
        {
            // Exact solution of x'' = -w^2 x - 2 z w x' over the step: frame rate independent and unconditionally stable
            var decay = Mathf.Exp(-_decayRate * deltaTime);
            var cos = Mathf.Cos(_dampedOmega * deltaTime);
            var sin = Mathf.Sin(_dampedOmega * deltaTime);

            var value = decay * (_value * cos + (_velocity + _decayRate * _value) / _dampedOmega * sin);
            _velocity = decay * (_velocity * cos - (_decayRate * _velocity + _omega * _omega * _value) / _dampedOmega * sin);
            _value = value;
        }

        Push = SoftClip(_value);
    }

    /// <summary>
    ///     Back to rest, pending shots dropped: for when the push stops being applied (1st person, the game posing the body).
    /// </summary>
    public void Reset()
    {
        _value = _velocity = _pendingVelocity = 0f;
        Push = 0f;
    }

    private float SoftClip(float value)
    {
        var knee = KneeAtStrengthOne * _strength;
        var limit = LimitAtStrengthOne * _strength;

        if (value < 0f)
            return Mathf.Max(value, -MaxReboundShare * knee);

        if (value <= knee || !(limit > knee))
            return Mathf.Min(value, Mathf.Max(knee, 0f));

        var room = limit - knee;
        return knee + room * Tanh((value - knee) / room);
    }

    private static float Tanh(float x)
    {
        if (x > 10f)
            return 1f;

        var e = Mathf.Exp(2f * x);
        return (e - 1f) / (e + 1f);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
