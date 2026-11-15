using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Animations;
using SPT.Reflection.Patching;
using UnityEngine;

namespace HollywoodCam.Patches;

/*
 * The shoulder recoil push (ShoulderRecoilPoser). Both hooks are non-virtual and neither is overridden or patched by
 * Fika, but both run for every bot and every Fika remote player, so each checks for the local player before anything
 * else.
 */

/// <summary>
///     The signal: every firearm shot the game gives recoil to (rifles, shotguns, launchers, flares, rockets) comes
///     through here, from FirearmController.ManualLateUpdate at the end of VisualPass, with that shot's recoil already
///     computed. Melee and grenades never do.
/// </summary>
public class ProceduralWeaponAnimationShootPostfixPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProceduralWeaponAnimation).GetMethod(nameof(ProceduralWeaponAnimation.Shoot));
    }

    [PatchPostfix]
    // ReSharper disable once InconsistentNaming
    public static void Postfix(ProceduralWeaponAnimation __instance, float str)
    {
        var localPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        if (localPlayer == null || __instance != localPlayer.ProceduralWeaponAnimation)
            return;

        // Shoot itself does nothing with the shooting mask off, so neither does the push
        if ((__instance.Mask & EProceduralAnimationMask.Shooting) == 0)
            return;

        var view = Singleton<ThirdPersonView>.Instance;
        if (view != null)
            view.OnShotFired(__instance, str);
    }
}

/// <summary>
///     The write point: Player.IkProcess runs on VisualPass's full path after the hands object is placed at the ribcage
///     and the upper arms are pinned to the hands rig, and right before the arm IK solves the hands onto the weapon.
///     _firstPersonRightHand is set just before it; below 1 the game moves the weapon to the right palm after the IK.
/// </summary>
public class PlayerIkProcessPrefixPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.IkProcess));
    }

    [PatchPrefix]
    // ReSharper disable InconsistentNaming
    public static void Prefix(Player __instance, float ____firstPersonRightHand, Transform[] ____markers)
    // ReSharper restore InconsistentNaming
    {
        // The static first: Fika's remote players call IkProcess from their own visual pass
        if (!LocalVisualPass.Running || __instance != Singleton<GameWorld>.Instance.MainPlayer)
            return;

        var view = Singleton<ThirdPersonView>.Instance;
        if (view != null)
            view.OnArmIkStarting(____firstPersonRightHand, ____markers);
    }
}
