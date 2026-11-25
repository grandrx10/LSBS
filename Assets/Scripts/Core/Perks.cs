using UnityEngine;

namespace Gunbound
{
    /// <summary>Perks. Saved by number, so append new ones at the end. None = empty perk slot.</summary>
    public enum PerkId : byte { None, Lifesteal, Bloodlust, Nimble, Sharpshooter, Swiftloader, Hyper, Copy, Collector, Rejuvenation }

    public class PerkInfo
    {
        public PerkId id;
        public string displayName;
        public string description;
        public Color color;
        /// <summary>False: only one copy allowed in a loadout (extra copies would do nothing).</summary>
        public bool stackable = true;
    }

    /// <summary>
    /// Every perk, its numbers, and the stacking rules. A loadout has up to <see cref="Slots"/> perks and the same
    /// perk may be picked more than once — each copy adds its effect again (see the helpers below).
    /// </summary>
    public static class Perks
    {
        public const int Slots = 3;

        // ---- tuning (per copy of the perk) ----
        public const float LifestealFraction = 0.30f;   // of damage dealt
        public const float LifestealDuration = 1f;      // healed over this long
        public const float BloodlustBonus = 0.50f;      // extra damage
        public const float BloodlustDuration = 10f;
        public const float NimbleMaxBonus = 0.50f;      // speed / cap multiplier added at 0 health
        public const float SharpshooterBonus = 0.50f;   // bullet radius
        public const float SwiftloaderBonus = 0.50f;    // reload speed
        public const float HyperBonus = 0.50f;          // movement speed
        public const float HyperDuration = 10f;
        public const float CollectorHealth = 15f;       // max health (and healing) per kill
        public const float CollectorSpeed = 0.15f;      // movement speed per kill
        public const float CollectorMaxHealthBonus = 100f; // total extra max health is capped here
        public static float CollectorHealthBonus(int collected) => Mathf.Min(CollectorHealth * collected, CollectorMaxHealthBonus);
        public const float RejuvenationFraction = 0.40f; // of max health, per kill
        public const float RejuvenationDuration = 2f;    // healed over this long
        /// <summary>All speed perks together can at most triple your speed and speed cap.</summary>
        public const float MaxSpeedMultiplier = 3f;

        public static readonly PerkInfo[] All =
        {
            new() { id = PerkId.Lifesteal, displayName = "Lifesteal", color = new Color(0.95f, 0.25f, 0.35f),
                description = "30% of the damage you deal is restored to you as health." },
            new() { id = PerkId.Bloodlust, displayName = "Bloodlust", color = new Color(0.85f, 0.1f, 0.1f),
                description = "Upon killing an enemy, you deal 50% more damage for the next 10 seconds." },
            new() { id = PerkId.Nimble, displayName = "Nimble", color = new Color(0.3f, 0.9f, 0.85f),
                description = "Gain speed as you lose health." },
            new() { id = PerkId.Sharpshooter, displayName = "Sharpshooter", color = new Color(1f, 0.8f, 0.25f),
                description = "Your bullets are 50% bigger." },
            new() { id = PerkId.Swiftloader, displayName = "Swiftloader", color = new Color(0.45f, 0.65f, 1f),
                description = "You reload 50% faster." },
            new() { id = PerkId.Hyper, displayName = "Hyper", color = new Color(0.75f, 0.45f, 1f),
                description = "Upon killing an enemy, you move 50% faster for 10 seconds." },
            new() { id = PerkId.Copy, displayName = "Copy", color = new Color(0.55f, 0.95f, 0.4f), stackable = false,
                description = "Upon killing an enemy, take their gun." },
            new() { id = PerkId.Collector, displayName = "Collector", color = new Color(1f, 0.55f, 0.75f),
                description = "Upon killing an enemy, gain 15 max health and 15% speed." },
            new() { id = PerkId.Rejuvenation, displayName = "Rejuvenation", color = new Color(0.4f, 1f, 0.55f),
                description = "Upon killing an enemy, restore 40% of your max health over 2 seconds." },
        };

        public static bool IsValid(int id) => id > 0 && id <= All.Length;
        public static PerkInfo Get(PerkId id) => IsValid((int)id) ? All[(int)id - 1] : null;

        // ---- packed form (one byte per slot) used on the network ----

        public static int Pack(int[] perks)
        {
            int p = 0;
            for (int i = 0; i < Slots; i++)
            {
                int v = perks != null && i < perks.Length && IsValid(perks[i]) ? perks[i] : 0;
                p |= v << (i * 8);
            }
            return p;
        }

        /// <summary>How many copies of a perk a packed set holds.</summary>
        public static int Count(int packed, PerkId id)
        {
            if (id == PerkId.None) return 0;
            int n = 0;
            for (int i = 0; i < Slots; i++) if (((packed >> (i * 8)) & 0xFF) == (int)id) n++;
            return n;
        }

        // ---- effects ----

        public static float LifestealOf(int stacks) => LifestealFraction * stacks;
        public static float RejuvenationOf(int stacks) => RejuvenationFraction * stacks;
        public static float BloodlustMultiplier(int stacks) => 1f + BloodlustBonus * stacks;
        /// <summary>Nimble: linear with missing health — 0 at full health, +0.5 per copy at 0.</summary>
        public static float NimbleBonus(int stacks, float health01) => NimbleMaxBonus * stacks * Mathf.Clamp01(1f - health01);
        public static float HyperSpeedBonus(int stacks) => HyperBonus * stacks;
        public static float BulletRadiusMultiplier(int stacks) => 1f + SharpshooterBonus * stacks;
        /// <summary>Reload time multiplier: 50% faster = 1.5x speed per copy.</summary>
        public static float ReloadTimeMultiplier(int stacks) => 1f / (1f + SwiftloaderBonus * stacks);
    }
}
