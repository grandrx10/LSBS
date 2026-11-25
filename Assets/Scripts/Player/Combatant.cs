using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Anything with the player model that can be shot: health, hitboxes, death, ragdoll, respawn.
    /// Players (PlayerController) and test dummies (TestDummy) derive from this.
    /// </summary>
    /// <summary>What killed someone (picks the kill feed wording).</summary>
    public enum DeathCause : byte { Weapon = 0, Lava = 1, Crushed = 2 }

    public abstract class Combatant : NetworkBehaviour
    {
        [Header("References")]
        public Transform model;

        [Header("Health")]
        public float maxHealth = 100f;
        public float respawnDelay = 3f;

        public readonly NetworkVariable<float> Health = new(100f);
        public readonly NetworkVariable<bool> Dead = new(false);
        /// <summary>Current max health: maxHealth plus any bonus (Collector perk). Use this, not maxHealth.</summary>
        public virtual float MaxHealth => maxHealth;

        public ProceduralAnimator Animator { get; private set; }
        public HitboxSet Hitboxes { get; private set; }
        public Dictionary<string, Transform> Bones { get; private set; }
        public GameObject LastRagdoll { get; protected set; }
        public bool Visible { get; private set; } = true;
        public bool IsAlive => IsSpawned && !Dead.Value;

        // view state used by the procedural animator
        public abstract Vector3 ViewVelocity { get; }
        public abstract float ViewYaw { get; }
        public abstract float ViewPitch { get; }
        public abstract byte ViewFlags { get; }
        /// <summary>True when this body is seen from inside (local player's first person).</summary>
        public virtual bool IsFirstPersonView => false;
        /// <summary>Fallback for first person when the arms-only body can't be built: shrink the head away.</summary>
        public virtual bool HideHead => false;
        public virtual WeaponController Weapon => null;
        /// <summary>Exact camera rotation (incl. roll) when available.</summary>
        public virtual bool TryGetViewRotation(out Quaternion rotation) { rotation = default; return false; }

        Renderer[] _renderers;

        protected virtual void Awake()
        {
            Animator = GetComponent<ProceduralAnimator>();
            if (model == null) model = transform.Find("PlayerModel");
            Bones = Rig.MapBones(model);
            _renderers = model.GetComponentsInChildren<Renderer>(true);
            foreach (var r in _renderers)
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
        }

        public override void OnNetworkSpawn()
        {
            Hitboxes ??= new HitboxSet(this, transform, Bones);
            Registry.Add(this);
            Dead.OnValueChanged += OnDeadChanged;
            if (IsServer) Health.Value = MaxHealth;
        }

        public override void OnNetworkDespawn()
        {
            Dead.OnValueChanged -= OnDeadChanged;
            Registry.Remove(this);
            if (LastRagdoll) Destroy(LastRagdoll);
        }

        // ---------------- server ----------------

        public void ServerApplyDamage(float dmg, ulong attacker, Vector3 point, Vector3 dir, int segment, float impulse) =>
            ServerApplyDamage(dmg, attacker, point, dir, segment, impulse, Vector3.zero);

        /// <param name="cutNormal">Non-zero: a lethal hit cuts the body in half along the plane through
        /// <paramref name="point"/> with this normal (sword kills) instead of ragdolling it.</param>
        public void ServerApplyDamage(float dmg, ulong attacker, Vector3 point, Vector3 dir, int segment, float impulse, Vector3 cutNormal)
        {
            if (!IsServer || Dead.Value) return;
            // perks of whoever dealt the damage (never from hurting yourself)
            var source = PlayerController.ServerFind(attacker);
            if (MatchManager.AreTeammates(source, this)) return; // no friendly fire
            // remember who hurt us last this life (environmental kills credit them)
            if (source != null && source != this) _lastAttacker = attacker;
            if (source == this || (source != null && !source.IsAlive)) source = null;
            if (source != null) dmg *= source.ServerDamageMultiplier;

            float before = Health.Value;
            Health.Value = Mathf.Max(0f, before - dmg);
            if (source != null) source.ServerOnDealtDamage(before - Health.Value);
            if (Health.Value <= 0f)
                ServerDie(attacker, source, point, dir * impulse, segment, cutNormal, DeathCause.Weapon);
        }

        /// <summary>
        /// Server: instant death from the level (lava etc.). Whoever damaged this combatant last during this life
        /// gets the kill; if nobody did, it's a suicide.
        /// </summary>
        public void ServerKillByHazard(DeathCause cause, Vector3 point)
        {
            if (!IsServer || Dead.Value) return;
            ulong attacker = _lastAttacker;
            var source = attacker == NoAttacker ? null : PlayerController.ServerFind(attacker);
            if (source == null || source == this) attacker = NoAttacker;
            if (source == this || (source != null && !source.IsAlive)) source = null;
            Health.Value = 0f;
            ServerDie(attacker, source, point, Vector3.zero, -1, Vector3.zero, cause);
        }

        /// <summary>"Nobody" as an attacker: deaths with this attacker count as suicides.</summary>
        public const ulong NoAttacker = ulong.MaxValue;
        ulong _lastAttacker = NoAttacker;

        void ServerDie(ulong attacker, PlayerController source, Vector3 point, Vector3 impulse, int segment, Vector3 cutNormal, DeathCause cause)
        {
            Dead.Value = true;
            OnServerDied();
            if (source != null) source.ServerOnKill(this);
            OnServerKilled(attacker);
            ServerBroadcastKill(attacker, segment, cause);
            DieRpc(point, impulse, (byte)Mathf.Clamp(segment, 0, 255), cutNormal);
            _serverDeathTime = Time.time;
            _lastAttacker = NoAttacker;
            if (AutoRespawn) StartCoroutine(RespawnRoutine());
        }

        float _serverDeathTime;

        /// <summary>True: respawn automatically after respawnDelay. False: wait for ServerTryRespawn (players press a key).</summary>
        protected virtual bool AutoRespawn => true;

        IEnumerator RespawnRoutine()
        {
            yield return new WaitForSeconds(respawnDelay);
            if (!IsSpawned) yield break;
            ServerDoRespawn();
        }

        /// <summary>Server: respawn now if dead and the respawn delay has passed.</summary>
        protected void ServerTryRespawn()
        {
            if (!IsServer || !Dead.Value) return;
            if (Time.time - _serverDeathTime < respawnDelay - 0.1f) return;
            ServerDoRespawn();
        }

        /// <summary>Server: respawn now if dead, ignoring the respawn delay (a new round started).</summary>
        public void ServerForceRespawn()
        {
            if (!IsServer || !IsSpawned || !Dead.Value) return;
            ServerDoRespawn();
        }

        void ServerDoRespawn()
        {
            Health.Value = MaxHealth;
            Dead.Value = false;
            ServerRespawn();
        }

        // ---------------- kill feed ----------------

        /// <summary>(killer name, killer colour, victim name, victim colour, headshot, self-kill, cause). Fired on every client.</summary>
        public static event System.Action<string, int, string, int, bool, bool, DeathCause> KillFeed;

        /// <summary>Name shown in the kill feed.</summary>
        public virtual string FeedName => name;
        /// <summary>Palette index for the kill feed (-1 = neutral grey).</summary>
        public virtual int FeedColor => -1;

        void ServerBroadcastKill(ulong attacker, int segment, DeathCause cause)
        {
            PlayerController killer = null;
            if (attacker != NoAttacker && NetworkManager.ConnectedClients.TryGetValue(attacker, out var client) && client.PlayerObject != null)
                killer = client.PlayerObject.GetComponent<PlayerController>();
            bool self = killer != null && killer == this;
            bool head = segment >= 0 && segment < Rig.Segments.Length && Rig.Segments[segment].region == HitRegion.Head;
            var kName = new FixedString32Bytes();
            kName.CopyFromTruncated(killer != null ? killer.FeedName : "");
            var vName = new FixedString32Bytes();
            vName.CopyFromTruncated(FeedName);
            KillFeedRpc(kName, (sbyte)(killer != null ? killer.FeedColor : -1), vName, (sbyte)FeedColor, head, self, (byte)cause);
        }

        [Rpc(SendTo.Everyone)]
        void KillFeedRpc(FixedString32Bytes killer, sbyte killerColor, FixedString32Bytes victim, sbyte victimColor, bool headshot, bool self, byte cause)
        {
            KillFeed?.Invoke(killer.ToString(), killerColor, victim.ToString(), victimColor, headshot, self, (DeathCause)cause);
        }

        /// <summary>Server-side: this combatant just died (before kill credit is handed out).</summary>
        protected virtual void OnServerDied() { }

        /// <summary>Server-side: this combatant was just killed by the given client.</summary>
        protected virtual void OnServerKilled(ulong attackerClientId) { }

        /// <summary>Called on the server after the respawn delay (health already restored).</summary>
        protected abstract void ServerRespawn();

        // ---------------- everyone ----------------

        [Rpc(SendTo.Everyone)]
        void DieRpc(Vector3 point, Vector3 impulse, byte segment, Vector3 cutNormal)
        {
            if (LastRagdoll) Destroy(LastRagdoll);
            SetOutline(null); // the ragdoll copies the body's materials
            GameObject body = null;
            if (cutNormal.sqrMagnitude > 0.01f)
            {
                try { body = BodySlicer.Slice(this, point, cutNormal, impulse, ViewVelocity); }
                catch (System.Exception e) { Debug.LogWarning("[Gunbound] Slice failed, using ragdoll: " + e.Message); }
            }
            LastRagdoll = body != null ? body : RagdollFactory.Spawn(this, ViewVelocity, segment, point, impulse);
            SetVisible(false);
            OnDiedLocal();
        }

        /// <summary>Runs on every client right after the ragdoll spawns.</summary>
        protected virtual void OnDiedLocal() { }

        protected virtual void OnDeadChanged(bool prev, bool dead)
        {
            SetVisible(!dead);
        }

        public virtual void SetVisible(bool on)
        {
            Visible = on;
            foreach (var r in _renderers) if (r) r.enabled = on;
            Hitboxes?.SetEnabled(on);
        }

        // ---------------- outline (enemy red / teammate blue) ----------------

        /// <summary>Every spawned combatant (players and dummies).</summary>
        public static readonly List<Combatant> Registry = new();
        Material[][] _baseMaterials;
        Material _outline;

        /// <summary>Adds (or with null removes) an outline material on the body's renderers.</summary>
        public void SetOutline(Material m)
        {
            if (m == _outline || _renderers == null) return;
            if (_baseMaterials == null)
            {
                _baseMaterials = new Material[_renderers.Length][];
                for (int i = 0; i < _renderers.Length; i++) _baseMaterials[i] = _renderers[i] ? _renderers[i].sharedMaterials : null;
            }
            _outline = m;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                var baseMats = _baseMaterials[i];
                if (!r || baseMats == null) continue;
                if (m == null) { r.sharedMaterials = baseMats; continue; }
                var mats = new Material[baseMats.Length + 1];
                baseMats.CopyTo(mats, 0);
                mats[^1] = m;
                r.sharedMaterials = mats;
            }
        }
    }
}
