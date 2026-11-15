using System.Collections.Generic;
using System.Text;
using EFT;
using EFT.Animations;
using EFT.Animations.NewRecoil;
using EFT.InventoryLogic;
using UnityEngine;

namespace HollywoodCam;

/// <summary>
///     Puts ShoulderRecoil on the local player's skeleton as the upper body taking the shot. The push, measured at the
///     firing shoulder along the barrel, is split three ways:
///     - a rock back: small pitch rotations over the spine chain (Spine1 up to the ribcage), growing toward the chest, so
///       the torso gives from the waist and both shoulders and the head go back;
///     - a twist about the spine, spread over the same chain, so the firing side of the torso goes back further;
///     - the firing side's clavicle pulled back, the smallest share, so the shoulder joint itself gives a little.
///     The neck takes back most of the chest's rotation so the head rides the chest but stays about level, and the eye
///     transform the camera pivot is read from is put back exactly, so the camera does not move.
///
///     Applied from the Player.IkProcess prefix on VisualPass's full path (see Patches/Recoil.cs): after the hands object
///     has been placed at the ribcage position for this frame (MouseLook) and the body's upper arms pinned to the hands rig
///     (PlayerBones.SetShoulders), and before the arm IK re-solves the hands onto the weapon. The hands object is not
///     parented to the body and nothing later in the frame places anything from these bones, so the weapon, its fireport
///     and the aim do not move even though the ribcage does. The mod's own aim solve reads the ribcage in Update, which
///     can run before the restore; it gets the position from before this pose instead (TryGetRibcageBeforePose).
///
///     Every bone written is cached right before it is written and put back in ThirdPersonView.Update, before the
///     animator, and before GaitPoser restores its own. Spine1 and the neck are shared with it; this caches them after the
///     gait has moved them, so restoring this first and the gait second leaves them exactly as the animator left them.
/// </summary>
public class ShoulderRecoilPoser
{
    // Effective back recoil of an unmodded assault rifle (SPT item database RecoilForceBack: M4A1 342, AK-74M 318,
    // AKM 378, assault rifle median 344): kick 1
    public const float ReferenceBack = 340f;
    // No stock in the shoulder (pistols, folded stocks): the kick goes into the arms, which the game already shows.
    // Glock 17 at 260 lands about 1.5 cm.
    public const float PistolFactor = 0.65f;
    // Shotguns' RecoilForceBack (MP-153 474, M870 471) understates the shove of a 12 gauge relative to rifles; this
    // lands them about 6 cm
    public const float ShotgunFactor = 1.4f;
    public const float LauncherFactor = 1.2f;
    // A normalised shot reading outside this is not trusted, the weapon's stats are used instead
    public const float MinTrustedBack = 30f;
    public const float MaxTrustedBack = 3000f;

    // Shares of the push at the firing shoulder. The rock and twist together are three quarters, so what the eye reads
    // is the torso moving; the clavicle is the remainder. The twist is the larger body term because it moves the firing
    // side most and gives the support arm slack, where the rock costs both arms reach.
    public const float RockShare = 0.35f;
    public const float TwistShare = 0.4f;
    public const float ClavicleShare = 0.25f;
    // Without usable clavicles their share goes to the body terms in the same proportion
    public const float RockShareNoClavicle = RockShare / (RockShare + TwistShare);

    // Absolute limits whatever the strength, as a total over the chain and per bone: past these the waist folds and
    // the chest and shoulder skin visibly stretch
    public const float MaxRockDegrees = 12f;
    public const float MaxRockDegreesPerBone = 6f;
    public const float MaxTwistDegrees = 15f;
    public const float MaxTwistDegreesPerBone = 8f;
    public const float MaxClavicleDegrees = 16f;
    // The neck takes back this share of the chest's rotation: the head stays about level and looking forward, and
    // still visibly belongs to the chest
    public const float HeadLevelShare = 0.8f;

    // A hand may reach at most this share of its arm after the push, so it does not come off the gun
    public const float ReachMargin = 0.98f;
    // Below this the game moves the weapon to the right palm after the IK (Player.VisualPass's Kinematics call), so
    // moving the arm would move the gun
    public const float RightHandIkMin = 0.999f;

