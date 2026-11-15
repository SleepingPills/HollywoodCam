using System.Reflection;
using Comfort.Common;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine;

namespace HollywoodCam.Patches;

/*
 * Free aim: the mouse turns the camera rig and the player is turned to follow it (see ThirdPersonView.IsCapturingLook).
 *
 * The delta is substituted inside the game's own mouse look Player.Rotate call rather than calling Rotate a second time:
 * a second call per frame would reset PreviousRotation and skew the rotation speed tracking that feeds sprint speed and
 * turn sounds.
 */
public static class LookInput
{
    // Set while MoveInputTranslator dispatches the local player's axes, so only the mouse look Rotate call is touched
    public static bool LocalDispatch;

    // Set by the Rotate prefix when it replaced the delta, tells the postfix to reconcile
    public static bool Substituted;

    // Player._lastMovement before a substituted call whose only content was a convergence correction; NaN when the
    // player's own mouse moved and the game's stamp stands
    public static float RestoreLastMovement = float.NaN;
}

public class MoveInputTranslatorTranslateAxesPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MoveInputTranslator).GetMethod(nameof(MoveInputTranslator.TranslateAxes));
    }

    [PatchPrefix]
    // ReSharper disable once InconsistentNaming
    public static void Prefix(MoveInputTranslator __instance)
    {
        var view = Singleton<ThirdPersonView>.Instance;
        LookInput.LocalDispatch = view != null && __instance._controller == view.localPlayer;
    }

    [PatchFinalizer]
    public static void Finalizer()
    {
        // Always, so a throw inside TranslateAxes or Rotate cannot leave the scope open for other Rotate callers
        LookInput.LocalDispatch = false;
        LookInput.Substituted = false;
        LookInput.RestoreLastMovement = float.NaN;
    }
}

public class PlayerRotatePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.Rotate));
    }

    [PatchPrefix]
    // ReSharper disable InconsistentNaming
    public static void Prefix(Player __instance, ref Vector2 deltaRotation, ref bool ignoreClamp, float ____lastMovement)
    {
        // Runs for every bot every frame, the static flag keeps the common case to a single branch
        if (!LookInput.LocalDispatch)
            return;

        var view = Singleton<ThirdPersonView>.Instance;
        if (view == null || __instance != view.localPlayer || !view.IsCapturingLook)
            return;

        if (!view.OnLookInput(deltaRotation, out var aimDelta, out var hadInput))
            return;

        // The view already scaled and clamped the input exactly as the state would have
        deltaRotation = aimDelta;
        ignoreClamp = true;
        LookInput.Substituted = true;

        // Rotate stamps _lastMovement for any non-zero delta, and with convergence there is a small correction almost
        // every frame. That stamp feeds MovementIdlingTime (the weapon idle animation), so undo it unless the mouse moved.
        LookInput.RestoreLastMovement = hadInput ? float.NaN : ____lastMovement;
    }

    [PatchPostfix]
    // ReSharper disable once InconsistentNaming
    public static void Postfix(Player __instance, ref float ____lastMovement)
    {
        if (!LookInput.Substituted)
            return;

        LookInput.Substituted = false;

        if (!float.IsNaN(LookInput.RestoreLastMovement))
        {
            ____lastMovement = LookInput.RestoreLastMovement;
            LookInput.RestoreLastMovement = float.NaN;
        }

        var view = Singleton<ThirdPersonView>.Instance;
        if (view == null || __instance != view.localPlayer)
            return;

        view.OnLookApplied();
    }
}
