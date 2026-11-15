using System;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.CameraControl;
using EFT.Settings;
using HarmonyLib;
using HollywoodCam.Collision;
using HollywoodCam.Helpers;
using UnityEngine;

namespace HollywoodCam;

public static class CameraExtensions
{
    public static Vector2 WorldPointToVisibleScreenPoint(this Camera camera, Vector3 worldPoint)
    {
        var screenPoint = camera.WorldToScreenPoint(worldPoint);
        var scale = Screen.height / (float)camera.scaledPixelHeight;
        screenPoint.y = Screen.height - screenPoint.y * scale;
        screenPoint.x *= scale;
        if (screenPoint is { z: > 0.01f, x: > -5f, y: > -5f } && screenPoint.x < Screen.width && screenPoint.y < Screen.height)
            return screenPoint;
        return Vector2.zero;
    }
}

public class ThirdPersonView : MonoBehaviour
{
    public Player localPlayer;

    // The debug UI setting as of this frame's UpdateCamera: everything recorded only for the debug UI checks this, so with
    // the UI off none of it runs. The posers' hooks later in the frame read it too, at most a frame old.
    private bool _debug;

    // The crosshair and debug overlay (see HudOverlay), enabled only while one of them can draw
    private HudOverlay _hud;
    private bool _hudEnabled;

    private bool _unknownAdsModeLogged;

    // Static Config
    private LayerMask _eyeCameraHitMask;
    private readonly Vector3 _eyeCameraOffset = new(0f, 0.1f, 0f);
    // The collision cast origin is _eyeCameraOffset above the pivot, pulled down to this far short of a ceiling closer than
    // that: a cast that starts inside a collider does not see that collider, so the solver would be blind to it
    private const float CastOriginSkin = 0.02f;
    // How far the last cast origin was pulled down, for the debug UI
    private float _castOriginDrop;

    // Measured cost of the camera solve (cast origin, tight space probes, collision solver), see CollisionUtils.CastCount
    private int _solveCasts;
    private int _originCasts;
    private int _boomCasts;
    private int _solverCasts;
    // Highest per-frame count in the last completed second and in the current one
    private int _solveCastsPeak;
    private int _solveCastsWindowPeak;
    private float _solveCastsWindowStart;
    // Timed only while the debug UI is on
    private readonly System.Diagnostics.Stopwatch _solveWatch = new();
    private double _solveMs;
    private double _solveMsAverage;
    private const double SolveMsAverageWeight = 0.05;

    // Tight space camera (see BoomModulator, IntentProbe): decided here, probed with IntentProbe, applied by shaping the
    // offset the collision solver is given
    private readonly BoomModulator _boom = new();
    private IntentProbe _intent;
    // What blocks sight: the camera mask without glass (TransparentCollider), which the camera must not pass through but
    // can see through, and without corpses (Deadbody), which lie on the floor and rarely hide anything worth seeing
    private int _visibilityMask;
    // Each level's camera is first collision-clamped with one cast of the solver's middle phase radius, roughly where
    // the solver would leave a camera asked to go there
    private const float TightSpaceCandidateRadius = 0.09f;
    // The setting's floor, also its range's
    private const float TightSpaceMinClosest = 0.2f;
    // Character shapes for the sight test. A sample behind the character counts as seen (the character hiding a close
    // point is ordinary 3rd person framing, no shorter boom should try to cure it); the shapes only save the cast and
    // label the sample for the debug UI.
    private const float BodyHeadRadius = 0.15f;
    private const float BodyTorsoRadius = 0.2f;
    // The last decision is reused while the eye and pivot have moved less than this, the aim has turned less than this,
    // nothing else it depends on changed and it is younger than the age limit. Standing still that makes a re-decision a
    // few probes a second instead of every frame, and the age limit still catches doors and people moving.
    private const float TightSpaceReuseMoveSqr = 0.02f * 0.02f;
    private const float TightSpaceReuseAngle = 0.5f;
    private const float TightSpaceReuseMaxAge = 0.25f;
    private bool _boomDecisionValid;
    private Vector3 _boomDecisionEye;
    private Vector3 _boomDecisionPivot;
    private Quaternion _boomDecisionAim;
    private Vector3 _boomDecisionDesired;
    private bool _boomDecisionAiming;
    private float _boomDecisionClosest;
    private float _boomDecisionTarget;
    private float _boomDecisionTime;
    // Frame the modulator last ran on; a gap (1st person, posed by game, off) means the next run snaps instead of easing
    private int _boomLastFrame = -10;
    // For the debug UI: each probed level's visibility and sample results, which level the decision took, the state
    private readonly SightResult[] _levelResults = new SightResult[BoomModulator.LevelCount];
    private readonly int[] _levelMisses = new int[BoomModulator.LevelCount];
    private readonly TargetView[][] _levelViews = new TargetView[BoomModulator.LevelCount][];
    private int _levelsProbed;
    private int _boomDecidedLevel;
    private string _boomStatus = "Not run";
    private Vector3 _trailCancel;
    private float _boomClosest = 1f;
    // Stage 1's result for the level it took, for Stage 2: whether a level passed, its clamped camera and its misses
    private readonly Vector3[] _levelCameras = new Vector3[BoomModulator.LevelCount];
    private bool _homePassed;
    private Vector3 _homeCamera;
    private int _homeMisses;

    // Stage 2, the over the head position (see TightSpaceCandidates): decided here from the same samples
    private readonly TightSpaceCandidates _candidates = new();
    // The position last asked for, re-asked while a decision is reused so its dwell keeps running
    private int _candidateWanted = TightSpaceCandidates.Home;
    // State the reused decision was made in: a change of either re-decides
    private bool _boomDecisionCollapsing;
    private int _boomDecisionActive = TightSpaceCandidates.Home;
    private bool _candidateSwitched;
    // The aim point is held while the camera travels between home and over the head (see HandleThirdPerson); for this
    // long past the blend, after which the convergence hold's own settle check has normally closed it already
    private const float CandidateHoldMargin = 0.5f;
    // For the debug UI: the last Stage 2 probe and its casts
    private string _candidateProbe = "-";
    private int _candidateCasts;

    // Opening camera, for a head leaned through a doorway (see OpeningCamera): while active it places the camera itself, by
    // sweeping it from where it was, instead of the collision solver
    private readonly OpeningCamera _opening = new();
    // The camera's world position as placed last frame, where a sweep continues from; valid only if placed last frame
    private Vector3 _lastCameraWorld;
    private int _lastCameraFrame = -10;
    // The opening camera's own position and easing velocity while active
    private Vector3 _openingCamera;
    private Vector3 _openingVelocity;
    // The ordinary solve's target is reached when a sweep toward it ends this close to it
    private const float OpeningReachTolerance = 0.05f;
    // The camera is hidden from the head when a cast of this radius from the solver's origin stops short of this share of
    // the way: the solver's finest phase radius, and short enough that only the jamb, not a graze, counts
    private const float OpeningHeadCastRadius = 0.05f;
    private const float OpeningHeadClearScore = 0.95f;
    // The aim point is held while a bootstrap carries the camera out through the opening, for this long past its travel
    private const float OpeningHoldMargin = 0.5f;
    // The opening camera counts as clear when a sphere this much bigger than the swept one (half the skin) touches nothing.
    // Unity reports colliders a sweep starts touching, which stops the move; if that test were looser than this one, a
    // camera could rest where every move stops yet nothing is resolved, and freeze.
    private const float OpeningClearance = OpeningCamera.CameraRadius + CameraSweep.Skin * 0.5f;
    // A push out of an overlap longer than this is refused: a touching sphere needs its own size plus the skin at most,
    // and a longer push means the camera was inside a solid, where the nearest way out can be the far side of a wall
    private const float OpeningMaxPush = OpeningClearance + CameraSweep.Skin;
    private bool _openingBootstrapped;
    // Moves the opening camera (and the bootstrap's probes) as a sphere; the player's own colliders are not obstacles
    private CameraSweep _sweep;
    // Where the camera's full sphere was last found clear, for when it is found overlapping something it cannot be pushed
    // out of; valid only while the mode holds the camera
    private Vector3 _openingLastClear;
    private bool _openingLastClearValid;
    // Whether the camera was overlapping last frame, so a run of overlapping frames is one occurrence (logged once)
    private bool _openingOverlapping;
    // What the camera's last move stopped at: a reference written every move, described only when the mode retreats or cuts
    private Collider _openingMoveBlocker;
    // For the debug UI and the log, built only at the events themselves and only when either can show them: how many
    // overlap occurrences there were, the last one's collider and how it was resolved, and what the camera had stopped at
    // when the mode last retreated or cut
    private int _openingOverlapEvents;
    private string _openingOverlapInfo;
    private string _openingBlockerInfo;
    // For the debug UI
    private OpeningCheck _openingCheck;
    private string _openingStatus = "Off";
    private int _openingCasts;

    // Per collider: whether a sight line passes it (chain link, grates, glass), from its ballistic material
    private readonly System.Collections.Generic.Dictionary<int, bool> _seeThroughCache = new();
    private const int SeeThroughCacheLimit = 4096;

    // State
    private bool _thirdPersonEnabled;
    private CameraPositionEnum _cameraPosition;
    private int _cameraStance;
    private Vector3 _currentOffset;

    private bool _aimFlag;
    private bool _sprintFlag;

    private AdsModeEnum _adsModeOptic;
    private AdsModeEnum _adsModeBasic;
    private bool _currentScopeIsOptic;

    private PositionSolver _positionSolver;
    private CameraRig _rig;
    // Cam's local rotation as 1st person left it, put back when we leave 3rd person
    private Quaternion _fpCamLocalRotation = Quaternion.identity;

    // Free aim (see Patches/Look.cs). Below this many degrees an aim delta is float residue from wrapping the rig's yaw;
    // passed on, it would register as player movement every frame.
    private const float AimDeltaEpsilon = 1e-4f;
    // Whether the rig was driving the player at the previous opportunity; false means seed it from the player next time
    private bool _rigCapturing;
    // MovementContext.Rotation as of our last look at it, anything beyond this was done by the game
    private Vector2 _knownPlayerRotation;
    // The player rotation asked for in the current Rotate call
    private Vector2 _aimRequested;
    private Vector2 _lastReconcileShortfall;

    // Aim convergence (see AimSolver). Below this many degrees a reconcile shortfall is float noise; with the aim no
    // longer equal to the rig it would otherwise random walk the rig.
    private const float ReconcileDeadband = 1e-3f;
    private AimSolver _aimSolver;
    // _currentOffset only means something once HandleThirdPerson has run
    private bool _pivotPlaced;
    // Whether the last aim solve converged; false means the next one snaps the distance smoothing
    private bool _convergenceActive;
    private string _convergenceFallback = "Not run";

    // After a convergence seed the camera usually starts near the eye (back from 1st person) and eases out to the
    // shoulder; while this window is open the rig is re-pointed so that translation does not drag the aim off target.
    private const float ConvergenceHoldDuration = 1f;
    private const float ConvergenceHoldSettledSqr = 0.01f * 0.01f;
    // Time.time the hold window closes at, negative when closed
    private float _convergenceHoldUntil = -1f;

    // Free look orbit (see Patches/Look.cs). Player.Look's own head return rate, so camera and head come back together.
    private const float OrbitReturnRate = 15f;
    // Set by free look input (the look axes only carry the mouse while the key is held), cleared by the game's
    // ResetLookDirection, which the key's release sends
    private bool _orbitHeld;

    // The offset the aim maths models the camera at (see UpdateAimOffset). _currentOffset is collision-solved in the view
    // frame, which an orbit swings away from the aim frame; re-used as is, an orbit into a wall would move the aim.
    private Vector3 _aimOffset;
    // Whether _aimOffset was frozen by an orbit and has not yet caught up with _currentOffset
    private bool _aimOffsetDetached;
    // Metres per second the aim offset eases back at after an orbit, so the aim drifts back rather than jumps
    private const float AimOffsetCatchUpSpeed = 1f;

    // Camera pivot, see GetPivotPosition. The filtered eye offset from the body, updated once per LateUpdate.
    private readonly PivotFilter _pivotFilter = new();
    // Last raw eye offset fed to the filter, for the debug UI
    private Vector3 _pivotRawOffset;
    // Body Bob as applied, eased toward the setting so changing it mid-raid does not jump the camera
    private float _bodyBobWeight;
    private const float BodyBobWeightRate = 2f;
    // How far short of a hit between the eye and the pivot the pivot is pulled back to
    private const float PivotCollisionSkin = 0.02f;
    private const float PivotMinCastSqr = 1e-8f;
    // Body yaw as of the last time it could be read, for a body pitched vertical
    private Quaternion _lastBodyYaw = Quaternion.identity;

    // The pivot's lag behind the character's travel (see PivotTrail), stepped with the pivot filter
    private readonly PivotTrail _pivotTrail = new();
    // The follow settings as applied: eased toward the settings, and toward zero while aiming down sights, so neither a
    // settings change nor ADS jumps the camera: a shrinking smoothing time closes the lag progressively faster, so by the
    // frame it reaches zero (a hard deadzone edge) the lag is already within a centimetre or so of the deadzone.
    private float _followDeadzone;
    private float _followSmoothing;
    private const float FollowDeadzoneRate = 0.5f;
    private const float FollowSmoothingRate = 1f;

    // Upper body aim deadzone, applied through Player.TrunkRotationLimit (see TorsoDeadzone)
    private readonly TorsoDeadzone _torsoDeadzone = new();
    // Whether the limit was last left to the game because the player is prone or getting in or out of it
    private bool _torsoProneHold;

    // Moving deadzone: the walking state's legs, driven from the MovePlayerState.UpdateRotation patch (see MovingDeadzone)
    private readonly MovingDeadzone _movingDeadzone = new();
    // Whether the moving deadzone had the walking legs as of the last LateUpdate, for the standing hand-off and debug UI
    private bool _movingDeadzoneInControl;
    // The game's own "not getting anywhere" speed for walking (MovePlayerState.velocityThreshold, its wall lock), so a
    // walk the game would lock against a wall reads as blocked too, and no real walk does
    private const float WalkBlockedSpeed = 0.2f;
    // Longer than the game's 0.1s wall lock wait and than a walk takes to get going, so starting to walk eases
    private const float WalkBlockedGraceTime = 0.3f;
    private const float WalkInputMinSqr = 0.01f;
    // Below this the travel direction is mostly noise and inertia, the debug readout does not show it
    private const float TravelErrorMinSpeed = 0.3f;
    private bool _walkBlocked;
    private float _walkBlockedTime;
    // Measured from the player's position each LateUpdate, not taken from the controller, so it is what really happened
    private Vector3 _horizontalVelocity;
    private Vector3 _lastPlayerPosition;
    private bool _hasLastPlayerPosition;
    // The walking state's raw, body-local input as of the last LateUpdate (zero when not walking)
    private Vector2 _walkInput;

    // Procedural weight on the walk and sprint cycle (see GaitPoser), applied from the Player.FBBIKUpdate postfix
    private GaitPoser _gaitPoser;
    // Cosmetic recoil push of the upper body per shot (see ShoulderRecoilPoser), applied from the Player.IkProcess prefix
    private ShoulderRecoilPoser _shoulderRecoilPoser;

    private EventHandler _adsModeOpticChanged;
    private EventHandler _adsModeBasicChanged;

    private Player.ItemHandsController _handsController;
    private Player.FirearmController _firearmController;

    private SettingsManager _gameSettings;