    public const float WeightRate = 8f;
    public const float IntensityRate = 4f;
    public const float MaxStrength = 3f;
    public const float ProneScale = 0.35f;
    public const float FullCrouchScale = 0.85f;

    private const int MaxChainDepth = 6;
    private const float MinAxisLength = 0.05f;
    // Metres of shoulder travel per radian below which a term cannot move the shoulder usefully and is left out
    private const float MinResponse = 0.02f;
    private const float MinClavicleLength = 0.04f;
    private const float MaxClavicleLength = 0.35f;
    // Shoulder to palm of an adult arm, a little under the usual 0.55-0.6 m
    private const float MinArmLength = 0.5f;
    private const int LogChainDepth = 8;

    private readonly ShoulderRecoil _spring = new();
    private float _master;
    private float _intensity;
    private int _lastStepFrame = -1;

    // Resolved once per player (see Resolve)
    private Player _resolvedFor;
    private string _resolveProblem;
    private Transform _ribcage;
    // Spine1 up to the ribcage, bottom first; the ribcage alone when Spine1 is not one of its ancestors
    private readonly List<Transform> _chain = new();
    private float[] _chainWeights = new float[0];
    private float _maxChainWeight = 1f;
    private Transform _neck;
    private bool _counterNeck;
    private readonly Transform[] _shoulders = new Transform[2];
    private readonly Transform[] _clavicles = new Transform[2];
    private Transform _body;
    private Transform _eye;
    private bool _preserveEye;
    private readonly Transform[] _forearms = new Transform[2];
    private readonly Transform[] _palms = new Transform[2];

    // The pose put back by Restore
    private bool _restorePending;
    private Quaternion[] _chainLocalRotations = new Quaternion[0];
    private Quaternion _neckLocalRotation;
    private readonly bool[] _clavicleCached = new bool[2];
    private readonly Quaternion[] _clavicleLocalRotations = new Quaternion[2];
    private Vector3 _eyeLocalPosition;
    private Quaternion _eyeLocalRotation;
    private Vector3 _ribcagePositionBeforePose;

    // Scratch for predicting where the shoulders go
    private Vector3[] _pivots = new Vector3[0];
    private Vector3[] _predictPivots = new Vector3[0];
    private Quaternion[] _rotations = new Quaternion[0];
    private readonly Vector3[] _twistMoves = new Vector3[2];
    private readonly Vector3[] _restMoves = new Vector3[2];
    private readonly Vector3[] _rockMoves = new Vector3[2];

    // Debug UI
    public string LastSource { get; private set; } = "-";
    public float LastBack { get; private set; }
    public float LastClassFactor { get; private set; } = 1f;
    public float LastKick => _spring.LastKick;
    public float Push { get; private set; }
    // Metres each term moved the firing shoulder back along the barrel, after the reach limit
    public float RockPush { get; private set; }
    public float TwistPush { get; private set; }
    public float ClaviclePush { get; private set; }
    // Totals over the chain
    public float RockDegrees { get; private set; }
    public float TwistDegrees { get; private set; }
    public float ClavicleDegrees { get; private set; }
    public float HeadMove { get; private set; }
    // Share of the rock and of the rest the reach limit let through
    public float RockReach { get; private set; } = 1f;
    public float RestReach { get; private set; } = 1f;
    public float RightHandWeight { get; private set; } = 1f;
    public bool ClavicleActive => _clavicles[0] != null && _clavicles[1] != null;
    public int ChainLength => _chain.Count;
    public float Master => _master;
    public float Intensity => _intensity;
    // Why the push is held or easing out, null when it is not
    public string Suppression { get; private set; } = "Not run";

    public ShoulderRecoilPoser(float intensity)
    {
        _intensity = Mathf.Clamp(intensity, 0f, MaxStrength);
    }

    /// <summary>
    ///     Puts back the pose the last Apply cached. ThirdPersonView.Update before the gait restore, and at the start of
    ///     the next body IK pass as a backstop, so it always comes back before GaitPoser touches Spine1 and the neck again.
    /// </summary>
    public void Restore()
    {
        if (!_restorePending)
            return;

        _restorePending = false;

        for (var i = 0; i < _chain.Count && i < _chainLocalRotations.Length; i++)
        {
            if (_chain[i] != null)
                _chain[i].localRotation = _chainLocalRotations[i];
        }

        if (_counterNeck && _neck != null)
            _neck.localRotation = _neckLocalRotation;

        for (var i = 0; i < 2; i++)
        {
            if (_clavicleCached[i] && _clavicles[i] != null)
                _clavicles[i].localRotation = _clavicleLocalRotations[i];

            _clavicleCached[i] = false;
        }

        if (_preserveEye && _eye != null)
        {
            _eye.localPosition = _eyeLocalPosition;
            _eye.localRotation = _eyeLocalRotation;
        }
    }

