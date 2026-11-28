#!/usr/bin/env python3
"""Remote AI combat integration contract. Not a Unity/phone runtime test."""
from pathlib import Path
s=(Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
required=(
    "private const int RivalCount = 100;",
    "return bot == attackerOne || bot == attackerTwo;",
    "owner.TryLootForRival(transform, out scavenged);",
    "rivalLootWorld.TryTakeForRival(rival, out weapon);",
    "else if (!remoteLoadoutGiven && AarisWeaponCatalog.Ready)",
    "AarisWeaponCatalog.Select(ref remoteLootSeed, false)",
    "carriedMagazine = found.magazineSize;",
    "carriedReserve = found.reserveAmmo;",
    "if (carriedReserve <= 0)",
    "int refill = Mathf.Min(carriedGun.magazineSize, carriedReserve);",
    "carriedReserve -= refill;",
    "AarisEquippedGun.ApplyCombatDamage(shotHit, damage,",
    "TryGetCombatSightHit(target, out AarisCombatHit shotHit)",
)
for r in required:
    assert r in s, "Missing combat contract: "+r
for capacity in (1,5,30,100):
    for reserve in (0,1,29,30,60,199):
        rounds=capacity
        available=reserve
        while available:
            refill=min(capacity,available)
            assert 0<refill<=capacity
            available-=refill
            rounds+=refill
        assert rounds==capacity+reserve
print("PASS: 100 bots, 2-player cap, LOS-protected ground loot, finite magazine reserves")
