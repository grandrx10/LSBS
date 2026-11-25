using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Networked player: replicates movement (owner-authoritative) and exposes a unified
    /// view state for animation. Health/death/ragdoll/respawn live in Combatant.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PlayerController : Combatant
    {
        public static PlayerController Local { get; private set; }
        public static readonly List<PlayerController> All = new();
        public static event System.Action<PlayerController> LocalSpawned, LocalDespawned;

        [Tooltip("Remote players are rendered this far in the past for smooth interpolation.")]
        public float interpolationDelay = 0.1f;

        public readonly NetworkVariable<FixedString32Bytes> PlayerName = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        /// <summary>Palette index assigned by the host (unique per lobby). 255 = not assigned yet.</summary>
        public readonly NetworkVariable<byte> ColorIndex = new(255);
        public override string FeedName => DisplayName;
        public override int FeedColor => ColorIndex.Value == 255 ? -1 : ColorIndex.Value;
        public Color PlayerColor => PlayerPalette.Get(ColorIndex.Value == 255 ? -1 : ColorIndex.Value);
        public string ColorHex => ColorIndex.Value == 255 ? "FFFFFF" : PlayerPalette.HexString(ColorIndex.Value);
        public readonly NetworkVariable<int> Score = new();
        public readonly NetworkVariable<int> Kills = new();
        /// <summary>Local player only: (victim name, points gained).</summary>
        public static event System.Action<string, int, int> LocalScored;
        public readonly NetworkVariable<int> Deaths = new();
        /// <summary>Team Deathmatch team (1 or 2); 0 in free-for-all modes. Set by the host.</summary>
        public readonly NetworkVariable<byte> Team = new(0);
        /// <summary>Equipped perks (Perks.Pack of the loadout), written by the owner on every spawn.</summary>
        /// <summary>The face drawn on this player's head (FaceArt encoding), written by the owner.</summary>
        public readonly NetworkVariable<FixedString4096Bytes> Face = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        FacePainter _facePainter;
        MeshRenderer _faceRenderer;
        bool _hasFace;
        public readonly NetworkVariable<int> PerkSet =new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public int PerkCount(PerkId id) => Perks.Count(PerkSet.Value, id);
        /// <summary>Collector: bonuses collected this life (one per kill per copy of the perk). Reset on death.</summary>
        public readonly NetworkVariable<int> Collected = new(0);
        public override float MaxHealth => maxHealth + Perks.CollectorHealthBonus(Collected.Value);

        /// <summary>The player owned by a client, or null (dummies / disconnected).</summary>
        public static PlayerController FindByClient(ulong clientId)
        {
            foreach (var p in All) if (p != null && p.OwnerClientId == clientId) return p;
            return null;
        }
        public static PlayerController ServerFind(ulong clientId) => FindByClient(clientId);

        public string DisplayName
        {
            get
            {
                string n = PlayerName.Value.ToString();
                return string.IsNullOrEmpty(n) ? "Player " + (OwnerClientId + 1) : n;
            }
        }

        readonly NetworkVariable<NetState> _state = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public PlayerMotor Motor { get; private set; }
        public CharacterController CC { get; private set; }
        WeaponController _weapon;
        GrappleHook _grapple;

        Vector3 _viewVelocity;
        float _viewYaw, _viewPitch;
        byte _viewFlags;
        public override Vector3 ViewVelocity => _viewVelocity;
        public override float ViewYaw => _viewYaw;
        public override float ViewPitch => _viewPitch;
        public override byte ViewFlags => _viewFlags;
        public override bool IsFirstPersonView => IsOwner;
        public override bool HideHead => IsOwner && (_fpBody == null || _fpBody.Arms == null);
        FirstPersonBody _fpBody;
        public override WeaponController Weapon => _weapon;

        public override bool TryGetViewRotation(out Quaternion rotation)
        {
            if (IsOwner && Motor != null) { rotation = Motor.ViewRotation; return true; }
            rotation = default;
            return false;
        }

        struct Snap { public float time; public NetState s; }
        readonly List<Snap> _snaps = new();
        byte _teleportCounter;
        byte _lastSeenTeleport;
        bool _hiddenUntilTeleport;

        protected override void Awake()
        {
            base.Awake();
            Motor = GetComponent<PlayerMotor>();
            _weapon = GetComponent<WeaponController>();
            CC = GetComponent<CharacterController>();
            _grapple = GetComponent<GrappleHook>();
            if (_grapple == null) _grapple = gameObject.AddComponent<GrappleHook>();
            if (GetComponent<SpeedTrail>() == null) gameObject.AddComponent<SpeedTrail>();            Motor.enabled = false;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            All.Add(this);
            gameObject.layer = Layers.Player;
            ColorIndex.OnValueChanged += OnColorChanged;
            ApplyColor();
            // drawn face. Your own stays hidden while you're alive (first person) but shows on your ragdoll.
            _facePainter = new FacePainter();
            _faceRenderer = FaceDecal.Attach(model, transform, _facePainter.Texture);
            Face.OnValueChanged += OnFaceChanged;
            ApplyFace();

            if (IsOwner)
            {
                Local = this;
                Motor.enabled = true;
                // first person: body only casts shadows (head included), camera sees just the arms
                _fpBody = gameObject.AddComponent<FirstPersonBody>();
                _fpBody.Build(model);
                ApplyColor();
                PushName();
                PushPerks();
                PushFace();
                GameSettings.NameChanged += PushName;
                FaceArt.Changed += PushFace;
                RequestColor();
                SpawnPoint.Pick(out var pos, out var yaw);
                Motor.Teleport(pos, yaw);
                WriteState();
                LocalSpawned?.Invoke(this);
            }
            else
            {
                Motor.enabled = false;
                _state.OnValueChanged += OnStateChanged;
                _lastSeenTeleport = _state.Value.Teleport;
                PushSnap(_state.Value);
                ApplySnapImmediate(_state.Value);
            }
            SetVisible(!Dead.Value);

            // the host decides where everyone spawns (safe spawn rules)
            if (IsServer)
            {
                MatchManager.ServerOnPlayerSpawned(this);
                SpawnPoint.PickFor(this, out var sp, out var sy);
                RespawnRpc(sp, sy);
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            All.Remove(this);
            _state.OnValueChanged -= OnStateChanged;
            GameSettings.NameChanged -= PushName;
            FaceArt.Changed -= PushFace;
            Face.OnValueChanged -= OnFaceChanged;
            ColorIndex.OnValueChanged -= OnColorChanged;
            if (Local == this)
            {
                Local = null;
                LocalDespawned?.Invoke(this);
            }
        }

        void Update()
        {
            if (!IsSpawned) return;
            if (IsServer) ServerTickPerks();
            if (IsOwner)
            {
                // Nimble: faster (and a higher speed cap) the lower your health. Hyper: faster after a kill.
                // Speed bonuses add together (Nimble at 0 HP + Hyper = +100%).
                float speedBonus = Perks.NimbleBonus(PerkCount(PerkId.Nimble), Health.Value / Mathf.Max(1f, MaxHealth));
                if (HyperRemaining > 0f) speedBonus += Perks.HyperSpeedBonus(PerkCount(PerkId.Hyper));
                speedBonus += Perks.CollectorSpeed * Collected.Value; // Collector
                Motor.SpeedMultiplier = Mathf.Min(1f + speedBonus, Perks.MaxSpeedMultiplier);
                if (!Dead.Value) WriteState();
                else if (CanRespawn && InputMap.Down(RespawnAction) && Time.time >= _respawnRequestCooldown)
                {
                    _respawnRequestCooldown = Time.time + 0.5f;
                    RequestRespawnRpc();
                }
                _viewVelocity = Motor.Velocity;
                _viewYaw = Motor.ViewYaw;
                _viewPitch = Motor.ViewPitch;
                _viewFlags = BuildFlags();
            }
            else
            {
                Interpolate();
            }
        }

        // ---------------- replication ----------------

        byte BuildFlags()
        {
            byte f = 0;
            if (Motor.IsGrounded) f |= NetState.Grounded;
            if (Motor.IsSliding) f |= NetState.Sliding;
            if (Motor.IsCrouching) f |= NetState.Crouching;
            if (Motor.WallSide < 0) f |= NetState.WallLeft;
            if (Motor.WallSide > 0) f |= NetState.WallRight;
            if (_weapon != null && _weapon.IsReloading) f |= NetState.Reloading;
            if (_grapple != null && _grapple.IsOut) f |= NetState.HookOut;
            if (_grapple != null && _grapple.IsOnPlayer) f |= NetState.HookOnPlayer;
            return f;
        }

        void WriteState()
        {
            _state.Value = new NetState
            {
                Position = transform.position,
                Velocity = Motor.Velocity,
                Hook = _grapple != null && _grapple.IsOut ? _grapple.Tip : Vector3.zero,
                Yaw = Motor.ViewYaw,
                Pitch = Motor.ViewPitch,
                Flags = BuildFlags(),
                Teleport = _teleportCounter,
            };
        }

        void OnStateChanged(NetState prev, NetState next) => PushSnap(next);

        void PushSnap(NetState s)
        {
            if (s.Teleport != _lastSeenTeleport)
            {
                _lastSeenTeleport = s.Teleport;
                _snaps.Clear();
                _hiddenUntilTeleport = false;
                ApplySnapImmediate(s);
                SetVisible(!Dead.Value);
            }
            _snaps.Add(new Snap { time = Time.time, s = s });
            if (_snaps.Count > 30) _snaps.RemoveAt(0);
        }

        void ApplySnapImmediate(NetState s)
        {
            transform.SetPositionAndRotation(s.Position, Quaternion.Euler(0, s.Yaw, 0));
            _viewVelocity = s.Velocity;
            _viewYaw = s.Yaw;
            _viewPitch = s.Pitch;
            _viewFlags = s.Flags;
            if (_grapple != null) _grapple.SetRemote(s.Has(NetState.HookOut), s.Hook);
        }

        void Interpolate()
        {
            if (_snaps.Count == 0) return;
            float t = Time.time - interpolationDelay;
            var newest = _snaps[_snaps.Count - 1];
            NetState s;
            if (t >= newest.time)
            {
                // past the newest snapshot: extrapolate briefly
                s = newest.s;
                s.Position += newest.s.Velocity * Mathf.Min(t - newest.time, 0.15f);
            }
            else
            {
                int i = _snaps.Count - 1;
                while (i > 0 && _snaps[i - 1].time > t) i--;
                if (i == 0) s = _snaps[0].s;
                else
                {
                    var a = _snaps[i - 1];
                    var b = _snaps[i];
                    float k = Mathf.Clamp01((t - a.time) / Mathf.Max(0.0001f, b.time - a.time));
                    s = b.s;
                    s.Position = Vector3.Lerp(a.s.Position, b.s.Position, k);
                    s.Velocity = Vector3.Lerp(a.s.Velocity, b.s.Velocity, k);
                    s.Yaw = Mathf.LerpAngle(a.s.Yaw, b.s.Yaw, k);
                    s.Pitch = Mathf.Lerp(a.s.Pitch, b.s.Pitch, k);
                    if (a.s.Has(NetState.HookOut) && b.s.Has(NetState.HookOut)) s.Hook = Vector3.Lerp(a.s.Hook, b.s.Hook, k);
                }
            }
            while (_snaps.Count > 2 && _snaps[1].time < t - 0.5f) _snaps.RemoveAt(0);
            ApplySnapImmediate(s);
        }

        [Rpc(SendTo.Owner)]
        void ScoredRpc(FixedString32Bytes victim, int points, byte victimColor) => LocalScored?.Invoke(victim.ToString(), points, victimColor == 255 ? -1 : victimColor);

        // ---------------- grapple hits ----------------

        /// <summary>Owner: our hook just struck someone.</summary>
        public void NotifyHookHit(Combatant target, int segment, Vector3 point, Vector3 dir)
        {
            if (target == null || !target.IsSpawned) return;
            HookHitRpc(target.NetworkObjectId, (byte)segment, point, dir);
        }

        [Rpc(SendTo.Server)]
        void HookHitRpc(ulong targetId, byte segment, Vector3 point, Vector3 dir)
        {
            if (!IsAlive) return;
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(targetId, out var no)) return;
            var target = no.GetComponent<Combatant>();
            if (target == null || target == this || !target.IsAlive || MatchManager.AreTeammates(this, target)) return;
            var hook = _grapple;
            if (hook != null && !hook.Unlimited && Vector3.Distance(transform.position, target.transform.position) > hook.maxRange + 10f) return; // sanity
            if (target is PlayerController victim) victim.HookedByRpc(NetworkObjectId);
            target.ServerApplyDamage(GameRules.HookDamage, OwnerClientId, point, dir.normalized, segment, 40f);
        }

        /// <summary>Victim's owner: you've been hooked — get reeled toward the hooker.</summary>
        [Rpc(SendTo.Owner)]
        void HookedByRpc(ulong hookerId)
        {
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(hookerId, out var no)) return;
            var hooker = no.GetComponent<PlayerController>();
            if (hooker != null && Motor != null) Motor.SetHookedBy(hooker);
        }

        // ---------------- perks ----------------

        void PushPerks()
        {
            if (!IsOwner || !IsSpawned) return;
            PerkSet.Value = Perks.Pack(Loadouts.Selected.perks);
        }

        struct Heal { public float start, end, rate; }
        readonly List<Heal> _heals = new();
        float _serverBloodlustUntil, _lastHealTick;
        float _localBloodlustUntil, _localHyperUntil;

        /// <summary>Server: damage multiplier from active perks (Bloodlust).</summary>
        public float ServerDamageMultiplier => Time.time < _serverBloodlustUntil ? Perks.BloodlustMultiplier(PerkCount(PerkId.Bloodlust)) : 1f;
        /// <summary>Owner: same multiplier, predicted locally (for the kill hitmarker).</summary>
        public float LocalDamageMultiplier => BloodlustRemaining > 0f ? Perks.BloodlustMultiplier(PerkCount(PerkId.Bloodlust)) : 1f;
        /// <summary>Owner: seconds of Bloodlust left (0 = inactive).</summary>
        public float BloodlustRemaining => Mathf.Max(0f, _localBloodlustUntil - Time.time);
        /// <summary>Owner: seconds of Hyper left (0 = inactive).</summary>
        public float HyperRemaining => Mathf.Max(0f, _localHyperUntil - Time.time);

        /// <summary>Server: we just took this much health off someone else.</summary>
        public void ServerOnDealtDamage(float dealt)
        {
            int n = PerkCount(PerkId.Lifesteal);
            if (n <= 0 || dealt <= 0f) return;
            // Lifesteal: restored over a second, not instantly
            float amount = dealt * Perks.LifestealOf(n);
            _heals.Add(new Heal { start = Time.time, end = Time.time + Perks.LifestealDuration, rate = amount / Perks.LifestealDuration });
        }

        /// <summary>Server: we just killed someone else.</summary>
        public void ServerOnKill(Combatant victim)
        {
            // Collector: +15 max health (already healed) and +15% speed per kill, per copy, until death
            int collector = PerkCount(PerkId.Collector);
            if (collector > 0)
            {
                // the heal matches the max health actually gained (none once the +100 cap is reached)
                float before = MaxHealth;
                Collected.Value += collector;
                Health.Value = Mathf.Min(MaxHealth, Health.Value + (MaxHealth - before));
            }

            // Rejuvenation: 40% of max health per copy, restored over 2 seconds
            int rejuv = PerkCount(PerkId.Rejuvenation);
            if (rejuv > 0)
            {
                float amount = MaxHealth * Perks.RejuvenationOf(rejuv);
                _heals.Add(new Heal { start = Time.time, end = Time.time + Perks.RejuvenationDuration, rate = amount / Perks.RejuvenationDuration });
            }

            bool bloodlust = PerkCount(PerkId.Bloodlust) > 0, hyper = PerkCount(PerkId.Hyper) > 0;
            if (bloodlust) _serverBloodlustUntil = Time.time + Perks.BloodlustDuration;
            // Copy: the gun the victim had out when they died (dummies: a random gun)
            short copy = -1;
            if (PerkCount(PerkId.Copy) > 0)
            {
                if (victim is PlayerController && victim.Weapon != null) copy = (short)victim.Weapon.Current.id;
                else
                {
                    var all = WeaponCatalog.All;
                    if (all.Count > 0) copy = (short)all[UnityEngine.Random.Range(0, all.Count)].id;
                }
            }
            if (bloodlust || hyper || copy >= 0) KillPerksRpc(bloodlust, hyper, copy);
        }

        /// <summary>Owner: a kill triggered on-kill perks (Bloodlust timer for the HUD/prediction, Hyper speed, Copy gun).</summary>
        [Rpc(SendTo.Owner)]
        void KillPerksRpc(bool bloodlust, bool hyper, short copyWeapon)
        {
            if (bloodlust) _localBloodlustUntil = Time.time + Perks.BloodlustDuration;
            if (hyper) _localHyperUntil = Time.time + Perks.HyperDuration;
            if (copyWeapon >= 0 && !Dead.Value && _weapon != null) _weapon.TakeWeapon((WeaponId)copyWeapon);
        }

        void ServerTickPerks()
        {
            float now = Time.time, prev = _lastHealTick;
            _lastHealTick = now;
            if (_heals.Count == 0) return;
            if (Dead.Value) { _heals.Clear(); return; }
            float heal = 0f;
            for (int i = _heals.Count - 1; i >= 0; i--)
            {
                var h = _heals[i];
                // the part of this heal's window that falls in this frame
                float overlap = Mathf.Min(now, h.end) - Mathf.Max(prev, h.start);
                if (overlap > 0f) heal += h.rate * overlap;
                if (now >= h.end) _heals.RemoveAt(i);
            }
            if (heal > 0f) Health.Value = Mathf.Min(MaxHealth, Health.Value + heal);
        }

        protected override void OnServerDied()
        {
            _heals.Clear();
            _serverBloodlustUntil = 0f;
            Collected.Value = 0; // Collector bonuses last until you die
        }

        // ---------------- colour ----------------

        void RequestColor()
        {
            if (IsOwner && IsSpawned) RequestColorRpc(-1); // random colour on join
        }

        [Rpc(SendTo.Server)]
        void RequestColorRpc(sbyte wanted)
        {
            int n = PlayerPalette.Count;
            bool Taken(int i)
            {
                foreach (var p in All)
                    if (p != null && p != this && p.ColorIndex.Value == i) return true;
                return false;
            }
            int start = wanted >= 0 && wanted < n ? wanted : UnityEngine.Random.Range(0, n);
            // wanted (or random) colour if free, otherwise the next free one in the list
            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % n;
                if (!Taken(i)) { ColorIndex.Value = (byte)i; return; }
            }
            ColorIndex.Value = (byte)start; // more players than colours: share
        }

        void OnColorChanged(byte prev, byte next) => ApplyColor();

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        MaterialPropertyBlock _mpb;

        void ApplyColor()
        {
            if (ColorIndex.Value == 255 || model == null) return;
            _mpb ??= new MaterialPropertyBlock();
            Color c = PlayerColor;
            void Tint(Renderer r)
            {
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(BaseColorId, c);
                _mpb.SetColor(ColorId, c);
                r.SetPropertyBlock(_mpb);
            }
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                if (r.name != FaceDecal.ObjectName) Tint(r); // the face keeps its own colours
            if (_fpBody != null && _fpBody.Arms != null) Tint(_fpBody.Arms);
        }

        void PushFace()
        {
            if (!IsOwner || !IsSpawned) return;
            var fs = new FixedString4096Bytes();
            fs.CopyFromTruncated(FaceArt.Encoded ?? "");
            if (Face.Value.Equals(fs)) return;
            Face.Value = fs;
            ApplyFace();
        }

        void OnFaceChanged(FixedString4096Bytes prev, FixedString4096Bytes next) => ApplyFace();

        void ApplyFace()
        {
            if (_facePainter == null) return;
            var strokes = FaceArt.Decode(Face.Value.ToString());
            _hasFace = strokes.Count > 0;
            _facePainter.Draw(strokes);
            UpdateFaceVisible();
        }

        void UpdateFaceVisible()
        {
            if (_faceRenderer != null) _faceRenderer.enabled = _hasFace && Visible && !IsOwner;
        }

        void PushName()
        {
            if (!IsOwner || !IsSpawned) return;
            var fs = new FixedString32Bytes();
            fs.CopyFromTruncated(GameSettings.PlayerName ?? "");
            PlayerName.Value = fs;
        }

        // ---------------- death / respawn ----------------

        protected override void OnServerKilled(ulong attackerClientId)
        {
            if (!MatchManager.ScoringOpen) return; // between rounds nothing counts
            Deaths.Value++;
            if (attackerClientId == OwnerClientId) return; // self-kill
            if (NetworkManager.ConnectedClients.TryGetValue(attackerClientId, out var client) && client.PlayerObject != null)
            {
                var killer = client.PlayerObject.GetComponent<PlayerController>();
                if (killer != null)
                {
                    int points = MatchManager.PointsPerKill;
                    killer.Kills.Value++;
                    killer.Score.Value += points;
                    MatchManager.ServerOnKill(killer);
                    killer.ScoredRpc(PlayerName.Value.Length > 0 ? PlayerName.Value : new FixedString32Bytes(DisplayName), points, ColorIndex.Value);
                }
            }
        }

        /// <summary>Owner: a moving map just crushed us.</summary>
        public void ReportCrushed()
        {
            if (IsOwner && IsSpawned) CrushedRpc(transform.position);
        }

        [Rpc(SendTo.Server)]
        void CrushedRpc(Vector3 where)
        {
            if (IsAlive && MatchManager.ServerCrushAllowed) ServerKillByHazard(DeathCause.Crushed, where);
        }

        /// <summary>Players respawn manually: after respawnDelay, press the Jump key (Space by default).</summary>
        protected override bool AutoRespawn => false;
        public const GameAction RespawnAction = GameAction.Jump;
        float _localDeathTime = -100f, _respawnRequestCooldown;
        /// <summary>Owner: seconds until respawning is allowed (0 = press the key now).</summary>
        public float RespawnWait => Mathf.Max(0f, respawnDelay - (Time.time - _localDeathTime));
        public bool CanRespawn => Dead.Value && RespawnWait <= 0f && MatchManager.RespawnsAllowed;

        [Rpc(SendTo.Server)]
        void RequestRespawnRpc()
        {
            if (MatchManager.RespawnsAllowed) ServerTryRespawn(); // paused while the maps change
        }

        protected override void OnDiedLocal()
        {
            _hiddenUntilTeleport = !IsOwner;
            _localDeathTime = Time.time;
            _localBloodlustUntil = _localHyperUntil = 0f;
            if (IsOwner)
            {
                Motor.enabled = false;
                Motor.Velocity = Vector3.zero;
            }
        }

        protected override void ServerRespawn()
        {
            SpawnPoint.PickFor(this, out var pos, out var yaw);
            RespawnRpc(pos, yaw);
        }

        [Rpc(SendTo.Everyone)]
        void RespawnRpc(Vector3 pos, float yaw)
        {
            if (IsOwner)
            {
                _teleportCounter++;
                MatchManager.LocalRespawned();
                PushPerks(); // loadout perks changed while dead apply now
                Motor.Teleport(pos, yaw);
                Motor.enabled = true;
                if (_weapon != null) _weapon.OnRespawned(); // applies the selected loadout and draws the primary
                WriteState();
                SetVisible(true);
            }
            else
            {
                SetVisible(!_hiddenUntilTeleport);
            }
        }

        protected override void OnDeadChanged(bool prev, bool dead)
        {
            if (dead) SetVisible(false);
            else if (IsOwner || !_hiddenUntilTeleport) SetVisible(true);
        }

        public override void SetVisible(bool on)
        {
            base.SetVisible(on);
            UpdateFaceVisible();
            if (_weapon != null) _weapon.SetVisible(on);
            if (_fpBody != null) _fpBody.SetVisible(on);
            if (CC) CC.enabled = on;
        }
    }
}