    /// <summary>
    ///     Where the ribcage was before this pose moved it, while the pose is still on; false when it is not (the live
    ///     position is then the right one). For anything that has to see the ribcage the weapon was placed at.
    /// </summary>
    public bool TryGetRibcagePositionBeforePose(out Vector3 position)
    {
        position = _ribcagePositionBeforePose;
        return _restorePending && _ribcage != null;
    }

    /// <summary>
    ///     A frame on which the push cannot be applied at all: no bone writes, pending shots dropped, and the next applied
    ///     frame starts from rest.
    /// </summary>
    public void Hold(string reason)
    {
        Restore();
        _master = 0f;
        _spring.Reset();
        ClearOutput();
        Suppression = reason;
    }

    /// <summary>
    ///     From the ProceduralWeaponAnimation.Shoot postfix, at the end of the shot's VisualPass; takes effect on the next
    ///     Apply, the frame the game's own hand recoil starts moving too.
    /// </summary>
    public void OnShot(ProceduralWeaponAnimation pwa, float str, float strengthSetting)
    {
        if (!(Mathf.Clamp(strengthSetting, 0f, MaxStrength) > 0f))
            return;

        var shotEffector = pwa.Shootingg;
        var weapon = shotEffector?._weapon;

        if (TryGetShotBack(shotEffector, out var back))
        {
            LastSource = "shot";
        }
        else if (weapon != null && IsFinite(weapon.RecoilForceBack) && weapon.RecoilForceBack > 0f)
        {
            // Same inputs the game's back recoil starts from: the template value and the mods' recoil change, and the
            // ammo's recoil modifier the shot was passed
            back = weapon.RecoilForceBack * Mathf.Max(0f, 1f + weapon.RecoilDelta) * (IsFinite(str) && str > 0f ? str : 1f);
            LastSource = "weapon stats";
        }
        else
        {
            back = ReferenceBack;
            LastSource = "default";
        }

        LastBack = back;
        LastClassFactor = GetClassFactor(weapon);
        _spring.AddShot(back / ReferenceBack * LastClassFactor, _intensity);
    }