    public void Awake()
    {
        _thirdPersonEnabled = Plugin.PointOfViewDefault.Value == PointOfViewEnum.ThirdPerson;
        _cameraPosition = Plugin.CameraPositionDefault.Value;
        _cameraStance = (int)Plugin.CameraStanceDefault.Value;

        _adsModeOptic = Plugin.AdsModeOptic.Value;
        _adsModeBasic = Plugin.AdsModeBasic.Value;

        // Kept so OnDestroy can unsubscribe: the config entries outlive this component, one per raid
        _adsModeOpticChanged = (_, _) => _adsModeOptic = Plugin.AdsModeOptic.Value;
        _adsModeBasicChanged = (_, _) => _adsModeBasic = Plugin.AdsModeBasic.Value;
        Plugin.AdsModeOptic.SettingChanged += _adsModeOpticChanged;
        Plugin.AdsModeBasic.SettingChanged += _adsModeBasicChanged;

        // Hit mask for the camera root. Remove players from this to avoid colliding with ourselves, duh
        _eyeCameraHitMask = BallisticsCalculatorConstants.HitMask.value & ~(1 << LayerMask.NameToLayer("HitCollider"));

        _gameSettings = Singleton<SettingsManager>.Instance;

        _positionSolver = new PositionSolver(
            new AdvectionSolver(
                new CircleScan(12, 0.7f, 2f),
                new LineScan(1.0f, 0.05f)
            ),
            new AdvectionSolver(
                new CircleScan(10, 0.25f, 2f, centerRadius: 0.09f),
                new LineScan(0.5f, 0.025f)
            ),
            new AdvectionSolver(
                new CircleScan(8, 0.125f, 2f, centerRadius: 0.05f),
                new LineScan(0.3f, 0.015f)
            ), 0.2f, 0.05f
        );

        _rig = new CameraRig();

        _bodyBobWeight = Mathf.Clamp01(Plugin.BodyBob.Value);
        _followDeadzone = Mathf.Max(0f, Plugin.CameraFollowDeadzone.Value);
        _followSmoothing = Mathf.Max(0f, Plugin.CameraFollowSmoothing.Value);

        // What bullets can hit, other characters' hit colliders included, so aiming at someone converges on them
        _aimSolver = new AimSolver(BallisticsCalculatorConstants.HitMask.value);

        _gaitPoser = new GaitPoser(Plugin.GaitWeight.Value);
        _shoulderRecoilPoser = new ShoulderRecoilPoser(Plugin.ShoulderRecoil.Value);

        _visibilityMask = _eyeCameraHitMask & ~LayerBit("TransparentCollider") & ~LayerBit("Deadbody");
        _intent = new IntentProbe(_visibilityMask, _aimSolver.IsIgnored, IsSeeThrough);
        _sweep = new CameraSweep(_aimSolver.IsIgnored);
        for (var i = 0; i < _levelViews.Length; i++)
            _levelViews[i] = new TargetView[IntentProbe.MaxTargets];

        _hud = gameObject.AddComponent<HudOverlay>();
        _hud.View = this;
        _hud.enabled = _hudEnabled = false;
    }

    private static int LayerBit(string layerName)
    {
        var layer = LayerMask.NameToLayer(layerName);
        return layer >= 0 ? 1 << layer : 0;
    }

    /// <summary>
    ///     Whether a sight line passes a collider: its ballistic material is chain link, a grate or glass. Looked up the way
    ///     the game's shots find it (on the collider or its parent), once per collider.
    /// </summary>
    private bool IsSeeThrough(Collider collider)
    {
        if (collider == null)
            return true;

        var id = collider.GetInstanceID();
        if (_seeThroughCache.TryGetValue(id, out var seeThrough))
            return seeThrough;

        if (!collider.TryGetComponent<BallisticCollider>(out var ballistic))
        {
            var parent = collider.transform.parent;
            if (parent == null || !parent.TryGetComponent(out ballistic))
                ballistic = null;
        }

        seeThrough = ballistic != null && ballistic.TypeOfMaterial is MaterialType.Chainfence or MaterialType.Grate or MaterialType.Glass or MaterialType.GlassShattered;

        if (_seeThroughCache.Count >= SeeThroughCacheLimit)
            _seeThroughCache.Clear();
        _seeThroughCache[id] = seeThrough;
        return seeThrough;
    }

    public void OnDestroy()
    {
        Plugin.AdsModeOptic.SettingChanged -= _adsModeOpticChanged;
        Plugin.AdsModeBasic.SettingChanged -= _adsModeBasicChanged;

        // Before the game's OnDead/Dispose (both destroy this immediately), so the body goes to ragdoll unmodified.
        // Shoulder recoil first: it wrote the shared neck last, so it is put back first.
        _shoulderRecoilPoser?.Dispose();
        _gaitPoser?.Dispose();

        _rig?.Destroy();
        _sweep?.Destroy();

        // Deferred: this runs inside the DestroyImmediate of Player.Dispose and Player.OnDead. Until the end of the frame
        // the overlay finds its view gone and draws nothing.
        if (_hud != null)
        {
            _hud.enabled = false;
            Destroy(_hud);
        }

        _hud = null;
    }

    /// <summary>
    ///     Puts back the bones the gait weight moved last frame. Script Updates run before the animator's pass for the
    ///     local player (body update mode Auto on the Update queue, animator update mode Normal), which then poses the body
    ///     from the clean pose; a bone the clips do not key would otherwise carry the offset into the next frame.
    /// </summary>
    public void Update()
    {
        // Reverse of the order they write in: both touch the neck, and the shoulder recoil caches it after the gait has
        // moved it, so the gait's cache is the clean one and has to be applied last
        _shoulderRecoilPoser?.Restore();
        _gaitPoser?.Restore();
    }

    /// <summary>
    ///     From the Player.FBBIKUpdate postfix, once per frame on VisualPass's full path: after the full body IK, before the
    ///     hands are placed at the ribcage.
    /// </summary>
    public void OnBodyIkSolved()
    {
        // Backstop for a missed Update, ahead of the gait's own: see Update for the order
        _shoulderRecoilPoser?.Restore();

        if (localPlayer == null || _gaitPoser == null)
            return;

        var movementContext = localPlayer.MovementContext;
        if (movementContext == null)
            return;

        // Immediately: the 1st person camera rides the same bones
        if (!IsViewThirdPerson)
        {
            _gaitPoser.Hold(movementContext, "1st person");
            return;
        }

        // The restore in Update is only ahead of the animator when the animator runs in Unity's own animation pass
        if (localPlayer.BodyUpdateMode != Player.EUpdateMode.Auto || localPlayer.UpdateQueue != EUpdateQueue.Update)
        {
            _gaitPoser.Hold(movementContext, "Animator timing");
            return;
        }

        // Neither reaches the full path (VisualPass returns early, or takes the reduced path), checked for completeness
        if (localPlayer.CustomAnimationsAreProcessing || localPlayer.UsedSimplifiedSkeleton)
        {
            _gaitPoser.Hold(movementContext, "Custom animation");
            return;
        }

        string fadeReason = null;
        if (IsBodyPosedByGame)
            fadeReason = "Posed by game";
        else if (IsProneOrTransitioning(movementContext))
            fadeReason = "Prone";
        else if (IsGaitBlockedState(movementContext))
            fadeReason = "Vault/climb";
        else if (movementContext.PlayerAnimator != null && movementContext.PlayerAnimator.AnimatedInteractions is { IsInteractionPlaying: true })
            fadeReason = "Interaction";

        var pwa = localPlayer.ProceduralWeaponAnimation;
        var scale = pwa != null && pwa.IsAiming ? GaitPoser.AimingScale : 1f;
        scale *= Mathf.Lerp(GaitPoser.FullCrouchScale, 1f, Mathf.Clamp01(movementContext.SmoothedPoseLevel));

        _gaitPoser.Apply(localPlayer, fadeReason, scale, Plugin.GaitWeight.Value);
    }

