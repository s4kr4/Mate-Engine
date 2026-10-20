using UnityEngine;

namespace VRMate
{
    /// <summary>
    /// Keeps a VRM 1.0 avatar seated on a window while it is dragged. Upstream's AvatarWindowHandler
    /// unsnaps once its probe point (the hips moved by probeZoneYOffsetLocal) leaves a band around the
    /// window's top edge; on a seated VRM 1.0 body the scene's offset puts that point below the band, so
    /// the avatar always fell off shortly after being grabbed. While the avatar sits the offset is 0 (the
    /// hips, which rest on the edge); otherwise it is upstream's value, so the check for whether the
    /// avatar may sit down is unchanged. SitProbeOffset decides the value; this component finds the
    /// handler and reads whether the avatar sits.
    /// </summary>
    // Before AvatarWindowHandler (order 0): its Update reads the offset written here this frame.
    [DefaultExecutionOrder(-10)]
    [DisallowMultipleComponent]
    public class WindowSitProbe : MonoBehaviour
    {
        // Upstream sets it when the avatar snaps onto a window and clears it when it unsnaps.
        private static readonly int WindowSitParam = Animator.StringToHash("isWindowSit");

        private static WindowSitProbe instance;

        private readonly SitProbeOffset offset = new SitProbeOffset();
        private readonly AvatarWatch avatar = new AvatarWatch();
        private Animator avatarAnimator;
        private HandlerTarget target;

        // The controller whose parameters were last looked at: reading animator.parameters allocates,
        // so it is read again only when the controller changes (upstream swaps it, e.g. for dances).
        private RuntimeAnimatorController checkedController;
        private bool hasSitParam;

        private void Awake()
        {
            // A second copy would take the 0 written by the first as upstream's value and keep it.
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }

            instance = this;
        }

        private void Update()
        {
            if (avatar.Refresh())
            {
                SetAvatar(avatar.Animator);
            }

            var current = target != null && target.Handler != null ? target : null;
            offset.Apply(current, current != null && IsSitting());
        }

        private void OnDisable()
        {
            offset.Release();
        }

        private void OnDestroy()
        {
            offset.Release();
            if (instance == this)
            {
                instance = null;
            }
        }

        private void SetAvatar(Animator found)
        {
            avatarAnimator = null;
            checkedController = null;
            hasSitParam = false;

            // VRM 0.x and upstream's default avatars are left alone: their bodies may already sit within
            // the band with upstream's offset, and 0 could move the probe out of it instead.
            var handler = AvatarState.IsVrm10Humanoid(found) ? found.GetComponent<AvatarWindowHandler>() : null;
            if (handler == null)
            {
                target = null;
                return;
            }

            avatarAnimator = found;

            // SitProbeOffset tells targets apart by reference: the same handler keeps the same wrapper, so
            // its first-seen value is not read again (it may be 0 while the avatar sits).
            if (target == null || !ReferenceEquals(target.Handler, handler))
            {
                target = new HandlerTarget(handler);
            }
        }

        private bool IsSitting()
        {
            var animator = avatarAnimator;
            if (animator == null || !animator.isActiveAndEnabled)
            {
                return false;
            }

            var controller = animator.runtimeAnimatorController;
            if (controller == null)
            {
                return false;
            }

            if (!ReferenceEquals(controller, checkedController))
            {
                checkedController = controller;
                hasSitParam = HasBool(animator, WindowSitParam);
            }

            // GetBool on a missing parameter logs a warning every frame.
            return hasSitParam && animator.GetBool(WindowSitParam);
        }

        private static bool HasBool(Animator animator, int hash)
        {
            foreach (var parameter in animator.parameters)
            {
                if (parameter.nameHash == hash && parameter.type == AnimatorControllerParameterType.Bool)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class HandlerTarget : ISitProbeTarget
        {
            public HandlerTarget(AvatarWindowHandler handler)
            {
                Handler = handler;
            }

            public AvatarWindowHandler Handler { get; }

            public float ProbeOffset
            {
                get { return Handler.probeZoneYOffsetLocal; }

                set
                {
                    // A destroyed handler is no longer read by anyone; nothing to restore there.
                    if (Handler != null)
                    {
                        Handler.probeZoneYOffsetLocal = value;
                    }
                }
            }
        }
    }
}
