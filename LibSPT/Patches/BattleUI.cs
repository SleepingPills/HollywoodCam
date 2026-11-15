using System.Reflection;
using Comfort.Common;
using EFT;
using SPT.Reflection.Patching;

// ReSharper disable InconsistentNaming

namespace HollywoodCam.Patches;


public class BattleUIOnShowFireModePatch : ModulePatch
{
    // Must target Idling, NOT its base FirearmOperation: Idling *overrides* these methods, and a Harmony
    // patch on a base method does not intercept calls dispatched to the override.
    //
    // Targets ShowFireMode rather than CheckFireMode: ShowFireMode is the actual choke point that tests
    // Player.FirstPersonPointOfView and then invokes Player.OnShowFireMode (the UI callback), and it is
    // reached from BOTH CheckFireMode (inspect the selector) and ChangeFireMode (switch it). Wrapping only
    // CheckFireMode would leave the switch path unspoofed, so the text would stay hidden in 3rd person.
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player.FirearmController.Idling).GetMethod(nameof(Player.FirearmController.Idling.ShowFireMode));
    }

    [PatchPrefix]
    public static void Prefix(Player.FirearmController.Idling __instance)
    {
        if (__instance.Player != Singleton<GameWorld>.Instance.MainPlayer)
            return;
        
        PlayerPoVFuckery.OverridePoV = true;
    }
    
    [PatchFinalizer]
    public static void Finalizer(Player.FirearmController.Idling __instance)
    {
        if (__instance.Player != Singleton<GameWorld>.Instance.MainPlayer)
            return;
        
        PlayerPoVFuckery.OverridePoV = false;
    }
}

public class BattleUIOnShowAmmoPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player.FirearmController.Idling).GetMethod(nameof(Player.FirearmController.Idling.CheckAmmo));
    }

    [PatchPrefix]
    public static void Prefix(Player.FirearmController.Idling __instance)
    {
        if (__instance.Player != Singleton<GameWorld>.Instance.MainPlayer)
            return;
        
        PlayerPoVFuckery.OverridePoV = true;
    }
    
    [PatchFinalizer]
    public static void Finalizer(Player.FirearmController.Idling __instance)
    {
        if (__instance.Player != Singleton<GameWorld>.Instance.MainPlayer)
            return;
        
        PlayerPoVFuckery.OverridePoV = false;
    }
}

