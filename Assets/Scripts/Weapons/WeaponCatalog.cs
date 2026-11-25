using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Network/save id of a weapon. Never renumber — append new weapons at the end.
    /// None (255) = empty loadout slot; it is never in the catalog, so WeaponCatalog.IsValid(None) is false.
    /// </summary>
    public enum WeaponId : byte { Wolfgang = 0, DualBeethovens = 1, Sebastion = 2, Chopin = 3, Rachmaninoff = 4, None = 255 }

    /// <summary>How the weapon is held and drawn (drives ProceduralAnimator).</summary>
    public enum HoldStyle : byte { OneHanded, Dual, Rifle, Sword }
    public enum DrawStyle : byte { CrossDrawLeftHip, SpinDrawBothHips, RaiseFromLow, OverTheBack }

    public class WeaponInfo
    {
        public WeaponId id;
        public string displayName;
        public string description;
        public HoldStyle hold;
        public DrawStyle draw;
        /// <summary>Gameplay stats. Wolfgang's come from the DefaultGun asset on the player prefab (see RegisterAsset).</summary>
        public GunDefinition stats;
        /// <summary>
        /// Model prefab for this weapon (barrel along +X, with TriggerPoint / BarrelPoint markers like Gun.prefab).
        /// Null = use the player's built-in "Gun" (the Wolfgang model). Gunbound > Install Weapon Models stores the
        /// references on the player prefab so builds get them too; in the editor they also load straight from this path.
        /// </summary>
        public string modelPath;
        public float modelScale = 0.4f;
        public Vector3 modelEuler = new(0, -90, 0);
        public bool Dual => hold == HoldStyle.Dual;
        /// <summary>Melee weapon: left click swings instead of firing (see GunDefinition melee fields).</summary>
        public bool Melee => hold == HoldStyle.Sword;
    }

    /// <summary>
    /// Every weapon in the game. To add one: add a WeaponId, then an entry in Build() — it shows up in the
    /// loadout menu automatically (see LoadoutSchema for which slots may use it).
    /// </summary>
    public static class WeaponCatalog
    {
        static List<WeaponInfo> _all;
        static readonly Dictionary<WeaponId, WeaponInfo> ById = new();

        public static IReadOnlyList<WeaponInfo> All { get { Ensure(); return _all; } }

        public static WeaponInfo Get(WeaponId id)
        {
            Ensure();
            return ById.TryGetValue(id, out var w) ? w : _all[0];
        }

        public static bool IsValid(int id) { Ensure(); return id >= 0 && id < 256 && ById.ContainsKey((WeaponId)id); }

        /// <summary>Use a designer-tuned asset for a weapon's stats (keeps the per-weapon fields set in code).</summary>
        public static void RegisterAsset(WeaponId id, GunDefinition asset)
        {
            if (asset == null) return;
            var w = Get(id);
            if (w.id != id || w.stats == asset) return;
            w.stats = asset;
        }

        static void Ensure()
        {
            if (_all != null) return;
            _all = Build();
            foreach (var w in _all) ById[w.id] = w;
        }

        static List<WeaponInfo> Build()
        {
            var list = new List<WeaponInfo>();

            // ---- Wolfgang: the original semi-auto pistol (stats from Assets/Gunbound/DefaultGun.asset when present) ----
            var wolfgang = ScriptableObject.CreateInstance<GunDefinition>();
            wolfgang.name = "Wolfgang";
            wolfgang.drawTime = 0.25f;
            wolfgang.damage = 45f; // only used if DefaultGun.asset is missing (the asset's values win)
            list.Add(new WeaponInfo
            {
                id = WeaponId.Wolfgang,
                displayName = "Wolfgang",
                description = "Semi-auto pistol. Hard hitting, ricochets and pierces.",
                hold = HoldStyle.OneHanded,
                draw = DrawStyle.CrossDrawLeftHip,
                stats = wolfgang,
            });

            // ---- Dual Beethovens: full-auto pair, alternating right/left ----
            var beet = ScriptableObject.CreateInstance<GunDefinition>();
            beet.name = "DualBeethovens";
            beet.magazineSize = 30;
            beet.reloadTime = 2f;
            beet.fireRate = 500f;
            beet.fullAuto = true;
            beet.damage = 30f;      // body; head/limb use the default multipliers
            beet.penetrations = 0;
            beet.ricochets = 1;
            beet.drawTime = 0.5f;
            beet.ragdollImpulse = 180f;
            beet.recoilPitch = 0.9f;
            beet.recoilYawJitter = 0.45f;
            beet.recoilRecovery = 11f;
            beet.visualKickBack = 0.05f;
            beet.visualKickPitch = 6f;
            list.Add(new WeaponInfo
            {
                id = WeaponId.DualBeethovens,
                displayName = "Dual Beethovens",
                description = "Full-auto pistol pair. 500 RPM, fires alternating hands.",
                hold = HoldStyle.Dual,
                draw = DrawStyle.SpinDrawBothHips,
                stats = beet,
                modelPath = "Assets/Models/Beethoven.prefab", // same layout as Gun.prefab; the left gun is a second copy
            });

            // ---- Sebastion: lever-action rifle, loaded one round at a time ----
            var seb = ScriptableObject.CreateInstance<GunDefinition>();
            seb.name = "Sebastion";
            seb.magazineSize = 4;
            seb.reloadPerRound = true;
            seb.reloadTime = 0.5f;     // per round
            seb.fireRate = 200f;       // semi auto; the lever is the real limit
            seb.cockDelay = 0.2f;      // after every shot: wait 0.2s...
            seb.cockTime = 0.4f;       // ...then work the lever for 0.4s (0.6s per shot in total)
            seb.damage = 85f;
            seb.penetrations = 2;
            seb.ricochets = 5;
            seb.drawTime = 0.4f;
            seb.ragdollImpulse = 420f;
            seb.recoilPitch = 4.5f;
            seb.recoilYawJitter = 0.8f;
            seb.recoilRecovery = 7f;
            seb.visualKickBack = 0.09f;
            seb.visualKickPitch = 9f;
            list.Add(new WeaponInfo
            {
                id = WeaponId.Sebastion,
                displayName = "Sebastion",
                description = "Lever-action rifle. Pierces 2 bodies, ricochets 5 times.",
                hold = HoldStyle.Rifle,
                draw = DrawStyle.RaiseFromLow,
                stats = seb,
                modelPath = "Assets/Models/Sebastion.prefab",
            });

            // ---- Chopin: two-handed sword. Swings alternate right→left / left→right; kills cut the body in half ----
            var chopin = ScriptableObject.CreateInstance<GunDefinition>();
            chopin.name = "Chopin";
            chopin.melee = true;
            chopin.damage = 70f;
            chopin.headMultiplier = chopin.limbMultiplier = chopin.bodyMultiplier = 1f; // flat 70 wherever it lands
            chopin.fireRate = 75f;       // one swing every 0.8s
            chopin.meleeRadius = 4f;
            chopin.meleeForward = 2f;
            chopin.swingTime = 0.5f;     // the swing (and its lunge) lasts 0.5s; anyone clipped during it is hit
            chopin.lungeSpeed = 22f;     // at least ~11m lunge toward the crosshair
            chopin.lungeBoost = 2f;      // moving faster than that? all your speed is redirected where you look, +2 m/s
            chopin.lungeCarry = 0.4f;            chopin.magazineSize = 1;     // no ammo; never reloads
            chopin.drawTime = 0.3f;
            chopin.ragdollImpulse = 300f;
            chopin.recoilPitch = 0f;
            chopin.recoilYawJitter = 0f;
            chopin.visualKickBack = 0f;
            chopin.visualKickPitch = 0f;
            list.Add(new WeaponInfo
            {
                id = WeaponId.Chopin,
                displayName = "Chopin",
                description = "Two-handed sword. Lunges where you look and cleaves everyone in reach; kills cut them in half.",
                hold = HoldStyle.Sword,
                draw = DrawStyle.OverTheBack,
                stats = chopin,
                modelPath = "Assets/Models/Sword.prefab",
                // the model's blade runs along +Y: point it along the holder's +Z (edges along the holder's ±Y)
                modelEuler = new Vector3(90, 0, 0),
                modelScale = 0.6f,
            });

            // ---- Rachmaninoff: pump shotgun, loaded one shell at a time, racked after every shot ----
            var rach = ScriptableObject.CreateInstance<GunDefinition>();
            rach.name = "Rachmaninoff";
            rach.magazineSize = 5;
            rach.reloadPerRound = true;
            rach.reloadTime = 0.5f;     // per shell
            rach.fireRate = 200f;       // semi auto; the pump is the real limit
            rach.pumpAction = true;
            rach.cockDelay = 0.1f;      // after every shot: wait 0.1s...
            rach.cockTime = 0.3f;       // ...then rack the Handle back and forward for 0.3s (0.4s per shot in total)
            rach.pellets = 10;
            rach.spreadAngle = 5f;
            rach.damage = 15f;          // per pellet
            rach.penetrations = 0;
            rach.ricochets = 1;
            rach.drawTime = 0.4f;
            rach.ragdollImpulse = 120f; // per pellet — a full point-blank blast stacks up
            rach.recoilPitch = 5.5f;
            rach.recoilYawJitter = 1f;
            rach.recoilRecovery = 7f;
            rach.visualKickBack = 0.1f;
            rach.visualKickPitch = 10f;
            list.Add(new WeaponInfo
            {
                id = WeaponId.Rachmaninoff,
                displayName = "Rachmaninoff",
                description = "Pump shotgun. 10 pellets of 15 damage, each ricochets once.",
                hold = HoldStyle.Rifle,
                draw = DrawStyle.RaiseFromLow,
                stats = rach,
                modelPath = "Assets/Models/Rachmaninoff.prefab",
            });

            return list;
        }
    }
}
