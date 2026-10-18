namespace VRMate
{
    /// <summary>
    /// How an expression holds back the mouth (VRM 1.0 overrideMouth). Blend multiplies the mouth by
    /// (1 - the expression's weight); Block shuts it whenever the expression's weight is above 0.
    /// </summary>
    public enum MouthOverride
    {
        None = 0,
        Blend = 1,
        Block = 2,
    }
}
