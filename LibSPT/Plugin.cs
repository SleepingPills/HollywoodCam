using System.Diagnostics.CodeAnalysis;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HollywoodCam.Patches;
using UnityEngine;

namespace HollywoodCam;

public enum PointOfViewEnum
{
    FirstPerson,
    ThirdPerson
}

public enum CameraPositionEnum
{
    Main,
    Shoulder,
}

public enum CameraStanceEnum
{
    Left = -1,
    Right = 1
}

public enum GunStanceSyncEnum
{
    Cam,
    Lean,
    None
}

public enum AdsModeEnum
{
    FirstPerson,
    Shoulder,
    None
}

[BepInPlugin("com.janky.hollywoodcam", "Janky-HollywoodCam", HollywoodCamVersion)]
[SuppressMessage("ReSharper", "HeapView.ObjectAllocation.Evident")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class Plugin : BaseUnityPlugin
{
    public const string HollywoodCamVersion = "1.2.0";

    public static ManualLogSource Log;

    public static ConfigEntry<PointOfViewEnum> PointOfViewDefault;
    public static ConfigEntry<KeyCode> ThirdPersonToggleKey;

    public static ConfigEntry<CameraPositionEnum> CameraPositionDefault;
    public static ConfigEntry<Vector3> CameraMainOffset;
    public static ConfigEntry<Vector3> CameraShoulderOffset;
    public static ConfigEntry<Vector3> CameraGrenadeOffset;
    public static ConfigEntry<KeyCode> CameraShoulderKey;
    public static ConfigEntry<float> CameraMoveSpeed;
    public static ConfigEntry<float> BodyBob;
    public static ConfigEntry<float> CameraFollowDeadzone;
    public static ConfigEntry<float> CameraFollowSmoothing;
    public static ConfigEntry<bool> CameraTightSpace;
    public static ConfigEntry<float> CameraTightSpaceClosest;
    public static ConfigEntry<bool> CameraTightSpaceOverHead;
    public static ConfigEntry<bool> CameraTightSpaceOpening;
    public static ConfigEntry<float> AimConvergenceMinDistance;
    public static ConfigEntry<float> AimConvergenceSpeed;
    public static ConfigEntry<float> FreeLookMaxAngle;

    public static ConfigEntry<AdsModeEnum> AdsModeOptic;
    public static ConfigEntry<AdsModeEnum> AdsModeBasic;
    public static ConfigEntry<bool> AdsModeRememberEnabled;
    public static ConfigEntry<KeyCode> AdsModeSwapKey;
    public static ConfigEntry<float> AdsThirdPersonSensitivity;
    public static ConfigEntry<int> AdsBasicFovChange;
    public static ConfigEntry<float> AdsFovChangeTime;

    public static ConfigEntry<bool> CrosshairEnabled;
    public static ConfigEntry<bool> CrosshairAdsOnlyEnabled;
    public static ConfigEntry<Color> CrosshairColor;
    public static ConfigEntry<float> CrosshairThickness;

    public static ConfigEntry<CameraStanceEnum> CameraStanceDefault;
    public static ConfigEntry<KeyCode> CameraStanceToggleKey;
    public static ConfigEntry<KeyCode> CameraStanceLeftKey;
    public static ConfigEntry<KeyCode> CameraStanceRightKey;
    public static ConfigEntry<bool> CameraStanceSwapOnLeanEnabled;
    public static ConfigEntry<GunStanceSyncEnum> GunStanceSync;
    public static ConfigEntry<bool> GunStanceReset;

    public static ConfigEntry<int> SprintFovChange;
    public static ConfigEntry<float> SprintFovChangeTime;

    public static ConfigEntry<float> InteractionRayRadius;
    public static ConfigEntry<float> FlinchScale;
    public static ConfigEntry<float> TorsoAimDeadzone;
    public static ConfigEntry<float> BodyTurnSpeed;
    public static ConfigEntry<float> MovingTorsoDeadzone;
    public static ConfigEntry<float> MovingRecentreSpeed;
    public static ConfigEntry<float> GaitWeight;
    public static ConfigEntry<float> ShoulderRecoil;
    public static ConfigEntry<float> InteractionRange;
    
    public static ConfigEntry<bool> DebugUIEnabled;
    private static ConfigEntry<bool> _loggingEnabled;

    // For log lines that would otherwise fire per player or bot
    public static bool LoggingEnabled => _loggingEnabled != null && _loggingEnabled.Value;

    // The slowest Cam Movement Speed the solver applies. The setting goes down to 0, which divided into the smoothing times
    // gave an infinite smoothing time and froze the camera offset where it was; 0.1 is a 2 s ease, as slow as is still usable.
    public const float MinCameraMoveSpeed = 0.1f;

    // Cam Movement Speed as the smoothing times are divided by it
    public static float CameraMoveSpeedScale => Mathf.Max(MinCameraMoveSpeed, CameraMoveSpeed.Value);

    private void Awake()
    {
        Log = Logger;

        SetupConfig();

        // Lifecycle
        new GameWorldStartedPostfixPatch().Enable();
        new PlayerDisposePrefixPatch().Enable();
        new PlayerOnDeadPrefixPatch().Enable();

        // UI
        new BattleUIOnShowAmmoPatch().Enable();
        new BattleUIOnShowFireModePatch().Enable();

        // Camera
        new PlayerPointOfViewPrefixPatch().Enable();
        new PlayerVisualPassPatch().Enable();
        new PlayerCameraControllerLateUpdatePrefixPatch().Enable();
        new ProceduralWeaponAnimationSetStrategyPrefixPatch().Enable();
        new PlayerFBBIKUpdatePostfixPatch().Enable();
        new ProceduralWeaponAnimationShootPostfixPatch().Enable();
        new PlayerIkProcessPrefixPatch().Enable();

        // Look
        new MoveInputTranslatorTranslateAxesPatch().Enable();
        new PlayerRotatePatch().Enable();
        new PlayerResetLookDirectionPatch().Enable();

        // Movement
        new SideStepPlayerStateEnterPrefixPatch().Enable();
        new MovePlayerStateUpdateRotationPrefixPatch().Enable();
        new MovePlayerStateConvertDirectionFromLocalToGlobalPrefixPatch().Enable();

        // Player
        new PlayerConstructorPostfixPatch().Enable();
        new PlayerShotReactionsPostfixPatch().Enable();

        // Lean
        new PlayerOnLeanPostfixPatch().Enable();

        // Sound
        new BaseSoundPlayerPointOfViewPrefixPatch().Enable();

        // Sensitivity
        new FirearmControllerUpdateSensitivityPrefixPatchPatch().Enable();

        // Interaction
        new GameWorldFindInteractablePrefixPatch().Enable();
        new PlayerInteractionRayPrefixPatch().Enable();

        if (_loggingEnabled.Value)
        {
            Log.LogInfo("Logging enabled");
        }
        else
        {
            Log.LogInfo("Logging disabled");
            BepInEx.Logging.Logger.Sources.Remove(Log);
        }
    }

    private void SetupConfig()
    {
        const string headerPerspective = "1. Perspective";
        const string headerAiming = "2. Aiming";
        const string headerStance = "3. Stance Control";
        const string headerCamera = "4. Camera";
        const string headerCrosshair = "5. Crosshair";
        const string headerSprint = "6. Sprint Camera";
        const string headerMisc = "7. Misc Flotsam";
        const string headerDebug = "8. Debug";

        PointOfViewDefault = Config.Bind(headerPerspective, "Default PoV", PointOfViewEnum.ThirdPerson, new ConfigDescription(
            "The default PoV to use at the start of the raid.",
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        ThirdPersonToggleKey = Config.Bind(headerPerspective, "3rd Person Toggle Key", KeyCode.None, new ConfigDescription(
            "Set the key that will toggle between first and third person view.",
            tags: new ConfigurationManagerAttributes { Order = 1 }
        ));

        AdsModeOptic = Config.Bind(headerAiming, "Optic Sight ADS Mode", AdsModeEnum.FirstPerson, new ConfigDescription(
            "Determines the ADS logic for magnifying optic sights.",
            tags: new ConfigurationManagerAttributes { Order = 7 }
        ));
        AdsModeBasic = Config.Bind(headerAiming, "Basic Sight ADS Mode", AdsModeEnum.Shoulder, new ConfigDescription(
            "Determines the ADS logic for non-optic sights (this is iron, holo, reflex, etc...).",
            tags: new ConfigurationManagerAttributes { Order = 6 }
        ));
        AdsModeRememberEnabled = Config.Bind(headerAiming, "Remember ADS Mode", true, new ConfigDescription(
            "Toggles whether the ADS mode selected is remembered when using the ADS Mode Swap Key. Turn this off if you want ADS mode to always " +
            "revert to what is configured here.",
            tags: new ConfigurationManagerAttributes { Order = 5 }
        ));
        AdsModeSwapKey = Config.Bind(headerAiming, "ADS Mode Swap Key", KeyCode.None, new ConfigDescription(
            "Switches between First Person and Shoulder Cam ADS for the currently equipped weapon.",
            tags: new ConfigurationManagerAttributes { Order = 4 }
        ));
        AdsThirdPersonSensitivity = Config.Bind(headerAiming, "ADS 3rd Person Sensitivity", 0.75f, new ConfigDescription(
            "Sensitivity multiplier for 3rd person ADS. The Live Tarkov default is 0.75.",
            new AcceptableValueRange<float>(0.1f, 5f),
            tags: new ConfigurationManagerAttributes { Order = 3 }
        ));
        AdsBasicFovChange = Config.Bind(headerAiming, "3rd Person ADS FoV Change", -15, new ConfigDescription(
            "How much to change the FoV during ADS in third person. This is relative to the baseline FoV. Negative values will zoom in. " +
            "The BSG default is -15, in other words, the FoV is reduced by 15 degrees.",
            new AcceptableValueRange<int>(-100, 100),
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        AdsFovChangeTime = Config.Bind(headerAiming, "ADS FoV Change Time", 1f, new ConfigDescription(
            "The timespan (in seconds) that it takes to adjust the FoV for ADS. The BSG default is 1 second.",
            new AcceptableValueRange<float>(0f, 5f),
            tags: new ConfigurationManagerAttributes { Order = 1 }
        ));

        CameraStanceDefault = Config.Bind(headerStance, "Default Camera Stance", CameraStanceEnum.Right, new ConfigDescription(
            "The default camera stance at the start of the raid. Note, the game might adjust the precise position on multiple factors like " +
            "visibility, ADS, etc.",
            tags: new ConfigurationManagerAttributes { Order = 6 }
        ));
        CameraStanceToggleKey = Config.Bind(headerStance, "Toggle Stance Key", KeyCode.None, new ConfigDescription(
            "Toggles between each stance.",
            tags: new ConfigurationManagerAttributes { Order = 5 }
        ));
        CameraStanceLeftKey = Config.Bind(headerStance, "Left Side Stance Key", KeyCode.None, new ConfigDescription(
            "Switches the camera to the left side.",
            tags: new ConfigurationManagerAttributes { Order = 4 }
        ));
        CameraStanceRightKey = Config.Bind(headerStance, "Right Side Stance Key", KeyCode.None, new ConfigDescription(
            "Switches the camera to the right side.",
            tags: new ConfigurationManagerAttributes { Order = 3 }
        ));
        CameraStanceSwapOnLeanEnabled = Config.Bind(headerStance, "Stance Swap Cam On Lean", false, new ConfigDescription(
            "Swap the camera based on the lean direction. Note, there's no leaning during running, so you won't be able to always control " +
            "the stance based purely on the lean direction.",
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        GunStanceSync = Config.Bind(headerStance, "Gun Stance Sync", GunStanceSyncEnum.Cam, new ConfigDescription(
            "Automatically shoulder swaps the gun (left or right shoulder) based on either the Camera Stance, Lean direction or nothing.",
            tags: new ConfigurationManagerAttributes { Order = 1 }
        ));
        GunStanceReset = Config.Bind(headerStance, "Gun Stance Reset", true, new ConfigDescription(
            "Reset the gun stance when not leaning.",
            tags: new ConfigurationManagerAttributes { Order = 0 }
        ));

        AimConvergenceMinDistance = Config.Bind(headerCamera, "Aim Convergence Min Distance", 1f, new ConfigDescription(
            "In third person the character aims at the point under the centre of the screen. This is the closest that point is " +
            "allowed to be to the muzzle (in meters). Closer than this the weapon would swing hard to converge, so very close " +
            "shots land off-centre instead (the crosshair shows where).",
            new AcceptableValueRange<float>(0.5f, 10f),
            tags: new ConfigurationManagerAttributes { Order = 9 }
        ));
        AimConvergenceSpeed = Config.Bind(headerCamera, "Aim Convergence Speed", 12f, new ConfigDescription(
            "How quickly the aim adapts when the distance under the centre of the screen changes, e.g. sweeping from a far wall " +
            "onto a near pillar. Higher values are snappier.",
            new AcceptableValueRange<float>(1f, 60f),
            tags: new ConfigurationManagerAttributes { Order = 8 }
        ));
        FreeLookMaxAngle = Config.Bind(headerCamera, "Free Look Max Angle", 150f, new ConfigDescription(
            "Holding the game's free look key in third person orbits the camera around the character, which keeps aiming where " +
            "it was; the camera swings back when the key is released. This is how far (in degrees) the orbit can swing the " +
            "camera to either side of the aim direction.",
            new AcceptableValueRange<float>(40f, 180f),
            tags: new ConfigurationManagerAttributes { Order = 7 }
        ));
        CameraPositionDefault = Config.Bind(headerCamera, "Default Camera Position", CameraPositionEnum.Main, new ConfigDescription(
            "Determines the default camera position at the start of the raid, note that the game will dynamically adjust the actual position " +
            "based on multiple factors like visibility, ADS, etc.",
            tags: new ConfigurationManagerAttributes { Order = 6 }
        ));
        CameraMainOffset = Config.Bind(headerCamera, "Main Cam Offset", new Vector3(0.5f, 0.15f, -1.5f), new ConfigDescription(
            "The default camera position offset relative to the first person view (in meters).",
            tags: new ConfigurationManagerAttributes { Order = 5 }
        ));
        CameraShoulderOffset = Config.Bind(headerCamera, "Shoulder Cam Offset", new Vector3(0.5f, 0.05f, -0.5f), new ConfigDescription(
            "The shoulder camera position offset relative to the first person view (in meters).",
            tags: new ConfigurationManagerAttributes { Order = 4 }
        ));
        CameraGrenadeOffset = Config.Bind(headerCamera, "Grenade Cam Offset", new Vector3(0.5f, 0.2f, -0.5f), new ConfigDescription(
            "The camera position offset relative to the first person view (in meters) used while a grenade is in hands, " +
            "in place of the main and shoulder offsets.",
            tags: new ConfigurationManagerAttributes { Order = 3 }
        ));
        CameraShoulderKey = Config.Bind(headerCamera, "Shoulder Cam Key", KeyCode.None, new ConfigDescription(
            "Switches between the shoulder and main camera.",
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        CameraMoveSpeed = Config.Bind(headerCamera, "Cam Movement Speed", 1f, new ConfigDescription(
            "The speed with which the camera repositions itself when changing stances and avoiding obstacles. " +
            "Higher values will result in a twitchier camera. Values below 0.1 behave as 0.1.",
            new AcceptableValueRange<float>(0f, 3f),
            tags: new ConfigurationManagerAttributes { Order = 1 }
        ));
        BodyBob = Config.Bind(headerCamera, "Body Bob", 0.8f, new ConfigDescription(
            "How much the character's head and shoulders are allowed to move on screen when walking and sprinting. " +
            "0 locks the camera to the head so the head stays perfectly level, 1 keeps the camera steady " +
            "so the full motion of the animation shows.",
            new AcceptableValueRange<float>(0f, 1f),
            tags: new ConfigurationManagerAttributes { Order = 0 }
        ));
        CameraFollowDeadzone = Config.Bind(headerCamera, "Camera Follow Deadzone", 0.0f, new ConfigDescription(
            "How far (in meters) the character can move sideways or forwards before the camera starts to follow, so small " +
            "steps and direction changes show as the character moving in the frame. Height changes are always followed. " +
            "Not used while aiming down sights. 0 together with Camera Follow Smoothing 0 keeps the camera rigidly on the character.",
            new AcceptableValueRange<float>(0f, 0.5f),
            tags: new ConfigurationManagerAttributes { Order = -1 }
        ));
        CameraFollowSmoothing = Config.Bind(headerCamera, "Camera Follow Smoothing", 0.1f, new ConfigDescription(
            "How long (in seconds) the camera takes to catch up with the character once it is past the deadzone. Higher " +
            "values trail further behind; however fast the character runs, the camera never falls more than 0.25 meters " +
            "behind the deadzone edge. Not used while aiming down sights.",
            new AcceptableValueRange<float>(0f, 0.5f),
            tags: new ConfigurationManagerAttributes { Order = -2 }
        ));
        CameraTightSpace = Config.Bind(headerCamera, "Tight Space Camera", true, new ConfigDescription(
            "In cramped places (doorways, corridors, corners, stairwells) moves the camera in closer behind the character when " +
            "its usual position cannot see what the character is facing, for example while walking through a doorway with the " +
            "camera still behind the wall, and eases it back out once it can. Off keeps the camera at the configured offsets, " +
            "only pushed out of walls.",
            tags: new ConfigurationManagerAttributes { Order = -3 }
        ));
        CameraTightSpaceClosest = Config.Bind(headerCamera, "Tight Space Closest Distance", 0.3f, new ConfigDescription(
            "How close the Tight Space Camera may bring the camera in, as a share of its configured distance behind the " +
            "character: 0.3 means down to 30% of it. Lower values see better in tight spots but fill more of the screen with " +
            "the character. 1 never moves it in.",
            new AcceptableValueRange<float>(0.2f, 1f),
            tags: new ConfigurationManagerAttributes { Order = -4 }
        ));
        CameraTightSpaceOverHead = Config.Bind(headerCamera, "Tight Space Over The Head", true, new ConfigDescription(
            "With the Tight Space Camera on: when the character is pressed against a wall on the camera's side, so the camera " +
            "ends up right behind the head and the character covers the middle of the screen, lift the camera over the " +
            "character's head instead, and bring it back down once the wall is gone. Never while aiming down sights. Off keeps " +
            "the camera behind the head there.",
            tags: new ConfigurationManagerAttributes { Order = -5 }
        ));
        CameraTightSpaceOpening = Config.Bind(headerCamera, "Tight Space Lean Through Openings", true, new ConfigDescription(
            "With the Tight Space Camera on: when leaning out through a doorway or round an edge from beside it, keep the " +
            "camera on the far side, against the wall there, instead of letting it be pulled back through the opening to right " +
            "behind the head. The character is then hidden behind the wall while the camera sees what you lean out to see. " +
            "Off lets the camera be pulled in behind the head there.",
            tags: new ConfigurationManagerAttributes { Order = -6 }
        ));

        CrosshairEnabled = Config.Bind(headerCrosshair, "Enable Crosshair", true, new ConfigDescription(
            "Toggles the world space crosshair in third person view.",
            tags: new ConfigurationManagerAttributes { Order = 4 }
        ));
        CrosshairAdsOnlyEnabled = Config.Bind(headerCrosshair, "Only During ADS", true, new ConfigDescription(
            "Show the crosshair only during ADS. It's a bit cheesy otherwise. Buy a laser sight you bum.",
            tags: new ConfigurationManagerAttributes { Order = 3 }
        ));
        CrosshairColor = Config.Bind(headerCrosshair, "Crosshair Color", Color.red, new ConfigDescription(
            "Color of the crosshair.",
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        CrosshairThickness = Config.Bind(headerCrosshair, "Crosshair Thiccness", 2f, new ConfigDescription(
            "Thiccness of the crosshair. Giggity",
            new AcceptableValueRange<float>(1f, 10f),
            tags: new ConfigurationManagerAttributes { Order = 1 }
        ));

        SprintFovChange = Config.Bind(headerSprint, "Sprint FoV Change", 25, new ConfigDescription(
            "How much to change the FoV during sprinting in third person. This is relative to the baseline FoV. Positive numbers open up the FoV for " +
            "more peripheral vision. Negative numbers apply tunnel vision because you are a masochist.",
            new AcceptableValueRange<int>(-100, 100),
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        SprintFovChangeTime = Config.Bind(headerSprint, "Sprint FOV Change Time", 2f, new ConfigDescription(
            "The timespan (in seconds) that it takes to adjust the FOV for sprinting.",
            new AcceptableValueRange<float>(0f, 3f),
            tags: new ConfigurationManagerAttributes { Order = 1 }
        ));

        InteractionRayRadius = Config.Bind(headerMisc, "Interaction Radius", 0.1f, new ConfigDescription(
            "The radius of the raycast for picking interactable objects (in meters).",
            new AcceptableValueRange<float>(0.01f, 0.1f),
            tags: new ConfigurationManagerAttributes { Order = 8 }
        ));
        FlinchScale = Config.Bind(headerMisc, "Flinch Amount", 0.35f, new ConfigDescription(
            "How much flinch is applied when shot. A small value goes a long way. Set to 5 if you want to larp being a bobble head.",
            new AcceptableValueRange<float>(0f, 5f),
            tags: new ConfigurationManagerAttributes { Order = 7 }
        ));
        TorsoAimDeadzone = Config.Bind(headerMisc, "Torso Aim Deadzone", 45f, new ConfigDescription(
            "How far (in degrees) the character can aim with the upper body while standing still before the legs turn. " +
            "0 makes the body always follow the aim. 45 is the game's own value and the most the animations allow: past it " +
            "the game plays a turn-in-place animation that turns the legs regardless. Not used while prone, which keeps the " +
            "game's own limit. Walking uses the Moving Torso Deadzone instead.",
            // Capped at 45: the Turn_Left/Right_90_Aim animator states take over beyond it (see TorsoDeadzone)
            new AcceptableValueRange<float>(0f, 45f),
            tags: new ConfigurationManagerAttributes { Order = 6 }
        ));
        MovingTorsoDeadzone = Config.Bind(headerMisc, "Moving Torso Deadzone", 25f, new ConfigDescription(
            "How far (in degrees) the aim can move away from the legs while walking before the legs turn to follow at " +
            "Body Turn Speed. Only changes how the legs look: walking still goes where you aim. 0 keeps the legs locked " +
            "to the aim while walking, as the game does. Sprinting always faces the direction of travel.",
            // Kept well under 26.6, where the game's discrete walk direction switches to a diagonal
            new AcceptableValueRange<float>(0f, 40f),
            tags: new ConfigurationManagerAttributes { Order = 5 }
        ));
        BodyTurnSpeed = Config.Bind(headerMisc, "Body Turn Speed", 120f, new ConfigDescription(
            "How fast (in degrees per second) the legs turn to follow the aim once it leaves the Torso Aim Deadzone " +
            "(standing still) or the Moving Torso Deadzone (walking). Standing still, 0 keeps the legs trailing at the edge " +
            "of the deadzone instead of re-centring, which is the game's native behaviour; walking, 0 makes the legs keep " +
            "up with the edge of the deadzone at once.",
            new AcceptableValueRange<float>(0f, 720f),
            tags: new ConfigurationManagerAttributes { Order = 4 }
        ));
        MovingRecentreSpeed = Config.Bind(headerMisc, "Moving Recentre Speed", 30f, new ConfigDescription(
            "How fast (in degrees per second) the legs square up to the aim while walking with the aim inside the Moving " +
            "Torso Deadzone. 0 holds them where they are until the aim leaves the deadzone.",
            new AcceptableValueRange<float>(0f, 180f),
            tags: new ConfigurationManagerAttributes { Order = 3 }
        ));
        GaitWeight = Config.Bind(headerMisc, "Gait Weight", 1f, new ConfigDescription(
            "Adds a bob and sag of the head and shoulders with each step in third person, so the character moves with weight " +
            "instead of gliding. Bigger strides (sprinting) dip more, and landings from a jump or fall dip too. 1 is the intended " +
            "amount, 2 doubles it. 0 turns it off.",
            new AcceptableValueRange<float>(0f, 2f),
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        ShoulderRecoil = Config.Bind(headerMisc, "Shoulder Recoil", 1.5f, new ConfigDescription(
            "Rocks the upper body back along the gun with each shot in third person, firing shoulder first, so the body visibly " +
            "takes the recoil. Harder kicking guns push harder; shotguns and bolt actions the most, pistols the least. Purely cosmetic: " +
            "it does not change the recoil, the aim or where the bullets go. 1 is the intended amount, 3 triples it. 0 turns it off.",
            new AcceptableValueRange<float>(0f, 3f),
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        InteractionRange = Config.Bind(headerMisc, "3rd Person Interaction Range (RESTART)", 3.5f, new ConfigDescription(
            "How far away (in meters) you can interact with objects like doors or adult toy vending machines. This is measured from the camera " +
            "position, not the player position. If the camera is 2m behind the player, you need at least 3m to get reasonable interaction experience.",
            new AcceptableValueRange<float>(0f, 25f),
            tags: new ConfigurationManagerAttributes { Order = 1 }
        ));
        InteractionRange.SettingChanged += (_, _) =>
        {
            EFTHardSettings.Instance.LOOT_RAYCAST_DISTANCE = InteractionRange.Value;
            EFTHardSettings.Instance.DOOR_RAYCAST_DISTANCE = InteractionRange.Value;
            EFTHardSettings.Instance.PLAYER_RAYCAST_DISTANCE = InteractionRange.Value;
        };

        EFTHardSettings.Instance.LOOT_RAYCAST_DISTANCE = InteractionRange.Value;
        EFTHardSettings.Instance.DOOR_RAYCAST_DISTANCE = InteractionRange.Value;
        EFTHardSettings.Instance.PLAYER_RAYCAST_DISTANCE = InteractionRange.Value;

        // Note: this is the overlap threshold where ADSing is prevented. Default is 0.
        EFTHardSettings.Instance.STOP_AIMING_AT = 100f;

        DebugUIEnabled = Config.Bind(headerDebug, "Enable Debug UI", false, new ConfigDescription(
            "Enables the debug UI for diagnosing common issues.",
            tags: new ConfigurationManagerAttributes { Order = 2 }
        ));
        _loggingEnabled = Config.Bind(headerDebug, "Enable Debug Logging", false, new ConfigDescription(
            "Duh. Requires restarting the game to take effect.",
            tags: new ConfigurationManagerAttributes { Order = 1 }
        ));
    }
}