    /// <summary>
    ///     Once per frame, from the Player.IkProcess prefix. fadeReason non-null eases the push out; scale is its target
    ///     weight otherwise (prone, crouch). rightHandWeight is the game's right arm IK weight for this frame; markers are
    ///     the hand IK markers on the weapon (left, right), for the reach limit. recordDebug fills the debug UI properties,
    ///     which cost extra bone reads; the pose is the same either way.
    /// </summary>
    public void Apply(Player player, float rightHandWeight, Transform[] markers, string fadeReason, float scale, float strengthSetting, bool recordDebug)
    {
        var frame = Time.frameCount;
        if (frame == _lastStepFrame)
            return;

        // Backstop: should Update not have run since the last Apply, the rotations must not stack
        Restore();

        if (frame - _lastStepFrame > 1)
        {
            _master = 0f;
            _spring.Reset();
        }

        _lastStepFrame = frame;

        var deltaTime = Time.deltaTime;
        _intensity = Mathf.MoveTowards(_intensity, Mathf.Clamp(strengthSetting, 0f, MaxStrength), IntensityRate * deltaTime);

        // Off: nothing measured, nothing touched
        if (!(_intensity > 0f))
        {
            _master = 0f;
            _spring.Reset();
            ClearOutput();
            Suppression = "Off";
            return;
        }

        if (!Resolve(player))
        {
            Hold(_resolveProblem);
            return;
        }

        // The whole design rests on the weapon not hanging off the bones that are moved
        var handsObject = player.HandsController?.ControllerGameObject;
        if (handsObject != null && handsObject.transform.IsChildOf(_chain[0]))
        {
            Hold("Weapon under spine");
            return;
        }

        Suppression = fadeReason;
        _master = Mathf.MoveTowards(_master, fadeReason == null ? Mathf.Clamp01(scale) : 0f, WeightRate * deltaTime);

        _spring.Step(deltaTime, _intensity);
        Push = _spring.Push * _master;
        if (recordDebug)
            RightHandWeight = rightHandWeight;

        if (!(rightHandWeight >= RightHandIkMin))
        {
            Suppression = "Right hand off IK";
            ClearTerms();
            return;
        }

        if (Mathf.Abs(Push) < 1e-5f)
        {
            ClearTerms();
            return;
        }

        var back = -GetWeaponDirection(player);
        // 0 right shoulder, 1 left (the game's left stance curve, blended so a stance switch does not jump)
        var side = Mathf.Clamp01(player.GetCurveValue(PlayerAnimator.LEFT_STANCE_CURVE));
        var clavicleOn = ClavicleActive;
        var rockShare = clavicleOn ? RockShare : RockShareNoClavicle;
        var twistShare = clavicleOn ? TwistShare : 1f - RockShareNoClavicle;

        var count = _chain.Count;
        for (var j = 0; j < count; j++)
            _pivots[j] = _chain[j].position;

        var up = _body.up;
        var rockAxis = Vector3.Cross(up, back);
        rockAxis = rockAxis.sqrMagnitude > 1e-4f ? rockAxis.normalized : _body.right;
        var twistAxis = _neck != null ? _neck.position - _pivots[0] : Vector3.zero;
        twistAxis = twistAxis.sqrMagnitude >= MinAxisLength * MinAxisLength ? twistAxis.normalized : up;

        var right = _shoulders[1].position;
        var left = _shoulders[0].position;

        // Angles from the first-order response of the firing shoulder: metres back per radian over the chain. Shoulders[0]
        // is the left upper arm, [1] the right (PlayerBones.SetShoulders' weights).
        var rockRadians = Mathf.Lerp(GetAngle(right, rockAxis, back, Push * rockShare), GetAngle(left, rockAxis, back, Push * rockShare), side);
        var twistRadians = Mathf.Lerp(GetAngle(right, twistAxis, back, Push * twistShare), GetAngle(left, twistAxis, back, Push * twistShare), side);

        var rockLimit = Mathf.Min(MaxRockDegrees, MaxRockDegreesPerBone / _maxChainWeight);
        var twistLimit = Mathf.Min(MaxTwistDegrees, MaxTwistDegreesPerBone / _maxChainWeight);
        var rockDegrees = Mathf.Clamp(rockRadians * Mathf.Rad2Deg, -rockLimit, rockLimit);
        var twistDegrees = Mathf.Clamp(twistRadians * Mathf.Rad2Deg, -twistLimit, twistLimit);

        var clavicleWeights = new Vector2(side, 1f - side);
        var claviclePush = back * (Push * ClavicleShare);
        if (!clavicleOn)
            claviclePush = Vector3.zero;

        // Reach: the twist and clavicle first (the twist gives the support arm slack), then as much rock as is left
        var restReach = 1f;
        var rockReach = 1f;
        for (var i = 0; i < 2; i++)
        {
            var shoulder = _shoulders[i].position;
            _twistMoves[i] = Predict(shoulder, 0f, rockAxis, twistDegrees, twistAxis) - shoulder;
            _restMoves[i] = _twistMoves[i] + claviclePush * clavicleWeights[i];
            _rockMoves[i] = Predict(shoulder, rockDegrees, rockAxis, 0f, twistAxis) - shoulder;
        }

        for (var i = 0; i < 2; i++)
        {
            if (TryGetReach(i, markers, out var toTarget, out var reachSqr))
                restReach = Mathf.Min(restReach, MaxScale(toTarget, _restMoves[i], reachSqr));
        }

        for (var i = 0; i < 2; i++)
        {
            if (TryGetReach(i, markers, out var toTarget, out var reachSqr))
                rockReach = Mathf.Min(rockReach, MaxScale(toTarget - _restMoves[i] * restReach, _rockMoves[i], reachSqr));
        }

        twistDegrees *= restReach;
        claviclePush *= restReach;
        rockDegrees *= rockReach;

        var firing = side < 0.5f ? 1 : 0;
        if (recordDebug)
        {
            RestReach = restReach;
            RockReach = rockReach;
            RockPush = Vector3.Dot(_rockMoves[firing], back) * rockReach;
            TwistPush = Vector3.Dot(_twistMoves[firing], back) * restReach;
        }

        // Cache, then write: the chain bottom up, then the neck from its world rotation from before, then the clavicles
        // from where the chain moved them, then the eye back where it was
        _ribcagePositionBeforePose = _ribcage.position;
        for (var j = 0; j < count; j++)
            _chainLocalRotations[j] = _chain[j].localRotation;
        if (_counterNeck)
            _neckLocalRotation = _neck.localRotation;
        if (_preserveEye)
        {
            _eyeLocalPosition = _eye.localPosition;
            _eyeLocalRotation = _eye.localRotation;
        }

        _restorePending = true;

        var neckRotation = _counterNeck ? _neck.rotation : Quaternion.identity;
        var neckPosition = recordDebug && _neck != null ? _neck.position : Vector3.zero;
        var eyePosition = _preserveEye ? _eye.position : Vector3.zero;
        var eyeRotation = _preserveEye ? _eye.rotation : Quaternion.identity;

        BuildRotations(rockDegrees, rockAxis, twistDegrees, twistAxis);
        for (var j = 0; j < count; j++)
            _chain[j].rotation = _rotations[j] * _chain[j].rotation;

        if (_counterNeck)
            _neck.rotation = Quaternion.Slerp(_neck.rotation, neckRotation, HeadLevelShare);

        if (recordDebug)
            HeadMove = _neck != null ? (_neck.position - neckPosition).magnitude : 0f;

        var clavicleDegrees = 0f;
        var clavicleMoved = 0f;
        if (clavicleOn)
        {
            for (var i = 0; i < 2; i++)
            {
                var weight = clavicleWeights[i];
                if (!(weight > 0f))
                    continue;

                var before = recordDebug ? _shoulders[i].position : Vector3.zero;
                var degrees = PullClavicle(i, claviclePush * weight);
                if (!recordDebug)
                    continue;

                if (Mathf.Abs(degrees) > Mathf.Abs(clavicleDegrees))
                    clavicleDegrees = degrees;
                if (i == firing)
                    clavicleMoved = Vector3.Dot(_shoulders[i].position - before, back);
            }
        }

        if (_preserveEye)
            _eye.SetPositionAndRotation(eyePosition, eyeRotation);

        if (!recordDebug)
            return;

        RockDegrees = rockDegrees;
        TwistDegrees = twistDegrees;
        ClavicleDegrees = clavicleDegrees;
        ClaviclePush = clavicleMoved;
    }

