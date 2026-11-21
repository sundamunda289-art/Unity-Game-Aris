using System;
using Aaris.MapReview;

internal static class CombatSelectionPolicyTests
{
    private struct Rival
    {
        internal float DistanceSquared;
        internal bool HasSight;
        internal int Id;
    }

    private static int CountAttackers(Rival[] rivals)
    {
        int count = 0;
        for (int i = 0; i < rivals.Length; i++)
        {
            Rival contender = rivals[i];
            int outrank = 0;
            for (int j = 0; j < rivals.Length; j++)
            {
                Rival other = rivals[j];
                if (i == j || !other.HasSight ||
                    other.DistanceSquared > AarisCombatSelectionPolicy.PlayerAwarenessDistanceSquared)
                    continue;
                if (AarisCombatSelectionPolicy.HigherPriority(
                    other.DistanceSquared, other.Id,
                    contender.DistanceSquared, contender.Id))
                    outrank++;
            }
            if (AarisCombatSelectionPolicy.CanAttackPlayer(
                contender.HasSight, contender.DistanceSquared, outrank))
                count++;
        }
        return count;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Main()
    {
        Check(AarisCombatSelectionPolicy.MaximumPlayerAttackers == 2,
            "exact player attacker cap");
        Check(CountAttackers(new Rival[0]) == 0, "no attackers");
        Check(CountAttackers(new[] {
            new Rival { Id = 4, HasSight = true, DistanceSquared = 50f }
        }) == 1, "one visible player target");
        Check(CountAttackers(new[] {
            new Rival { Id = 0, HasSight = false, DistanceSquared = 2f },
            new Rival { Id = 1, HasSight = true, DistanceSquared = 4f },
            new Rival { Id = 2, HasSight = true, DistanceSquared = 9f },
            new Rival { Id = 3, HasSight = true, DistanceSquared = 16f },
        }) == 2, "occluded nearest must not consume engage slots");
        Check(CountAttackers(new[] {
            new Rival { Id = 9, HasSight = true, DistanceSquared = 12f },
            new Rival { Id = 1, HasSight = true, DistanceSquared = 12f },
            new Rival { Id = 3, HasSight = true, DistanceSquared = 12f },
        }) == 2, "deterministic exact distance ties");
        Check(CountAttackers(new[] {
            new Rival { Id = 1, HasSight = true, DistanceSquared = 1800f },
            new Rival { Id = 2, HasSight = true, DistanceSquared = 1764f },
            new Rival { Id = 3, HasSight = false, DistanceSquared = 1740f },
        }) == 1, "42 metre cutoff and blocked rival");

        var random = new Random(84117);
        var group = new Rival[100];
        for (int trial = 0; trial < 1000; trial++)
        {
            int visibleEligible = 0;
            for (int i = 0; i < group.Length; i++)
            {
                group[i].Id = i;
                group[i].HasSight = random.Next(4) != 0;
                float metres = random.Next(0, 80);
                group[i].DistanceSquared = (trial % 3 == 0) ?
                    metres * metres : (float)(random.NextDouble() * 4000d);
                if (group[i].HasSight &&
                    group[i].DistanceSquared <= AarisCombatSelectionPolicy.PlayerAwarenessDistanceSquared)
                    visibleEligible++;
            }
            int attackers = CountAttackers(group);
            Check(attackers <= 2, "too many player attackers");
            Check(attackers == Math.Min(2, visibleEligible),
                "visible rivals were unnecessarily blocked");
        }
        Console.WriteLine("AI_ATTACK_CAP_CSHARP_PASS: 1000 scenarios, 100 rivals each");
        return 0;
    }
}
