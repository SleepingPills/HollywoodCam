using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace HollywoodCam.Patches;

public class PlayerShotReactionsPostfixPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.ShotReactions));
    }

    [PatchPostfix]
    // ReSharper disable once InconsistentNaming
    public static void Postfix(Player __instance)
    {
        if (!__instance.IsYourPlayer)
            return;

        // We just loop through here and manually molest the HitPoint.force for each hitpoint in the recoil list. This should allow us to rotate & amplify the force
        foreach (var hitPoint in __instance.HitReaction.boneHitPoints)
        {
            hitPoint.force *= Plugin.FlinchScale.Value;
        }

        foreach (var hitPoint in __instance.HitReaction.effectorHitPoints)
        {
            hitPoint.force *= Plugin.FlinchScale.Value;
        }
    }
}

public class PlayerConstructorPostfixPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetConstructor(BindingFlags.Instance | BindingFlags.Public, null, [], null);
    }

    [PatchPostfix]
    // ReSharper disable once InconsistentNaming
    public static void Postfix(Player __instance)
    {
        Traverse.Create(__instance).Field("_fbbikCooldown").SetValue(4f);

        // Runs for every bot spawned, so only with debug logging on
        if (!Plugin.LoggingEnabled)
            return;

        var val = Traverse.Create(__instance).Field("_fbbikCooldown").GetValue<float>();
        Plugin.Log.LogInfo($"IK Cooldown: {val}");
    }
}

// The old SlotViewChangedPostfixPatch (which patched EquipmentSlotClass.method_4 to force-enable renderers)
// has been replaced by ThirdPersonView.SyncHolsteredEquipmentVisibility, called each frame from UpdateCamera.
// A one-shot postfix on model creation races with the draw/holster model rebuild and leaves the primary
// visible on your back while it's in your hands; see the comment on that method for the full ordering.