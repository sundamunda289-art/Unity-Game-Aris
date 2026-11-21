namespace Aaris.MapReview
{
    /// <summary>
    /// Deterministic, allocation-free player-attack fairness policy.
    /// No Unity object references: the exact production logic can run under
    /// a plain C# unit runner as well as inside the Android PlayerLoop.
    /// </summary>
    internal static class AarisCombatSelectionPolicy
    {
        internal const int MaximumPlayerAttackers = 2;
        internal const float PlayerAwarenessDistanceSquared = 42f * 42f;

        internal static bool HigherPriority(float rivalDistanceSquared, int rivalId,
            float candidateDistanceSquared, int candidateId)
        {
            float delta = rivalDistanceSquared - candidateDistanceSquared;
            return delta < -.01f || (delta >= -.01f && delta <= .01f &&
                rivalId < candidateId);
        }

        internal static bool CanAttackPlayer(bool hasSight,
            float distanceSquared, int higherPriorityVisibleRivals)
        {
            return hasSight && distanceSquared <= PlayerAwarenessDistanceSquared &&
                higherPriorityVisibleRivals < MaximumPlayerAttackers;
        }
    }
}
