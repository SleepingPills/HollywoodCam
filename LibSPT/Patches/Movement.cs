using System.Reflection;
using Comfort.Common;
using SPT.Reflection.Patching;

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
