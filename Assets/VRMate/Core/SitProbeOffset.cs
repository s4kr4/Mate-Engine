namespace VRMate
{
    /// <summary>The probe offset of upstream's window sitting, read and written through this.</summary>
    public interface ISitProbeTarget
    {
        float ProbeOffset { get; set; }
    }

    /// <summary>
    /// Sets the probe offset to 0 while the avatar sits and to upstream's value otherwise. Upstream's
    /// value is the one the target held when it was received (told apart by reference, not Equals); it
    /// is written back unchanged, bit for bit, when sitting ends, when another target or null arrives,
    /// and on Release. A released target is never written again until it is received anew, which reads
    /// its value again. Nobody else is expected to write the offset meanwhile.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class SitProbeOffset
    {
        private ISitProbeTarget _target;
        private float _original;

        /// <summary>target may be null: the previous target gets its value back and nothing is written.</summary>
        public void Apply(ISitProbeTarget target, bool sitting)
        {
            if (!ReferenceEquals(target, _target))
            {
                Release();
                if (ReferenceEquals(target, null))
                {
                    return;
                }

                // Read before the first write: on a call that already sits, this is still upstream's value.
                _original = target.ProbeOffset;
                _target = target;
            }

            if (ReferenceEquals(_target, null))
            {
                return;
            }

            _target.ProbeOffset = sitting ? 0f : _original;
        }

        /// <summary>Writes upstream's value back to the current target and lets it go. Does nothing without one.</summary>
        public void Release()
        {
            var target = _target;
            if (ReferenceEquals(target, null))
            {
                return;
            }

            // Let go first: a throwing setter must not leave a target that later calls keep writing to.
            _target = null;
            target.ProbeOffset = _original;
        }
    }
}
