using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniGLTF.SpringBoneJobs.Blittables;
using UnityEngine;
using UniVRM10;
using VRM;
using Object = UnityEngine.Object;

namespace VRMate.Tests
{
    /// <summary>
    /// 本家の AvatarGravityController（Assembly-CSharp）が、窓の移動を揺れものにどう伝えるかを確かめる。
    /// asmdef から Assembly-CSharp は参照できないので、型は名前で取り、Start / Update と差し替え口のフィールドは反射で扱う。
    /// 窓の位置は windowPositionProvider で与える。VRM 1.0 の揺れもののランタイムは本体に差し替え口を持たせず、
    /// UniVRM 自身の保持場所（Vrm10Instance.m_runtime と、その Vrm10Runtime.SpringBone）に偽物を入れて、本番と同じ経路で本体に読ませる。
    /// Vrm10Instance.Runtime は m_runtime が null のときだけ Runtime を作るので、m_runtime は常に埋めておき、
    /// ランタイムが無いフレームは SpringBone を null にして表す。
    /// 偽物は本番の Vrm10Runtime.SpringBone と同じ IVrm10SpringBoneRuntime なので、本体がランタイムへ出す呼び出しをそのまま記録できる。
    /// </summary>
    public class AvatarGravityControllerTests
    {
        // 既定値（0.05）と区別できる倍率
        private const float Multiplier = 0.37f;
        private const float ForceTolerance = 1e-6f;
        private const int ManyJoints = 300;

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type ControllerType = Type.GetType("AvatarGravityController, Assembly-CSharp");

        // 実際の窓の位置（GetWindowRect）とは別の値にして、Start が差し替え口から初期位置を取ることを確かめる
        private static readonly Vector2Int StartPosition = new Vector2Int(100, 200);

        // フレームごとの窓の位置。止まる・上下左右に 1px・斜め・負の座標・大きな移動を含む
        private static readonly Vector2Int[] WindowPath =
        {
            new Vector2Int(100, 200), // 止まっている
            new Vector2Int(101, 200), // 右へ 1px
            new Vector2Int(101, 199), // 上へ 1px
            new Vector2Int(-1820, -50), // 負の座標へ大きく
            new Vector2Int(-1820, -50), // 止まっている
            new Vector2Int(-1813, -57), // 右上へ斜め
            new Vector2Int(98187, 99943), // 右下へ大きく
            new Vector2Int(98187, 99943), // 止まっている
            new Vector2Int(98187, 99944), // 下へ 1px
            new Vector2Int(98186, 99944), // 左へ 1px
            new Vector2Int(98186, 99944), // 止まっている
        };

        private readonly List<Object> created = new List<Object>();

        /// <summary>.vrm から読み込んだ関節の値の代わりに、Start の前に関節へ書く値。</summary>
        private struct JointSpec
        {
            public Vector3 GravityDir;
            public float GravityPower;
            public float Stiffness;
            public float Drag;
            public float Radius;
        }

        /// <summary>
        /// Vrm10Runtime.SpringBone の代わり。SetModelLevel の引数を記録し、
        /// 関節の設定・再構築・初期姿勢への復帰・毎フレームの処理が呼ばれたら、その名前を記録する。
        /// </summary>
        private sealed class FakeSpringBoneRuntime : IVrm10SpringBoneRuntime
        {
            public readonly List<(Transform root, BlittableModelLevel level)> ModelLevels = new List<(Transform, BlittableModelLevel)>();
            public readonly List<string> UnexpectedCalls = new List<string>();

            public Task InitializeAsync(Vrm10Instance instance, IAwaitCaller awaitCaller)
            {
                return Task.CompletedTask;
            }

            public bool ReconstructSpringBone()
            {
                UnexpectedCalls.Add("ReconstructSpringBone");
                return true;
            }

            public void RestoreInitialTransform()
            {
                UnexpectedCalls.Add("RestoreInitialTransform");
            }

            public void SetJointLevel(Transform joint, BlittableJointMutable jointSettings)
            {
                UnexpectedCalls.Add("SetJointLevel(" + (joint != null ? joint.name : "null") + ", 重力の向き " + Describe(jointSettings.gravityDir)
                    + "、強さ " + jointSettings.gravityPower.ToString("R") + ")");
            }

