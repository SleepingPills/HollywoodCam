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

    // Static Config
    private LayerMask _eyeCameraHitMask;
    private readonly Vector3 _eyeCameraOffset = new(0f, 0.1f, 0f);

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

        // What bullets can hit, other characters' hit colliders included, so aiming at someone converges on them
        _aimSolver = new AimSolver(BallisticsCalculatorConstants.HitMask.value);
    }

    public void OnDestroy()
    {
        Plugin.AdsModeOptic.SettingChanged -= _adsModeOpticChanged;
        Plugin.AdsModeBasic.SettingChanged -= _adsModeBasicChanged;

        _rig?.Destroy();
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
        // NB: Has to be LateUpdate as doing all this in Update can cause the camera to occasionally clip into the wall when mousing violently.
        HandleInputs();

        if (localPlayer.CameraPosition == null || !localPlayer.HealthController.IsAlive)
            return;

        // Keeps the capture edge current on the frames that return before HandleThirdPerson (1st person, stationary weapons)
        if (!IsCapturingLook)
        {
            _rigCapturing = false;
            CloseConvergenceHold();
        }

        _handsController = localPlayer.HandsController as Player.ItemHandsController;
        _firearmController = localPlayer.HandsController as Player.FirearmController;

        // Before OnGUI's crosshair, which casts with it whether or not convergence ran
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
                Plugin.Log.LogError($"Unknown ADS mode selected: {adsModeSelected}");
                break;
        }

        _aimFlag = isAiming;

        HandleThirdPerson();
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

        // Same origin as Cam's parent, so _currentOffset keeps its meaning across the 1st <-> 3rd person switch
        var eye = eyeTransform.position;
        _rig.UpdatePivot(eye);
        _pivotPlaced = true;

        // After CaptureGameRotation, so a seed made this frame already holds this frame's offset step
        if (!_convergenceActive || !Plugin.AimConvergenceEnabled.Value)
            CloseConvergenceHold();

        var holding = IsConvergenceHoldOpen;
        var heldPoint = default(Vector3);
        if (holding)
        {
            // Where the centre ray points now, from the current rig rotation, so mouse input in the window still aims
            var rigRotation = _rig.Rotation;
            heldPoint = eye + rigRotation * _currentOffset + rigRotation * Vector3.forward * _aimSolver.SmoothedDistance;
        }

        // Cast origin is offset in world space, so it stays above the eye regardless of pitch
        var eyeCameraPos = _rig.Pivot.position + _eyeCameraOffset;

        _currentOffset = _positionSolver.Solve(
            _rig.Pivot, _currentOffset, desiredCameraOffset, eyeCameraPos, _eyeCameraHitMask
        );

        if (holding)
        {
            // Cancel the aim shift of the camera translating: same point under the centre from the new position
            _aimSolver.SnapDistance(PointRigAt(eye, heldPoint));
            _rig.UpdatePivot(eye);

            if ((_currentOffset - desiredCameraOffset).sqrMagnitude < ConvergenceHoldSettledSqr)
                CloseConvergenceHold();
        }

        // Written in world space: Cam's parent is re-rotated by the game every frame, the rig is what we aim with.
        // The game copies Cam onto the real camera right after this, in the same PlayerCameraController.LateUpdate.
        localPlayer.CameraPosition.SetPositionAndRotation(_rig.Pivot.TransformPoint(_currentOffset), _rig.Rotation);
    }

    /// <summary>
    ///     Locks the rig to the rotation the camera has always inherited from the player (Cam's parent), so every game
    ///     state that rotates it (vaulting, mounting, vehicles) still drives the camera.
    /// </summary>
    private void SyncRigToPlayer(Transform eyeTransform)
    {
        _rig.SyncFrom(eyeTransform.rotation);
    }

    /// <summary>
    ///     Whether the rig owns the look direction: the mouse delta goes to the rig and the player is turned to follow it.
    ///     Otherwise the game handles look input as it always has and the rig is locked to the player. Shared by the Rotate
    ///     patch (Update) and HandleThirdPerson (LateUpdate) so the two cannot disagree.
    /// </summary>
    public bool IsCapturingLook
    {
        get
        {
            if (!Plugin.FreeAimCameraEnabled.Value)
                return false;

            var movementContext = localPlayer.MovementContext;

            // PlayerBody's value rather than Player.PointOfView, which our PoV override fakes as 1st person during VisualPass.
            // 1st person ADS, the 3rd person toggle being off and stationary weapons all end up in 1st person here.
            // The rotation function check covers the states that pose Cam's parent from something other than HandsRotation
            // (high vault/climb, stationary, mounted, cutscenes). The mounting approach and the BTR keep the default function
            // (the BTR swaps HandsRotation's source instead), hence the explicit checks for those.
            return localPlayer.PlayerBody.PointOfView.Value == EPointOfView.ThirdPerson
                   && movementContext.RotationAction == MovementContext.DefaultRotationFunction
                   && !movementContext.IsInMountedState
                   && movementContext.StationaryWeapon == null
                   && localPlayer.BtrState == EPlayerBtrState.Outside;
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
            // Should not happen; give the game its input back and reseed the rig from the player next time
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
    ///     subtracting the current rotation gives the delta to apply. With convergence, the rotation that puts the line of
    ///     fire through the point under the centre of the screen; otherwise (or when that cannot be solved) parallel aim,
    ///     exactly where the rig looks.
    /// </summary>
    private Vector2 ComputeAimRotation(Vector2 current)
    {
        var desired = TrySolveConvergence(current, out var converged) ? converged : _rig.Angles;
        return current + CameraRig.AngleDelta(current, desired);
    }

    private bool TrySolveConvergence(Vector2 current, out Vector2 desired)
    {
        desired = default;

        if (!TryGetConvergenceInputs(out var ribcage, out var muzzleOffset, out var eye))
        {
            _convergenceActive = false;
            CloseConvergenceHold();
            return false;
        }

        // A stale distance (convergence just enabled, back from a fallback) would sweep the aim in from wherever it was
        if (!_convergenceActive)
            _aimSolver.ResetSmoothing();

        // The rig's current rotation, so this frame's mouse turn already orbits the camera
        var rigRotation = _rig.Rotation;
        var cameraPosition = eye + rigRotation * _currentOffset;
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
    ///     The eye is where the pivot is placed each LateUpdate; read live so it is not stale after a spell in 1st person.
    /// </summary>
    private bool TryGetConvergenceInputs(out Vector3 ribcage, out Vector3 muzzleOffset, out Vector3 eye)
    {
        ribcage = muzzleOffset = eye = default;

        if (!Plugin.AimConvergenceEnabled.Value)
        {
            _convergenceFallback = "Disabled";
            return false;
        }

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

        ribcage = ribcageBone.Original.position;
        // Low-passed every LateUpdate, toward zero without a firearm, so it is valid whatever is in hands
        muzzleOffset = _aimSolver.MuzzleOffset;
        eye = eyeTransform.position;

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
        if (!TryGetConvergenceInputs(out var ribcage, out var muzzleOffset, out var eye))
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
        _aimSolver.SnapDistance(PointRigAt(eye, target));
        _convergenceActive = true;
        _convergenceFallback = null;

        // The camera is usually about to ease out from the eye, keep this target under the centre while it does
        _convergenceHoldUntil = Time.time + ConvergenceHoldDuration;

        return true;
    }

    /// <summary>
    ///     Turns the rig, from its current rotation, so the centre ray from eye + rotation * _currentOffset passes through
    ///     target, and returns target's distance along that ray. The camera position depends on the rotation, hence the
    ///     iteration; it contracts by about |offset| / distance per step.
    /// </summary>
    private float PointRigAt(Vector3 eye, Vector3 target)
    {
        for (var i = 0; i < 2; i++)
        {
            if (!AimSolver.TryYawPitch(target - (eye + _rig.Rotation * _currentOffset), _rig.Yaw, out var rigAngles))
                break;

            _rig.SetAngles(rigAngles.x, rigAngles.y);
        }

        var rigRotation = _rig.Rotation;
        return Vector3.Dot(target - (eye + rigRotation * _currentOffset), rigRotation * Vector3.forward);
    }

    private bool IsConvergenceHoldOpen => Time.time < _convergenceHoldUntil;

    private void CloseConvergenceHold()
    {
        _convergenceHoldUntil = -1f;
    }

    /// <summary>
    ///     Seeds the rig from the player when capture starts. Afterwards adds whatever the game itself did to the player's
    ///     rotation since we last looked (spawn, doors, approach animations...), so the camera follows game-driven turns
    ///     without knowing about each state.
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

    public void OnGUI()
    {
        if (!localPlayer.HealthController.IsAlive)
            return;

        // CollisionDebug.DrawCollisionInfo(_positionSolver.Phase1.CircleScan);

        // @formatter:off
        if (Plugin.DebugUIEnabled.Value)
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
            // SPT 4.1: PlayerCameraController no longer has a "gclass3688_0" strategy field, and the
            // GClass3688 type is gone with no equivalent. This was a debug-overlay label only, so it now
            // reports the animation strategy off ProceduralWeaponAnimation instead.
            var pcaStrategy = Traverse.Create(localPlayer.ProceduralWeaponAnimation).Field("_strategy").GetValue<IProceduralAnimationStrategy>();
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"PCA Strat: {pcaStrategy}", centered: false);

            if (_firearmController != null)
            {
                rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Aim Sens: {_firearmController.AimingSensitivity} Smooth Sens: {_firearmController.AimingSmoothSensitivity}", centered: false);
                rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Current Scope: {pwa.CurrentScope}", centered: false);
            }

            var gameCamera = CameraManager.Instance.Camera;
            var camTransform = localPlayer.CameraPosition;
            var eyeTransform = camTransform != null ? camTransform.parent : null;
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Near Clip Plane: {(gameCamera != null ? gameCamera.nearClipPlane.ToString() : "-")}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Line Scan Radius P1: {_positionSolver.Phase1.LineScanRadius} P2: {_positionSolver.Phase2.LineScanRadius} P3: {_positionSolver.Phase3.LineScanRadius}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Player Velocity: {localPlayer.Velocity} Motion: {localPlayer.Motion} Rot: {localPlayer.MovementContext.Rotation}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Free Aim: {Plugin.FreeAimCameraEnabled.Value} Capturing: {IsCapturingLook} Default RotAction: {localPlayer.MovementContext.RotationAction == MovementContext.DefaultRotationFunction} BTR: {localPlayer.BtrState}", centered: false);
            var playerRotation = localPlayer.MovementContext.Rotation;
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Rig Yaw: {_rig.Yaw:F2} Pitch: {_rig.Pitch:F2} | Player Yaw: {playerRotation.x:F2} Pitch: {playerRotation.y:F2} | Rig - Player: {CameraRig.AngleDelta(playerRotation, _rig.Angles).ToString("F3")}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Last Reconcile Shortfall: {_lastReconcileShortfall.ToString("F3")} Eye Scale: {(eyeTransform != null ? eyeTransform.lossyScale.ToString("F3") : "-")}", centered: false);
            var holdState = IsConvergenceHoldOpen ? $"Open ({_convergenceHoldUntil - Time.time:F2}s)" : "Closed";
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Aim Convergence: {Plugin.AimConvergenceEnabled.Value} Solver: {(_convergenceActive ? "Converged" : "Parallel (" + _convergenceFallback + ")")} Hold: {holdState} Dist Raw: {_aimSolver.RawDistance:F2} Smoothed: {_aimSolver.SmoothedDistance:F2}", centered: false);
            // Miss angle from the real transforms: how far the actual line of fire is off the aim point. Judges the
            // "barrel rest direction = look forward" model, so it must not be computed from the model. Needs a barrel.
            var missAngle = "-";
            if (_convergenceActive && _firearmController != null && _firearmController.CurrentFireport?.Original != null)
                missAngle = Vector3.Angle(_firearmController.WeaponDirection, _aimSolver.AimPoint - _firearmController.CurrentFireport.position).ToString("F3");
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Muzzle Offset: {_aimSolver.MuzzleOffset.ToString("F3")} Miss Angle: {missAngle}", centered: false);
            rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Offset: {_currentOffset} PWA Offset: {pwa.HandsContainer.CameraOffset}", centered: false);
            if (camTransform != null)
            {
                rect = DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Pos: {camTransform.position} Local: {camTransform.localPosition}", centered: false);
                DebugUI.Label(new Vector2(50, rect.y + rect.height), $"Cam Rot: {camTransform.rotation} Local: {camTransform.localRotation}", centered: false);
            }
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