    /// <summary>
    ///     States whose animation moves the whole body on its own (vaults and climbs, door kicks). The vault and climb states
    ///     only swap the rotation function (IsBodyPosedByGame) above their hands-free height, so the low ones need this.
    /// </summary>
    private static bool IsGaitBlockedState(MovementContext movementContext)
    {
        var state = movementContext.CurrentState;
        if (state == null)
            return false;

        if (state is VaultingMovementState)
            return true;

        switch (state.Name)
        {
            case EPlayerState.ClimbOver:
            case EPlayerState.ClimbUp:
            case EPlayerState.VaultingFallDown:
            case EPlayerState.VaultingLanding:
            case EPlayerState.BreachDoor:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    ///     From the Player.IkProcess prefix, once per frame on VisualPass's full path: the weapon is placed and the upper
    ///     arms pinned, the arm IK is next. rightHandWeight is the game's right arm IK weight; markers its hand IK markers.
    /// </summary>
    public void OnArmIkStarting(float rightHandWeight, Transform[] markers)
    {
        if (localPlayer == null || _shoulderRecoilPoser == null)
            return;

        var movementContext = localPlayer.MovementContext;
        if (movementContext == null)
            return;

        // The same hard stops as the gait (see OnBodyIkSolved), plus the game posing the body: mounted the game does not
        // even pin the shoulders, and a stationary weapon or vehicle has no shot to take
        if (!IsViewThirdPerson)
        {
            _shoulderRecoilPoser.Hold("1st person");
            return;
        }

        if (localPlayer.BodyUpdateMode != Player.EUpdateMode.Auto || localPlayer.UpdateQueue != EUpdateQueue.Update)
        {
            _shoulderRecoilPoser.Hold("Animator timing");
            return;
        }

        if (localPlayer.CustomAnimationsAreProcessing || localPlayer.UsedSimplifiedSkeleton)
        {
            _shoulderRecoilPoser.Hold("Custom animation");
            return;
        }

        if (IsBodyPosedByGame)
        {
            _shoulderRecoilPoser.Hold("Posed by game");
            return;
        }

        string fadeReason = null;
        if (IsGaitBlockedState(movementContext))
            fadeReason = "Vault/climb";
        else if (movementContext.PlayerAnimator != null && movementContext.PlayerAnimator.AnimatedInteractions is { IsInteractionPlaying: true })
            fadeReason = "Interaction";
        else if (localPlayer.HandsController is not Player.FirearmController)
            fadeReason = "No firearm";

        // Lying on the chest there is little to twist; crouched a little less than standing
        var scale = IsProneOrTransitioning(movementContext)
            ? ShoulderRecoilPoser.ProneScale
            : Mathf.Lerp(ShoulderRecoilPoser.FullCrouchScale, 1f, Mathf.Clamp01(movementContext.SmoothedPoseLevel));

        _shoulderRecoilPoser.Apply(localPlayer, rightHandWeight, markers, fadeReason, scale, Plugin.ShoulderRecoil.Value, _debug);
    }

    /// <summary>
    ///     From the ProceduralWeaponAnimation.Shoot postfix, for the local player's shots. str is the shot's recoil
    ///     multiplier from the firearm controller (ammo, double barrel).
    /// </summary>
    public void OnShotFired(EFT.Animations.ProceduralWeaponAnimation pwa, float str)
    {
        _shoulderRecoilPoser?.OnShot(pwa, str, Plugin.ShoulderRecoil.Value);
    }

    private void HandleInputs()
    {
        if (Input.GetKeyDown(Plugin.ThirdPersonToggleKey.Value))
            _thirdPersonEnabled = !_thirdPersonEnabled;

        if (Input.GetKeyDown(Plugin.CameraShoulderKey.Value))
            _cameraPosition = _cameraPosition != CameraPositionEnum.Shoulder ? CameraPositionEnum.Shoulder : Plugin.CameraPositionDefault.Value;

        if (Input.GetKeyDown(Plugin.CameraStanceToggleKey.Value))
            _cameraStance *= -1;

        if (Input.GetKeyDown(Plugin.CameraStanceLeftKey.Value))
            _cameraStance = -1;

        if (Input.GetKeyDown(Plugin.CameraStanceRightKey.Value))
            _cameraStance = 1;
    }

    /// <summary>
    ///     Keeps holstered/back equipment visible in 3rd person, EXCEPT the item currently in hands.
    ///
    ///     BSG's SlotView.CreateAndParent disables every renderer when _isYourPlayer (you never see your own
    ///     back in 1st person), so showing that gear requires re-enabling them. Doing it once in a
    ///     CreateAndParent postfix isn't enough: drawing a weapon runs
    ///     ItemHandsController.CreateItemHandsController -> DestroyCurrentModel, and the slot view then
    ///     recreates the model asynchronously. That recreate can land before HandsController.Item is set,
    ///     so a one-shot check at creation time sees no item in hands and re-enables the back model --
    ///     leaving the primary drawn on your back while it's also in your hands.
    ///
    ///     Re-evaluating every frame is self-correcting regardless of that ordering, and is only a handful
    ///     of slot views per frame.
    /// </summary>
    private void SyncHolsteredEquipmentVisibility()
    {
        var body = localPlayer.PlayerBody;
        if (body?.SlotViews == null)
            return;

        var itemInHands = localPlayer.HandsController?.Item;

        // NB: compare against the REAL inventory slot, not slotView.ContainedItem.
        // slotView.ContainedItem is _slot.ReactiveContainedItem, and other mods can hand PlayerBody a cloned
        // equipment -- Transmog does exactly this (CloneVisibleItem in its PlayerBody.Init patch) -- so the
        // body's slot items are clones with different instance ids and never reference-match the hands item.
        // The real equipment is untouched, so resolving the slot there is clone-proof.
        var equipment = localPlayer.InventoryController?.Inventory?.Equipment;

        // DictionaryListHydra exposes Count/GetByIndex rather than a Values collection.
        for (var s = 0; s < body.SlotViews.Count; s++)
        {
            var slotView = body.SlotViews.GetByIndex(s);
            var renderers = slotView?.Renderers;
            if (renderers == null)
                continue;

            var realItemInSlot = equipment?.GetSlot(slotView.EquipmentSlot)?.ContainedItem;
            var shouldShow = itemInHands == null || realItemInSlot == null || realItemInSlot != itemInHands;
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].enabled != shouldShow)
                    renderers[i].enabled = shouldShow;
            }
        }
    }

    public void UpdateCamera()
    {
        _debug = Plugin.DebugUIEnabled.Value;
        _positionSolver.RecordDiagnostics = _debug;

        UpdateCameraAndBody();
        UpdateHud();
    }

    /// <summary>
    ///     Enables the overlay only on frames where DrawHud would draw something: the debug UI, or the crosshair with the
    ///     same conditions DrawHud checks (it checks them again, and the fireport, when it draws). After the frame's update,
    ///     so the point of view and the hands controller are this frame's.
    /// </summary>
    private void UpdateHud()
    {
        var show = localPlayer.HealthController.IsAlive && (_debug || IsCrosshairShown);
        if (show == _hudEnabled || _hud == null)
            return;

        _hudEnabled = show;
        _hud.enabled = show;
    }

    private bool IsCrosshairShown =>
        Plugin.CrosshairEnabled.Value
        && localPlayer.PointOfView != EPointOfView.FirstPerson
        && _firearmController != null
        && (_firearmController.IsAiming || !Plugin.CrosshairAdsOnlyEnabled.Value);

    private void UpdateCameraAndBody()
    {
        // NB: Has to be LateUpdate as doing all this in Update can cause the camera to occasionally clip into the wall when mousing violently.
        HandleInputs();

        if (localPlayer.CameraPosition == null || !localPlayer.HealthController.IsAlive)
            return;

        // Before the 1st person early returns: it is the same body in either view. Walk tracking first, the torso
        // deadzone hands over to the moving one from what it finds.
        UpdateWalkTracking();
        UpdateTorsoDeadzone();

        // Before the early returns too, so an orbit is dropped on any frame that is not 3rd person
        UpdateOrbit();

        // Every frame, 1st person included (where it snaps), so 3rd person always starts with the pivot on the eye.
        // Before anything this frame reads the pivot: CaptureGameRotation's seed, the pivot placement.
        var eyeTransform = localPlayer.CameraPosition.parent;
        if (eyeTransform != null)
            UpdatePivotFilter(eyeTransform);

        // Keeps the capture edge current on the frames that return before HandleThirdPerson (1st person, stationary weapons)
        if (!IsCapturingLook)
        {
            _rigCapturing = false;
            CloseConvergenceHold();
        }

        _handsController = localPlayer.HandsController as Player.ItemHandsController;
        _firearmController = localPlayer.HandsController as Player.FirearmController;

        // Before the crosshair (DrawHud), which casts with it whether or not convergence ran
        RefreshAimIgnores();

        // Every frame, 1st person included, so the sample is settled by the time capture (re)starts
        SampleMuzzleOffset();

        SyncHolsteredEquipmentVisibility();

        var isAiming = _handsController != null && _handsController.IsAiming;

        if (Plugin.CameraStanceSwapOnLeanEnabled.Value)
        {
            _cameraStance = localPlayer.MovementContext.Tilt switch
            {
                < 0 => -1,
                > 0 => 1,
                _ => _cameraStance
            };
        }

        if (_firearmController != null)
        {
            switch (Plugin.GunStanceSync.Value)
            {
                case GunStanceSyncEnum.Cam:
                {
                    if ((localPlayer.MovementContext.LeftStanceEnabled && _cameraStance > 0f)
                        || (!localPlayer.MovementContext.LeftStanceEnabled && _cameraStance < 0f))
                        _firearmController.ChangeLeftStance();

                    break;
                }
                case GunStanceSyncEnum.Lean when Plugin.GunStanceReset.Value:
                {
                    if (localPlayer.MovementContext.LeftStanceEnabled && localPlayer.MovementContext.Tilt == 0f)
                        _firearmController.ChangeLeftStance();

                    break;
                }
                case GunStanceSyncEnum.None:
                    break;
            }
        }

        var newScopeIsOptic = _firearmController != null && localPlayer.ProceduralWeaponAnimation.CurrentScope.IsOptic;

        // Reset the ADS mode variables in case the memory is switched off
        if (!Plugin.AdsModeRememberEnabled.Value && newScopeIsOptic != _currentScopeIsOptic && localPlayer.PointOfView == EPointOfView.ThirdPerson)
        {
            _adsModeBasic = Plugin.AdsModeBasic.Value;
            _adsModeOptic = Plugin.AdsModeOptic.Value;
        }

        _currentScopeIsOptic = newScopeIsOptic;

        if (Input.GetKeyDown(Plugin.AdsModeSwapKey.Value))
        {
            if (_currentScopeIsOptic)
                _adsModeOptic = _adsModeOptic == AdsModeEnum.FirstPerson ? AdsModeEnum.Shoulder : AdsModeEnum.FirstPerson;
            else
                _adsModeBasic = _adsModeBasic == AdsModeEnum.FirstPerson ? AdsModeEnum.Shoulder : AdsModeEnum.FirstPerson;
        }

        // Force to first person when mounting stationary weapons. They tend to glitch out otherwise.
        if (!_thirdPersonEnabled || localPlayer.MovementContext.StationaryWeapon != null)
        {
            if (localPlayer.PointOfView != EPointOfView.ThirdPerson) return;

            UpdatePointOfView(EPointOfView.FirstPerson);
            return;
        }

        var adsModeSelected = _currentScopeIsOptic
            ? _adsModeOptic
            : _adsModeBasic;

        switch (adsModeSelected)
        {
            case AdsModeEnum.FirstPerson:
            {
                switch (isAiming)
                {
                    case true when localPlayer.PointOfView == EPointOfView.FirstPerson:
                        return;
                    case true when localPlayer.PointOfView == EPointOfView.ThirdPerson:
                        UpdatePointOfView(EPointOfView.FirstPerson);
                        return;
                    case false when _aimFlag:
                        _cameraPosition = Plugin.CameraPositionDefault.Value;
                        localPlayer.BodyAnimatorCommon.SetLayerWeight(8, 1f);
                        ResetFoV();
                        break;
                }

                break;
            }
            case AdsModeEnum.Shoulder:
            {
                switch (isAiming)
                {
                    case true when !_aimFlag:
                        _cameraPosition = CameraPositionEnum.Shoulder;
                        localPlayer.BodyAnimatorCommon.SetLayerWeight(8, 0);
                        ScopeFoV();
                        break;
                    case false when _aimFlag:
                        _cameraPosition = Plugin.CameraPositionDefault.Value;
                        localPlayer.BodyAnimatorCommon.SetLayerWeight(8, 1f);
                        ResetFoV();
                        break;
                }

                break;
            }
            case AdsModeEnum.None:
                switch (isAiming)
                {
                    case true when !_aimFlag:
                        localPlayer.BodyAnimatorCommon.SetLayerWeight(8, 0);
                        ScopeFoV();
                        break;
                    case false when _aimFlag:
                        localPlayer.BodyAnimatorCommon.SetLayerWeight(8, 1f);
                        ResetFoV();
                        break;
                }

                break;
            default:
                // Reached every frame for as long as the setting holds the bad value
                if (!_unknownAdsModeLogged)
                {
                    _unknownAdsModeLogged = true;
                    Plugin.Log.LogError($"Unknown ADS mode selected: {adsModeSelected}");
                }

                break;
        }

        _aimFlag = isAiming;

        HandleThirdPerson();
    }

    /// <summary>
    ///     Writes the trunk rotation limit every frame, which also re-applies it after anything the game reset it for.
    ///     It is consumed by the next animator pass. Prone is left alone: it uses its own limit (25, set and reset by the
    ///     prone transitions) and clamps the look yaw to the body ± 35, so a larger limit would stop it turning at all.
    /// </summary>
    private void UpdateTorsoDeadzone()
    {
        var movementContext = localPlayer.MovementContext;
        if (movementContext == null)
            return;

        _torsoProneHold = IsProneOrTransitioning(movementContext);
        if (_torsoProneHold)
        {
            _torsoDeadzone.Abort();
            return;
        }

        // The limit does nothing while walking, but a turn started from the twist seen there would carry into standing.
        // The plain deadzone instead: stopping inside it leaves the legs where they are, beyond it starts a clean turn.
        if (_movingDeadzoneInControl)
        {
            localPlayer.TrunkRotationLimit = _torsoDeadzone.Hold(Plugin.TorsoAimDeadzone.Value);
            return;
        }

        localPlayer.TrunkRotationLimit = _torsoDeadzone.Update(
            movementContext.HandsToBodyAngle, Plugin.TorsoAimDeadzone.Value, Plugin.BodyTurnSpeed.Value, Time.deltaTime
        );
    }

    /// <summary>
    ///     Whether the moving deadzone drives this walking state: the local player's, in real 3rd person with the look
    ///     captured, and the setting on. The one rule shared by both MovePlayerState patches and the standing hand-off,
    ///     so they cannot disagree. The caller has already checked the state is exactly MovePlayerState. The context
    ///     test comes first: every bot's walking state asks.
    /// </summary>
    public bool IsMovingDeadzoneFor(MovementContext movementContext)
    {
        return movementContext == localPlayer.MovementContext && Plugin.MovingTorsoDeadzone.Value > 0f && IsCapturingLook;
    }

    /// <summary>
    ///     From the MovePlayerState.UpdateRotation patch (animator pass): the walking state's new leg yaw.
    /// </summary>
    public float StepWalkingLegs(float legYaw, float lookYaw, float deltaTime)
    {
        return _movingDeadzone.Step(
            legYaw, lookYaw, Plugin.MovingTorsoDeadzone.Value, Plugin.BodyTurnSpeed.Value, Plugin.MovingRecentreSpeed.Value, _walkBlocked, deltaTime
        );
    }

    /// <summary>
    ///     The plain walking state, not its sprint and prone-move subclasses (nor Fika's observed ones, which are never the
    ///     local player's). The game creates the walking state as exactly MovePlayerState (MovementContext.GetNewState).
    /// </summary>
    private static MovePlayerState GetWalkingState(MovementContext movementContext)
    {
        var state = movementContext.CurrentState;
        return state != null && state.GetType() == typeof(MovePlayerState) ? (MovePlayerState)state : null;
    }

    /// <summary>
    ///     Once per LateUpdate, after this frame's animator pass: the measured horizontal velocity, the walking input, and
    ///     whether the walk is blocked (input held, walking, barely moving for longer than the grace time). Blocked legs
    ///     square up, which also keeps the game's wall lock working: it lets go whenever the body is twisted over 5 degrees.
    /// </summary>
    private void UpdateWalkTracking()
    {
        var movementContext = localPlayer.MovementContext;
        if (movementContext == null)
            return;

        var deltaTime = Time.deltaTime;
        var position = movementContext.TransformPosition;
        if (_hasLastPlayerPosition && deltaTime > 0f)
        {
            var moved = position - _lastPlayerPosition;
            _horizontalVelocity = new Vector3(moved.x, 0f, moved.z) / deltaTime;
        }
        else
        {
            _horizontalVelocity = Vector3.zero;
        }

        _lastPlayerPosition = position;
        _hasLastPlayerPosition = true;

        var walking = GetWalkingState(movementContext);
        // Raw again by now: the walking state only holds the converted direction inside its update
        _walkInput = walking != null ? walking.Direction : Vector2.zero;

        if (walking != null && _walkInput.sqrMagnitude > WalkInputMinSqr && _horizontalVelocity.sqrMagnitude < WalkBlockedSpeed * WalkBlockedSpeed)
            _walkBlockedTime += deltaTime;
        else
            _walkBlockedTime = 0f;

        _walkBlocked = _walkBlockedTime > WalkBlockedGraceTime;
        _movingDeadzoneInControl = walking != null && IsMovingDeadzoneFor(movementContext);
    }

    /// <summary>
    ///     Signed angle (degrees) from the direction the held walk input means relative to the aim to the measured
    ///     horizontal velocity, or NaN when not walking or too slow to tell. For judging in game whether travel stays
    ///     aim-relative while the legs lag.
    /// </summary>
    private float GetTravelError()
    {
        if (_walkInput.sqrMagnitude <= WalkInputMinSqr || _horizontalVelocity.sqrMagnitude < TravelErrorMinSpeed * TravelErrorMinSpeed)
            return float.NaN;

        var intended = Quaternion.Euler(0f, localPlayer.MovementContext.Yaw, 0f) * new Vector3(_walkInput.x, 0f, _walkInput.y);
        return Vector3.SignedAngle(intended, _horizontalVelocity, Vector3.up);
    }

    /// <summary>
    ///     IsInPronePose is the pose flag, set and cleared when prone or standing is requested, so it also covers states
    ///     entered while lying down. The state names cover what it misses: the prone states until the state machine catches
    ///     up with a stand request, and Prone2Stand, which runs on the prone limit until its Exit resets it.
    /// </summary>
    private static bool IsProneOrTransitioning(MovementContext movementContext)
    {
        if (movementContext.IsInPronePose)
            return true;

        var state = movementContext.CurrentState;
        if (state == null)
            return false;

        switch (state.Name)
        {
            case EPlayerState.ProneIdle:
            case EPlayerState.ProneMove:
            case EPlayerState.Transit2Prone:
            case EPlayerState.Prone2Stand:
            case EPlayerState.Roll:
                return true;
            default:
                return false;
        }
    }

    private void HandleThirdPerson()
    {
        if (localPlayer.PointOfView != EPointOfView.ThirdPerson)
        {
            UpdatePointOfView(EPointOfView.ThirdPerson);
        }

        // The offset vector is passed by value, which means it's safe to modify it here
        var desiredCameraOffset = (_handsController is Player.GrenadeHandsController) switch
        {
            true => Plugin.CameraGrenadeOffset.Value,
            false => _cameraPosition switch
            {
                CameraPositionEnum.Main => Plugin.CameraMainOffset.Value,
                CameraPositionEnum.Shoulder => Plugin.CameraShoulderOffset.Value,
                _ => Plugin.CameraMainOffset.Value
            }
        };
        desiredCameraOffset.x *= _cameraStance;

        if (localPlayer.IsSprintEnabled)
        {
            if (!_sprintFlag)
                AdjustFoV(Plugin.SprintFovChange.Value, Plugin.SprintFovChangeTime.Value);
        }
        else if (_sprintFlag)
        {
            ResetFoV();
        }

        _sprintFlag = localPlayer.IsSprintEnabled;

        var eyeTransform = localPlayer.CameraPosition.parent;
        if (eyeTransform == null)
            return;

        if (IsCapturingLook)
        {
            // Frames without a Rotate call (axes ignored, menus) and game turns after it (approach states) still move the camera
            CaptureGameRotation();
        }
        else
        {
            SyncRigToPlayer(eyeTransform);
            _rigCapturing = false;
            CloseConvergenceHold();
        }

        // On Cam's parent when 3rd person starts (the filter is snapped in 1st person), so _currentOffset keeps its
        // meaning across the 1st <-> 3rd person switch
        var pivot = GetPivotPosition(eyeTransform);
        _rig.UpdatePivot(pivot);
        _pivotPlaced = true;

        // After CaptureGameRotation, so a seed made this frame already holds this frame's offset step
        if (!_convergenceActive)
            CloseConvergenceHold();

        var holding = IsConvergenceHoldOpen;
        var heldPoint = holding ? GetHeldAimPoint(pivot) : default;

        if (_debug)
            _solveWatch.Restart();
        var castsAtStart = _debug ? CollisionUtils.CastCount : 0;

        // Cast origin is offset in world space, so it stays above the pivot regardless of pitch
        var eyeCameraPos = GetCastOrigin(_rig.Pivot.position);
        var castsAfterOrigin = _debug ? CollisionUtils.CastCount : 0;

        // The configured offset, shortened in tight spaces. Exactly desiredCameraOffset when the boom is at rest, when the
        // setting is off and while the game poses the body. The solver below still has the last word on geometry.
        var solverDesiredOffset = UpdateTightSpace(eyeTransform, pivot, desiredCameraOffset, eyeCameraPos, _aimFlag);
        var castsAfterBoom = _debug ? CollisionUtils.CastCount : 0;

        // A switch over or off the head moves the camera sideways and up or down, which unlike a boom change shifts the
        // point under the crosshair: hold that point through the move with the convergence hold, from this frame on (the
        // offset has not been solved yet, so the point is still the one under the centre). Never taken while aiming down
        // sights, see UpdateTightSpace.
        if (_candidateSwitched && _convergenceActive)
        {
            if (!holding)
            {
                holding = true;
                heldPoint = GetHeldAimPoint(pivot);
            }

            var holdFor = TightSpaceCandidates.BlendTime / Plugin.CameraMoveSpeedScale + CandidateHoldMargin;
            _convergenceHoldUntil = Mathf.Max(_convergenceHoldUntil, Time.time + holdFor);
        }

        // The opening camera places the camera itself while active; otherwise the solver does, and the opening camera may
        // take over from where it left it. Reset on any gap in 3rd person (1st person, a frame not placed here).
        var openingOn = IsOpeningCameraEnabled;
        _openingBootstrapped = false;
        if (_opening.Active && (!openingOn || _lastCameraFrame != Time.frameCount - 1))
            ResetOpening(openingOn ? "reset (3rd person resumed)" : "off");

        _openingCasts = 0;
        if (_opening.Active)
        {
            _currentOffset = StepOpening(eyeTransform, solverDesiredOffset, eyeCameraPos);
        }
        else
        {
            _currentOffset = _positionSolver.Solve(
                _rig.Pivot, _currentOffset, solverDesiredOffset, eyeCameraPos, _eyeCameraHitMask
            );

            if (openingOn)
                TryEnterOpening(eyeTransform, solverDesiredOffset, eyeCameraPos);
            else
                _openingStatus = "Off";
        }

        // A bootstrap carries the camera out through the opening, sideways past the head: hold the aim point through it as
        // a Stage 2 switch does (the point is taken before this frame's move reaches the aim offset)
        if (_openingBootstrapped && _convergenceActive)
        {
            if (!holding)
            {
                holding = true;
                heldPoint = GetHeldAimPoint(pivot);
            }

            _convergenceHoldUntil = Mathf.Max(_convergenceHoldUntil, Time.time + OpeningCamera.TravelMaxTime + OpeningHoldMargin);
        }

        // Between the held point and the re-point, so the hold cancels exactly the aim camera's translation
        UpdateAimOffset();

        if (holding)
        {
            // Cancel the aim shift of the camera translating: same point under the centre from the new position
            _aimSolver.SnapDistance(PointRigAt(pivot, heldPoint));
            _rig.UpdatePivot(pivot);

            // The offset was solved in the frame before the re-point: check it again in the frame it is placed in. Only
            // phase 3 (unsmoothed, no state), so where nothing is in the way it returns the offset unchanged. Not for the
            // opening camera, whose position is a world point that phase 3 (a line from the head) would pull back in: it
            // just stays where it is.
            if (_opening.Active)
                _currentOffset = _rig.Pivot.InverseTransformPoint(_openingCamera);
            else
                _currentOffset = _positionSolver.Enforce(_rig.Pivot, _currentOffset, eyeCameraPos, _eyeCameraHitMask);

            // Settled once the camera is where it is going: the shaped offset, which in a tight space is not the configured one.
            // Not while Stage 2 is still blending, or the opening camera still travelling: the camera can sit on the
            // shaped offset part way through either.
            if (!_candidates.Blending && !_opening.Travelling && (_aimOffset - solverDesiredOffset).sqrMagnitude < ConvergenceHoldSettledSqr)
                CloseConvergenceHold();
        }

        _lastCameraWorld = _rig.Pivot.TransformPoint(_currentOffset);
        _lastCameraFrame = Time.frameCount;

        if (_debug)
            RecordSolveCost(castsAtStart, castsAfterOrigin, castsAfterBoom);

        // Written in world space: Cam's parent is re-rotated by the game every frame, the rig is what we aim with.
        // The game copies Cam onto the real camera right after this, in the same PlayerCameraController.LateUpdate.
        // The view rotation: the pivot already carries it, the free look orbit included.
        localPlayer.CameraPosition.SetPositionAndRotation(_rig.Pivot.TransformPoint(_currentOffset), _rig.ViewRotation);
    }

    /// <summary>
    ///     The collision solver's cast origin: _eyeCameraOffset above the pivot, or just short of whatever is above it closer
    ///     than that (a low ceiling, a shelf crouched under). One raycast; in the open it returns the old origin exactly.
    /// </summary>
    private Vector3 GetCastOrigin(Vector3 pivot)
    {
        var rise = _eyeCameraOffset.y;
        _castOriginDrop = 0f;

        if (CollisionUtils.Raycast(pivot, Vector3.up, out var hit, rise + CastOriginSkin, _eyeCameraHitMask))
        {
            var allowed = Mathf.Max(0f, hit.distance - CastOriginSkin);
            if (allowed < rise)
            {
                _castOriginDrop = rise - allowed;
                rise = allowed;
            }
        }

        return pivot + new Vector3(_eyeCameraOffset.x, rise, _eyeCameraOffset.z);
    }

    /// <summary>
    ///     The opening camera needs the tight space camera's view samples machinery and is off while the game poses the body.
    /// </summary>
    private bool IsOpeningCameraEnabled => Plugin.CameraTightSpace.Value && Plugin.CameraTightSpaceOpening.Value && !IsBodyPosedByGame;

    private void ResetOpening(string reason)
    {
        _opening.Reset(reason);
        _openingVelocity = Vector3.zero;
        _openingLastClearValid = false;
        _openingStatus = reason;
    }

    /// <summary>
    ///     After an ordinary solve: whether the opening camera should take over this frame. Only when leaning, and the
    ///     ordinary solve is heading for a poor view (its phase 1 target, or where its smoothing has got to, keeps the centre
    ///     ray under OpeningCamera.EnterOrdinaryCover from the character). Then it tries the camera continuing from where it
    ///     was last frame, and failing that a bootstrap out through the opening in the lean's direction. Taking over replaces
    ///     this frame's offset; nothing jumps, the camera continues from where it was.
    /// </summary>
    private void TryEnterOpening(Transform eyeTransform, Vector3 desired, Vector3 castOrigin)
    {
        // This runs every 3rd person frame, and a refusal repeats for as long as the player leans: the statuses that carry
        // numbers are only built while the debug UI can show them
        var describe = _debug;

        var lean = localPlayer.MovementContext.SmoothedTilt;
        if (Mathf.Abs(lean) < OpeningCamera.LeanEnter)
        {
            _openingStatus = "off: not leaning";
            return;
        }

        if (_opening.CoolingDown(Time.time))
        {
            _openingStatus = "off: cooling down after a cut";
            return;
        }

        var body = GetBodyShape();
        if (!body.Valid || _lastCameraFrame != Time.frameCount - 1)
        {
            _openingStatus = "off: no previous camera";
            return;
        }

        var frame = _rig.Pivot;
        var forward = frame.forward;
        // A centred camera is the player's framing (as GetSolvedCoverDistance)
        if (TightSpaceCandidates.RayCoverDistance(frame.TransformPoint(desired), forward, body.Head, body.TorsoTop) < TightSpaceCandidates.ReturnCoverDistance)
        {
            _openingStatus = "off: configured offset is centred";
            return;
        }

        var ordinaryCover = Mathf.Min(
            TightSpaceCandidates.RayCoverDistance(frame.TransformPoint(_positionSolver.LastPhase1Target), forward, body.Head, body.TorsoTop),
            TightSpaceCandidates.RayCoverDistance(frame.TransformPoint(_currentOffset), forward, body.Head, body.TorsoTop));
        if (ordinaryCover >= OpeningCamera.EnterOrdinaryCover)
        {
            _openingStatus = describe ? $"off: ordinary solve fine (cover {ordinaryCover:F2})" : "off: ordinary solve fine";
            return;
        }

        var castsAtStart = describe ? CollisionUtils.CastCount : 0;
        var eye = eyeTransform.position;
        _intent.Sample(eye, _rig.Rotation, 0f);
        var target = frame.TransformPoint(desired);
        var now = Time.time;

        // The camera continuing from where it was, as the opening camera would move it. The solve can leave the camera
        // closer to a surface than the sphere's radius (its finest phase keeps 0.05 m), and a sweep starting there would
        // not see that surface: the start is pushed clear first, and if it cannot be the camera is not kept.
        var r = OpeningCamera.CameraRadius;
        var keptStart = _lastCameraWorld;
        var keptClear = _sweep.Overlap(keptStart, OpeningClearance, _eyeCameraHitMask) == 0 || _sweep.Depenetrate(ref keptStart, OpeningClearance, _eyeCameraHitMask, OpeningMaxPush);
        var velocity = Vector3.zero;
        var kept = keptClear ? StepOpeningCamera(keptStart, target, OpeningCamera.SmoothTime, ref velocity) : keptStart;
        _openingCheck = MeasureOpening(kept, castOrigin, eye, body, forward, desired, lean, ordinaryCover, default, true, !keptClear);
        var keptCheck = _openingCheck;
        if (OpeningCamera.CanEnter(keptCheck))
        {
            EnterOpening(now, false, default, kept, velocity);
            _currentOffset = frame.InverseTransformPoint(kept);
            if (describe)
                _openingCasts = CollisionUtils.CastCount - castsAtStart;
            return;
        }

        // Out through the opening in the lean's direction, then back toward the offset, to see where that comes to rest
        var side = frame.right * Mathf.Sign(lean);
        var way = _sweep.Move(castOrigin, castOrigin + side * OpeningCamera.BootstrapReach, r, _eyeCameraHitMask);
        var rest = _sweep.Move(way, target, r, _eyeCameraHitMask);
        var bootstrapCheck = MeasureOpening(rest, castOrigin, eye, body, forward, desired, lean, ordinaryCover, default, true, false);
        if (OpeningCamera.CanEnter(bootstrapCheck))
        {
            // From the camera, pushed clear; if it cannot be, from the same point swept in from the head, which is clear
            // (from the head always, it can stop well short of the camera: simulated jumps of half a metre)
            var start = keptClear ? keptStart : _sweep.Move(castOrigin, _lastCameraWorld, r, _eyeCameraHitMask);
            velocity = Vector3.zero;
            var first = StepOpeningCamera(start, way, OpeningCamera.SmoothTime, ref velocity);
            EnterOpening(now, true, way, first, velocity);
            _openingCheck = bootstrapCheck;
            _currentOffset = frame.InverseTransformPoint(first);
            _openingBootstrapped = true;
        }
        else
        {
            _openingStatus = describe
                ? $"refused: kept {OpeningCamera.DescribeRefusal(keptCheck)}; through the opening {OpeningCamera.DescribeRefusal(bootstrapCheck)}"
                : "refused";
        }

        if (describe)
            _openingCasts = CollisionUtils.CastCount - castsAtStart;
    }

    /// <summary>
    ///     While the opening camera is active: moves it (toward the offset, its bootstrap waypoint, or back to the head when
    ///     retreating), checks it, and returns the offset to place. On a handover the solver continues from this position;
    ///     on a cut it starts again from its own phase 1 target.
    /// </summary>
    private Vector3 StepOpening(Transform eyeTransform, Vector3 desired, Vector3 castOrigin)
    {
        var castsAtStart = _debug ? CollisionUtils.CastCount : 0;
        var now = Time.time;
        var frame = _rig.Pivot;
        var forward = frame.forward;
        var eye = eyeTransform.position;
        var body = GetBodyShape();
        var lean = localPlayer.MovementContext.SmoothedTilt;
        var retreating = _opening.State == OpeningCamera.Phase.Retreating;

        // Out of anything it was found in first: a move from inside a collider would not see it
        var clear = ResolveOpeningCamera(ref _openingCamera);

        // Toward the bootstrap waypoint, the head when retreating, or the offset
        if (clear)
        {
            var goal = _opening.Travelling ? _opening.Waypoint : retreating ? castOrigin : frame.TransformPoint(desired);
            _openingCamera = StepOpeningCamera(_openingCamera, goal, retreating ? OpeningCamera.RetreatSmoothTime : OpeningCamera.SmoothTime, ref _openingVelocity);
            _openingMoveBlocker = _sweep.LastBlocker;
        }
        else
        {
            _openingVelocity = Vector3.zero;
        }

        if (_opening.Travelling && ((_openingCamera - _opening.Waypoint).sqrMagnitude < OpeningReachTolerance * OpeningReachTolerance || _opening.TravelExpired(now)))
            _opening.Arrived();

        // Where the ordinary solve would go now, for the handover and for a cut
        var ordinaryOffset = _positionSolver.Phase1Target(frame, desired, castOrigin, _eyeCameraHitMask);
        var ordinary = frame.TransformPoint(ordinaryOffset);

        OpeningAction action;
        if (retreating || !body.Valid)
        {
            var hidden = CollisionUtils.SphereCast(castOrigin, _openingCamera, OpeningHeadCastRadius, _eyeCameraHitMask).Score < OpeningHeadClearScore;
            action = body.Valid ? _opening.UpdateRetreat(hidden, now) : OpeningAction.Cut;
        }
        else
        {
            _intent.Sample(eye, _rig.Rotation, 0f);
            var ordinaryCover = TightSpaceCandidates.RayCoverDistance(ordinary, forward, body.Head, body.TorsoTop);
            _openingCheck = MeasureOpening(_openingCamera, castOrigin, eye, body, forward, desired, lean, ordinaryCover, ordinary, false, !clear);
            action = _opening.Update(_openingCheck, now, _debug);
        }

        _openingStatus = _opening.Reason;
        if (action is OpeningAction.Retreat or OpeningAction.Cut)
            RecordOpeningBlocker(action);
        if (_debug)
            _openingCasts = CollisionUtils.CastCount - castsAtStart;

        switch (action)
        {
            case OpeningAction.Handover:
                _positionSolver.ResetVelocity();
                _openingLastClearValid = false;
                return frame.InverseTransformPoint(_openingCamera);
            case OpeningAction.Cut:
                if (!body.Valid)
                    _opening.Reset("cut: no body");
                _positionSolver.ResetVelocity();
                _openingLastClearValid = false;
                return ordinaryOffset;
            default:
                return frame.InverseTransformPoint(_openingCamera);
        }
    }

    private void EnterOpening(float now, bool bootstrapped, Vector3 waypoint, Vector3 camera, Vector3 velocity)
    {
        _opening.Enter(now, bootstrapped, waypoint);
        _openingCamera = camera;
        _openingVelocity = velocity;
        _openingLastClear = camera;
        _openingLastClearValid = true;
        _openingOverlapping = false;
        _openingStatus = _opening.Reason;
    }

    /// <summary>
    ///     Makes sure the opening camera's sphere is clear where it is. Found overlapping something (a rough mesh it slid onto,
    ///     a crease, a door leaf swinging into it), it is pushed out by depenetration; failing that it goes back to where it
    ///     was last found clear, if that is still clear. False only if neither works: the camera is stuck inside something,
    ///     and OpeningCamera cuts once that has lasted StuckCutTime. One overlap query when clear.
    /// </summary>
    private bool ResolveOpeningCamera(ref Vector3 position)
    {
        if (_sweep.Overlap(position, OpeningClearance, _eyeCameraHitMask) == 0)
        {
            _openingLastClear = position;
            _openingLastClearValid = true;
            _openingOverlapping = false;
            return true;
        }

        var overlapped = _sweep.LastOverlap;
        string resolution;
        var resolved = true;
        var pushed = position;
        if (_sweep.Depenetrate(ref pushed, OpeningClearance, _eyeCameraHitMask, OpeningMaxPush))
        {
            position = pushed;
            _openingLastClear = position;
            resolution = "pushed out";
        }
        else if (_openingLastClearValid && _sweep.Overlap(_openingLastClear, OpeningClearance, _eyeCameraHitMask) == 0)
        {
            position = _openingLastClear;
            resolution = "back to last clear";
        }
        else
        {
            resolution = "could not move out";
            resolved = false;
        }

        RecordOpeningOverlap(overlapped, resolution);
        return resolved;
    }

    /// <summary>
    ///     An overlap occurrence (the first frame of a run of them): counted, described for the debug UI and logged when
    ///     either is on. Strings only at the event.
    /// </summary>
    private void RecordOpeningOverlap(Collider collider, string resolution)
    {
        if (_openingOverlapping)
            return;

        _openingOverlapping = true;
        _openingOverlapEvents++;
        if (!_debug && !Plugin.LoggingEnabled)
            return;

        _openingOverlapInfo = $"{DescribeCollider(collider)}, {resolution}";
        if (Plugin.LoggingEnabled)
            Plugin.Log.LogInfo($"Opening camera overlap #{_openingOverlapEvents}: {_openingOverlapInfo}");
    }

    /// <summary>
    ///     What the camera's last move had stopped at when the mode retreats or cuts.
    /// </summary>
    private void RecordOpeningBlocker(OpeningAction action)
    {
        if (!_debug && !Plugin.LoggingEnabled)
            return;

        _openingBlockerInfo = $"{(action == OpeningAction.Cut ? "cut" : "retreat")}, last stopped at {DescribeCollider(_openingMoveBlocker)}";
        if (Plugin.LoggingEnabled)
            Plugin.Log.LogInfo($"Opening camera {_opening.Reason}: {_openingBlockerInfo}");
    }

    private static string DescribeCollider(Collider collider)
    {
        if (collider == null)
            return "nothing";

        var go = collider.gameObject;
        return $"{collider.name} (root {collider.transform.root.name}, layer {LayerMask.LayerToName(go.layer)})";
    }

    /// <summary>
    ///     One step of the opening camera from a clear position: eased toward the goal like the solver's phase 1, as a sweep,
    ///     so it stops at and slides along what it meets. The velocity is what it actually moved, so pushing against a wall
    ///     does not wind it up.
    /// </summary>
    private Vector3 StepOpeningCamera(Vector3 from, Vector3 goal, float smoothTime, ref Vector3 velocity)
    {
        var deltaTime = Time.deltaTime;
        if (!(deltaTime > 0f))
            return from;

        var step = Vector3.SmoothDamp(from, goal, ref velocity, smoothTime / Plugin.CameraMoveSpeedScale, Mathf.Infinity, deltaTime);
        var moved = _sweep.Move(from, step, OpeningCamera.CameraRadius, _eyeCameraHitMask);
        velocity = (moved - from) / deltaTime;
        return moved;
    }

    /// <summary>
    ///     What OpeningCamera decides on, for a camera position. overlap is whether it is in something it could not be moved
    ///     out of; for a candidate not yet in use, it is tested here. The ordinary target is swept to only when its cover could
    ///     allow a handover. Casts: the view sample lines (stopping at need), maybe an overlap test and a sweep, and a head
    ///     cast for the debug UI.
    /// </summary>
    private OpeningCheck MeasureOpening(Vector3 camera, Vector3 castOrigin, Vector3 eye, in BodyShape body, Vector3 forward, Vector3 desired,
        float lean, float ordinaryCover, Vector3 ordinary, bool entering, bool overlap)
    {
        var check = new OpeningCheck
        {
            Lean = Mathf.Abs(lean),
            OrdinaryCover = ordinaryCover,
            CameraCover = TightSpaceCandidates.RayCoverDistance(camera, forward, body.Head, body.TorsoTop),
            Distance = (camera - eye).magnitude,
            MaxDistance = desired.magnitude + OpeningCamera.DistanceSlack,
            Samples = _intent.Count,
            Overlap = overlap || (entering && _sweep.Overlap(camera, OpeningCamera.CameraRadius, _eyeCameraHitMask) > 0),
            // No rule reads it (a retreat makes its own head cast), only the debug UI: a cast saved otherwise
            HiddenFromHead = _debug && CollisionUtils.SphereCast(castOrigin, camera, OpeningHeadCastRadius, _eyeCameraHitMask).Score < OpeningHeadClearScore
        };

        var need = entering ? OpeningCamera.SeenNeededToEnter(_intent.Count) : OpeningCamera.SeenToStay;
        check.Seen = _intent.CountSeenBy(camera, OpeningCamera.CameraRadius, _eyeCameraHitMask, need);

        if (!entering && ordinaryCover >= OpeningCamera.HandoverCover)
            check.OrdinaryReachable = (_sweep.Move(camera, ordinary, OpeningCamera.CameraRadius, _eyeCameraHitMask) - ordinary).sqrMagnitude
                                      < OpeningReachTolerance * OpeningReachTolerance;

        return check;
    }

    /// <summary>
    ///     Cast counts per section (from CollisionUtils.CastCount marks), a per-second peak, and the time. Debug UI only.
    /// </summary>
    private void RecordSolveCost(int castsAtStart, int castsAfterOrigin, int castsAfterBoom)
    {
        var castsAtEnd = CollisionUtils.CastCount;
        _originCasts = castsAfterOrigin - castsAtStart;
        _boomCasts = castsAfterBoom - castsAfterOrigin;
        _solverCasts = castsAtEnd - castsAfterBoom;
        _solveCasts = castsAtEnd - castsAtStart;

        var now = Time.unscaledTime;
        if (now - _solveCastsWindowStart >= 1f)
        {
            _solveCastsPeak = _solveCastsWindowPeak;
            _solveCastsWindowPeak = 0;
            _solveCastsWindowStart = now;
        }

        _solveCastsWindowPeak = Mathf.Max(_solveCastsWindowPeak, _solveCasts);

        _solveWatch.Stop();
        _solveMs = _solveWatch.Elapsed.TotalMilliseconds;
        _solveMsAverage += (_solveMs - _solveMsAverage) * SolveMsAverageWeight;
    }

    /// <summary>
    ///     The tight space camera, once per 3rd person LateUpdate between the cast origin and the collision solve: decides the
    ///     boom (probing only when something it depends on changed), eases it, and returns the offset to give the solver.
    ///     Returns desired unchanged and resets when the setting is off or the game poses the body.
    /// </summary>
    private Vector3 UpdateTightSpace(Transform eyeTransform, Vector3 pivot, Vector3 desired, Vector3 castOrigin, bool aiming)
    {
        var closest = Mathf.Clamp(Plugin.CameraTightSpaceClosest.Value, TightSpaceMinClosest, 1f);
        _boomClosest = closest;

        string inactive = null;
        if (!Plugin.CameraTightSpace.Value)
            inactive = "Off";
        else if (closest >= 1f)
            inactive = "Off (closest 1)";
        else if (IsBodyPosedByGame)
            inactive = "Posed by game";

        // Stage 2 off is exactly Stage 1: reset every frame, nothing probed or computed for it
        var overHead = Plugin.CameraTightSpaceOverHead.Value;
        _candidateSwitched = false;

        if (inactive != null)
        {
            _boom.Reset();
            _candidates.Reset();
            _boomDecisionValid = false;
            _boomLastFrame = -10;
            _trailCancel = Vector3.zero;
            _levelsProbed = 0;
            _boomStatus = inactive;
            return desired;
        }

        var frameCount = Time.frameCount;
        var entering = _boomLastFrame != frameCount - 1;
        _boomLastFrame = frameCount;

        // Back in 3rd person (or back on) the camera starts at home
        if (!overHead || entering)
            _candidates.Reset();

        var frame = _rig.Pivot;
        var eye = eyeTransform.position;

        // The pivot trails the character's travel (PivotTrail) by up to a third of a metre: a short boom is measured from
        // the eye instead, or the trail would keep the camera behind a wall plane the eye has crossed. Horizontal only,
        // like the trail; in the pivot frame, like the offset.
        var trail = eye - pivot;
        trail.y = 0f;
        _trailCancel = frame.InverseTransformVector(trail);

        var now = Time.time;

        // Where the solver left the camera, every frame: whether the character covers the middle of the view. No cast.
        // Not during an orbit, which swings the view round the character on purpose.
        if (overHead && !_rig.HasOrbit && !_opening.Active)
            _candidates.UpdateCollapse(GetSolvedCoverDistance(desired), now);

        if (_opening.Active)
        {
            // The opening camera is placing the camera on the far side of the wall: boom levels and the over the head
            // position are judged along lines from the head, which the wall cuts, so both hold where they are
            _boomDecisionValid = false;
            _boom.Propose(_boom.Target, now);
            _boomStatus = "Held (opening camera)";
            _candidates.Hold();
            _candidateProbe = "opening camera";
        }
        else if (_rig.HasOrbit)
        {
            // The view samples are along the aim, which an orbit swings off screen: no probes, ease back to the configured
            // offset (the solver keeps it out of walls as always). Stage 2 stays where it is.
            _boomDecisionValid = false;
            if (entering)
                _boom.Snap(1f, closest);
            else
                _boom.Propose(1f, now);
            _boomStatus = "Orbit";
            _candidates.Hold();
            _candidateProbe = "orbit";
        }
        else if (!entering && CanReuseBoomDecision(eye, pivot, desired, aiming, closest, now, overHead))
        {
            _boom.Propose(_boom.Proposed, now);
            _boomStatus = "Held";
            if (overHead)
                RequestCandidate(_candidateWanted, aiming, now);
        }
        else
        {
            var proposed = DecideBoom(eye, frame, desired, castOrigin, aiming, closest, out var facing);
            if (entering)
                _boom.Snap(proposed, closest);
            else
                _boom.Propose(proposed, now);

            _candidateCasts = 0;
            if (overHead)
            {
                if (facing || aiming)
                {
                    // Nothing to judge facing a surface; no lateral or vertical move while aiming down sights
                    _candidateWanted = _candidates.Active;
                    _candidates.Hold();
                    _candidateProbe = facing ? "facing surface" : "aiming (frozen)";
                }
                else
                {
                    DecideCandidate(eye, frame, desired, castOrigin, closest, now);
                }
            }

            _boomDecisionValid = true;
            _boomDecisionEye = eye;
            _boomDecisionPivot = pivot;
            _boomDecisionAim = _rig.Rotation;
            _boomDecisionDesired = desired;
            _boomDecisionAiming = aiming;
            _boomDecisionClosest = closest;
            _boomDecisionTarget = _boom.Target;
            _boomDecisionCollapsing = _candidates.Collapsing;
            _boomDecisionActive = _candidates.Active;
            _boomDecisionTime = now;
            _boomStatus = facing ? "Facing surface (held)" : entering ? "Probed (entry snap)" : "Probed";
        }

        _boom.Step(aiming, closest, Plugin.CameraMoveSpeedScale, Time.deltaTime);

        var home = BoomModulator.Shape(desired, _boom.Boom, _boom.Lateral, _trailCancel, BoomModulator.Shortening(_boom.Boom, closest));
        if (!overHead)
            return home;

        _candidates.Step(aiming, Plugin.CameraMoveSpeedScale, Time.deltaTime);
        _candidateSwitched = _candidates.ConsumeSwitch();

        return _candidates.Shown == TightSpaceCandidates.Home
            ? home
            : _candidates.Offset(home, TightSpaceCandidates.Shape(_candidates.Shown, desired, _trailCancel, closest));
    }

    /// <summary>
    ///     Re-asks for the last wanted position while a decision is reused, so its dwell keeps running; aiming down sights
    ///     holds instead.
    /// </summary>
    private void RequestCandidate(int wanted, bool aiming, float now)
    {
        if (aiming)
            _candidates.Hold();
        else
            _candidates.Request(wanted, now);
    }

    /// <summary>
    ///     Whether the last decision still stands: nothing it was made from moved or changed (the target included, since a
    ///     level's threshold depends on it) and it is recent enough to have seen doors and people move.
    /// </summary>
    private bool CanReuseBoomDecision(Vector3 eye, Vector3 pivot, Vector3 desired, bool aiming, float closest, float now, bool overHead)
    {
        // Stage 2 re-decides when the collapse state or the position in use changed, whatever else did not
        if (overHead && (_candidates.Collapsing != _boomDecisionCollapsing || _candidates.Active != _boomDecisionActive))
            return false;

        return _boomDecisionValid
               && now - _boomDecisionTime <= TightSpaceReuseMaxAge
               && (eye - _boomDecisionEye).sqrMagnitude <= TightSpaceReuseMoveSqr
               && (pivot - _boomDecisionPivot).sqrMagnitude <= TightSpaceReuseMoveSqr
               && Quaternion.Angle(_rig.Rotation, _boomDecisionAim) <= TightSpaceReuseAngle
               && desired == _boomDecisionDesired
               && aiming == _boomDecisionAiming
               && closest == _boomDecisionClosest
               && _boom.Target == _boomDecisionTarget;
    }

    /// <summary>
    ///     Samples the character's view from the eye, then probes the boom levels longest first and takes the first that
    ///     misses no more than it may (BoomModulator.AllowedMissesFor); if none does, the longest within reach of the best.
    ///     Facing a surface closer than BoomModulator.FacingSurfaceDistance nothing is probed and the target is kept.
    ///     Casts: IntentProbe.Sample, then per level probed one collision cast plus Evaluate's.
    /// </summary>
    private float DecideBoom(Vector3 eye, Transform frame, Vector3 desired, Vector3 castOrigin, bool aiming, float closest, out bool facing)
    {
        _levelsProbed = 0;
        facing = !_intent.Sample(eye, _rig.Rotation, BoomModulator.FacingSurfaceDistance);
        if (facing)
            return _boom.Target;

        var body = GetBodyShape();

        for (var level = 0; level < BoomModulator.LevelCount; level++)
        {
            var boom = BoomModulator.LevelBoom(level, closest);
            var lateral = _boom.CandidateLateral(boom, closest, aiming);
            var offset = BoomModulator.Shape(desired, boom, lateral, _trailCancel, BoomModulator.Shortening(boom, closest));
            var camera = CollisionUtils.SphereCast(castOrigin, frame.TransformPoint(offset), TightSpaceCandidateRadius, _eyeCameraHitMask).TangentPoint;
            _levelCameras[level] = camera;

            var allowed = _boom.AllowedMissesFor(boom);
            // The per-sample classification is only drawn by the debug UI
            var result = _intent.Evaluate(camera, eye, allowed, body, _debug ? _levelViews[level] : null);
            _levelResults[level] = result;
            _levelMisses[level] = result.Misses;
            _levelsProbed = level + 1;

            if (result.Misses <= allowed)
            {
                _boomDecidedLevel = level;
                RecordHome(true);
                return boom;
            }
        }

        var fallback = _boom.FallbackBoom(_levelMisses, BoomModulator.LevelCount, closest, out _boomDecidedLevel);
        RecordHome(false);
        return fallback;
    }

    private void RecordHome(bool passed)
    {
        _homePassed = passed;
        _homeCamera = _levelCameras[_boomDecidedLevel];
        _homeMisses = _levelMisses[_boomDecidedLevel];
    }

    /// <summary>
    ///     Stage 2's decision, after Stage 1 probed this frame (and not facing a surface). At home: probes the alternatives
    ///     only while collapsing, taking the first that is not covered itself and misses at least SwitchMargin fewer than
    ///     collapsed home (whose cost includes CoverPenalty). Away from home: returns once home is clean on its own terms
    ///     (Stage 1's level passes the lengthen rule and its predicted camera keeps the centre ray ReturnCoverDistance from
    ///     the character), or the alternative has become clearly worse than home. Casts: one collision cast and up to a
    ///     sample set per position looked at, nothing at home without a collapse.
    /// </summary>
    private void DecideCandidate(Vector3 eye, Transform frame, Vector3 desired, Vector3 castOrigin, float closest, float now)
    {
        // The descriptions are built per decision, which is every frame while moving: only when someone can read them
        var describe = _debug;
        var castsAtStart = describe ? CollisionUtils.CastCount : 0;
        var body = GetBodyShape();
        var forward = frame.forward;
        var wanted = TightSpaceCandidates.Home;

        if (_candidates.Active == TightSpaceCandidates.Home)
        {
            if (!_candidates.Collapsing || !body.Valid)
            {
                _candidateProbe = "no collapse";
            }
            else
            {
                var allowed = TightSpaceCandidates.Cost(_homeMisses, true) - TightSpaceCandidates.SwitchMargin;
                if (describe)
                    _candidateProbe = $"collapse, home {TightSpaceCandidates.Cost(_homeMisses, true)} (misses {_homeMisses} + cover {TightSpaceCandidates.CoverPenalty})";

                for (var k = 0; k < TightSpaceCandidates.Alternatives.Length; k++)
                {
                    var camera = ClampCandidate(castOrigin, frame, TightSpaceCandidates.Shape(k, desired, _trailCancel, closest));
                    var cover = TightSpaceCandidates.RayCoverDistance(camera, forward, body.Head, body.TorsoTop);
                    if (cover < TightSpaceCandidates.CoverRadius)
                    {
                        if (describe)
                            _candidateProbe += $" | {TightSpaceCandidates.NameOf(k)}: covered ({cover:F2}m)";
                        continue;
                    }

                    var result = _intent.Evaluate(camera, eye, allowed, body, null, allowed);
                    if (describe)
                        _candidateProbe += $" | {TightSpaceCandidates.NameOf(k)}: {DescribeSight(result, allowed)}";
                    if (result.Misses <= allowed)
                    {
                        wanted = k;
                        break;
                    }
                }
            }
        }
        else
        {
            var active = _candidates.Active;
            var homeCover = _homePassed && body.Valid ? TightSpaceCandidates.RayCoverDistance(_homeCamera, forward, body.Head, body.TorsoTop) : 0f;
            var clean = false;
            if (_homePassed && homeCover >= TightSpaceCandidates.ReturnCoverDistance)
            {
                var homeResult = _intent.Evaluate(_homeCamera, eye, BoomModulator.LengthenAllowedMisses, body, null, BoomModulator.LengthenAllowedMisses);
                clean = homeResult.Misses <= BoomModulator.LengthenAllowedMisses;
                if (describe)
                    _candidateProbe = $"home cover {homeCover:F2}m, {DescribeSight(homeResult, BoomModulator.LengthenAllowedMisses)}";
            }
            else if (describe)
            {
                _candidateProbe = _homePassed ? $"home cover {homeCover:F2}m" : "home level failed";
            }

            if (clean)
            {
                if (describe)
                    _candidateProbe += ": clean";
            }
            else
            {
                var homeCost = TightSpaceCandidates.Cost(_homeMisses, homeCover < TightSpaceCandidates.CoverRadius);
                var camera = ClampCandidate(castOrigin, frame, TightSpaceCandidates.Shape(active, desired, _trailCancel, closest));
                var covered = TightSpaceCandidates.RayCoverDistance(camera, forward, body.Head, body.TorsoTop) < TightSpaceCandidates.CoverRadius;
                // Staying as long as the alternative is not SwitchMargin worse than home
                var limit = homeCost + TightSpaceCandidates.SwitchMargin - 1 - (covered ? TightSpaceCandidates.CoverPenalty : 0);
                var holds = false;
                if (limit >= 0)
                {
                    var result = _intent.Evaluate(camera, eye, limit, body, null, limit);
                    holds = result.Misses <= limit;
                    if (describe)
                        _candidateProbe += $" | home {homeCost} | {TightSpaceCandidates.NameOf(active)}{(covered ? " covered" : "")}: {DescribeSight(result, limit)}";
                }
                else if (describe)
                {
                    _candidateProbe += $" | home {homeCost} | {TightSpaceCandidates.NameOf(active)} covered";
                }

                if (holds)
                    wanted = active;
            }
        }

        _candidateWanted = wanted;
        _candidates.Request(wanted, now);
        if (describe)
            _candidateCasts = CollisionUtils.CastCount - castsAtStart;
    }

    private Vector3 ClampCandidate(Vector3 castOrigin, Transform frame, Vector3 offset)
    {
        return CollisionUtils.SphereCast(castOrigin, frame.TransformPoint(offset), TightSpaceCandidateRadius, _eyeCameraHitMask).TangentPoint;
    }

    private static string DescribeSight(in SightResult result, int allowed)
    {
        return $"{result.Misses}/{allowed} g{result.Geometry} c{result.Character} f{result.Far} s{result.Graze} u{result.Unchecked}";
    }

    /// <summary>
    ///     How far the camera's centre ray, where the solver left it last frame (this frame's pivot), passes from the
    ///     character's head-to-ribcage line. No cast. Infinite (never a collapse) when the configured offset itself keeps
    ///     the ray closer than home would have to be to come back to: a centred camera is the player's framing, not
    ///     something the solver did, and a camera lifted over the head from it could never return.
    /// </summary>
    private float GetSolvedCoverDistance(Vector3 desired)
    {
        var body = GetBodyShape();
        if (!body.Valid)
            return float.PositiveInfinity;

        var frame = _rig.Pivot;
        var forward = frame.forward;
        if (TightSpaceCandidates.RayCoverDistance(frame.TransformPoint(desired), forward, body.Head, body.TorsoTop) < TightSpaceCandidates.ReturnCoverDistance)
            return float.PositiveInfinity;

        return TightSpaceCandidates.RayCoverDistance(frame.TransformPoint(_currentOffset), forward, body.Head, body.TorsoTop);
    }

    /// <summary>
    ///     The character as a head sphere and a chest-to-pelvis capsule, from this frame's bones.
    /// </summary>
    private BodyShape GetBodyShape()
    {
        var bones = localPlayer.PlayerBones;
        var head = bones != null ? bones.Head?.Original : null;
        var ribcage = bones != null ? bones.Ribcage?.Original : null;
        var pelvis = bones != null ? bones.Pelvis?.Original : null;

        if (head == null || ribcage == null || pelvis == null)
            return default;

        return new BodyShape
        {
            Valid = true,
            Head = head.position,
            HeadRadius = BodyHeadRadius,
            TorsoTop = ribcage.position,
            TorsoBottom = pelvis.position,
            TorsoRadius = BodyTorsoRadius
        };
    }

    /// <summary>
    ///     _currentOffset as the aim maths sees it (see UpdateAimOffsetCore). With the tight space camera on, the aim solver's
    ///     smoothed distance is also moved by the aim camera's forward travel, so a boom change (which slides the camera along
    ///     its own centre ray) leaves the smoothed aim point where it is instead of re-converging onto it.
    /// </summary>
    private void UpdateAimOffset()
    {
        var before = _aimOffset;
        UpdateAimOffsetCore();

        if (Plugin.CameraTightSpace.Value)
            _aimSolver.ShiftOrigin(_aimOffset.z - before.z);
    }

    /// <summary>
    ///     Exactly _currentOffset without an orbit. Frozen while the rig has one; afterwards eased back at a bounded speed,
    ///     then exact again once it has caught up.
    /// </summary>
    private void UpdateAimOffsetCore()
    {
        if (_rig.HasOrbit)
        {
            _aimOffsetDetached = true;
            return;
        }

        if (_aimOffsetDetached)
        {
            var step = AimOffsetCatchUpSpeed * Time.deltaTime;
            if ((_currentOffset - _aimOffset).sqrMagnitude > step * step)
            {
                _aimOffset = Vector3.MoveTowards(_aimOffset, _currentOffset, step);
                return;
            }

            _aimOffsetDetached = false;
        }

        _aimOffset = _currentOffset;
    }

    /// <summary>
    ///     The camera pivot: the eye moved by Body Bob's share of the difference between its filtered and raw offsets from
    ///     the body, plus the trail's lag behind the travel. That is anchor + lag + yaw * Lerp(raw, filtered, weight): the
    ///     filter smooths only the body-relative bob, the trail only the travel. Everything but the filtered offset and
    ///     the lag is read live, so the aim solve in Update sees this frame's root motion. Exactly the eye while bypassed.
    /// </summary>
    private Vector3 GetPivotPosition(Transform eyeTransform)
    {
        var eye = eyeTransform.position;

        if (IsPivotFilterBypassed || !TryGetPivotFrame(eye, out var yaw, out var raw))
            return eye;

        // The trail lag is added before the clamp, so a pivot left behind a corner the character has just walked round is
        // pulled back to the eye's side of it like any other pivot offset
        var pivot = eye + yaw * ((_pivotFilter.Offset - raw) * _bodyBobWeight) + _pivotTrail.Lag;
        return ClampPivotToEye(eye, pivot);
    }

    /// <summary>
    ///     The game keeps the real eye out of geometry, a pivot that lags it is not: pull it back to just short of anything
    ///     between the two, with the mask the camera solver uses for its own origin.
    /// </summary>
    private Vector3 ClampPivotToEye(Vector3 eye, Vector3 pivot)
    {
        var delta = pivot - eye;
        var sqrDistance = delta.sqrMagnitude;
        if (!(sqrDistance > PivotMinCastSqr))
            return pivot;

        var distance = Mathf.Sqrt(sqrDistance);
        var direction = delta / distance;

        if (Physics.Raycast(eye, direction, out var hit, distance + PivotCollisionSkin, _eyeCameraHitMask, QueryTriggerInteraction.Ignore))
            return eye + direction * Mathf.Max(0f, Mathf.Min(distance, hit.distance - PivotCollisionSkin));

        return pivot;
    }

    /// <summary>
    ///     The eye's offset from the anchor in the body's yaw frame. The anchor is the animated model root: it moves with
    ///     the character (its height smoothed over steps by Player.HeightInterpolation) but not with the gait. The yaw is
    ///     the body root's, which MovementContext.ApplyRotation turns, so turning the character is not an offset change.
    /// </summary>
    private bool TryGetPivotFrame(Vector3 eye, out Quaternion yaw, out Vector3 rawOffset)
    {
        yaw = Quaternion.identity;
        rawOffset = default;

        var bones = localPlayer.PlayerBones;
        var body = bones != null ? bones.BodyTransform?.Original : null;
        if (body == null)
            return false;

        var animated = bones.AnimatedTransform?.Original;
        var anchor = animated != null ? animated.position : body.position;

        // From the forward vector, as CameraRig.SyncFrom: only the heading, whatever the body's pitch (prone on a slope)
        var forward = body.rotation * Vector3.forward;
        if (forward.x * forward.x + forward.z * forward.z > PivotMinCastSqr)
            _lastBodyYaw = Quaternion.Euler(0f, Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 0f);

        yaw = _lastBodyYaw;
        rawOffset = Quaternion.Inverse(yaw) * (eye - anchor);

        return !float.IsNaN(rawOffset.x) && !float.IsNaN(rawOffset.y) && !float.IsNaN(rawOffset.z)
               && !float.IsInfinity(rawOffset.x) && !float.IsInfinity(rawOffset.y) && !float.IsInfinity(rawOffset.z);
    }

    /// <summary>
    ///     Pivot on the eye: in 1st person (3rd person relies on starting there) and while the game poses the body itself
    ///     (vault and climb, mounting, stationary weapons, vehicles), which a slow filter should not chase.
    /// </summary>
    private bool IsPivotFilterBypassed => !IsViewThirdPerson || IsBodyPosedByGame;

    /// <summary>
    ///     Once per LateUpdate, before anything reads the pivot.
    /// </summary>
    private void UpdatePivotFilter(Transform eyeTransform)
    {
        var deltaTime = Time.deltaTime;
        _bodyBobWeight = Mathf.MoveTowards(_bodyBobWeight, Mathf.Clamp01(Plugin.BodyBob.Value), BodyBobWeightRate * deltaTime);

        // ADS in 3rd person aims precisely, the camera stays on the character there (1st person ADS is bypassed anyway)
        // Read live: _handsController is only refreshed after this runs
        var handsController = localPlayer.HandsController as Player.ItemHandsController;
        var aiming = handsController != null && handsController.IsAiming;
        var followScale = aiming ? 0f : 1f;
        _followDeadzone = Mathf.MoveTowards(_followDeadzone, Mathf.Max(0f, Plugin.CameraFollowDeadzone.Value) * followScale, FollowDeadzoneRate * deltaTime);
        _followSmoothing = Mathf.MoveTowards(_followSmoothing, Mathf.Max(0f, Plugin.CameraFollowSmoothing.Value) * followScale, FollowSmoothingRate * deltaTime);

        var eye = eyeTransform.position;
        if (!TryGetPivotFrame(eye, out var yaw, out var raw))
        {
            _pivotFilter.Reset();
            _pivotTrail.Reset();
            return;
        }

        _pivotRawOffset = raw;
        // The frame's anchor: the trail follows the character's travel, the filter its eye relative to that
        var anchor = eye - yaw * raw;

        if (IsPivotFilterBypassed)
        {
            _pivotFilter.Snap(raw);
            _pivotTrail.Snap(anchor);
        }
        else
        {
            _pivotFilter.Step(raw, deltaTime);
            _pivotTrail.Step(anchor, _followDeadzone, _followSmoothing, deltaTime);
        }
    }

    /// <summary>
    ///     Called from the MoveInputTranslator.TranslateAxes prefix (Update) with the look axes as TranslateAxes hands
    ///     them to Player.Look. They are only non-zero while the free look key is held. Read only: the game's own Look
    ///     still turns the head with them. The orbit only applies while the view really is 3rd person.
    /// </summary>
    public void OnFreeLookInput(Vector2 rawDelta)
    {
        if ((rawDelta.x == 0f && rawDelta.y == 0f) || !CameraRig.IsFinite(rawDelta) || !IsViewThirdPerson)
            return;

        _orbitHeld = true;

        var state = localPlayer.CurrentManagedState;
        if (state == null)
            return;

        // Scaled like normal mouse look (OnLookInput) rather than raw like Look's head turn, so the orbit has the
        // player's sensitivity. The look and turn axes have the same bindings, so the signs match normal look too.
        Vector2 scaledDelta = state.ClampRotation(rawDelta);
        _rig.AddOrbit(scaledDelta.x, scaledDelta.y, Plugin.FreeLookMaxAngle.Value);
    }

    /// <summary>
    ///     Called from the Player.ResetLookDirection postfix: the free look key was released (or a cutscene reset the
    ///     look), the orbit returns from here on.
    /// </summary>
    public void OnFreeLookReset()
    {
        _orbitHeld = false;
    }

    private void UpdateOrbit()
    {
        if (!IsViewThirdPerson)
        {
            // 1st person (ADS included) keeps the game's native free look; a leftover orbit must not reappear later
            _orbitHeld = false;
            _rig.ClearOrbit();
            return;
        }

        if (!_orbitHeld)
            _rig.DecayOrbit(OrbitReturnRate, Time.deltaTime);

        // Every frame: the max angle is live and the aim pitch the pitch limit depends on moves
        _rig.ClampOrbit(Plugin.FreeLookMaxAngle.Value);
    }

    /// <summary>
    ///     Locks the rig to Cam's parent while the game poses the body itself (vaulting and climbing, mounting, vehicles):
    ///     those states rotate the player on their own, so the rig cannot own the look there and follows the game instead.
    /// </summary>
    private void SyncRigToPlayer(Transform eyeTransform)
    {
        _rig.SyncFrom(eyeTransform.rotation);
    }

    /// <summary>
    ///     Whether the rig owns the look direction: the mouse delta goes to the rig and the player is turned to follow it.
    ///     Otherwise the game handles look input itself and the rig is locked to the player (SyncRigToPlayer). Shared by
    ///     the Rotate patch (Update) and HandleThirdPerson (LateUpdate) so the two cannot disagree.
    /// </summary>
    public bool IsCapturingLook => IsViewThirdPerson && !IsBodyPosedByGame;

    /// <summary>
    ///     PlayerBody's value rather than Player.PointOfView, which our PoV override fakes as 1st person during VisualPass.
    ///     1st person ADS, the 3rd person toggle being off and stationary weapons all end up in 1st person here.
    /// </summary>
    private bool IsViewThirdPerson => localPlayer.PlayerBody.PointOfView.Value == EPointOfView.ThirdPerson;

    /// <summary>
    ///     The rotation function check covers the states that pose Cam's parent from something other than HandsRotation
    ///     (high vault/climb, stationary, mounted, cutscenes). The mounting approach and the BTR keep the default function
    ///     (the BTR swaps HandsRotation's source instead), hence the explicit checks for those.
    /// </summary>
    private bool IsBodyPosedByGame
    {
        get
        {
            var movementContext = localPlayer.MovementContext;
            return movementContext.RotationAction != MovementContext.DefaultRotationFunction
                   || movementContext.IsInMountedState
                   || movementContext.StationaryWeapon != null
                   || localPlayer.BtrState != EPlayerBtrState.Outside;
        }
    }

    /// <summary>
    ///     Called from the Player.Rotate prefix with the raw mouse delta (Update, only while IsCapturingLook). Puts the input
    ///     on the rig and returns the delta that turns the player onto it, to be applied with ignoreClamp. False leaves the
    ///     call to the game untouched. hadInput is whether the player's own (scaled) mouse input was non-zero, as opposed
    ///     to the delta being only a convergence correction.
    /// </summary>
    public bool OnLookInput(Vector2 rawDelta, out Vector2 aimDelta, out bool hadInput)
    {
        aimDelta = rawDelta;
        hadInput = true;

        var state = localPlayer.CurrentManagedState;
        // Player.Rotate rejects a non-finite delta itself, leave that (and its error log) to the game
        if (state == null || !CameraRig.IsFinite(rawDelta))
            return false;

        CaptureGameRotation();

        // Exactly the scaling the state would have applied: mouse/aiming sensitivity, state sensitivity, speed clamp
        Vector2 scaledDelta = state.ClampRotation(rawDelta);
        _rig.AddAngles(scaledDelta.x, scaledDelta.y);

        var current = localPlayer.MovementContext.Rotation;
        var delta = ComputeAimRotation(current) - current;

        if (!CameraRig.IsFinite(delta))
        {
            // Should not happen; give the game its input back and reseed the rig next time
            _rigCapturing = false;
            CloseConvergenceHold();
            return false;
        }

        if (Mathf.Abs(delta.x) < AimDeltaEpsilon) delta.x = 0f;
        if (Mathf.Abs(delta.y) < AimDeltaEpsilon) delta.y = 0f;

        _aimRequested = current + delta;
        aimDelta = delta;
        // Same threshold Player.Rotate uses to count a delta as movement
        hadInput = scaledDelta.sqrMagnitude >= 1E-45f;
        return true;
    }

    /// <summary>
    ///     Called from the Player.Rotate postfix after the game applied the delta from OnLookInput.
    /// </summary>
    public void OnLookApplied()
    {
        var actual = localPlayer.MovementContext.Rotation;

        // A state vetoed or altered the turn (prone against a wall, picking up) or a yaw/pitch limit clamped it. Pull the
        // rig back by the shortfall so the camera cannot run away from where the character is able to aim.
        _lastReconcileShortfall = CameraRig.AngleDelta(actual, _aimRequested);

        if (_convergenceActive)
        {
            if (Mathf.Abs(_lastReconcileShortfall.x) < ReconcileDeadband) _lastReconcileShortfall.x = 0f;
            if (Mathf.Abs(_lastReconcileShortfall.y) < ReconcileDeadband) _lastReconcileShortfall.y = 0f;
        }

        _rig.AddAngles(-_lastReconcileShortfall.x, -_lastReconcileShortfall.y);

        _knownPlayerRotation = actual;
    }

    /// <summary>
    ///     The player rotation the character should aim at, in the player's own yaw continuity (it can sit past ±180), so
    ///     subtracting the current rotation gives the delta to apply: the rotation that puts the line of fire through the
    ///     point under the centre of the screen, or parallel aim, exactly where the rig looks, when that cannot be solved.
    /// </summary>
    private Vector2 ComputeAimRotation(Vector2 current)
    {
        var desired = TrySolveConvergence(current, out var converged) ? converged : _rig.Angles;
        return current + CameraRig.AngleDelta(current, desired);
    }

    private bool TrySolveConvergence(Vector2 current, out Vector2 desired)
    {
        desired = default;

        if (!TryGetConvergenceInputs(out var ribcage, out var muzzleOffset, out var pivot))
        {
            _convergenceActive = false;
            CloseConvergenceHold();
            return false;
        }

        // A stale distance (back from a parallel fallback) would sweep the aim in from wherever it was
        if (!_convergenceActive)
            _aimSolver.ResetSmoothing();

        // The rig's current rotation, so this frame's mouse turn already orbits the camera
        var rigRotation = _rig.Rotation;
        var cameraPosition = pivot + rigRotation * _aimOffset;
        var cameraForward = rigRotation * Vector3.forward;
        var aimOrigin = ribcage + Quaternion.Euler(current.y, current.x, 0f) * muzzleOffset;

        var aimPoint = _aimSolver.UpdateAimPoint(
            cameraPosition, cameraForward, aimOrigin,
            Plugin.AimConvergenceMinDistance.Value, Plugin.AimConvergenceSpeed.Value, Time.deltaTime
        );

        if (!AimSolver.SolveConvergence(ribcage, muzzleOffset, aimPoint, current, out desired))
        {
            _convergenceFallback = "Degenerate solve";
            _convergenceActive = false;
            CloseConvergenceHold();
            return false;
        }

        _convergenceActive = true;
        _convergenceFallback = null;
        return true;
    }

    /// <summary>
    ///     Everything the convergence model needs, or false (with the reason recorded for the debug UI) to aim parallel.
    ///     The pivot is the one the camera is placed at each LateUpdate; read live so it is not stale after a spell in 1st
    ///     person and carries this frame's root motion when called from Update.
    /// </summary>
    private bool TryGetConvergenceInputs(out Vector3 ribcage, out Vector3 muzzleOffset, out Vector3 pivot)
    {
        ribcage = muzzleOffset = pivot = default;

        if (!_pivotPlaced)
        {
            _convergenceFallback = "No pivot yet";
            return false;
        }

        var ribcageBone = localPlayer.PlayerBones != null ? localPlayer.PlayerBones.Ribcage : null;
        var eyeTransform = localPlayer.CameraPosition != null ? localPlayer.CameraPosition.parent : null;

        if (ribcageBone?.Original == null || eyeTransform == null)
        {
            _convergenceFallback = "Missing transform";
            return false;
        }

        // Where the hands were placed this frame: the shoulder recoil pose may still be moving the ribcage when Update
        // runs ahead of its restore
        if (_shoulderRecoilPoser == null || !_shoulderRecoilPoser.TryGetRibcagePositionBeforePose(out ribcage))
            ribcage = ribcageBone.Original.position;
        // Low-passed every LateUpdate, toward zero without a firearm, so it is valid whatever is in hands
        muzzleOffset = _aimSolver.MuzzleOffset;
        pivot = GetPivotPosition(eyeTransform);

        // Read live: this runs in Update, possibly right after a weapon swap
        RefreshAimIgnores();

        return true;
    }

    /// <summary>
    ///     Our body parts and armour plates (the set bullets use to ignore the shooter), plus anything else under the
    ///     player or the scene-root hands object of whatever is in hands. Shared by the aim solve and the crosshair.
    /// </summary>
    private void RefreshAimIgnores()
    {
        var bones = localPlayer.PlayerBones;
        if (bones == null)
        {
            _aimSolver.SetIgnored(null, null, null);
            return;
        }

        var body = bones.BodyTransform?.Original;
        var hands = localPlayer.HandsController;
        var handsRoot = hands != null ? hands.ControllerGameObject : null;

        _aimSolver.SetIgnored(bones.BodyPartCollidersHashSet, body != null ? body.root : null, handsRoot != null ? handsRoot.transform : null);
    }

    /// <summary>
    ///     Measures where the fireport sits in the hands root's rotation frame. The hands root is placed at the ribcage
    ///     with HandsRotation, so this is the muzzle relative to the look rotation, whichever order the LateUpdates run in.
    ///     Without a firearm the target is zero: grenades, melee and empty hands aim from the ribcage.
    /// </summary>
    private void SampleMuzzleOffset()
    {
        var target = Vector3.zero;

        if (_firearmController != null)
        {
            var handsRoot = _firearmController.ControllerGameObject;
            var fireport = _firearmController.CurrentFireport;

            // Nothing to measure (mid setup): hold the current offset rather than pull it toward the ribcage
            if (handsRoot == null || fireport?.Original == null)
                return;

            var rootTransform = handsRoot.transform;
            target = Quaternion.Inverse(rootTransform.rotation) * (fireport.position - rootTransform.position);
        }

        _aimSolver.SampleMuzzleOffset(target, Time.deltaTime);
    }

    /// <summary>
    ///     Seeds the rig at the start of capture so that nothing moves: the player keeps its rotation and the point it is
    ///     aiming at goes under the centre of the screen. Seeding the rig to the player's rotation instead would have the
    ///     solver turn the player by the parallax correction on every return from 1st person ADS.
    /// </summary>
    private bool SeedRigForConvergence(Vector2 playerRotation)
    {
        if (!TryGetConvergenceInputs(out var ribcage, out var muzzleOffset, out var pivot))
        {
            _convergenceActive = false;
            CloseConvergenceHold();
            return false;
        }

        var playerLook = Quaternion.Euler(playerRotation.y, playerRotation.x, 0f);
        var muzzle = ribcage + playerLook * muzzleOffset;
        var target = _aimSolver.CastAimPoint(muzzle, playerLook * Vector3.forward, Plugin.AimConvergenceMinDistance.Value);

        // Iterate from the player's rotation
        _rig.SetAngles(playerRotation.x, playerRotation.y);
        _aimSolver.SnapDistance(PointRigAt(pivot, target));
        _convergenceActive = true;
        _convergenceFallback = null;

        // The camera is usually about to ease out from the eye, keep this target under the centre while it does
        _convergenceHoldUntil = Time.time + ConvergenceHoldDuration;

        return true;
    }

    /// <summary>
    ///     Turns the rig, from its current rotation, so the centre ray from pivot + rotation * _aimOffset passes through
    ///     target, and returns target's distance along that ray. The camera position depends on the rotation, hence the
    ///     iteration; it contracts by about |offset| / distance per step.
    /// </summary>
    private float PointRigAt(Vector3 pivot, Vector3 target)
    {
        for (var i = 0; i < 2; i++)
        {
            if (!AimSolver.TryYawPitch(target - (pivot + _rig.Rotation * _aimOffset), _rig.Yaw, out var rigAngles))
                break;

            _rig.SetAngles(rigAngles.x, rigAngles.y);
        }

        var rigRotation = _rig.Rotation;
        return Vector3.Dot(target - (pivot + rigRotation * _aimOffset), rigRotation * Vector3.forward);
    }

    /// <summary>
    ///     Where the centre ray points now, from the current rig rotation (this frame's mouse input included, so the hold
    ///     never fights it) and the aim camera as of the last solve.
    /// </summary>
    private Vector3 GetHeldAimPoint(Vector3 pivot)
    {
        var rigRotation = _rig.Rotation;
        return pivot + rigRotation * _aimOffset + rigRotation * Vector3.forward * _aimSolver.SmoothedDistance;
    }

    private bool IsConvergenceHoldOpen => Time.time < _convergenceHoldUntil;

    private void CloseConvergenceHold()
    {
        _convergenceHoldUntil = -1f;
    }

    /// <summary>
    ///     Seeds the rig when capture starts (SeedRigForConvergence, or the player's own rotation when the convergence
    ///     inputs are not available yet). Afterwards adds whatever the game itself did to the player's rotation since we
    ///     last looked (spawn, doors, approach animations...), so the camera follows game-driven turns without knowing
    ///     about each state.
    /// </summary>
    private void CaptureGameRotation()
    {
        var current = localPlayer.MovementContext.Rotation;

        if (_rigCapturing)
        {
            var external = CameraRig.AngleDelta(_knownPlayerRotation, current);
            _rig.AddAngles(external.x, external.y);
        }
        else if (!SeedRigForConvergence(current))
        {
            _rig.SetAngles(current.x, current.y);
        }

        _knownPlayerRotation = current;
        _rigCapturing = true;
    }

    private void UpdatePointOfView(EPointOfView value)
    {
        localPlayer.PointOfView = value;

        if (value == EPointOfView.ThirdPerson)
        {
            // The rig overwrites Cam's rotation from here on, so remember what 1st person had
            _fpCamLocalRotation = localPlayer.CameraPosition.localRotation;

            // We force weapon handling to be first person. This allows optic sight rendering to work correctly (they don't render properly otherwise).
            localPlayer.ProceduralWeaponAnimation.PointOfView = EPointOfView.FirstPerson;
            // Turnaway must be forced back to 3rd person otherwise the hands misbehave
            localPlayer.ProceduralWeaponAnimation.TurnAway.PointOfView = value;

            localPlayer.POM.CameraCollider.enabled = false;

            // Force our own custom weapon animation strategy that enables proper recoil
            localPlayer.ProceduralWeaponAnimation.SetStrategy(StaticData.CustomAnimStrategy);
            CameraManager.Instance.Camera.nearClipPlane = 0.005f;

            // Re-enable recoil and hit reactions in third person
            if (localPlayer.HitReaction != null)
            {
                localPlayer.HitReaction.enabled = true;
            }
        }
        else
        {
            localPlayer.POM.CameraCollider.enabled = true;
            // Reset the offset back to the BSG default
            localPlayer.CameraPosition.localPosition = _currentOffset = localPlayer.ProceduralWeaponAnimation.HandsContainer.CameraOffset;
            // The seed on the way back to 3rd person models the camera from here too, any orbit is gone in 1st person
            _aimOffset = _currentOffset;
            _aimOffsetDetached = false;
            // Drop the rotation the rig wrote in 3rd person. PWA.LerpCamera rewrites it every 1st person frame, this
            // only covers the frame of the switch. Its neutral value is prefab data, hence restoring rather than assuming.
            localPlayer.CameraPosition.localRotation = _fpCamLocalRotation;

            // Explicitly set the pwa strategy to mounted in this case, because the game would just set it to the FP strategy and that's incorrect
            if (value == EPointOfView.FirstPerson)
            {
                localPlayer.ProceduralWeaponAnimation.SetStrategy(
                    localPlayer.ProceduralWeaponAnimation.IsMountedState
                        ? localPlayer.MovementContext._mountingStrategy
                        : StaticData.FpAnimStrategy
                );
            }

            CameraManager.Instance.Camera.nearClipPlane = 0.03f;
        }

        if (_firearmController != null)
            _firearmController.UpdateSensitivity();
    }

    private void ScopeFoV()
    {
        var currentScopeIsOptic = localPlayer.ProceduralWeaponAnimation.CurrentScope.IsOptic;
        var fov = currentScopeIsOptic ? 35 : _gameSettings.Game.Settings.FieldOfView.Value + Plugin.AdsBasicFovChange.Value;
        SetFoV(fov);
    }

    private void AdjustFoV(int adjustment, float time)
    {
        CameraManager.Instance.SetFov(_gameSettings.Game.Settings.FieldOfView.Value + adjustment, time);
    }

    private static void SetFoV(int fov)
    {
        CameraManager.Instance.SetFov(fov, Plugin.AdsFovChangeTime.Value);
    }

    private void ResetFoV()
    {
        CameraManager.Instance.SetFov(_gameSettings.Game.Settings.FieldOfView.Value, Plugin.AdsFovChangeTime.Value);
    }

    /// <summary>
    ///     The crosshair and the debug overlay, from HudOverlay's repaint pass.
    /// </summary>
    public void DrawHud()
    {
        if (localPlayer == null || !localPlayer.HealthController.IsAlive)
            return;

        // CollisionDebug.DrawCollisionInfo(_positionSolver.Phase1.CircleScan);

        // @formatter:off
        // The frame's setting, as the debug records were made with
        if (_debug)
        {
            var pwa = localPlayer.ProceduralWeaponAnimation;
            var pwaStrat = Traverse.Create(pwa).Field("_strategy").GetValue();
            var leftStanceCurve = Traverse.Create(pwa).Field("_leftStanceCurrentCurveValue").GetValue();

            var hitReactionsEnabled = false;
            
            if (localPlayer.HitReaction != null)
                hitReactionsEnabled = localPlayer.HitReaction.enabled;
            
            var rect = DebugUI.Label(new Vector2(50, 50), $"PL POV: {localPlayer.PointOfView} TP Enabled: {_thirdPersonEnabled} PWA POV: {pwa.PointOfView} PWA Strat: {pwaStrat}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"IsAiming: {pwa.IsAiming} MoveWeapCloser{pwa._shouldMoveWeaponCloser} IsMounted: {pwa.IsMountedState}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"ADS Mode Optic: {_adsModeOptic} Basic: {_adsModeBasic}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"SmoothTilt: {pwa.SmoothedTilt} PossibleTilt{pwa.PossibleTilt}", centered: false);
            if (_handsController is Player.GrenadeHandsController ghc)
            {
                rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Grenadening: {ghc.CurrentOperation} State: {ghc.CurrentOperation.State}", centered: false);
            }
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"LeftStanceCurve: {leftStanceCurve}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"StationaryWpn{localPlayer.MovementContext.StationaryWeapon}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"InteractionRay Pos: {localPlayer.InteractionRay.origin} Dir: {localPlayer.InteractionRay.direction}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Hit Reactions: {hitReactionsEnabled}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Environs: {localPlayer.Environment} roof: {localPlayer.IsUnderRoof}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"HandCtr: {_handsController}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"FACtr: {_firearmController}", centered: false);

            if (_firearmController != null)
            {
                rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Aim Sens: {_firearmController.AimingSensitivity} Smooth Sens: {_firearmController.AimingSmoothSensitivity}", centered: false);
                rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Current Scope: {pwa.CurrentScope}", centered: false);
            }

            var gameCamera = CameraManager.Instance.Camera;
            var camTransform = localPlayer.CameraPosition;
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Near Clip Plane: {(gameCamera != null ? gameCamera.nearClipPlane.ToString() : "-")}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Line Scan Radius P1: {_positionSolver.Phase1.LineScanRadius} P2: {_positionSolver.Phase2.LineScanRadius} P3: {_positionSolver.Phase3.LineScanRadius}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Player Velocity: {localPlayer.Velocity} Motion: {localPlayer.Motion} Rot: {localPlayer.MovementContext.Rotation}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Hands To Body: {localPlayer.MovementContext.HandsToBodyAngle:F2} Trunk Limit: {localPlayer.TrunkRotationLimit:F2} Turning: {_torsoDeadzone.IsTurning} Prone Hold: {_torsoProneHold}", centered: false);
            var travelError = GetTravelError();
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Moving Deadzone: {(_movingDeadzoneInControl ? "In control" : "Off")} Mode: {_movingDeadzone.Mode} Lag: {_movingDeadzone.Lag:F2} Blocked: {_walkBlocked} ({_walkBlockedTime:F2}s) | Speed: {_horizontalVelocity.magnitude:F2}m/s Travel Error: {(float.IsNaN(travelError) ? "-" : travelError.ToString("F2"))}", centered: false);
            var gait = _gaitPoser.Gait;
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Gait: Feet Sep: {_gaitPoser.FootSeparation:F2} Move W: {_gaitPoser.MovementWeight:F2}{(_gaitPoser.Airborne ? " (air)" : "")} Master: {_gaitPoser.Master:F2}{(_gaitPoser.Suppression != null ? " (" + _gaitPoser.Suppression + ")" : "")} Intensity: {_gaitPoser.Intensity:F2} | Dip Target: {gait.DipTarget * 100f:F2}cm Dip: {gait.Dip * 100f:F2}cm Sway: {gait.Sway * 100f:F2}cm Sag: {gait.Sag:F2} Head: {gait.Head:F2}", centered: false);
            var recoil = _shoulderRecoilPoser;
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Shoulder Recoil: Last Shot Back: {recoil.LastBack:F0} ({recoil.LastSource}) Class: {recoil.LastClassFactor:F2} Kick: {recoil.LastKick:F2} | Push: {recoil.Push * 100f:F2}cm = Rock {recoil.RockPush * 100f:F2}cm ({recoil.RockDegrees:F2}deg) + Twist {recoil.TwistPush * 100f:F2}cm ({recoil.TwistDegrees:F2}deg) + Clavicle {recoil.ClaviclePush * 100f:F2}cm ({recoil.ClavicleDegrees:F2}deg, {(recoil.ClavicleActive ? "on" : "off")}) Head: {recoil.HeadMove * 100f:F2}cm | Chain: {recoil.ChainLength} Reach Rock: {recoil.RockReach:F2} Rest: {recoil.RestReach:F2} | R Hand IK: {recoil.RightHandWeight:F3} Master: {recoil.Master:F2}{(recoil.Suppression != null ? " (" + recoil.Suppression + ")" : "")} Intensity: {recoil.Intensity:F2}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Capturing: {IsCapturingLook} Default RotAction: {localPlayer.MovementContext.RotationAction == MovementContext.DefaultRotationFunction} BTR: {localPlayer.BtrState}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Free Look Orbit Held: {_orbitHeld} Offset Yaw: {_rig.OrbitYaw:F2} Pitch: {_rig.OrbitPitch:F2} | Game MouseLookControl: {localPlayer.MouseLookControl} Resetting: {localPlayer.IsResettingLook} Head: {localPlayer.HeadRotation.ToString("F1")}", centered: false);
            var playerRotation = localPlayer.MovementContext.Rotation;
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Rig Yaw: {_rig.Yaw:F2} Pitch: {_rig.Pitch:F2} | Player Yaw: {playerRotation.x:F2} Pitch: {playerRotation.y:F2} | Rig - Player: {CameraRig.AngleDelta(playerRotation, _rig.Angles).ToString("F3")}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Last Reconcile Shortfall: {_lastReconcileShortfall.ToString("F3")}", centered: false);
            var holdState = IsConvergenceHoldOpen ? $"Open ({_convergenceHoldUntil - Time.time:F2}s)" : "Closed";
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Aim Convergence: {(_convergenceActive ? "Converged" : "Parallel (" + _convergenceFallback + ")")} Hold: {holdState} Dist Raw: {_aimSolver.RawDistance:F2} Smoothed: {_aimSolver.SmoothedDistance:F2}", centered: false);
            // Miss angle from the real transforms: how far the actual line of fire is off the aim point. Judges the
            // "barrel rest direction = look forward" model, so it must not be computed from the model. Needs a barrel.
            var missAngle = "-";
            if (_convergenceActive && _firearmController != null && _firearmController.CurrentFireport?.Original != null)
                missAngle = Vector3.Angle(_firearmController.WeaponDirection, _aimSolver.AimPoint - _firearmController.CurrentFireport.position).ToString("F3");
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Muzzle Offset: {_aimSolver.MuzzleOffset.ToString("F3")} Miss Angle: {missAngle}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Offset: {_currentOffset} Aim Offset: {_aimOffset}{(_aimOffsetDetached ? " (detached)" : "")} PWA Offset: {pwa.HandsContainer.CameraOffset}", centered: false);
            var pivotFiltered = _pivotFilter.Offset;
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Pivot Offset Raw: {_pivotRawOffset.ToString("F3")} Filtered: {pivotFiltered.ToString("F3")} Bob Shown: {((_pivotRawOffset - pivotFiltered) * _bodyBobWeight).ToString("F3")} Weight: {_bodyBobWeight:F2} Bypassed: {_pivotFilter.Bypassed}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Pivot Trail: {_pivotTrail.State} Lag: {_pivotTrail.Lag.magnitude * 100f:F1}cm Past Deadzone: {_pivotTrail.Excess * 100f:F1}cm ({_pivotTrail.Excess / PivotTrail.MaxTrail:P0} of cap) | Applied Deadzone: {_followDeadzone:F3}m Smoothing: {_followSmoothing:F3}s", centered: false);
            var solver = _positionSolver;
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Solver: Decided By: {solver.DecidedBy} | P1: {solver.Phase1.Describe()}{(solver.Phase1Shifted ? " shift" : "")} ({solver.Phase1Moved * 100f:F1}cm) P2: {solver.Phase2.Describe()}{(solver.Phase2Shifted ? " shift" : "")} ({solver.Phase2Moved * 100f:F1}cm) P3: {solver.Phase3.Describe()} ({solver.Phase3Moved * 100f:F1}cm) | Off Desired: {solver.DesiredError * 100f:F1}cm | Cast Origin Drop: {_castOriginDrop * 100f:F1}cm", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Solve Cost: {_solveCasts} casts (Origin {_originCasts} Tight Space {_boomCasts} Solver {_solverCasts}) Peak/s: {Mathf.Max(_solveCastsPeak, _solveCastsWindowPeak)} | {_solveMs:F3}ms (avg {_solveMsAverage:F3}ms)", centered: false);
            var boom = _boom;
            var lengthen = boom.LengthenWanted >= 0f ? $" (longer wanted {boom.LengthenWanted:F2}/{BoomModulator.LengthenDwell:F2}s)"
                : boom.ShortenWanted >= 0f ? $" (shorter wanted {boom.ShortenWanted:F2}/{BoomModulator.ShortenDwell:F2}s)" : "";
            var cancel = _trailCancel * BoomModulator.Shortening(boom.Boom, _boomClosest);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Tight Space: {_boomStatus} | Boom: {boom.Boom:F2} Target: {boom.Target:F2} Proposed: {boom.Proposed:F2}{lengthen} | Lateral: {boom.Lateral:F2}{(_aimFlag ? " held (ADS)" : "")} | Trail Cancel: {cancel.magnitude * 100f:F1}cm", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Tight Space Probes: {DescribeBoomLevels()} | Centre Sample: {_intent.CentreDistance:F1}m Samples: {_intent.Count} ({_intent.TotalWeight} weight)", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Tight Space Over Head: {DescribeCandidates()}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Tight Space Opening: {DescribeOpening()}", centered: false);
            if (camTransform != null)
            {
                rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Pos: {camTransform.position} Local: {camTransform.localPosition}", centered: false);
                DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Rot: {camTransform.rotation} Local: {camTransform.localRotation}", centered: false);
            }

            DrawTightSpaceSamples();
        }
        // @formatter:on

        if (!Plugin.CrosshairEnabled.Value || localPlayer.PointOfView == EPointOfView.FirstPerson)
            return;

        if (_firearmController == null)
            return;

        if (!_firearmController.IsAiming && Plugin.CrosshairAdsOnlyEnabled.Value)
            return;

        var fireport = _firearmController.CurrentFireport;
        if (fireport?.Original == null)
            return;

        // The aim solver's cast (what bullets hit, characters included, our own colliders skipped), so the crosshair
        // lands on an enemy rather than the wall behind them
        var targetPoint = _aimSolver.CastHitPoint(fireport.position, _firearmController.WeaponDirection.normalized);

        var screenPosition = CameraManager.Instance.Camera.WorldPointToVisibleScreenPoint(targetPoint);

        if (screenPosition == Vector2.zero)
            return;

        DrawCrosshair(screenPosition, 5, Plugin.CrosshairColor.Value, Plugin.CrosshairThickness.Value);
    }

    /// <summary>
    ///     The opening camera for the debug UI: its state and the reason for it (why it engaged, refused or let go), the
    ///     lean, then the last check's results: the ordinary solve's cover and whether it is reachable, the camera's cover,
    ///     whether the head sees it, how many view samples see it, overlap, distance from the eye; and the casts it made.
    /// </summary>
    private string DescribeOpening()
    {
        if (!IsOpeningCameraEnabled)
            return "Off";

        var c = _openingCheck;
        var lean = localPlayer.MovementContext.SmoothedTilt;
        var state = _opening.Active
            ? $"{_opening.State}{(_opening.Bootstrapped ? " (through the opening" + (_opening.Travelling ? ", travelling)" : ")") : " (kept)")} {Time.time - _opening.Since:F1}s"
            : "Off";
        var checks = c.Samples == 0
            ? ""
            : $" | Ordinary cover {c.OrdinaryCover:F2}{(c.OrdinaryReachable ? " reachable" : "")} | Cam cover {c.CameraCover:F2} | Head {(c.HiddenFromHead ? "hidden" : "sees it")} | Seen {c.Seen}/{c.Samples} | {(c.Overlap ? "STUCK | " : "")}Dist {c.Distance:F2}/{c.MaxDistance:F2}";
        // The last overlap occurrence (what it was, how it was resolved) and what the camera had stopped at when the mode
        // last retreated or cut: what a report needs to tell which cause it was
        var overlaps = _openingOverlapEvents == 0 ? "" : $" | Overlaps {_openingOverlapEvents}, last: {_openingOverlapInfo}";
        var blocker = _openingBlockerInfo == null ? "" : $" | Last exit: {_openingBlockerInfo}";
        return $"{state}: {_openingStatus} | Lean {lean:F1}{checks} ({_openingCasts} casts){overlaps}{blocker}";
    }

    /// <summary>
    ///     Stage 2 for the debug UI: the position in use and the blend toward it, the collapse trigger (the solved camera's
    ///     centre ray distance from the character, covered now, collapsed after the dwell), any pending switch with its
    ///     dwell, then the last probe (why, and each position looked at with misses/allowed and causes) and its casts.
    /// </summary>
    private string DescribeCandidates()
    {
        if (!Plugin.CameraTightSpaceOverHead.Value)
            return "Off";

        var c = _candidates;
        var active = TightSpaceCandidates.NameOf(c.Active);
        var shown = c.Shown != c.Active ? $" (easing back from {TightSpaceCandidates.NameOf(c.Shown)})" : "";
        var cover = float.IsInfinity(c.CoverDistance) ? "-" : $"{c.CoverDistance:F2}m";
        var trigger = c.Collapsing ? "COLLAPSING" : c.Collapsed ? "collapsed (lingering)" : c.Covered ? "covered" : "clear";
        var pending = c.HasPending
            ? $" | Pending: {TightSpaceCandidates.NameOf(c.Pending)} {c.PendingFor:F2}/{(c.Pending == TightSpaceCandidates.Home ? TightSpaceCandidates.ReturnDwell : TightSpaceCandidates.SwitchDwell):F2}s"
            : "";
        return $"{active}{shown} Blend: {c.Blend:F2} | Cover: {cover} {trigger}{pending} | Last: {_candidateProbe} ({_candidateCasts} casts)";
    }

    /// <summary>
    ///     The last probe's levels for the debug UI, per level probed: boom, misses/allowed, then the sample counts by cause:
    ///     g blocked by near geometry (the misses), c behind the character, f beyond the near range (or blocked only
    ///     beyond it), s grazing the sample's own surface, u left unchecked once the level could no longer fail. c, f and s
    ///     count as seen. * marks the level taken, - a level not probed (a longer one already passed).
    /// </summary>
    private string DescribeBoomLevels()
    {
        if (_levelsProbed == 0)
            return "-";

        var text = new System.Text.StringBuilder();
        for (var level = 0; level < BoomModulator.LevelCount; level++)
        {
            if (level > 0)
                text.Append(" | ");

            var boom = BoomModulator.LevelBoom(level, _boomClosest);
            text.Append(boom.ToString("F2"));
            if (level == _boomDecidedLevel)
                text.Append('*');

            if (level >= _levelsProbed)
            {
                text.Append(" -");
                continue;
            }

            var result = _levelResults[level];
            text.Append(' ').Append(result.Misses).Append('/').Append(_boom.AllowedMissesFor(boom));
            text.Append(" g").Append(result.Geometry).Append(" c").Append(result.Character).Append(" f").Append(result.Far)
                .Append(" s").Append(result.Graze).Append(" u").Append(result.Unchecked);
        }

        return text.ToString();
    }

    /// <summary>
    ///     The view samples on screen, coloured by how the last probe found them from the level it took: green seen, red
    ///     blocked by near geometry (a miss), yellow behind the character, cyan beyond the near range, blue grazing its own
    ///     surface, grey not checked. C is the centre sample, n the points along the centre ray, i the inner ring, o the
    ///     outer rays. From that level's camera, not necessarily from where the camera is right now (the solver may have it
    ///     elsewhere, and a held decision is up to 0.25 s old).
    /// </summary>
    private void DrawTightSpaceSamples()
    {
        if (_levelsProbed == 0 || _intent.Count == 0)
            return;

        var gameCamera = CameraManager.Instance.Camera;
        if (gameCamera == null)
            return;

        var level = Mathf.Clamp(_boomDecidedLevel, 0, _levelsProbed - 1);
        var views = _levelViews[level];
        var innerStart = 1 + _intent.NearSamples;
        var outerStart = innerStart + IntentProbe.InnerCount;
        var colorBackup = GUI.color;

        for (var i = 0; i < _intent.Count; i++)
        {
            var screen = gameCamera.WorldPointToVisibleScreenPoint(_intent.Targets[i]);
            if (screen == Vector2.zero)
                continue;

            GUI.color = views[i] switch
            {
                TargetView.Visible => Color.green,
                TargetView.Geometry => Color.red,
                TargetView.Character => Color.yellow,
                TargetView.Far => Color.cyan,
                TargetView.Graze => Color.blue,
                _ => Color.grey
            };

            var mark = i == 0 ? "C" : i < innerStart ? "n" : i < outerStart ? "i" : "o";
            DebugUI.Label(screen, mark);
        }

        GUI.color = colorBackup;
    }

    private static void DrawCrosshair(Vector2 position, float size, Color color, float thickness)
    {
        var colorBackup = GUI.color;
        GUI.color = color;

        var texture = Texture2D.whiteTexture;
        GUI.DrawTexture(new Rect(position.x - size, position.y, size * 2 + thickness, thickness), texture);
        GUI.DrawTexture(new Rect(position.x, position.y - size, thickness, size * 2 + thickness), texture);

        GUI.color = colorBackup;
    }
}