            public void SetModelLevel(Transform modelRoot, BlittableModelLevel modelSettings)
            {
                ModelLevels.Add((modelRoot, modelSettings));
            }

            public void Process()
            {
                UnexpectedCalls.Add("Process");
            }

            public void DrawGizmos()
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class Model
        {
            public Component Controller;
            public Transform Vrm10Root;
            public readonly List<(VRM10SpringBoneJoint joint, JointSpec spec)> Joints = new List<(VRM10SpringBoneJoint, JointSpec)>();
            public readonly List<VRMSpringBone> Vrm0Bones = new List<VRMSpringBone>();
            public readonly FakeSpringBoneRuntime Spring = new FakeSpringBoneRuntime();

            // Spring を保持する Vrm10Runtime。配置が "none" のときは、階層の外にある別のモデルのもの
            public Vrm10Runtime Runtime;
            public Vector2Int Window;
        }

        private readonly List<Vrm10Instance> instances = new List<Vrm10Instance>();

        [TearDown]
        public void TearDown()
        {
            // 偽の Vrm10Runtime は Dispose できないので、壊す前に Vrm10Instance から外しておく
            foreach (var instance in instances)
            {
                if (instance != null)
                {
                    RuntimeField().SetValue(instance, null);
                }
            }

            instances.Clear();

            foreach (var o in created)
            {
                if (o != null)
                {
                    Object.DestroyImmediate(o);
                }
            }

            created.Clear();
        }

        // ---- 関節の値 ----

        private static JointSpec Spec(Vector3 dir, float power, float stiffness = 1.3f, float drag = 0.45f, float radius = 0.03f)
        {
            return new JointSpec { GravityDir = dir, GravityPower = power, Stiffness = stiffness, Drag = drag, Radius = radius };
        }

        /// <summary>名前の付いた関節の組。</summary>
        private static JointSpec[] JointsNamed(string name)
        {
            switch (name)
            {
                case "none":
                    return new JointSpec[0];
                case "typical":
                    return new[] { Spec(new Vector3(0f, -1f, 0f), 0.5f) };
                case "zeroPower":
                    // 重力の強さが 0。向きは下向きのまま残っていなければならない
                    return new[] { Spec(new Vector3(0f, -1f, 0f), 0f) };
                case "zeroDirection":
                    return new[] { Spec(Vector3.zero, 1.2f) };
                case "unnormalized":
                    return new[] { Spec(new Vector3(3f, -4f, 0.5f), 2.5f) };
                case "mixed":
                    return new[]
                    {
                        Spec(new Vector3(0f, -1f, 0f), 0.5f, 0.8f, 0.2f, 0.01f),
                        Spec(new Vector3(0f, -1f, 0f), 0f),
                        Spec(Vector3.zero, 1.2f),
                        Spec(new Vector3(3f, -4f, 0.5f), 2.5f),
                        Spec(new Vector3(-0.2f, 0.1f, 0.97f), 0.03f, 4f, 1f, 0f),
                        Spec(new Vector3(0f, 1f, 0f), 7f),
                    };
                case "many":
                {
                    var specs = new JointSpec[ManyJoints];
                    for (var i = 0; i < specs.Length; i++)
                    {
                        specs[i] = Spec(new Vector3(Mathf.Sin(i), 2f * Mathf.Cos(i), i % 7 - 3), i % 11 * 0.25f, 0.5f + i % 5, i % 10 * 0.1f, 0.001f * i);
                    }

                    return specs;
                }
                default:
                    throw new ArgumentException("知らない関節の組: " + name);
            }
        }

        // ---- 反射 ----

        private static void SetField(Component c, string field, Type expectedType, object value)
        {
            var f = c.GetType().GetField(field, Members);
            Assert.That(f, Is.Not.Null, "AvatarGravityController に " + field + " が無い");
            Assert.That(f.FieldType, Is.EqualTo(expectedType), "AvatarGravityController の " + field + " の型");
            f.SetValue(c, value);
        }

