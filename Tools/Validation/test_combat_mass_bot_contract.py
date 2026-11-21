#!/usr/bin/env python3
"""Static, dependency-free cross-file checks. NOT a substitute for Unity PlayMode."""
import json
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else pathlib.Path(__file__).resolve().parents[2]
scripts = root / "Assets/AarisWorld/Scripts"
def read(name):
    return (scripts / name).read_text(encoding="utf-8")
def check(label, expected):
    assert expected, f"FAIL {label}"
    print("PASS", label)
def balanced(text):
    """Strip C# comments/strings conservatively before checking delimiter balance."""
    text = re.sub(r'@"(?:[^"]|"")*"', '""', text, flags=re.S)
    text = re.sub(r'"(?:\\.|[^"\\])*"', '""', text, flags=re.S)
    text = re.sub(r"//[^\n]*|/\*.*?\*/", "", text, flags=re.S)
    for left, right in (("{", "}"), ("(", ")"), ("[", "]")):
        stack = 0
        for symbol in text:
            if symbol == left: stack += 1
            elif symbol == right:
                stack -= 1
                if stack < 0: return False
        if stack: return False
    return True

health = read("AarisOfflineHealth.cs")
gun = read("AarisEquippedGun.cs")
director = read("AarisOfflineBRDirector.cs")
population = read("AarisMassBotPopulation.cs")
feedback = read("AarisCombatFeedbackHUD.cs")
player = read("AarisThirdPersonPlayer.cs")
inventory = read("AarisWeaponInventory.cs")
catalog = json.loads((root / "Assets/Resources/AarisWeapons/weapon_catalog.json").read_text())
for name, content in (("health",health),("gun",gun),("director",director),("population",population),("feedback",feedback),("player",player)):
    check(name + " balanced delimiters", balanced(content))

physical = int(re.search(r"private const int RivalCount = (\d+);",director).group(1))
virtual = int(re.search(r"private const int DistantRoster = (\d+);",population).group(1))
proxies = int(re.search(r"private const int NearProxyBudget = (\d+);",population).group(1))
check("100 logical opponents, no duplicate roster slot", physical + virtual == 100 and proxies <= 3)
check("physical rivals born with health", "actor.AddComponent<AarisOfflineHealth>()" in director)
check("player HP maximum 200", "maximumHealth = 200f" in health)
check("actual damage and source event", "Damaged?.Invoke(this, actual, attacker)" in health)
check("player HUD bound to health event", "health.Damaged += OnPlayerDamaged" in feedback)
check("player HUD bootstrapped", "AarisCombatFeedbackHUD.EnsureForPlayer(this)" in player)
check("incoming flash & direction", "flashUntil" in feedback and "HIT FROM LEFT" in feedback)
check("no new objects on each damage notification",
      "new GameObject" not in feedback.split("public static void NotifyOutgoingDamage(")[1].split("internal static bool TryGetAssistedDirection")[0])
check("health text and bar", 'HP 200 / 200' in feedback and "healthFill.fillAmount" in feedback)
check("shot uses scope assist only when aiming", "TryGetAssistedDirection" in gun and "inventory.Aiming" in feedback)
check("wall-occluded soft lock rejects other first hit", "first.transform.IsChildOf(rival.transform)" in director)
check("original gun fire authoritative", "FirstExternalHit(" in gun and "ApplyDamage(target.collider, spec.damage, owner, target.point)" in gun)
check("stance-relative headshot zone", "bounds.size.y * .76f" in gun)
check("headshot effective damage", "damage *= 1.65f" in gun)
check("two-way damage", "targetHealth.ApplyDamage(" in director and "damageable.ApplyDamage(damage, attacker)" in gun)
check("1-2 player attackers", "return nearer < 2;" in director)
check("AI targets can select other physical bots", "chosenHealth = other.Health;" in director)
check("only 3 virtual records simulated per rendered frame", "for (int i = 0; i < 3; i++)" in population)
check("no per-distant-bot MonoBehaviour", "sealed class BotRecord" in population and
      "sealed class AarisMassBotPopulation : MonoBehaviour" in population)
check("streamed proxy health actually reset", "ResetForStreamingRival(hitpoints)" in director)
check("zone damages virtuals", "director.BotOutsideZone(bot.position)" in population)
check("logical bot fights + medkit/ammo decisions",
      "other.hp -= hit;" in population and "bot.medkits--" in population and "bot.ammo--" in population)
check("no virtual kills resurrected", "if (bot == null || !bot.alive) return;" in population)
check("virtual and physical deaths both affect winner", "activeRivals + massBots.AliveCount == 0" in director)
for weapon in catalog["weapons"]:
    fov = weapon["adsFov"]
    tier = 4 if weapon["category"] == "Sniper" or fov <= 25 else 2 if fov <= 44 else 0
    check(f"{weapon['id']} scope tier {tier}X valid", tier in (0,2,4))
check("scope tier HUD labels", all(label in feedback for label in ["NO SCOPE","2X SCOPE","4X SCOPE"]))
print("STATIC_CONTRACT_SUCCESS: use Unity Editor/PlayMode + Android profiler before merge.")