    public void Dispose()
    {
        Restore();
    }

    /// <summary>
    ///     The bones, checked rather than assumed: the push needs the upper arms under the ribcage, the chain needs Spine1
    ///     to be an ancestor of it, the clavicle term needs real clavicles under it, and the camera is protected wherever
    ///     it hangs under the chain.
    /// </summary>
    private bool Resolve(Player player)
    {
        // A skeleton that failed the checks is not retried (and not logged again); one whose bones were destroyed is
        if (_resolvedFor == player && (_resolveProblem != null || IsResolvedAlive()))
            return _resolveProblem == null;

        _resolvedFor = player;
        _resolveProblem = null;
        _ribcage = _neck = _body = _eye = null;
        _counterNeck = _preserveEye = false;
        _chain.Clear();
        for (var i = 0; i < 2; i++)
            _shoulders[i] = _clavicles[i] = _forearms[i] = _palms[i] = null;

        var bones = player.PlayerBones;
        _ribcage = bones != null ? bones.Ribcage?.Original : null;
        _body = bones != null ? bones.BodyTransform?.Original : null;
        var shoulders = bones != null ? bones.Shoulders : null;

        if (_ribcage == null || _body == null)
            _resolveProblem = "No ribcage";
        else if (shoulders == null || shoulders.Length < 2 || shoulders[0] == null || shoulders[1] == null)
            _resolveProblem = "No shoulders";
        else if (!shoulders[0].IsChildOf(_ribcage) || !shoulders[1].IsChildOf(_ribcage))
            _resolveProblem = "Shoulders not under ribcage";

        if (_resolveProblem == null)
        {
            ResolveChain(bones.Spine1);

            _shoulders[0] = shoulders[0];
            _shoulders[1] = shoulders[1];

            _neck = bones.Neck;
            _counterNeck = _neck != null && _neck != _ribcage && _neck.IsChildOf(_ribcage);

            // Both or neither, so the effect stays the same in left stance
            var left = GetClavicle(_shoulders[0]);
            var right = GetClavicle(_shoulders[1]);
            if (left != null && right != null)
            {
                _clavicles[0] = left;
                _clavicles[1] = right;
            }

            var forearms = bones.Forearms;
            if (forearms != null && forearms.Length >= 2)
            {
                _forearms[0] = forearms[0];
                _forearms[1] = forearms[1];
            }

            _palms[0] = bones.LeftPalm;
            _palms[1] = bones.RightPalm;

            // Anywhere under the chain the eye moves with it, the head included; put back exactly after every write
            _eye = player.CameraPosition != null ? player.CameraPosition.parent : null;
            _preserveEye = _eye != null && _eye.IsChildOf(_chain[0]);
        }

        if (Plugin.LoggingEnabled)
            LogResolution(player);

        return _resolveProblem == null;
    }