        private static void Call(Component c, string method)
        {
            var m = c.GetType().GetMethod(method, Members, null, Type.EmptyTypes, null);
            Assert.That(m, Is.Not.Null, "AvatarGravityController に " + method + " が無い");
            m.Invoke(c, null);
        }

        private static FieldInfo RuntimeField()
        {
            var f = typeof(Vrm10Instance).GetField("m_runtime", Members);
            Assert.That(f, Is.Not.Null, "Vrm10Instance に m_runtime が無い（UniVRM の保持場所が変わった）");
            Assert.That(f.FieldType, Is.EqualTo(typeof(Vrm10Runtime)), "Vrm10Instance.m_runtime の型");
            return f;
        }

        private static FieldInfo SpringBoneField()
        {
            var f = typeof(Vrm10Runtime).GetField("<SpringBone>k__BackingField", Members);
            Assert.That(f, Is.Not.Null, "Vrm10Runtime に SpringBone の backing field が無い（UniVRM の保持場所が変わった）");
            Assert.That(f.FieldType, Is.EqualTo(typeof(IVrm10SpringBoneRuntime)), "Vrm10Runtime.SpringBone の backing field の型");
            return f;
        }

        /// <summary>
        /// spring を保持する Vrm10Runtime をコンストラクタを通さずに作り、Vrm10Instance.m_runtime に入れる。
        /// Vrm10Instance.Runtime がその Vrm10Runtime をそのまま返す（Runtime を作り直さない）ことも確かめる。
        /// </summary>
        private Vrm10Runtime InstallRuntime(Vrm10Instance instance, IVrm10SpringBoneRuntime spring)
        {
            var runtime = (Vrm10Runtime)FormatterServices.GetUninitializedObject(typeof(Vrm10Runtime));
            SpringBoneField().SetValue(runtime, spring);
            RuntimeField().SetValue(instance, runtime);
            instances.Add(instance);

            Assert.That(instance.Runtime, Is.SameAs(runtime), "Vrm10Instance.Runtime が m_runtime に入れた Vrm10Runtime を返さない");
            Assert.That(instance.Runtime.SpringBone, Is.SameAs(spring), "Vrm10Runtime.SpringBone が入れた偽物を返さない");
            return runtime;
        }

        /// <summary>Vrm10Runtime.SpringBone を差し替える。null にするとランタイムが無いフレームになる。</summary>
        private static void SetSpringBone(Vrm10Runtime runtime, IVrm10SpringBoneRuntime spring)
        {
            SpringBoneField().SetValue(runtime, spring);
        }

        /// <summary>型 t の中（総称型の引数・配列の要素・デリゲートの引数と戻り値）に target が現れるか。</summary>
        private static bool Mentions(Type t, Type target)
        {
            if (t == null)
            {
                return false;
            }

            if (target.IsAssignableFrom(t) || t == target)
            {
                return true;
            }

            if (t.HasElementType && Mentions(t.GetElementType(), target))
            {
                return true;
            }

            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    if (Mentions(arg, target))
                    {
                        return true;
                    }
                }
            }

