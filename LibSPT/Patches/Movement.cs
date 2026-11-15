using System.Reflection;
using Comfort.Common;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine;

namespace HollywoodCam.Patches;

/*
 * The sidestep state reads the trunk rotation limit once, on Enter, to clamp the look yaw to the body ± (limit - 1) for
 * as long as the sidestep lasts. The torso deadzone (ThirdPersonView.UpdateTorsoDeadzone) rewrites that limit every frame
 * and ramps it down to a couple of degrees while the legs re-centre, so a sidestep started mid turn would all but lock
 * the look, and a deadzone of 0 would invert the range. Give the sidestep the game's own limit for that one read; the
 * next deadzone update puts ours back.
 */
public class SideStepPlayerStateEnterPrefixPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SideStepPlayerState).GetMethod(nameof(SideStepPlayerState.Enter));
    }

    [PatchPrefix]
    // ReSharper disable once InconsistentNaming
    public static void Prefix(SideStepPlayerState __instance)
    {
        var view = Singleton<ThirdPersonView>.Instance;
        if (view == null || __instance.MovementContext != view.localPlayer.MovementContext)
            return;

        __instance.MovementContext.TrunkRotationLimit = EFTHardSettings.Instance.HANDS_TO_BODY_MAX_ANGLE;
    }
}

/*
 * Moving torso deadzone (see MovingDeadzone and ThirdPersonView.IsMovingDeadzoneFor). Only the local player's plain
 * walking state is touched: the sprint and prone-move states (and Fika's observed states) are subclasses reaching the
 * same methods, hence the exact type test, which is also the cheap first check for every bot.
 *
 * The legs: the walking state lerps _smoothedYawRotation to the look yaw within a few frames and turns the body to it.
 * Here the controller advances it instead; writing the result back to the same field keeps a hand-back to the original
 * seamless at any frame.
 */
public class MovePlayerStateUpdateRotationPrefixPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MovePlayerState).GetMethod(nameof(MovePlayerState.UpdateRotation));
    }

    [PatchPrefix]
    // ReSharper disable once InconsistentNaming
    public static bool Prefix(MovePlayerState __instance, float deltaTime)
    {
        if (__instance.GetType() != typeof(MovePlayerState))
            return true;

        var view = Singleton<ThirdPersonView>.Instance;
        var movementContext = __instance.MovementContext;
        if (view == null || !view.IsMovingDeadzoneFor(movementContext))
            return true;

        // As the original: feeds the rotation speed tracking (sprint speed, turn sounds)
        __instance.UpdateRotationSpeed(deltaTime);
        __instance._smoothedYawRotation = view.StepWalkingLegs(__instance._smoothedYawRotation, movementContext.Yaw, deltaTime);
        movementContext.ApplyRotation(Quaternion.AngleAxis(__instance._smoothedYawRotation, Vector3.up) * movementContext.AnimatorDeltaRotation);
        return false;
    }
}

/*
 * The travel direction: the walking state turns the body-local input into a world direction through the body, smooths
 * it, and converts it back through the body for the animator. With the legs lagging, converting through the look yaw
 * instead keeps travel where the aim points and hands the animator the matching body-relative (strafing) direction. The
 * gameplay checks read the raw input, so they see nothing. The original's result is turned by the look's yaw offset
 * from the body, which keeps its exact convention (transform scale included) and is the original at zero lag.
 */
public class MovePlayerStateConvertDirectionFromLocalToGlobalPrefixPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MovePlayerState).GetMethod(nameof(MovePlayerState.ConvertDirectionFromLocalToGlobal));
    }

    [PatchPrefix]
    // ReSharper disable InconsistentNaming
    public static bool Prefix(MovePlayerState __instance, Vector2 direction, ref Vector2 __result)
    {
        if (__instance.GetType() != typeof(MovePlayerState))
            return true;

        var view = Singleton<ThirdPersonView>.Instance;
        var movementContext = __instance.MovementContext;
        if (view == null || !view.IsMovingDeadzoneFor(movementContext))
            return true;

        var playerTransform = movementContext.PlayerTransform;
        var world = playerTransform.TransformVector(new Vector3(direction.x, 0f, direction.y));
        // Same body yaw reading as MovementContext.ApplyRotation's hands-to-body angle
        var offset = Mathf.DeltaAngle(playerTransform.eulerAngles.y, movementContext.Yaw);
        world = Quaternion.AngleAxis(offset, Vector3.up) * world;

        __result = new Vector2(world.x, world.z);
        return false;
    }
}
