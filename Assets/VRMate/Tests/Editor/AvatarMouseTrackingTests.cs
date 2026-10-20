using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRMate.Tests
{
    /// <summary>
    /// 本家の AvatarMouseTracking（Assembly-CSharp）がボーンに足す回転を確かめる。
    /// asmdef から Assembly-CSharp は参照できないので、型は名前で取り、Start / LateUpdate と設定は反射で扱う。
    /// マウスの位置は制御できないので、角度の上下限を同じ値にして目標を固定する。
    /// Animator 役の姿勢は毎フレームの LateUpdate の前にテストが書く（追従はその時点の回転に左から掛ける）。
    /// </summary>
    public class AvatarMouseTrackingTests
    {
        private const string StateName = "Tracking";
        private const float SpineBlend = 0.5f;
        private const float HeadBlend = 0.75f;
        private const float SpineTargetDegrees = 10f;
        private const float ToleranceDegrees = 0.01f;
        private const int Frames = 120;
        private const int ConvergingFrames = 10;
        private const float HeadTurnLimitDegrees = 45f;

        // 収束に要するフレーム数を Time.deltaTime の大きさに依存させないため、Slerp の割合が 1 フレームで 1 に届く値にする
        private const float Immediate = 1e6f;

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type TrackingType = Type.GetType("AvatarMouseTracking, Assembly-CSharp");
        private static readonly Type PermissionType = Type.GetType("TrackingPermission, Assembly-CSharp");

        private static readonly (HumanBodyBones bone, string name, string parent, Vector3 local)[] Skeleton =
        {
            (HumanBodyBones.Hips, "Hips", null, new Vector3(0f, 1f, 0f)),
            (HumanBodyBones.Spine, "Spine", "Hips", new Vector3(0f, 0.1f, 0f)),
            (HumanBodyBones.Chest, "Chest", "Spine", new Vector3(0f, 0.12f, 0f)),
            (HumanBodyBones.UpperChest, "UpperChest", "Chest", new Vector3(0f, 0.12f, 0f)),
            (HumanBodyBones.Neck, "Neck", "UpperChest", new Vector3(0f, 0.12f, 0f)),
            (HumanBodyBones.Head, "Head", "Neck", new Vector3(0f, 0.1f, 0f)),
            (HumanBodyBones.LeftShoulder, "LeftShoulder", "UpperChest", new Vector3(-0.04f, 0.1f, 0f)),
            (HumanBodyBones.LeftUpperArm, "LeftUpperArm", "LeftShoulder", new Vector3(-0.1f, 0f, 0f)),
            (HumanBodyBones.LeftLowerArm, "LeftLowerArm", "LeftUpperArm", new Vector3(-0.25f, 0f, 0f)),
            (HumanBodyBones.LeftHand, "LeftHand", "LeftLowerArm", new Vector3(-0.22f, 0f, 0f)),
            (HumanBodyBones.RightShoulder, "RightShoulder", "UpperChest", new Vector3(0.04f, 0.1f, 0f)),
            (HumanBodyBones.RightUpperArm, "RightUpperArm", "RightShoulder", new Vector3(0.1f, 0f, 0f)),
            (HumanBodyBones.RightLowerArm, "RightLowerArm", "RightUpperArm", new Vector3(0.25f, 0f, 0f)),
            (HumanBodyBones.RightHand, "RightHand", "RightLowerArm", new Vector3(0.22f, 0f, 0f)),
            (HumanBodyBones.LeftUpperLeg, "LeftUpperLeg", "Hips", new Vector3(-0.09f, -0.05f, 0f)),
            (HumanBodyBones.LeftLowerLeg, "LeftLowerLeg", "LeftUpperLeg", new Vector3(0f, -0.42f, 0f)),
            (HumanBodyBones.LeftFoot, "LeftFoot", "LeftLowerLeg", new Vector3(0f, -0.42f, 0f)),
            (HumanBodyBones.RightUpperLeg, "RightUpperLeg", "Hips", new Vector3(0.09f, -0.05f, 0f)),
            (HumanBodyBones.RightLowerLeg, "RightLowerLeg", "RightUpperLeg", new Vector3(0f, -0.42f, 0f)),
            (HumanBodyBones.RightFoot, "RightFoot", "RightLowerLeg", new Vector3(0f, -0.42f, 0f)),
        };

        private readonly List<Object> created = new List<Object>();

        private struct Pose
        {
            public Quaternion Spine;
            public Quaternion Chest;
            public Quaternion UpperChest;
            public Quaternion Head;
        }

        private sealed class Rig
        {
            public Component Tracking;
            public Transform Spine;
            public Transform Chest;
            public Transform UpperChest;
            public Transform Head;
        }

        [TearDown]
        public void TearDown()
        {
            // GameObject（追従が作る driver を含む）を先に消し、Avatar と Controller はその後に消す
            foreach (var o in created)
            {
                if (o is GameObject && o != null)
                {
                    Object.DestroyImmediate(o);
                }
            }

            foreach (var o in created)
            {
                if (o != null)
                {
                    Object.DestroyImmediate(o);
                }
            }

            created.Clear();
        }

        // ---- 姿勢 ----

        private static Pose Uniform(Quaternion spine, Quaternion chest, Quaternion upperChest, Quaternion head)
        {
            return new Pose { Spine = spine, Chest = chest, UpperChest = upperChest, Head = head };
        }

        private static Quaternion Negated(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, -q.w);
        }

        /// <summary>名前の付いた姿勢。"animating" はフレームごとに別の姿勢を返し、最後のフレーム（Frames - 1）は "measured" になる。</summary>
        private static Pose PoseNamed(string name, int frame)
        {
            switch (name)
            {
                case "identity":
                    return Uniform(Quaternion.identity, Quaternion.identity, Quaternion.identity, Quaternion.identity);
                case "measured":
                    // 実機の aruka で Start の時点に保存された値（Spine・Head）と、Chest・UpperChest の小角度
                    return Uniform(
                        Quaternion.Euler(0.46f, 0.22f, 8.54f),
                        Quaternion.Euler(1.2f, -0.8f, 2.3f),
                        Quaternion.Euler(-0.9f, 1.5f, 1.7f),
                        Quaternion.Euler(-8.17f, 10.53f, -5.31f));
                case "measuredNegated":
                {
                    // 同じ回転を表す -q
                    var m = PoseNamed("measured", frame);
                    return Uniform(Negated(m.Spine), Negated(m.Chest), Negated(m.UpperChest), Negated(m.Head));
                }
                case "quarterTurn":
                    return Uniform(
                        Quaternion.AngleAxis(90f, new Vector3(1f, 2f, 3f).normalized),
                        Quaternion.AngleAxis(95f, Vector3.up),
                        Quaternion.AngleAxis(-88f, Vector3.right),
                        Quaternion.AngleAxis(92f, Vector3.forward));
                case "nearHalfTurn":
                    return Uniform(
                        Quaternion.AngleAxis(179f, Vector3.forward),
                        Quaternion.AngleAxis(178f, Vector3.up),
                        Quaternion.AngleAxis(-179f, new Vector3(1f, 1f, 0f).normalized),
                        Quaternion.AngleAxis(179.5f, Vector3.right));
                case "animating":
                    switch (frame % 3)
                    {
                        case 0: return PoseNamed("identity", frame);
                        case 1: return PoseNamed("quarterTurn", frame);
                        default: return PoseNamed("measured", frame);
                    }
                default:
                    throw new ArgumentException("知らない姿勢: " + name);
            }
        }

        private static Pose WithIdentityHead(Pose pose)
        {
            return Uniform(pose.Spine, pose.Chest, pose.UpperChest, Quaternion.identity);
        }

        private static void Write(Rig rig, Pose pose)
        {
            rig.Spine.localRotation = pose.Spine;
            rig.Chest.localRotation = pose.Chest;
            rig.UpperChest.localRotation = pose.UpperChest;
            rig.Head.localRotation = pose.Head;
        }

        private static Pose Read(Rig rig)
        {
            return Uniform(rig.Spine.localRotation, rig.Chest.localRotation, rig.UpperChest.localRotation, rig.Head.localRotation);
        }

        /// <summary>
        /// 2 つの回転の間の角度（度）。q と -q は同じ回転として 0 を返す。
        /// Quaternion.Angle は約 0.16° 未満を 0 に丸めるので、double で計算する。
        /// </summary>
        private static double AngleDegrees(Quaternion a, Quaternion b)
        {
            var r = Quaternion.Inverse(a) * b;
            var v = Math.Sqrt((double)r.x * r.x + (double)r.y * r.y + (double)r.z * r.z);
            return 2.0 * Math.Atan2(v, Math.Abs((double)r.w)) * 180.0 / Math.PI;
        }

        // ---- 反射 ----

        private static void Set(Component c, string field, object value)
        {
            var f = c.GetType().GetField(field, Members);
            Assert.That(f, Is.Not.Null, "AvatarMouseTracking に " + field + " が無い");
            f.SetValue(c, value);
        }

        private static object Call(Component c, string method, params object[] args)
        {
            var m = c.GetType().GetMethod(method, Members);
            Assert.That(m, Is.Not.Null, "AvatarMouseTracking に " + method + " が無い");
            return m.Invoke(c, args);
        }

        private static void SetPermission(object permission, string field, object value)
        {
            var f = PermissionType.GetField(field, Members);
            Assert.That(f, Is.Not.Null, "TrackingPermission に " + field + " が無い");
            f.SetValue(permission, value);
        }

        // ---- 骨格と追従 ----

        private Rig BuildRig()
        {
            Assert.That(TrackingType, Is.Not.Null, "Assembly-CSharp に AvatarMouseTracking が無い");
            Assert.That(PermissionType, Is.Not.Null, "Assembly-CSharp に TrackingPermission が無い");

            var cameraObject = new GameObject("MouseTrackingCamera", typeof(Camera));
            created.Add(cameraObject);
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 1.5f, 3f), Quaternion.Euler(0f, 180f, 0f));

            var root = new GameObject("MouseTrackingRig");
            created.Add(root);
            var bones = new Dictionary<string, Transform>();
            var human = new List<HumanBone>();
            var skeleton = new List<SkeletonBone>
            {
                new SkeletonBone { name = root.name, position = Vector3.zero, rotation = Quaternion.identity, scale = Vector3.one },
            };
            foreach (var (bone, name, parent, local) in Skeleton)
            {
                var t = new GameObject(name).transform;
                t.SetParent(parent == null ? root.transform : bones[parent], false);
                t.localPosition = local;
                bones[name] = t;
                skeleton.Add(new SkeletonBone { name = name, position = local, rotation = Quaternion.identity, scale = Vector3.one });
                human.Add(new HumanBone
                {
                    humanName = HumanTrait.BoneName[(int)bone],
                    boneName = name,
                    limit = new HumanLimit { useDefaultValues = true },
                });
            }

            var avatar = AvatarBuilder.BuildHumanAvatar(root, new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            });
            created.Add(avatar);
            Assert.That(avatar.isValid && avatar.isHuman, Is.True, "Humanoid の Avatar を作れない");

            var controller = new AnimatorController();
            created.Add(controller);
            controller.AddLayer("Base Layer");
            var machine = controller.layers[0].stateMachine;
            created.Add(machine);
            var state = machine.AddState(StateName);
            created.Add(state);
            machine.defaultState = state;

            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            animator.Rebind();
            animator.Update(0f);
            Assert.That(animator.isHuman, Is.True, "Animator が Humanoid でない");
            Assert.That(animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(Animator.StringToHash(StateName)),
                "Animator が許可したステートにいない");

            var tracking = root.AddComponent(TrackingType);
            var permission = Activator.CreateInstance(PermissionType);
            SetPermission(permission, "stateOrParameterName", StateName);
            SetPermission(permission, "isParameter", false);
            SetPermission(permission, "allowHead", true);
            SetPermission(permission, "allowSpine", true);
            SetPermission(permission, "allowEye", false);
            var permissions = (IList)TrackingType.GetField("trackingPermissions", Members).GetValue(tracking);
            permissions.Clear();
            permissions.Add(permission);

            return new Rig
            {
                Tracking = tracking,
                Spine = bones["Spine"],
                Chest = bones["Chest"],
                UpperChest = bones["UpperChest"],
                Head = bones["Head"],
            };
        }

        /// <summary>
        /// startPose で Start を呼び、毎フレーム framePose を書いてから LateUpdate を呼ぶ。
        /// 戻り値の written は最後のフレームに Animator 役が書いた姿勢。
        /// </summary>
        private (Rig rig, Pose written) Track(string startPose, string framePose, float spineTargetDegrees, float headLimitDegrees)
        {
            return Track(PoseNamed(startPose, 0), frame => PoseNamed(framePose, frame), spineTargetDegrees, headLimitDegrees, Immediate, Frames, null);
        }

        /// <summary>
        /// smoothness は head と spine の Slerp に掛かる値。afterFrame は各フレームの LateUpdate の直後に、
        /// フレーム番号・骨格・そのフレームに Animator 役が書いた姿勢を受け取る。
        /// </summary>
        private (Rig rig, Pose written) Track(Pose startPose, Func<int, Pose> framePose, float spineTargetDegrees, float headLimitDegrees,
            float smoothness, int frames, Action<int, Rig, Pose> afterFrame)
        {
            Assert.That(Time.deltaTime, Is.GreaterThan(0f), "Time.deltaTime が 0 では driver が初期値から動かず、追従の結果を確かめられない");

            var rig = BuildRig();
            Set(rig.Tracking, "enableMouseTracking", true);
            Set(rig.Tracking, "spineMinRotation", spineTargetDegrees);
            Set(rig.Tracking, "spineMaxRotation", spineTargetDegrees);
            Set(rig.Tracking, "headYawLimit", headLimitDegrees);
            Set(rig.Tracking, "headPitchLimit", headLimitDegrees);
            Set(rig.Tracking, "headSmoothness", smoothness);
            Set(rig.Tracking, "spineSmoothness", smoothness);
            Set(rig.Tracking, "spineFadeSpeed", Immediate);
            Set(rig.Tracking, "headBlend", HeadBlend);
            Set(rig.Tracking, "spineBlend", SpineBlend);

            Write(rig, startPose);
            Call(rig.Tracking, "Start");

            var written = default(Pose);
            for (var frame = 0; frame < frames; frame++)
            {
                Write(rig, framePose(frame));
                written = Read(rig);
                Call(rig.Tracking, "LateUpdate");
                afterFrame?.Invoke(frame, rig, written);
            }

            Assert.That((bool)Call(rig.Tracking, "IsAllowed", "Head"), Is.True, "頭の追従が許可されていない");
            Assert.That((bool)Call(rig.Tracking, "IsAllowed", "Spine"), Is.True, "背骨の追従が許可されていない");
            return (rig, written);
        }

        private static string Describe(string bone, double angle)
        {
            return bone + " " + angle.ToString("F4") + "°";
        }

        // ---- 目標が回転なしのとき ----

        [TestCase("identity", "identity")]
        [TestCase("identity", "measured")]
        [TestCase("identity", "animating")]
        [TestCase("measured", "measured")]
        [TestCase("measured", "identity")]
        [TestCase("measuredNegated", "measured")]
        [TestCase("quarterTurn", "quarterTurn")]
        [TestCase("nearHalfTurn", "measured")]
        [TestCase("measured", "animating")]
        public void LateUpdate_KeepsAnimatedPose_WhenTargetIsNoRotation(string startPose, string framePose)
        {
            var (rig, written) = Track(startPose, framePose, 0f, 0f);

            var spine = AngleDegrees(rig.Spine.localRotation, written.Spine);
            var chest = AngleDegrees(rig.Chest.localRotation, written.Chest);
            var upperChest = AngleDegrees(rig.UpperChest.localRotation, written.UpperChest);
            var head = AngleDegrees(rig.Head.localRotation, written.Head);

            var worst = Math.Max(Math.Max(spine, chest), Math.Max(upperChest, head));
            Assert.That(worst, Is.LessThanOrEqualTo(ToleranceDegrees),
                "Animator の姿勢からのずれ: " + Describe("Spine", spine) + ", " + Describe("Chest", chest) + ", "
                + Describe("UpperChest", upperChest) + ", " + Describe("Head", head));
        }

        // ---- 背骨の目標が固定の値のとき ----

        [TestCase("identity", "identity")]
        [TestCase("identity", "measured")]
        [TestCase("identity", "animating")]
        [TestCase("measured", "measured")]
        [TestCase("measured", "identity")]
        [TestCase("measuredNegated", "measured")]
        [TestCase("quarterTurn", "quarterTurn")]
        [TestCase("nearHalfTurn", "measured")]
        [TestCase("measured", "animating")]
        public void LateUpdate_AddsSpineTargetScaledPerBone_WhenSpineTargetIsFixed(string startPose, string framePose)
        {
            var (rig, written) = Track(startPose, framePose, SpineTargetDegrees, 0f);

            // 目標は Euler(0, -targetY, 0)。許可されている間 spineTrackingWeight は 1 に収束するので a = spineBlend
            var target = Quaternion.Euler(0f, -SpineTargetDegrees, 0f);
            var applied = 1f * SpineBlend;
            var expectedSpine = Quaternion.Slerp(Quaternion.identity, target, applied);
            var expectedChest = Quaternion.Slerp(Quaternion.identity, target, 0.8f * applied);
            var expectedUpperChest = Quaternion.Slerp(Quaternion.identity, target, 0.6f * applied);

            var addedSpine = rig.Spine.localRotation * Quaternion.Inverse(written.Spine);
            var addedChest = rig.Chest.localRotation * Quaternion.Inverse(written.Chest);
            var addedUpperChest = rig.UpperChest.localRotation * Quaternion.Inverse(written.UpperChest);

            Assert.That(AngleDegrees(Quaternion.identity, addedSpine), Is.GreaterThan(1.0), "背骨に何も足されていない");

            var spine = AngleDegrees(addedSpine, expectedSpine);
            var chest = AngleDegrees(addedChest, expectedChest);
            var upperChest = AngleDegrees(addedUpperChest, expectedUpperChest);
            var head = AngleDegrees(rig.Head.localRotation, written.Head);

            var worst = Math.Max(Math.Max(spine, chest), Math.Max(upperChest, head));
            Assert.That(worst, Is.LessThanOrEqualTo(ToleranceDegrees),
                "期待する追従からのずれ: " + Describe("Spine", spine) + ", " + Describe("Chest", chest) + ", "
                + Describe("UpperChest", upperChest) + ", " + Describe("Head", head)
                + "（足された角度 Spine " + AngleDegrees(Quaternion.identity, addedSpine).ToString("F4") + "°, 期待 "
                + AngleDegrees(Quaternion.identity, expectedSpine).ToString("F4") + "°）");
        }

        // ---- 頭の追従が働いていること ----

        [Test]
        public void LateUpdate_TurnsHead_WhenHeadLimitsAllowTurning()
        {
            var (rig, written) = Track("identity", "identity", 0f, 45f);

            var turned = AngleDegrees(rig.Head.localRotation, written.Head);

            Assert.That(turned, Is.GreaterThan(0.5), "頭が回っていない（DoHead が動いていない）: " + turned.ToString("F4") + "°");
        }

        // ---- 収束の途中 ----

        [TestCase("identity", "measured")]
        [TestCase("measured", "measured")]
        [TestCase("measured", "identity")]
        [TestCase("measuredNegated", "measured")]
        [TestCase("quarterTurn", "quarterTurn")]
        [TestCase("nearHalfTurn", "measured")]
        public void LateUpdate_KeepsAnimatedPoseOnEveryFrame_WhileConvergingToNoRotation(string startPose, string framePose)
        {
            // Slerp の割合が 1 フレームで 0.5 になる値。driver が目標に届く前のフレームも見る
            var halfPerFrame = 0.5f / Time.deltaTime;
            var names = new[] { "Spine", "Chest", "UpperChest", "Head" };
            var first = new double[4];
            var worst = new double[4];
            var worstFrame = new int[4];

            Track(PoseNamed(startPose, 0), frame => PoseNamed(framePose, frame), 0f, 0f, halfPerFrame, ConvergingFrames,
                (frame, rig, written) =>
                {
                    var angles = new[]
                    {
                        AngleDegrees(rig.Spine.localRotation, written.Spine),
                        AngleDegrees(rig.Chest.localRotation, written.Chest),
                        AngleDegrees(rig.UpperChest.localRotation, written.UpperChest),
                        AngleDegrees(rig.Head.localRotation, written.Head),
                    };
                    for (var i = 0; i < angles.Length; i++)
                    {
                        if (frame == 0)
                        {
                            first[i] = angles[i];
                        }

                        if (angles[i] > worst[i])
                        {
                            worst[i] = angles[i];
                            worstFrame[i] = frame;
                        }
                    }
                });

            var report = new List<string>();
            for (var i = 0; i < names.Length; i++)
            {
                report.Add(Describe(names[i], worst[i]) + "（frame " + worstFrame[i] + "、最初のフレーム " + first[i].ToString("F4") + "°）");
            }

            Assert.That(Math.Max(Math.Max(worst[0], worst[1]), Math.Max(worst[2], worst[3])), Is.LessThanOrEqualTo(ToleranceDegrees),
                "Animator の姿勢からの最大のずれ: " + string.Join(", ", report));
        }

        // ---- 頭の目標が回転を持つとき ----

        [TestCase("measured", "measured")]
        [TestCase("identity", "measured")]
        [TestCase("measuredNegated", "measured")]
        [TestCase("quarterTurn", "quarterTurn")]
        [TestCase("nearHalfTurn", "measured")]
        [TestCase("measured", "animating")]
        public void LateUpdate_AddsHeadRotationDecidedByTargetOnly_WhenHeadTurns(string startPose, string framePose)
        {
            // 基準は Start も頭のフレームの姿勢も単位回転にした骨格（頭以外のフレームの姿勢は同じなので、頭の目標も同じになる）
            var (reference, referenceWritten) = Track(PoseNamed("identity", 0), frame => WithIdentityHead(PoseNamed(framePose, frame)),
                0f, HeadTurnLimitDegrees, Immediate, Frames, null);
            var (rig, written) = Track(PoseNamed(startPose, 0), frame => PoseNamed(framePose, frame),
                0f, HeadTurnLimitDegrees, Immediate, Frames, null);

            var expected = reference.Head.localRotation * Quaternion.Inverse(referenceWritten.Head);
            var added = rig.Head.localRotation * Quaternion.Inverse(written.Head);
            var turned = AngleDegrees(Quaternion.identity, expected);

            Assert.That(turned, Is.GreaterThan(1.0), "頭が回っていない: " + turned.ToString("F4") + "°");
            var difference = AngleDegrees(added, expected);
            Assert.That(difference, Is.LessThanOrEqualTo(ToleranceDegrees),
                "頭に左から足された回転の基準との差 " + difference.ToString("F4") + "°（基準で回った角度 " + turned.ToString("F4")
                + "°、この骨格で回った角度 " + AngleDegrees(Quaternion.identity, added).ToString("F4") + "°）");
        }
    }
}