            if (typeof(Delegate).IsAssignableFrom(t))
            {
                var invoke = t.GetMethod("Invoke");
                if (invoke != null)
                {
                    if (Mentions(invoke.ReturnType, target))
                    {
                        return true;
                    }

                    foreach (var p in invoke.GetParameters())
                    {
                        if (Mentions(p.ParameterType, target))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // ---- モデル ----

        /// <summary>
        /// layout は Vrm10Instance の置き場所。"self" は AvatarGravityController と同じ GameObject、
        /// "parent" は AvatarGravityController の親、"none" は階層のどこにも置かない。
        /// 関節と VRMSpringBone は AvatarGravityController の子に置く。
        /// Vrm10Instance の Runtime.SpringBone は model.Spring にしておく。"none" のときは、階層の外に別のモデルの
        /// Vrm10Instance を置いてそこに model.Spring を持たせ、呼び出しが止まるのが Vrm10Instance の有無によることを確かめられるようにする。
        /// </summary>
        private Model Build(string layout, JointSpec[] joints, int vrm0Bones)
        {
            Assert.That(ControllerType, Is.Not.Null, "Assembly-CSharp に AvatarGravityController が無い");

            var model = new Model();
            var root = new GameObject("GravityModel");
            created.Add(root);

            GameObject owner;
            switch (layout)
            {
                case "self":
                {
                    owner = root;
                    var instance = root.AddComponent<Vrm10Instance>();
                    model.Vrm10Root = instance.transform;
                    model.Runtime = InstallRuntime(instance, model.Spring);
                    break;
                }
                case "parent":
                {
                    owner = new GameObject("GravityControllerChild");
                    owner.transform.SetParent(root.transform, false);
                    var instance = root.AddComponent<Vrm10Instance>();
                    model.Vrm10Root = instance.transform;
                    model.Runtime = InstallRuntime(instance, model.Spring);
                    break;
                }
                case "none":
                {
                    owner = root;
                    var other = new GameObject("UnrelatedVrm10Model");
                    created.Add(other);
                    model.Runtime = InstallRuntime(other.AddComponent<Vrm10Instance>(), model.Spring);
                    break;
                }
                default:
                    throw new ArgumentException("知らない配置: " + layout);
            }

            for (var i = 0; i < joints.Length; i++)
            {
                var go = new GameObject("Joint" + i);
                go.transform.SetParent(owner.transform, false);
                var joint = go.AddComponent<VRM10SpringBoneJoint>();
                joint.m_gravityDir = joints[i].GravityDir;
                joint.m_gravityPower = joints[i].GravityPower;
                joint.m_stiffnessForce = joints[i].Stiffness;
                joint.m_dragForce = joints[i].Drag;
                joint.m_jointRadius = joints[i].Radius;
                model.Joints.Add((joint, joints[i]));
            }

            for (var i = 0; i < vrm0Bones; i++)
            {
                var go = new GameObject("Vrm0Spring" + i);
                go.transform.SetParent(owner.transform, false);
                var spring = go.AddComponent<VRMSpringBone>();
                spring.m_gravityDir = new Vector3(0.1f, -0.9f, 0.2f);
                spring.m_gravityPower = 0.6f + i;
                model.Vrm0Bones.Add(spring);
            }

            model.Controller = owner.AddComponent(ControllerType);
            SetField(model.Controller, "impactMultiplier", typeof(float), Multiplier);
            SetField(model.Controller, "windowPositionProvider", typeof(Func<Vector2Int>), (Func<Vector2Int>)(() => model.Window));
            return model;
        }

        /// <summary>
        /// 窓を StartPosition に置いて Start を呼び、WindowPath の位置ごとに Update を呼ぶ。
        /// beforeFrame はフレーム番号を受け取り、Update の前に呼ばれる。
        /// afterFrame はフレーム番号と、そのフレームの窓の移動量を受け取る。
        /// Start で渡ったモデル単位の設定は数えない。ランタイムへの想定外の呼び出しは Start の分も残す。
        /// </summary>
        private static void Run(Model model, Action<int, Vector2Int> afterFrame, Action<int> beforeFrame = null)
        {
            model.Window = StartPosition;
            Call(model.Controller, "Start");
            model.Spring.ModelLevels.Clear();

            var previous = StartPosition;
            for (var frame = 0; frame < WindowPath.Length; frame++)
            {
                beforeFrame?.Invoke(frame);
                model.Window = WindowPath[frame];
                Call(model.Controller, "Update");
                afterFrame?.Invoke(frame, WindowPath[frame] - previous);
                previous = WindowPath[frame];
            }
        }

        /// <summary>移動量 delta から決まる力。画面の右・下が正の窓座標を、x を反転した向きにして倍率を掛ける。</summary>
        private static Vector3 ExpectedForce(Vector2Int delta)
        {
            if (delta == Vector2Int.zero)
            {
                return Vector3.zero;
            }

            double x = -delta.x;
            double y = delta.y;
            var length = Math.Sqrt(x * x + y * y);
            return new Vector3((float)(x / length * Multiplier), (float)(y / length * Multiplier), 0f);
        }

        private static string Describe(Vector3 v)
        {
            return "(" + v.x.ToString("R") + ", " + v.y.ToString("R") + ", " + v.z.ToString("R") + ")";
        }

        private static void AssertForce(Vector3 actual, Vector2Int delta, string what, int frame)
        {
            var expected = ExpectedForce(delta);
            var message = what + "（frame " + frame + "、移動量 " + delta + "）: 期待 " + Describe(expected) + "、実際 " + Describe(actual);
            if (delta == Vector2Int.zero)
            {
                Assert.That(actual.x == 0f && actual.y == 0f && actual.z == 0f, Is.True, message);
                return;
            }

            Assert.That(Math.Abs(actual.x - expected.x) <= ForceTolerance
                && Math.Abs(actual.y - expected.y) <= ForceTolerance
                && Math.Abs(actual.z - expected.z) <= ForceTolerance, Is.True, message);
        }

        /// <summary>各関節の値が Start の前に書いた値とビット単位で同じであることを確かめる。</summary>
        private static void AssertJointsAsLoaded(Model model, int frame)
        {
            foreach (var (joint, spec) in model.Joints)
            {
                var same = joint.m_gravityDir.x == spec.GravityDir.x
                    && joint.m_gravityDir.y == spec.GravityDir.y
                    && joint.m_gravityDir.z == spec.GravityDir.z
                    && joint.m_gravityPower == spec.GravityPower
                    && joint.m_stiffnessForce == spec.Stiffness
                    && joint.m_dragForce == spec.Drag
                    && joint.m_jointRadius == spec.Radius;
                Assert.That(same, Is.True,
                    joint.name + " の値が読み込んだ値から変わった（frame " + frame + "）: 重力の向き " + Describe(spec.GravityDir) + " → "
                    + Describe(joint.m_gravityDir) + "、強さ " + spec.GravityPower.ToString("R") + " → " + joint.m_gravityPower.ToString("R")
                    + "、stiffness " + spec.Stiffness.ToString("R") + " → " + joint.m_stiffnessForce.ToString("R")
                    + "、drag " + spec.Drag.ToString("R") + " → " + joint.m_dragForce.ToString("R")
                    + "、radius " + spec.Radius.ToString("R") + " → " + joint.m_jointRadius.ToString("R"));
            }
        }

        private static void AssertNoUnexpectedRuntimeCalls(Model model, int frame)
        {
            Assert.That(model.Spring.UnexpectedCalls, Is.Empty,
                "frame " + frame + " までに揺れもののランタイムへ想定外の呼び出しがあった: " + string.Join(" / ", model.Spring.UnexpectedCalls));
        }

        // ---- 関節の重力は読み込んだ値のまま ----

        [TestCase("self", "typical")]
        [TestCase("self", "zeroPower")]
        [TestCase("self", "zeroDirection")]
        [TestCase("self", "unnormalized")]
        [TestCase("self", "mixed")]
        [TestCase("self", "many")]
        [TestCase("parent", "mixed")]
        [TestCase("none", "mixed")]
        public void Update_KeepsEveryJointGravityAsLoaded_WhileWindowStopsAndMoves(string layout, string joints)
        {
            var model = Build(layout, JointsNamed(joints), 0);
            Assert.That(model.Joints.Count, Is.GreaterThan(0), "関節が無いと確かめられない");

            var moved = 0;
            var stopped = 0;
            Run(model, (frame, delta) =>
            {
                if (delta == Vector2Int.zero)
                {
                    stopped++;
                }
                else
                {
                    moved++;
                }

                AssertJointsAsLoaded(model, frame);
            });

            Assert.That(moved > 0 && stopped > 0, Is.True, "窓が止まったフレームと動いたフレームの両方を通っていない");
        }

        [TestCase("self", "mixed")]
        [TestCase("self", "many")]
        [TestCase("parent", "mixed")]
        [TestCase("self", "none")]
        public void Update_NeverSetsJointLevelOrRebuildsThroughSpringBoneRuntime_WhileWindowStopsAndMoves(string layout, string joints)
        {
            var model = Build(layout, JointsNamed(joints), 0);

            var moved = 0;
            var stopped = 0;
            Run(model, (frame, delta) =>
            {
                if (delta == Vector2Int.zero)
                {
                    stopped++;
                }
                else
                {
                    moved++;
                }

                AssertNoUnexpectedRuntimeCalls(model, frame);
            });

            Assert.That(moved > 0 && stopped > 0, Is.True, "窓が止まったフレームと動いたフレームの両方を通っていない");
            Assert.That(model.Spring.ModelLevels.Count, Is.GreaterThan(0), "ランタイムへ一度も設定が渡っておらず、呼び出しの経路を通っていない");
        }

        // ---- 窓の移動はモデル単位の外力として渡る ----

        [TestCase("self", "mixed")]
        [TestCase("parent", "mixed")]
        [TestCase("self", "none")]
        public void Update_PassesWindowForceAsModelExternalForce_OnEveryFrame(string layout, string joints)
        {
            var model = Build(layout, JointsNamed(joints), 0);

            Run(model, (frame, delta) =>
            {
                Assert.That(model.Spring.ModelLevels.Count, Is.EqualTo(1), "frame " + frame + " でモデル単位の設定を渡した回数");
                var (root, level) = model.Spring.ModelLevels[0];
                Assert.That(root, Is.SameAs(model.Vrm10Root), "frame " + frame + " で渡した Transform が Vrm10Instance のものでない");
                AssertForce(level.ExternalForce, delta, "モデル単位の外力", frame);
                Assert.That(level.StopSpringBoneWriteback, Is.False, "frame " + frame + " の StopSpringBoneWriteback");
                Assert.That(level.SupportsScalingAtRuntime, Is.False, "frame " + frame + " の SupportsScalingAtRuntime");
                model.Spring.ModelLevels.Clear();
            });
        }

        [Test]
        public void Update_KeysModelLevelByVrm10InstanceTransform_WhenControllerIsOnChild()
        {
            var model = Build("parent", JointsNamed("typical"), 0);
            Assert.That(model.Controller.transform, Is.Not.SameAs(model.Vrm10Root), "親子に分かれた配置になっていない");

            Run(model, (frame, delta) =>
            {
                Assert.That(model.Spring.ModelLevels.Count, Is.EqualTo(1), "frame " + frame + " でモデル単位の設定を渡した回数");
                Assert.That(model.Spring.ModelLevels[0].root, Is.SameAs(model.Vrm10Root),
                    "frame " + frame + " のキーが Vrm10Instance の Transform でない（実際: "
                    + (model.Spring.ModelLevels[0].root != null ? model.Spring.ModelLevels[0].root.name : "null") + "）");
                model.Spring.ModelLevels.Clear();
            });
        }

        // ---- ランタイムが無いフレーム ----

        [TestCase("self")]
        [TestCase("parent")]
        public void Update_SkipsSpringBoneRuntime_OnFramesWhereVrm10RuntimeHasNoSpringBone(string layout)
        {
            var model = Build(layout, JointsNamed("mixed"), 0);

            // Start と、3 フレームのうち 2 フレームは Vrm10Runtime.SpringBone が無い。止まった・動いたフレームの両方に有無が当たる
            var present = false;
            SetSpringBone(model.Runtime, null);
            var presentStopped = 0;
            var presentMoved = 0;
            var absentStopped = 0;
            var absentMoved = 0;
            Run(model,
                (frame, delta) =>
                {
                    var stopped = delta == Vector2Int.zero;
                    if (!present)
                    {
                        if (stopped)
                        {
                            absentStopped++;
                        }
                        else
                        {
                            absentMoved++;
                        }

                        Assert.That(model.Spring.ModelLevels, Is.Empty, "frame " + frame + " はランタイムが無いのにモデル単位の設定を渡した");
                    }
                    else
                    {
                        if (stopped)
                        {
                            presentStopped++;
                        }
                        else
                        {
                            presentMoved++;
                        }

                        Assert.That(model.Spring.ModelLevels.Count, Is.EqualTo(1), "frame " + frame + " でモデル単位の設定を渡した回数");
                        Assert.That(model.Spring.ModelLevels[0].root, Is.SameAs(model.Vrm10Root), "frame " + frame + " のキー");
                        AssertForce(model.Spring.ModelLevels[0].level.ExternalForce, delta, "ランタイムが戻ったフレームの外力", frame);
                    }

                    AssertNoUnexpectedRuntimeCalls(model, frame);
                    AssertJointsAsLoaded(model, frame);
                    model.Spring.ModelLevels.Clear();
                },
                frame =>
                {
                    present = frame % 3 == 1;
                    SetSpringBone(model.Runtime, present ? model.Spring : null);
                });

            Assert.That(presentStopped > 0 && presentMoved > 0 && absentStopped > 0 && absentMoved > 0, Is.True,
                "ランタイムの有無と窓の停止・移動の組み合わせを通っていない（有・停止 " + presentStopped + "、有・移動 " + presentMoved
                + "、無・停止 " + absentStopped + "、無・移動 " + absentMoved + "）");
        }

        // ---- VRM 0.x ----

        [Test]
        public void Update_SetsWindowForceOnVrm0SpringBones_OnEveryFrame()
        {
            var model = Build("none", JointsNamed("none"), 2);
            var dirs = new List<Vector3>();
            var powers = new List<float>();
            foreach (var spring in model.Vrm0Bones)
            {
                dirs.Add(spring.m_gravityDir);
                powers.Add(spring.m_gravityPower);
            }

            Run(model, (frame, delta) =>
            {
                for (var i = 0; i < model.Vrm0Bones.Count; i++)
                {
                    var spring = model.Vrm0Bones[i];
                    AssertForce(spring.ExternalForce, delta, spring.name + " の ExternalForce", frame);
                    Assert.That(spring.m_gravityDir.Equals(dirs[i]) && spring.m_gravityPower == powers[i], Is.True,
                        spring.name + " の重力が変わった（frame " + frame + "）");
                }
            });

            Assert.That(model.Spring.ModelLevels, Is.Empty, "Vrm10Instance が無いのにモデル単位の設定を渡した");
            Assert.That(model.Spring.UnexpectedCalls, Is.Empty, "Vrm10Instance が無いのにランタイムを呼んだ");
        }

        // ---- Vrm10Instance が無い階層 ----

        [TestCase("none")]
        [TestCase("mixed")]
        public void Update_DoesNotCallSpringBoneRuntime_WhenNoVrm10InstanceInHierarchy(string joints)
        {
            // 階層の外にある別のモデルの Vrm10Instance はランタイムを返せる状態にしておき、
            // 自分の階層に Vrm10Instance が無いことだけで呼び出しが止まることを確かめる
            var model = Build("none", JointsNamed(joints), 0);

            Run(model, (frame, delta) => AssertJointsAsLoaded(model, frame));

            Assert.That(model.Spring.ModelLevels, Is.Empty, "Vrm10Instance が無いのにモデル単位の設定を渡した");
            Assert.That(model.Spring.UnexpectedCalls, Is.Empty, "Vrm10Instance が無いのにランタイムを呼んだ");
        }

        // ---- VRM 1.0 の揺れものへの経路 ----

        [Test]
        public void Controller_ReachesVrm10SpringBoneOnlyThroughVrm10InstanceRuntime_WithoutReplaceableFields()
        {
            Assert.That(ControllerType, Is.Not.Null, "Assembly-CSharp に AvatarGravityController が無い");

            var found = new List<string>();
            var fields = ControllerType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var f in fields)
            {
                if (f.Name == "springBoneRuntimeProvider" || f.Name == "modelLevelSink"
                    || Mentions(f.FieldType, typeof(IVrm10SpringBoneRuntime))
                    || Mentions(f.FieldType, typeof(BlittableModelLevel)))
                {
                    found.Add(f.Name + " : " + f.FieldType);
                }
            }

            Assert.That(fields.Length, Is.GreaterThan(0), "AvatarGravityController のフィールドを読めていない");
            Assert.That(found, Is.Empty,
                "VRM 1.0 の揺れものへの経路を Vrm10Instance.Runtime.SpringBone 以外へ差し替えられるフィールドがある: " + string.Join(" / ", found));
        }
    }
}
