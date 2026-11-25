using UnityEngine;

namespace Gunbound
{
    [CreateAssetMenu(menuName = "Gunbound/Gun Definition", fileName = "NewGun")]
    public class GunDefinition : ScriptableObject
    {
        [Header("Ammo")]
        public int magazineSize = 8;
        [Tooltip("Whole reload time — or, with reloadPerRound, the time to load ONE round.")]
        public float reloadTime = 1.5f;
        [Tooltip("Load rounds one at a time (firing interrupts the reload if a round is loaded).")]
        public bool reloadPerRound = false;

        [Header("Action")]
        [Tooltip("Seconds spent working the action (e.g. lever) after each shot. 0 = none. The gun can't fire until it's done.")]
        public float cockTime = 0f;
        [Tooltip("Wait after the shot before the lever animation (cockTime) starts. Total per shot = cockDelay + cockTime.")]
        public float cockDelay = 0f;
        [Tooltip("The action is a pump: the model's 'Handle' slides back and forward over cockTime (instead of a LeverAction).")]
        public bool pumpAction = false;

        /// <summary>Full time from the shot until the action is ready again.</summary>
        public float CockTotal => cockTime > 0f ? cockDelay + cockTime : 0f;

        [Header("Firing")]
        [Tooltip("Rounds per minute (semi-auto cap).")]
        public float fireRate = 300f;
        [Tooltip("Hold the trigger to keep firing.")]
        public bool fullAuto = false;
        public float maxRange = 1500f;
        [Tooltip("Bullets are hitscan cylinders of this radius (metres). Every body part inside it counts; the most damaging one is used.")]
        public float bulletRadius = 0.1f;
        [Tooltip("Bullets per shot (shotguns). Each one does the full damage and has its own ricochets/penetrations.")]
        public int pellets = 1;
        [Tooltip("Max angle (degrees) a bullet can stray from the aim direction, spread evenly over the cone. 0 = perfectly accurate.")]
        public float spreadAngle = 0f;

        [Header("Melee (melee = true: left click swings, fireRate = swings per minute)")]
        public bool melee = false;
        [Tooltip("Radius of the hit sphere (metres).")]
        public float meleeRadius = 4f;
        [Tooltip("How far in front of the eyes the hit sphere's centre sits (metres).")]
        public float meleeForward = 2f;
        [Tooltip("Length of one swing (seconds). Anyone the hit sphere touches at any point during it is hit (once per swing). Also the lunge duration.")]
        public float swingTime = 0.32f;
        [Tooltip("Swinging lunges you toward the crosshair (up/down too) at this speed (m/s) for swingTime. 0 = no lunge.")]
        public float lungeSpeed = 0f;
        [Tooltip("Fraction of the base lunge speed you keep when it ends (you always keep your redirected momentum if that's more).")]
        public float lungeCarry = 0.4f;
        [Tooltip("Like a wall jump: your current speed is redirected into the lunge, plus this much (m/s). The lunge is never slower than lungeSpeed.")]
        public float lungeBoost = 2f;
        [Header("Equip")]
        [Tooltip("Seconds to draw the weapon when switching to it / spawning.")]
        public float drawTime = 0.25f;

        [Header("Damage")]
        public float damage = 35f;
        public float headMultiplier = 3f;
        public float limbMultiplier = 0.5f;
        public float bodyMultiplier = 1f;

        [Header("Bullet behaviour")]
        public int ricochets = 3;
        public int penetrations = 1;
        [Tooltip("Impulse applied to a ragdoll on a lethal hit.")]
        public float ragdollImpulse = 260f;

        [Header("Recoil (degrees)")]
        public float recoilPitch = 2.4f;
        public float recoilYawJitter = 0.6f;
        [Tooltip("How fast the view returns after recoil (per second).")]
        public float recoilRecovery = 9f;
        public float visualKickBack = 0.07f;
        public float visualKickPitch = 7f;

        public float SecondsBetweenShots => Mathf.Max(60f / Mathf.Max(1f, fireRate), CockTotal);

        public float Multiplier(HitRegion r) => r switch
        {
            HitRegion.Head => headMultiplier,
            HitRegion.Limb => limbMultiplier,
            _ => bodyMultiplier,
        };

        static GunDefinition _fallback;
        public static GunDefinition Fallback => _fallback ??= CreateInstance<GunDefinition>();
    }

    public enum HitRegion : byte { Body, Head, Limb }
}
