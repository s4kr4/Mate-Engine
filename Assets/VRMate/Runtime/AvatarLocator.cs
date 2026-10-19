using System;
using System.Linq;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRMate
{
    /// <summary>
    /// Finds the avatar's Animator the same way for every VRMate component, so that they always act
    /// on the same model.
    /// </summary>
    public static class AvatarLocator
    {
        /// <summary>Searching the scene every frame is expensive; the avatar only changes on a model switch.</summary>
        public const float SearchInterval = 0.5f;

        // Same search order as ChatBot.FindAvatarSmart.
        public static Animator FindAnimator()
        {
            Animator found = null;
            var loader = Object.FindFirstObjectByType<VRMLoader>();
            if (loader != null)
            {
                var current = loader.GetCurrentModel();
                if (current != null)
                {
                    found = current.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
                }
            }

            if (found == null)
            {
                var modelParent = GameObject.Find("Model");
                if (modelParent != null)
                {
                    found = modelParent.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
                }
            }

            if (found == null)
            {
                var all = Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                found = all.FirstOrDefault(a => a && a.isActiveAndEnabled);
            }

            return found;
        }
    }

    /// <summary>What the VRMate components that pose the body need to know about the avatar.</summary>
    public static class AvatarState
    {
        private const string IdleState = "Idle";

        /// <summary>A humanoid VRM 1.0 model; VRM 0.x is out of scope and keeps upstream's body.</summary>
        public static bool IsVrm10Humanoid(Animator found)
        {
            return found != null && found.avatar != null && found.avatar.isHuman && found.GetComponentInChildren<Vrm10Instance>() != null;
        }

        /// <summary>
        /// Base layer in Idle, and heading to Idle when in a transition. False with error set when the
        /// animator state cannot be read.
        /// </summary>
        public static bool IsIdle(Animator animator, out string error)
        {
            error = null;
            if (animator == null || !animator.isActiveAndEnabled)
            {
                return false;
            }

            try
            {
                if (animator.runtimeAnimatorController == null || animator.layerCount == 0)
                {
                    return false;
                }

                // Idle only: dragging, sitting, dancing and sleeping keep the body to upstream.
                if (!animator.GetCurrentAnimatorStateInfo(0).IsName(IdleState))
                {
                    return false;
                }

                return !animator.IsInTransition(0) || animator.GetNextAnimatorStateInfo(0).IsName(IdleState);
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Keeps track of the avatar's Animator for one component: searches again at most every
    /// SearchInterval while the current one is missing or inactive.
    /// </summary>
    public sealed class AvatarWatch
    {
        private float nextSearchTime;

        public Animator Animator { get; private set; }

        /// <summary>True when Animator changed since the last call (to another avatar or to none).</summary>
        public bool Refresh()
        {
            if (Animator != null && Animator.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (Time.unscaledTime < nextSearchTime)
            {
                // A destroyed avatar compares equal to null; report the loss once, without searching.
                if (!ReferenceEquals(Animator, null) && Animator == null)
                {
                    Animator = null;
                    return true;
                }

                return false;
            }

            nextSearchTime = Time.unscaledTime + AvatarLocator.SearchInterval;
            var found = AvatarLocator.FindAnimator();
            // Reference comparison: a destroyed avatar equals null under Unity's ==, but it is still a change.
            if (ReferenceEquals(found, Animator))
            {
                return false;
            }

            Animator = found;
            return true;
        }
    }
}