    private bool IsResolvedAlive()
    {
        if (_ribcage == null || _body == null || _shoulders[0] == null || _shoulders[1] == null || _chain.Count == 0)
            return false;

        foreach (var bone in _chain)
        {
            if (bone == null)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Spine1 up to the ribcage, found by walking up from the ribcage, with weights growing toward the chest so the
    ///     torso bends progressively from the waist rather than at one joint.
    /// </summary>
    private void ResolveChain(Transform spine1)
    {
        var upward = new List<Transform> { _ribcage };
        var found = false;
        var bone = _ribcage.parent;
        for (var depth = 0; bone != null && depth < MaxChainDepth; depth++, bone = bone.parent)
        {
            upward.Add(bone);
            if (bone == spine1)
            {
                found = true;
                break;
            }
        }

        _chain.Clear();
        if (found && spine1 != null)
        {
            for (var i = upward.Count - 1; i >= 0; i--)
                _chain.Add(upward[i]);
        }
        else
        {
            _chain.Add(_ribcage);
        }

        var count = _chain.Count;
        _chainWeights = new float[count];
        _chainLocalRotations = new Quaternion[count];
        _pivots = new Vector3[count];
        _predictPivots = new Vector3[count];
        _rotations = new Quaternion[count];

        var total = count * (count + 1) / 2f;
        _maxChainWeight = 0f;
        for (var i = 0; i < count; i++)
        {
            _chainWeights[i] = (i + 1) / total;
            _maxChainWeight = Mathf.Max(_maxChainWeight, _chainWeights[i]);
        }
    }

    private Transform GetClavicle(Transform upperArm)
    {
        var clavicle = upperArm.parent;
        if (clavicle == null || clavicle == _ribcage || !clavicle.IsChildOf(_ribcage))
            return null;

        var length = (upperArm.position - clavicle.position).magnitude;
        return length >= MinClavicleLength && length <= MaxClavicleLength ? clavicle : null;
    }

    /// <summary>
    ///     The total angle (radians, spread over the chain by its weights) about axis that moves the shoulder by push
    ///     along back, to first order; 0 when the chain cannot move it that way.
    /// </summary>
    private float GetAngle(Vector3 shoulder, Vector3 axis, Vector3 back, float push)
    {
        // A small rotation about axis at pivot p moves the shoulder by the angle times axis x (shoulder - p)
        var response = 0f;
        for (var j = 0; j < _chain.Count; j++)
            response += _chainWeights[j] * Vector3.Dot(Vector3.Cross(axis, shoulder - _pivots[j]), back);

        return Mathf.Abs(response) < MinResponse ? 0f : push / response;
    }

    private void BuildRotations(float rockDegrees, Vector3 rockAxis, float twistDegrees, Vector3 twistAxis)
    {
        for (var j = 0; j < _chain.Count; j++)
            _rotations[j] = Quaternion.AngleAxis(twistDegrees * _chainWeights[j], twistAxis) * Quaternion.AngleAxis(rockDegrees * _chainWeights[j], rockAxis);
    }

    /// <summary>
    ///     Where a point under the ribcage ends up after the chain rotations, each about its bone's pivot as the earlier
    ///     ones moved it: what the writes in Apply do, without touching the bones.
    /// </summary>
    private Vector3 Predict(Vector3 point, float rockDegrees, Vector3 rockAxis, float twistDegrees, Vector3 twistAxis)
    {
        BuildRotations(rockDegrees, rockAxis, twistDegrees, twistAxis);

        var count = _chain.Count;
        var pivots = _predictPivots;
        for (var j = 0; j < count; j++)
            pivots[j] = _pivots[j];

        for (var j = 0; j < count; j++)
        {
            var pivot = pivots[j];
            var rotation = _rotations[j];
            for (var k = j + 1; k < count; k++)
                pivots[k] = pivot + rotation * (pivots[k] - pivot);
            point = pivot + rotation * (point - pivot);
        }

        return point;
    }

    /// <summary>
    ///     Rotates a clavicle about its own pivot so its upper arm moves by about push, and returns the degrees used.
    /// </summary>
    private float PullClavicle(int index, Vector3 push)
    {
        var clavicle = _clavicles[index];
        var arm = _shoulders[index].position - clavicle.position;
        var armSqr = arm.sqrMagnitude;
        if (armSqr < MinClavicleLength * MinClavicleLength)
            return 0f;

        // Only the part of the push across the clavicle can come from rotating it
        var across = push - arm * (Vector3.Dot(push, arm) / armSqr);
        var acrossLength = across.magnitude;
        if (acrossLength < 1e-5f)
            return 0f;

        var degrees = Mathf.Min(acrossLength / Mathf.Sqrt(armSqr) * Mathf.Rad2Deg, MaxClavicleDegrees);
        var axis = Vector3.Cross(arm, across).normalized;

        _clavicleLocalRotations[index] = clavicle.localRotation;
        _clavicleCached[index] = true;
        clavicle.rotation = Quaternion.AngleAxis(degrees, axis) * clavicle.rotation;

        return degrees;
    }

    /// <summary>
    ///     The shoulder-to-marker vector for an arm and the squared distance its hand may reach: ReachMargin of the arm's
    ///     length, or the current distance if the arm is already past that. False when it cannot be measured.
    /// </summary>
    private bool TryGetReach(int index, Transform[] markers, out Vector3 toTarget, out float reachSqr)
    {
        toTarget = default;
        reachSqr = 0f;

        if (markers == null || markers.Length <= index || markers[index] == null || _forearms[index] == null || _palms[index] == null)
            return false;

        // Measured through the forearm bone, which may sit part way down the forearm (a twist bone) and then reads short
        // on a bent arm; the floor keeps that from choking the push instead of only stopping a real over-reach
        var shoulder = _shoulders[index].position;
        var elbow = _forearms[index].position;
        var armLength = Mathf.Max((elbow - shoulder).magnitude + (_palms[index].position - elbow).magnitude, MinArmLength);

        toTarget = markers[index].position - shoulder;
        reachSqr = Mathf.Max(ReachMargin * ReachMargin * armLength * armLength, toTarget.sqrMagnitude);
        return true;
    }

    /// <summary>
    ///     The largest share (0 to 1) of displacement that keeps |toTarget - k displacement| within the reach; 0 when the
    ///     shoulder is already out of reach of toTarget.
    /// </summary>
    private static float MaxScale(Vector3 toTarget, Vector3 displacement, float reachSqr)
    {
        var targetSqr = toTarget.sqrMagnitude;
        if (targetSqr > reachSqr * 1.0001f)
            return 0f;

        var displacementSqr = displacement.sqrMagnitude;
        if (displacementSqr < 1e-10f)
            return 1f;

        // |toTarget - k d|^2 = reach^2, solved for the largest k
        var along = Vector3.Dot(toTarget, displacement);
        var discriminant = along * along - displacementSqr * (targetSqr - reachSqr);
        if (discriminant < 0f)
            return 0f;

        return Mathf.Clamp01((along + Mathf.Sqrt(discriminant)) / displacementSqr);
    }

    private static Vector3 GetWeaponDirection(Player player)
    {
        if (player.HandsController is Player.FirearmController firearmController && firearmController.CurrentFireport?.Original != null)
        {
            var direction = firearmController.WeaponDirection;
            if (direction.sqrMagnitude > 1e-6f && IsFinite(direction.x) && IsFinite(direction.y) && IsFinite(direction.z))
                return direction.normalized;
        }

        return player.HandsRotation * Vector3.forward;
    }

    /// <summary>
    ///     The back recoil the game computed for this shot, in RecoilForceBack units: RecoilDirection.z with the prefab's
    ///     scaling, the per-shot random range's mean and the pose factor divided out (the pose is scaled separately).
    ///     Skills, attachments, ammo and shot index stay in. Its sign is not relied on.
    /// </summary>
    private static bool TryGetShotBack(ShotEffector shotEffector, out float back)
    {
        back = 0f;

        if (shotEffector?.CurrentRecoilEffect is not NewRecoilShotEffect effect || effect.HandPositionRecoil == null)
            return false;

        var range = effect.BasicRecoilPositionStrengthRange;
        var denominator = effect.HandPositionRecoil.Intensity * effect.IncomingRotationStrengthMultiplier * 0.5f * (range.x + range.y) * effect.IntensitySeparateFactors.z;
        if (!IsFinite(denominator) || !(Mathf.Abs(denominator) > 1e-6f))
            return false;

        back = Mathf.Abs(effect.RecoilDirection.z / denominator);
        return IsFinite(back) && back >= MinTrustedBack && back <= MaxTrustedBack;
    }

    private static float GetClassFactor(IWeapon weapon)
    {
        var template = weapon?.WeaponTemplate;
        var weaponClass = template != null ? template.weapClass : null;

        switch (weaponClass)
        {
            case "pistol":
                return PistolFactor;
            case "shotgun":
                return ShotgunFactor;
            case "grenadeLauncher":
                return LauncherFactor;
        }

        return weapon?.Item is Weapon { Folded: true } ? PistolFactor : 1f;
    }

    private void ClearOutput()
    {
        Push = 0f;
        ClearTerms();
    }

    private void ClearTerms()
    {
        RockPush = TwistPush = ClaviclePush = 0f;
        RockDegrees = TwistDegrees = ClavicleDegrees = HeadMove = 0f;
        RockReach = RestReach = 1f;
    }

    private void LogResolution(Player player)
    {
        var bones = player.PlayerBones;
        var text = new StringBuilder("Shoulder recoil bones: ");
        text.Append(_resolveProblem ?? "OK");
        text.Append(" | ribcage: ").Append(Chain(_ribcage));

        text.Append(" | spine chain: ");
        if (_resolveProblem != null)
            text.Append("-");
        else if (_chain.Count > 1)
            text.Append(string.Join(" > ", _chain.ConvertAll(bone => bone.name)));
        else
            text.Append("ribcage only (Spine1 ").Append(bones != null && bones.Spine1 != null ? bones.Spine1.name : "-").Append(" is not above it)");

        text.Append(" | neck: ").Append(Chain(bones != null ? bones.Neck : null)).Append(_counterNeck ? " (head kept level)" : " (not under ribcage, left alone)");

        var shoulders = bones != null ? bones.Shoulders : null;
        for (var i = 0; i < 2; i++)
        {
            var shoulder = shoulders != null && shoulders.Length > i ? shoulders[i] : null;
            text.Append(i == 0 ? " | left upper arm: " : " | right upper arm: ").Append(Chain(shoulder));
        }

        text.Append(" | clavicle term: ").Append(ClavicleActive ? "on" : "off (its share in the rock and twist)");

        var eye = player.CameraPosition != null ? player.CameraPosition.parent : null;
        text.Append(" | camera: ").Append(Chain(eye)).Append(_preserveEye ? " (world pose preserved)" : " (not under the spine, left alone)");

        var handsObject = player.HandsController?.ControllerGameObject;
        text.Append(" | hands object: ").Append(handsObject != null ? Chain(handsObject.transform) : "-");

        Plugin.Log.LogInfo(text.ToString());
    }

    private static string Chain(Transform transform)
    {
        if (transform == null)
            return "-";

        var text = new StringBuilder(transform.name);
        var parent = transform.parent;
        for (var depth = 0; parent != null && depth < LogChainDepth; depth++, parent = parent.parent)
            text.Append(" < ").Append(parent.name);

        text.Append(parent == null ? " < (root)" : " < ...");
        return text.ToString();
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
