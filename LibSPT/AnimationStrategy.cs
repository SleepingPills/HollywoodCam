using EFT;
using EFT.Animations;

namespace HollywoodCam;

/// <summary>
///     Replaces the game's ThirdPersonStrategy for the local player in 3rd person (see ProceduralWeaponAnimationSetStrategyPrefixPatch),
///     so the weapon keeps its 1st person behaviour. Wraps FirstPersonStrategy and delegates to it, except:
///     1. ApplyTransformations uses ApplyComplexRotation (as FirstPersonStrategy does, ThirdPersonStrategy uses ApplySimpleRotation),
///        which is what gives the gun recoil and sway, but skips AvoidObstacles so walls do not push the gun away.
///     2. ApplyCameraTransformations is deliberately a no-op: the mod drives the camera itself.
/// </summary>
public class CustomAnimStrategy : IProceduralAnimationStrategy
{
    private readonly FirstPersonStrategy _wrapped = new();

    public void ProcessEffectors(ProceduralWeaponAnimation pwa, float deltaTime, int nFixedFrames = 1)
    {
        _wrapped.ProcessEffectors(pwa, deltaTime, nFixedFrames);
    }

    public void ApplyTransformations(ProceduralWeaponAnimation pwa, float dt)
    {
        pwa.ZeroAdjustments();
        pwa.UpdateAimWeight(dt);
        pwa.BlendAnimatorPose(dt);
        pwa.ApplyPosition();
        // NB: This line enables the proper recoil response. In the regular third person strategy, BSG uses ApplySimpleRotation
        pwa.ApplyComplexRotation(dt);
        pwa.ApplyTacticalReloadTransformations();
        // pwa.AvoidObstacles();
    }

    public void ApplyCameraTransformations(ProceduralWeaponAnimation pwa, float dt)
    {
        // Deliberately a no-op, matching BSG's ThirdPersonStrategy (whose ApplyCameraTransformations is empty).
        //
        // This strategy only ever runs in 3rd person, where HollywoodCam drives the camera itself via
        // ThirdPersonView.UpdateCamera. Delegating to FirstPersonStrategy here would run
        // CalculateCameraPosition + LerpCamera and move the real camera, fighting the mod's own camera --
        // which shows up as violent shaking during reloads, when tactical-reload transforms are largest.
        //
        // NB: in 1.2.0 this method never actually ran. The strategy hijack tested `strategy is GClass908`,
        // which the 4.0->4.1 mapping shows was BetterValProcessor (an ordinary object, not a strategy), so
        // the check could never be true. Correcting it to ThirdPersonStrategy activated this path for the
        // first time and exposed the conflict.
    }

    public void LateTransformations(ProceduralWeaponAnimation pwa, float dt)
    {
        _wrapped.LateTransformations(pwa, dt);
    }

    public void ApplyFovAdjustments(ProceduralWeaponAnimation pwa, Player player)
    {
        _wrapped.ApplyFovAdjustments(pwa, player);
    }

    public void ResetFovAdjustments(ProceduralWeaponAnimation proceduralWeaponAnimation, Player player)
    {
        _wrapped.ResetFovAdjustments(proceduralWeaponAnimation, player);
    }

    public void OpticCalibration(ProceduralWeaponAnimation proceduralWeaponAnimation, bool calibrate)
    {
        _wrapped.OpticCalibration(proceduralWeaponAnimation, calibrate);
    }

    public float UpdatePossibleTilt(ProceduralWeaponAnimation proceduralWeaponAnimation, float smoothedCharacterMovementSpeed, float smoothedPoseLevel)
    {
        return _wrapped.UpdatePossibleTilt(proceduralWeaponAnimation, smoothedCharacterMovementSpeed, smoothedPoseLevel);
    }
}