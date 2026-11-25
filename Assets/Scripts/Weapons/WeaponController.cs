using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// The player's guns: a Primary and a Secondary from the selected loadout (applied on spawn).
    /// The shooter traces the bullet locally (instant feedback), the host validates and applies damage,
    /// then relays the bullet path to everyone else for VFX.
    /// Visuals: the "Gun" child is the built-in (Wolfgang) model; a copy ("GunL") is made for dual weapons.
    /// Weapons with their own model (WeaponInfo.modelPath) get their own "Gun_[name]" holder, built at runtime.
    /// </summary>
    public class WeaponController : NetworkBehaviour
    {
        [Tooltip("Stats for the Wolfgang (the default pistol). Other weapons are defined in WeaponCatalog.")]
        public GunDefinition definition;

        [Header("Gun transforms (children of 'Gun')")]
        public Transform gunRoot;
        public Transform muzzle;
        public Transform ejectPort;
        public Material casingMaterial;

        [Header("Slide (moves back when firing)")]
        public Transform slide;
        [Tooltip("How far the slide travels back, in metres.")]
        public float slideTravel = 0.035f;
        [Tooltip("Seconds for the slide to return forward.")]
        public float slideReturnTime = 0.12f;

        [Header("Lever action (models with LeverAction + LeverActionFulcrum)")]
        [Tooltip("Degrees the lever swings open around the fulcrum's Z axis. Flip the sign if it opens the wrong way.")]
        public float leverOpenAngle = 80f;

        [Header("Pump action (models with a 'Handle', e.g. Rachmaninoff)")]
        [Tooltip("How far the pump Handle racks back, in metres.")]
        public float pumpTravel = 0.1f;

        [Serializable]
        public class WeaponModel
        {
            public WeaponId id;
            public GameObject prefab;
        }

        [Tooltip("Model prefabs for weapons that have their own model. Filled by Gunbound > Install Weapon Models.")]
        public List<WeaponModel> models = new();

        /// <summary>One visible gun (right hand, or the left-hand copy for dual weapons).</summary>
        public class GunVisual
        {
            public Transform root, muzzle, eject, slide, trigger;
            /// <summary>Optional "OffhandHold" (or "SupportPoint"/"GripL") marker where the off hand holds a rifle.</summary>
            public Transform support;
            public Vector3 slideRest;
            public float slideT;
            /// <summary>0..1 visual recoil kick, decays quickly.</summary>
            public float kick;
            public GameObject flash;
            public Light flashLight;
            public float flashUntil;
            // lever action
            public Transform lever, fulcrum;
            public Vector3 leverRestPos;
            public Quaternion leverRestRot;
            public float cockStart = -10f, cockDuration, cockDelay;
            /// <summary>0 = lever closed, 1 = fully open.</summary>
            public float LeverOpen { get; internal set; }
            public float LeverAngle { get; internal set; }
            // pump action ("Handle" slides back and forward; its OffhandHold child carries the left hand)
            public Transform pump;
            public Vector3 pumpRest;
            /// <summary>0 = pump forward, 1 = racked fully back.</summary>
            public float PumpOpen { get; internal set; }
            /// <summary>The model has a working action (lever or pump) that cycles after each shot.</summary>
            public bool HasAction => lever != null || pump != null;
            // melee
            public Transform tip;
            public TrailRenderer trail;
        }

        class VisualSet
        {
            public GunVisual right, left;
        }

        VisualSet _builtinSet;
        readonly Dictionary<WeaponId, VisualSet> _sets = new();
        VisualSet CurrentSet => _sets.TryGetValue(_currentId, out var s) ? s : _builtinSet;

        public GunVisual Right => CurrentSet?.right;
        public GunVisual Left => CurrentSet?.left;

        /// <summary>Replicated equipment: loadout weapons, which slot is out, and a counter that bumps on every draw.</summary>
        public struct EquipState : INetworkSerializable, IEquatable<EquipState>
        {
            public byte primary, secondary, slot, drawSeq;
            public byte Current => slot == 0 ? primary : secondary;

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref primary);
                s.SerializeValue(ref secondary);
                s.SerializeValue(ref slot);
                s.SerializeValue(ref drawSeq);
            }

            public bool Equals(EquipState o) => primary == o.primary && secondary == o.secondary && slot == o.slot && drawSeq == o.drawSeq;
            public override bool Equals(object obj) => obj is EquipState o && Equals(o);
            public override int GetHashCode() => primary | (secondary << 8) | (slot << 16) | (drawSeq << 24);
        }

        readonly NetworkVariable<EquipState> _equip = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // ---- public state ----
        public WeaponInfo Current => WeaponCatalog.Get(_currentId);
        public GunDefinition Definition => Current.stats != null ? Current.stats : GunDefinition.Fallback;
        public bool IsDual => Current.Dual;
        public LoadoutSlot CurrentSlot => (LoadoutSlot)_slot;
        public WeaponId ItemIn(LoadoutSlot s) => _items[(int)s];
        public int Ammo => _ammo[_slot];
        public bool IsReloading { get; private set; }
        /// <summary>Whole-mag reload: 0..1 overall. Round-by-round reload: 0..1 for the round being loaded.</summary>
        public float ReloadProgress => IsReloading ? Mathf.Clamp01(1f - (_reloadEnd - Time.time) / Mathf.Max(0.01f, ReloadTime)) : 0f;
        /// <summary>Reload time (whole mag, or one round) after perks. Swiftloader doesn't touch the lever (cockTime).</summary>
        public float ReloadTime => Definition.reloadTime * Perks.ReloadTimeMultiplier(_pc != null ? _pc.PerkCount(PerkId.Swiftloader) : 0);
        /// <summary>0 = just started drawing, 1 = weapon ready.</summary>
        public float DrawProgress => _drawDuration <= 0f ? 1f : Mathf.Clamp01((Time.time - _drawStart) / _drawDuration);
        public bool IsDrawing => DrawProgress < 1f;
        /// <summary>Largest kick of the visible guns (0..1).</summary>
        public float Kick => Mathf.Max(Right != null ? Right.kick : 0f, Left != null ? Left.kick : 0f);

        /// <summary>(hitSelfOnly, killed) — killed is predicted from replicated health, works for dummies too.</summary>
        public static event Action<bool, bool> LocalHitConfirmed;

        PlayerController _pc;
        WeaponId _currentId = WeaponId.Wolfgang;
        readonly WeaponId[] _items = new WeaponId[LoadoutSchema.SlotCount];
        readonly int[] _ammo = new int[LoadoutSchema.SlotCount];
        int _slot;
        int _nextHand;
        float _nextFire, _reloadEnd;
        float _drawStart = -10f, _drawDuration;
        bool _visible = true;
        // server-side shot budget (lets a burst through network jitter without allowing a faster fire rate)
        float _serverBudget = 1f, _serverBudgetTime;
        readonly BulletSim.Result _result = new();

        void Awake()
        {
            _pc = GetComponent<PlayerController>();
            WeaponCatalog.RegisterAsset(WeaponId.Wolfgang, definition);
            for (int i = 0; i < _items.Length; i++) _items[i] = LoadoutSchema.Slots[i].defaultWeapon;

            if (gunRoot == null) gunRoot = transform.Find("Gun");
            if (gunRoot != null)
            {
                if (muzzle == null) muzzle = FindDeep(gunRoot, "BarrelPoint") ?? FindDeep(gunRoot, "Muzzle");
                if (ejectPort == null) ejectPort = FindDeep(gunRoot, "Eject");
                if (slide == null) slide = FindDeep(gunRoot, "Slide");

                // left-hand copy for dual weapons (uses the same model)
                var leftGo = Instantiate(gunRoot.gameObject, gunRoot.parent);
                leftGo.name = "GunL";
                _builtinSet = new VisualSet
                {
                    right = MakeVisual(gunRoot, muzzle, ejectPort, slide),
                    left = MakeVisual(leftGo.transform,
                        muzzle != null ? FindDeep(leftGo.transform, muzzle.name) : null,
                        ejectPort != null ? FindDeep(leftGo.transform, ejectPort.name) : null,
                        slide != null ? FindDeep(leftGo.transform, slide.name) : null),
                };
                StripColliders(gunRoot.gameObject);
                StripColliders(leftGo);
            }

            // weapons with their own model
            foreach (var w in WeaponCatalog.All)
            {
                if (string.IsNullOrEmpty(w.modelPath)) continue;
                var prefab = ModelPrefab(w);
                if (prefab == null) continue;
                var set = new VisualSet { right = BuildModelVisual(w, prefab, "Gun_" + w.id) };
                if (w.Dual) set.left = BuildModelVisual(w, prefab, "Gun_" + w.id + "_L");
                _sets[w.id] = set;
            }

            for (int i = 0; i < _ammo.Length; i++) _ammo[i] = MagSize(_items[i]);
            RefreshGunVisibility();
        }

        /// <summary>Full magazine for a weapon (0 for an empty slot).</summary>
        static int MagSize(WeaponId id) => WeaponCatalog.IsValid((int)id) ? WeaponCatalog.Get(id).stats.magazineSize : 0;

        bool HasItem(int slot) => slot >= 0 && slot < _items.Length && WeaponCatalog.IsValid((int)_items[slot]);

        GameObject ModelPrefab(WeaponInfo w)
        {
            foreach (var m in models) if (m != null && m.id == w.id && m.prefab != null) return m.prefab;
#if UNITY_EDITOR
            // works in the editor without running the install menu (builds need the stored reference)
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(w.modelPath);
#else
            Debug.LogWarning($"[Gunbound] No model for {w.displayName} — run Gunbound > Install Weapon Models.");
            return null;
#endif
        }

        GunVisual BuildModelVisual(WeaponInfo w, GameObject prefab, string holderName)
        {
            // holder: aims along +Z like the built-in "Gun"; the model inside is rotated/scaled the same way as Gun.prefab
            var holder = new GameObject(holderName).transform;
            holder.SetParent(gunRoot != null ? gunRoot.parent : transform, false);
            var inst = Instantiate(prefab, holder);
            inst.name = "Model";
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.Euler(w.modelEuler);
            inst.transform.localScale = Vector3.one * w.modelScale;
            StripColliders(inst);

            var m = FindDeep(inst.transform, "BarrelPoint") ?? FindDeep(inst.transform, "Muzzle");
            var sl = FindDeep(inst.transform, "Slide");
            var ej = FindDeep(inst.transform, "Eject");
            if (ej == null)
            {
                // casings come out of the receiver, just above the trigger
                ej = new GameObject("Eject").transform;
                var trig = FindDeep(inst.transform, "TriggerPoint");
                ej.SetParent(inst.transform, false);
                ej.localPosition = trig != null ? trig.localPosition + new Vector3(0.1f, 0.2f, 0) : Vector3.zero;
            }
            var g = MakeVisual(holder, m, ej, sl);
            if (w.Melee) BuildBladeTrail(g, holder, inst.transform);
            g.lever = FindDeep(inst.transform, "LeverAction");
            g.fulcrum = FindDeep(inst.transform, "LeverActionFulcrum");
            if (g.lever != null)
            {
                g.leverRestPos = g.lever.localPosition;
                g.leverRestRot = g.lever.localRotation;
            }
            if (w.stats != null && w.stats.pumpAction)
            {
                g.pump = FindDeep(inst.transform, "Handle");
                if (g.pump != null) g.pumpRest = g.pump.localPosition;
            }
            return g;
        }

        /// <summary>Blade tip marker ("Tip"/"BladeTip" if the model has one, else the far end of the blade) + swoosh trail.</summary>
        static void BuildBladeTrail(GunVisual g, Transform holder, Transform model)
        {
            var tip = FindDeep(model, "Tip") ?? FindDeep(model, "BladeTip");
            if (tip == null)
            {
                // furthest point of the meshes along the holder's +Z (the blade direction)
                float maxZ = 0f;
                foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    var b = mf.sharedMesh.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        maxZ = Mathf.Max(maxZ, holder.InverseTransformPoint(mf.transform.TransformPoint(corner)).z);
                    }
                }
                tip = new GameObject("Tip").transform;
                tip.SetParent(holder, false);
                tip.localPosition = new Vector3(0, 0, maxZ * 0.95f);
            }
            g.tip = tip;

            var trail = tip.gameObject.AddComponent<TrailRenderer>();
            trail.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            trail.time = 0.14f;
            trail.minVertexDistance = 0.03f;
            trail.widthCurve = new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 0));
            trail.widthMultiplier = 0.22f;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(new Color(0.8f, 0.85f, 1f), 1) },
                         new[] { new GradientAlphaKey(0.55f, 0), new GradientAlphaKey(0f, 1) });
            trail.colorGradient = grad;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.emitting = false;
            g.trail = trail;
        }

        static GunVisual MakeVisual(Transform root, Transform muzzle, Transform eject, Transform slide)
        {
            var g = new GunVisual
            {
                root = root,
                muzzle = muzzle,
                eject = eject,
                slide = slide,
                // where the hand grips: guns use TriggerPoint, the sword uses Handle
                trigger = FindDeep(root, "TriggerPoint") ?? FindDeep(root, "Trigger Point") ?? FindDeep(root, "Trigger") ?? FindDeep(root, "Handle"),
                support = FindDeep(root, "OffhandHold") ?? FindDeep(root, "SupportPoint") ?? FindDeep(root, "GripL"),
            };
            if (slide != null) g.slideRest = slide.localPosition;
            return g;
        }

        // the guns are visual only: their colliders must never block bullets or movement
        static void StripColliders(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
        }

        IEnumerable<GunVisual> AllVisuals()
        {
            if (_builtinSet != null) { yield return _builtinSet.right; yield return _builtinSet.left; }
            foreach (var s in _sets.Values) { yield return s.right; if (s.left != null) yield return s.left; }
        }

        public override void OnNetworkSpawn()
        {
            FxManager.Ensure();
            foreach (var g in AllVisuals())
            {
                if (g == null) continue;
                BuildMuzzleFlash(g);
                foreach (var r in g.root.GetComponentsInChildren<Renderer>(true))
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            if (IsOwner)
            {
                ApplyLoadout(Loadouts.Selected);
            }
            else
            {
                _equip.OnValueChanged += OnEquipChanged;
                _currentId = (WeaponId)_equip.Value.Current;
                RefreshGunVisibility();
            }
        }

        public override void OnNetworkDespawn()
        {
            _equip.OnValueChanged -= OnEquipChanged;
        }

        // ------------------------------------------------------------------ loadout / switching

        /// <summary>Owner: take the weapons from a loadout, refill all ammo and draw the primary.</summary>
        public void ApplyLoadout(Loadout l)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                _items[i] = l.Get((LoadoutSlot)i);
                _ammo[i] = MagSize(_items[i]);
            }
            Equip(0, force: true);
        }

        /// <summary>
        /// Owner (Copy perk): swap the weapon in your hands for this one, fully loaded, and draw it.
        /// Lasts until you respawn (which re-applies the loadout).
        /// </summary>
        public void TakeWeapon(WeaponId id)
        {
            if (!IsOwner || !WeaponCatalog.IsValid((int)id)) return;
            _items[_slot] = id;
            _ammo[_slot] = WeaponCatalog.Get(id).stats.magazineSize;
            Equip(_slot, force: true);
            WeaponTaken?.Invoke(id);
        }

        /// <summary>Owner: the Copy perk just gave us this gun.</summary>
        public static event Action<WeaponId> WeaponTaken;

        /// <summary>Owner: called on respawn — picks up any loadout change made while dead.</summary>
        public void OnRespawned() => ApplyLoadout(Loadouts.Selected);

        /// <summary>Kept for older callers: refill every slot.</summary>
        public void ResetAmmo()
        {
            for (int i = 0; i < _ammo.Length; i++) _ammo[i] = MagSize(_items[i]);
            IsReloading = false;
        }

        void Equip(int slot, bool force = false)
        {
            slot = Mathf.Clamp(slot, 0, _items.Length - 1);
            if (!HasItem(slot)) return; // empty slot: nothing to switch to
            if (slot == _slot && !force) return;
            _slot = slot;
            IsReloading = false;
            _currentId = _items[slot];
            _nextHand = 0;
            _swingIndex = 0;          // the first swing after a draw is always right → left
            _swingActive = false;
            _swingDuration = 0f;
            StartDraw();
            RefreshGunVisibility();
            if (IsOwner && IsSpawned)
            {
                var s = _equip.Value;
                s.primary = (byte)_items[0];
                s.secondary = (byte)_items[Mathf.Min(1, _items.Length - 1)];
                s.slot = (byte)slot;
                s.drawSeq++;
                _equip.Value = s;
            }
        }

        void StartDraw()
        {
            _drawStart = Time.time;
            _drawDuration = Definition.drawTime;
            _nextFire = Mathf.Max(_nextFire, Time.time + _drawDuration);
            _cockEnd = 0f;
            _autoReload = true;
            foreach (var g in AllVisuals()) if (g != null) g.cockStart = -10f; // a fresh draw is already cocked
        }

        void OnEquipChanged(EquipState prev, EquipState next)
        {
            _currentId = (WeaponId)next.Current;
            if (prev.drawSeq != next.drawSeq) { StartDraw(); _swingDuration = 0f; }
            RefreshGunVisibility();
        }

        public void SetVisible(bool on)
        {
            _visible = on;
            RefreshGunVisibility();
        }

        void RefreshGunVisibility()
        {
            var cur = CurrentSet;
            void Show(VisualSet s)
            {
                if (s == null) return;
                bool active = _visible && s == cur;
                if (s.right != null) s.right.root.gameObject.SetActive(active);
                if (s.left != null) s.left.root.gameObject.SetActive(active && IsDual);
            }
            Show(_builtinSet);
            foreach (var s in _sets.Values) Show(s);
        }

        // ------------------------------------------------------------------ update

        void Update()
        {
            foreach (var g in AllVisuals()) TickGun(g);

            if (!IsSpawned || !IsOwner || !_pc.IsAlive) { _swingActive = false; return; }
            var def = Definition;

            if (IsReloading) TickReload(def);

            // switching
            if (InputMap.Down(GameAction.Primary)) Equip((int)LoadoutSlot.Primary);
            else if (InputMap.Down(GameAction.Secondary)) Equip((int)LoadoutSlot.Secondary);
            else if (InputMap.Scroll != 0) Equip(NextFilledSlot());
            def = Definition;

            if (Current.Melee)
            {
                // for the whole swing, anyone the sphere clips is hit (once per swing)
                if (_swingActive)
                {
                    MeleeHit(_swingHitDir);
                    if (Time.time >= _swingHitUntil) _swingActive = false;
                }
                // a click shortly before the next swing is ready is remembered, so mashing feels responsive
                if (InputMap.FireDown) _swingQueuedUntil = Time.time + 0.15f;
                if (Time.time < _swingQueuedUntil && !IsDrawing && Time.time >= _nextFire)
                {
                    _swingQueuedUntil = 0f;
                    Swing();
                }
                return;
            }

            // the action (lever) has to finish cycling before a reload can start
            bool canReload = !IsReloading && !IsDrawing && !IsCocking && Ammo < def.magazineSize;
            if (canReload && (InputMap.Down(GameAction.Reload) || (_autoReload && Ammo <= 0))) StartReload();

            bool trigger = def.fullAuto ? InputMap.FireHeld : InputMap.FireDown;
            if (trigger)
            {
                if (IsDrawing || Time.time < _nextFire) return;
                bool interrupted = false;
                if (IsReloading)
                {
                    // round-by-round reloads are cancelled by firing a loaded round
                    if (!def.reloadPerRound || Ammo <= 0) return;
                    IsReloading = false;
                    interrupted = true;
                }
                if (Ammo <= 0)
                {
                    if (!IsCocking) StartReload();
                    return;
                }
                Fire();
                // emptying the gun normally auto-reloads (once the action has cycled), but not when the
                // shot was the one that cancelled a reload — the player chose to stop reloading
                _autoReload = !interrupted;
            }
        }

        /// <summary>The next slot after the current one that holds a weapon (the current slot if none does).</summary>
        int NextFilledSlot()
        {
            for (int k = 1; k < _items.Length; k++)
            {
                int s = (_slot + k) % _items.Length;
                if (HasItem(s)) return s;
            }
            return _slot;
        }

        float _cockEnd;
        bool _autoReload = true;
        /// <summary>Owner: the action is still being worked after a shot (no reloading until it's done).</summary>
        public bool IsCocking => Time.time < _cockEnd;

        void TickReload(GunDefinition def)
        {
            if (!def.reloadPerRound)
            {
                if (Time.time < _reloadEnd) return;
                IsReloading = false;
                _ammo[_slot] = def.magazineSize;
                return;
            }
            // one round at a time
            while (IsReloading && Time.time >= _reloadEnd)
            {
                _ammo[_slot] = Mathf.Min(def.magazineSize, _ammo[_slot] + 1);
                if (_ammo[_slot] >= def.magazineSize) IsReloading = false;
                else _reloadEnd += ReloadTime;
            }
        }

        void TickGun(GunVisual g)
        {
            if (g == null) return;
            if (g.trail != null)
            {
                // swoosh only during the fast part of a swing
                float s = SwingProgress;
                bool on = g == Right && Current.Melee && _visible && s > 0.12f && s < 0.62f;
                g.trail.emitting = on;
                if (!_visible) g.trail.Clear();
            }
            g.kick = Mathf.MoveTowards(g.kick, 0f, Time.deltaTime * 9f);
            AnimateSlide(g);
            AnimateLever(g);
            AnimatePump(g);
            if (g.flash != null && g.flash.activeSelf && Time.time > g.flashUntil)
            {
                g.flash.SetActive(false);
                g.flashLight.enabled = false;
            }
        }

        // ------------------------------------------------------------------ melee

        /// <summary>Degrees the swing plane is tilted: right→left swings go high-right to low-left, the return swing mirrors it.</summary>
        public const float SwingTilt = 25f;

        int _swingIndex;
        float _swingStart = -10f, _swingDuration;
        byte _swingDir;
        // owner: the live hit window of the current swing, and who it already hit
        bool _swingActive;
        float _swingHitUntil, _swingQueuedUntil;
        byte _swingHitDir;
        readonly HashSet<Combatant> _swingHits = new();
        // server: the window in which hits for the latest validated swing are accepted, and who it already hit
        float _serverSwingUntil = -10f;
        readonly HashSet<ulong> _serverSwingHits = new();
        static readonly Collider[] MeleeBuf = new Collider[128];

        /// <summary>0..1 through the current swing animation (1 when not swinging).</summary>
        public float SwingProgress => _swingDuration <= 0f ? 1f : Mathf.Clamp01((Time.time - _swingStart) / _swingDuration);
        /// <summary>0 = right → left, 1 = left → right.</summary>
        public int SwingDirection => _swingDir;

        void Swing()
        {
            var def = Definition;
            _nextFire = Time.time + def.SecondsBetweenShots;
            byte dir = (byte)_swingIndex;
            _swingIndex ^= 1; // alternate right→left, left→right
            StartSwingVisual(dir);
            SwingServerRpc((byte)_currentId, dir);
            // lunge toward the crosshair (up/down included) for the length of the swing
            if (def.lungeSpeed > 0f) _pc.Motor.Lunge(_pc.Motor.AimDirection, def.lungeSpeed, def.swingTime, def.lungeCarry, def.lungeBoost);
            // the hit sphere is live for the whole swing (checked every frame in Update, starting now)
            _swingActive = true;
            _swingHitDir = dir;
            _swingHitUntil = Time.time + Mathf.Max(0.05f, def.swingTime);
            _swingHits.Clear();
            MeleeHit(dir);
        }

        void StartSwingVisual(byte dir)
        {
            _swingDir = dir;
            _swingStart = Time.time;
            _swingDuration = Mathf.Max(0.05f, Definition.swingTime);
        }

        /// <summary>Server: rate-limits swings and opens the window in which this swing's hits are accepted.</summary>
        [Rpc(SendTo.Server)]
        void SwingServerRpc(byte weapon, byte dir)
        {
            if (!_pc.IsAlive) return;
            var eq = _equip.Value;
            if (!WeaponCatalog.IsValid(weapon) || (weapon != eq.primary && weapon != eq.secondary)) return;
            var info = WeaponCatalog.Get((WeaponId)weapon);
            if (!info.Melee) return;
            var def = info.stats;

            float now = Time.time;
            _serverBudget = Mathf.Min(2f, _serverBudget + (now - _serverBudgetTime) / (def.SecondsBetweenShots * 0.85f));
            _serverBudgetTime = now;
            if (_serverBudget < 1f) return;
            _serverBudget -= 1f;

            // generous slack: the owner's per-frame hit reports trail the swing by network jitter
            _serverSwingUntil = now + def.swingTime + 0.35f;
            _serverSwingHits.Clear();
            SwingFxRpc(dir);
        }

        [Rpc(SendTo.NotOwner)]
        void SwingFxRpc(byte dir)
        {
            if (Current.Melee) StartSwingVisual(dir);
        }

        /// <summary>The swing's motion direction (world) and the plane the blade cuts through (its normal).</summary>
        void SwingFrame(byte dir, out Vector3 motion, out Vector3 cutNormal)
        {
            Quaternion view = _pc.Motor.ViewRotation;
            Quaternion tilt = Quaternion.AngleAxis(dir == 0 ? SwingTilt : -SwingTilt, Vector3.forward);
            motion = view * (tilt * (dir == 0 ? Vector3.left : Vector3.right));
            cutNormal = view * (tilt * Vector3.up);
        }

        /// <summary>
        /// Owner, every frame of a swing: everyone inside the sphere in front of us whose body we can actually see
        /// (no cutting through walls) and who this swing hasn't hit yet is hit, on the visible part closest to where we're aiming.
        /// </summary>
        void MeleeHit(byte dir)
        {
            var def = Definition;
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : _pc.Animator.EyePosition;
            Vector3 aim = _pc.Motor.AimDirection;
            SwingFrame(dir, out var motion, out var cutNormal);
            Vector3 centre = eye + aim * def.meleeForward;

            Physics.SyncTransforms();
            int n = Physics.OverlapSphereNonAlloc(centre, def.meleeRadius, MeleeBuf, 1 << Layers.Hitbox, QueryTriggerInteraction.Collide);
            Dictionary<Combatant, (Hitbox hb, Vector3 point, float score)> best = null;
            for (int i = 0; i < n; i++)
            {
                var hb = MeleeBuf[i].GetComponent<Hitbox>();
                if (hb == null || hb.Owner == null || hb.Owner == (Combatant)_pc) continue;
                if (!hb.Owner.IsAlive || _swingHits.Contains(hb.Owner)) continue;               // dead, or already hit by this swing
                if (MatchManager.AreTeammates(_pc, hb.Owner)) continue;                         // no friendly fire
                Vector3 p = MeleeBuf[i].ClosestPoint(eye);
                Vector3 to = p - eye;
                if (Vector3.Dot(to, aim) < -0.25f) continue;                                    // behind us
                if (Physics.Linecast(eye, p, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue; // wall in the way
                float score = Vector3.Cross(aim, to).magnitude;                                 // distance from the aim line
                best ??= new Dictionary<Combatant, (Hitbox, Vector3, float)>();
                if (!best.TryGetValue(hb.Owner, out var cur) || score < cur.score) best[hb.Owner] = (hb, p, score);
            }
            if (best == null || best.Count == 0) return;
            foreach (var c in best.Keys) _swingHits.Add(c);

            int count = Mathf.Min(best.Count, 16);
            var targets = new ulong[count];
            var segs = new byte[count];
            var points = new Vector3[count];
            bool killed = false;
            int k = 0;
            foreach (var kv in best)
            {
                if (k >= count) break;
                targets[k] = kv.Key.NetworkObjectId;
                segs[k] = (byte)kv.Value.hb.SegmentIndex;
                points[k] = kv.Value.point;
                float dmg = def.damage * def.Multiplier(kv.Value.hb.Region) * _pc.LocalDamageMultiplier;
                if (kv.Key.Health.Value - dmg <= 0.001f) killed = true;
                FxManager.I?.Blood(kv.Value.point, motion);
                k++;
            }
            LocalHitConfirmed?.Invoke(false, killed);
            MeleeServerRpc((byte)_currentId, targets, segs, points, motion, cutNormal);
        }

        [Rpc(SendTo.Server)]
        void MeleeServerRpc(byte weapon, ulong[] targets, byte[] segs, Vector3[] points, Vector3 motion, Vector3 cutNormal)
        {
            if (!_pc.IsAlive) return;
            var eq = _equip.Value;
            if (!WeaponCatalog.IsValid(weapon) || (weapon != eq.primary && weapon != eq.secondary)) return;
            var info = WeaponCatalog.Get((WeaponId)weapon);
            if (!info.Melee) return;
            var def = info.stats;
            if (targets == null || targets.Length > 16 || segs.Length != targets.Length || points.Length != targets.Length) return;
            // only during a swing the server accepted (SwingServerRpc rate-limits swings), each victim once per swing
            if (Time.time > _serverSwingUntil) return;

            // reach sanity check (generous: positions are a little behind for remote players, more so mid-lunge)
            float reach = def.meleeForward + def.meleeRadius + 3f + def.lungeSpeed * 0.3f;
            if (cutNormal.sqrMagnitude < 0.01f) cutNormal = Vector3.up;
            var spawned = NetworkManager.SpawnManager.SpawnedObjects;
            for (int i = 0; i < targets.Length; i++)
            {
                if (!spawned.TryGetValue(targets[i], out var no)) continue;
                var victim = no.GetComponent<Combatant>();
                if (victim == null || victim == (Combatant)_pc || segs[i] >= Rig.Segments.Length) continue;
                if (Vector3.Distance(victim.transform.position, transform.position) > reach) continue;
                if (Vector3.Distance(points[i], victim.transform.position) > 3f) continue;
                if (!_serverSwingHits.Add(targets[i])) continue; // already hit by this swing
                float dmg = def.damage * def.Multiplier(Rig.Segments[segs[i]].region);
                victim.ServerApplyDamage(dmg, OwnerClientId, points[i], motion.normalized, segs[i], def.ragdollImpulse, cutNormal.normalized);
            }
            MeleeFxRpc(points, motion);
        }

        [Rpc(SendTo.NotOwner)]
        void MeleeFxRpc(Vector3[] points, Vector3 motion)
        {
            var fx = FxManager.I;
            if (fx == null) return;
            foreach (var p in points) fx.Blood(p, motion);
        }

        void StartReload()
        {
            IsReloading = true;
            _autoReload = true;
            _reloadEnd = Time.time + ReloadTime;
        }

        void Fire()
        {
            var def = Definition;
            _ammo[_slot]--;
            // full auto keeps a steady cadence even if a frame is late
            _nextFire = def.fullAuto && Time.time - _nextFire < Time.deltaTime * 2f ? _nextFire + def.SecondsBetweenShots : Time.time + def.SecondsBetweenShots;
            byte hand = 0;
            if (IsDual) { hand = (byte)_nextHand; _nextHand ^= 1; } // alternate right / left

            var cam = Camera.main;
            Vector3 origin = cam != null ? cam.transform.position : _pc.Animator.EyePosition;
            Vector3 dir = _pc.Motor.AimDirection;

            Physics.SyncTransforms();
            float radiusScale = Perks.BulletRadiusMultiplier(_pc.PerkCount(PerkId.Sharpshooter));
            int pellets = Mathf.Max(1, def.pellets);
            var gun = PlayGunFx(hand);
            Transform muzzleT = gun != null ? gun.muzzle : null;

            // every pellet is its own bullet; their paths are sent back to back (lens = points per pellet)
            var points = new List<Vector3>();
            var normals = new List<Vector3>();
            var kinds = new List<byte>();
            var lens = new byte[pellets];
            var allHits = new List<BulletSim.Hit>();
            for (int p = 0; p < pellets; p++)
            {
                BulletSim.Trace(origin, SpreadDirection(dir, def.spreadAngle), def, _pc, _result, radiusScale);
                PlayTracers(_result.points, _result.normals, _result.kinds, _result.surfaces, 0, _result.points.Count, muzzleT);
                lens[p] = (byte)_result.points.Count;
                points.AddRange(_result.points);
                normals.AddRange(_result.normals);
                kinds.AddRange(_result.kinds);
                allHits.AddRange(_result.hits);
            }

            bool hitOther = false, hitSelf = false;
            int hc = allHits.Count;
            var targets = new ulong[hc];
            var segs = new byte[hc];
            var hitPts = new Vector3[hc];
            var hitDirs = new Vector3[hc];
            for (int i = 0; i < hc; i++)
            {
                var h = allHits[i];
                targets[i] = h.target.NetworkObjectId;
                segs[i] = (byte)h.segment;
                hitPts[i] = h.point;
                hitDirs[i] = h.dir;
                if (h.target == _pc) hitSelf = true;
                else if (!MatchManager.AreTeammates(_pc, h.target)) hitOther = true; // teammates take no damage
            }
            // predict a kill: total this shot's damage per target against their current health
            bool killed = false;
            var dmgPer = new Dictionary<Combatant, float>();
            foreach (var h in allHits)
            {
                if (h.target == _pc || MatchManager.AreTeammates(_pc, h.target)) continue;
                float dmg = def.damage * def.Multiplier(Rig.Segments[h.segment].region) * _pc.LocalDamageMultiplier;
                dmgPer[h.target] = (dmgPer.TryGetValue(h.target, out var d) ? d : 0f) + dmg;
            }
            foreach (var kv in dmgPer)
                if (kv.Key.IsAlive && kv.Key.Health.Value - kv.Value <= 0.001f) killed = true;
            if (hitOther || hitSelf) LocalHitConfirmed?.Invoke(!hitOther, killed);

            ShootServerRpc((byte)_currentId, hand, points.ToArray(), normals.ToArray(), kinds.ToArray(), lens, targets, segs, hitPts, hitDirs);

            _pc.Motor.AddRecoil(def.recoilPitch, UnityEngine.Random.Range(-def.recoilYawJitter, def.recoilYawJitter), def.recoilRecovery);
            // every shot is followed by working the action (lever / pump); auto-reload waits for it in Update
            _cockEnd = Time.time + def.CockTotal;
        }

        /// <summary>A random direction within <paramref name="maxAngle"/> degrees of <paramref name="aim"/>, spread evenly over the cone.</summary>
        static Vector3 SpreadDirection(Vector3 aim, float maxAngle)
        {
            aim.Normalize();
            if (maxAngle <= 0f) return aim;
            Vector3 right = Vector3.Cross(Vector3.up, aim);
            if (right.sqrMagnitude < 1e-4f) right = Vector3.Cross(Vector3.forward, aim);
            right.Normalize();
            Vector3 up = Vector3.Cross(aim, right);
            Vector2 d = UnityEngine.Random.insideUnitCircle * Mathf.Tan(Mathf.Min(maxAngle, 80f) * Mathf.Deg2Rad);
            return (aim + right * d.x + up * d.y).normalized;
        }

        /// <summary>Paths are packed back to back; lens[i] = number of points in pellet i's path. False if malformed.</summary>
        static bool ValidPaths(Vector3[] points, Vector3[] normals, byte[] kinds, byte[] lens, int maxPellets)
        {
            if (points == null || normals == null || kinds == null || lens == null) return false;
            if (lens.Length < 1 || lens.Length > maxPellets) return false;
            if (points.Length > 40 * lens.Length || normals.Length != points.Length || kinds.Length != points.Length) return false;
            int sum = 0;
            foreach (var l in lens) { if (l < 2) return false; sum += l; }
            return sum == points.Length;
        }

        [Rpc(SendTo.Server)]
        void ShootServerRpc(byte weapon, byte hand, Vector3[] points, Vector3[] normals, byte[] kinds, byte[] lens, ulong[] targets, byte[] segs, Vector3[] hitPts, Vector3[] hitDirs)
        {
            if (!_pc.IsAlive) return;
            var eq = _equip.Value;
            if (!WeaponCatalog.IsValid(weapon) || (weapon != eq.primary && weapon != eq.secondary)) return;
            var def = WeaponCatalog.Get((WeaponId)weapon).stats;
            int pellets = Mathf.Max(1, def.pellets);
            if (!ValidPaths(points, normals, kinds, lens, pellets)) return;
            int maxHits = Mathf.Max(16, pellets * (def.penetrations + 1));
            if (targets == null || targets.Length > maxHits || segs.Length != targets.Length || hitPts.Length != targets.Length || hitDirs.Length != targets.Length) return;

            // rate limit: refill one shot per fire interval, allow a small burst for jitter
            float now = Time.time;
            _serverBudget = Mathf.Min(2f, _serverBudget + (now - _serverBudgetTime) / (def.SecondsBetweenShots * 0.85f));
            _serverBudgetTime = now;
            if (_serverBudget < 1f) return;
            _serverBudget -= 1f;

            // origin sanity check (every pellet's path starts at the shooter)
            for (int i = 0, start = 0; i < lens.Length; start += lens[i], i++)
                if (Vector3.Distance(points[start], transform.position) > 8f) return;

            var spawned = NetworkManager.SpawnManager.SpawnedObjects;
            for (int i = 0; i < targets.Length; i++)
            {
                if (!spawned.TryGetValue(targets[i], out var no)) continue;
                var victim = no.GetComponent<Combatant>();
                if (victim == null || segs[i] >= Rig.Segments.Length) continue;
                float dmg = def.damage * def.Multiplier(Rig.Segments[segs[i]].region);
                victim.ServerApplyDamage(dmg, OwnerClientId, hitPts[i], hitDirs[i].normalized, segs[i], def.ragdollImpulse);
            }

            ShotFxRpc(points, normals, kinds, lens, hand);
        }

        [Rpc(SendTo.NotOwner)]
        void ShotFxRpc(Vector3[] points, Vector3[] normals, byte[] kinds, byte[] lens, byte hand)
        {
            if (!ValidPaths(points, normals, kinds, lens, 255)) return;
            var gun = PlayGunFx(hand);
            var pts = new List<Vector3>(points);
            var nrm = new List<Vector3>(normals);
            var kd = new List<byte>(kinds);
            int start = 0;
            foreach (var l in lens)
            {
                PlayTracers(pts, nrm, kd, null, start, l, gun != null ? gun.muzzle : null);
                start += l;
            }
        }

        // ------------------------------------------------------------------ fx

        void BuildMuzzleFlash(GunVisual g)
        {
            if (g.muzzle == null || g.flash != null) return;
            var flash = GameObject.CreatePrimitive(PrimitiveType.Quad);
            flash.name = "MuzzleFlash";
            Destroy(flash.GetComponent<Collider>());
            // pivot aligned with the gun (muzzle markers may be rotated/scaled with the model)
            var pivot = new GameObject("FlashPivot").transform;
            pivot.SetParent(g.muzzle, false);
            pivot.rotation = g.root.rotation;
            float ls = Mathf.Max(0.0001f, g.muzzle.lossyScale.x);
            pivot.localScale = Vector3.one / ls;
            flash.transform.SetParent(pivot, false);
            flash.transform.localPosition = new Vector3(0, 0, 0.06f);
            flash.transform.localScale = Vector3.one * 0.22f;
            var r = flash.GetComponent<Renderer>();
            r.sharedMaterial = FxManager.I.FlashMaterial(new Color(1f, 0.85f, 0.5f, 1f));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            flash.SetActive(false);
            g.flash = flash;

            var lgo = new GameObject("MuzzleLight");
            lgo.transform.SetParent(pivot, false);
            lgo.transform.localPosition = new Vector3(0, 0, 0.1f);
            var light = lgo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 6f;
            light.intensity = 3f;
            light.color = new Color(1f, 0.75f, 0.4f);
            light.shadows = LightShadows.None;
            light.enabled = false;
            g.flashLight = light;
        }

        /// <summary>Once per shot: kick, slide, muzzle flash, start the action cycling, eject the casing. Returns the gun that fired.</summary>
        GunVisual PlayGunFx(byte hand)
        {
            var fx = FxManager.I;
            var g = hand == 1 && Left != null ? Left : Right;
            var def = Definition;
            if (g != null)
            {
                g.kick = 1f;
                g.slideT = 1f;
                if (def.cockTime > 0f && g.HasAction)
                {
                    // wait cockDelay, then the lever / pump animation plays over cockTime
                    g.cockDelay = Mathf.Max(0f, def.cockDelay);
                    g.cockStart = Time.time + g.cockDelay;
                    g.cockDuration = def.cockTime;
                }
                if (g.flash != null && _pc.Visible)
                {
                    g.flash.SetActive(true);
                    g.flash.transform.localRotation = Quaternion.Euler(0, 0, UnityEngine.Random.Range(0, 360f));
                    g.flashLight.enabled = true;
                    g.flashUntil = Time.time + 0.045f;
                }
                // lever / pump guns throw the casing (shell) when the action opens, not on the shot
                if (g.eject != null && _pc.Visible)
                {
                    float delay = def.cockTime > 0f && g.HasAction ? g.cockDelay + g.cockDuration * 0.4f : 0f;
                    var eg = g;
                    float side = g == Left ? -1f : 1f; // the left gun throws its casings to the left
                    void Eject()
                    {
                        if (eg.eject == null || !eg.root.gameObject.activeInHierarchy) return;
                        Transform axes = eg.root;
                        Vector3 v = axes.right * (side * UnityEngine.Random.Range(1.6f, 2.4f)) + axes.up * UnityEngine.Random.Range(1.2f, 2f) + _pc.ViewVelocity;
                        fx.Casing(eg.eject.position, axes.rotation, v, casingMaterial);
                    }
                    if (delay > 0f) fx.After(delay, Eject); else Eject();
                }
            }
            return g;
        }

        /// <summary>One bullet's path: pts[start .. start+count-1]. The first segment is drawn from the muzzle.</summary>
        void PlayTracers(List<Vector3> pts, List<Vector3> nrm, List<byte> kinds, List<Transform> surfaces, int start, int count, Transform muzzleT)
        {
            var fx = FxManager.I;
            // segments draw one after another; impact effects land when the trail arrives
            float arrive = 0f;
            for (int i = start + 1; i < start + count; i++)
            {
                Vector3 a = i == start + 1 && muzzleT != null && _pc.Visible ? muzzleT.position : pts[i - 1];
                Vector3 b = pts[i];
                Vector3 n = nrm[i];
                Transform surf = surfaces != null && i < surfaces.Count ? surfaces[i] : null;
                arrive = fx.Tracer(a, b, true, arrive);
                switch (kinds[i])
                {
                    case BulletSim.KRicochet: fx.After(arrive, () => fx.Ricochet(b, n, surf)); break;
                    case BulletSim.KImpact: fx.After(arrive, () => fx.Impact(b, n, surf)); break;
                    case BulletSim.KPlayer: fx.After(arrive, () => fx.Blood(b, -n)); break;                }
            }
        }

        void AnimateSlide(GunVisual g)
        {
            if (g.slide == null) return;
            g.slideT = Mathf.MoveTowards(g.slideT, 0f, Time.deltaTime / Mathf.Max(0.01f, slideReturnTime));
            // snaps back instantly, eases forward
            float t = g.slideT * g.slideT;
            Vector3 backLocal = g.slide.parent != null
                ? g.slide.parent.InverseTransformVector(-g.root.forward * slideTravel)
                : -g.root.forward * slideTravel;
            g.slide.localPosition = g.slideRest + backLocal * t;
        }

        /// <summary>Swings the LeverAction open and shut around LeverActionFulcrum after each shot.</summary>
        void AnimateLever(GunVisual g)
        {
            if (g.lever == null) return;
            float t = g.cockDuration > 0f ? (Time.time - g.cockStart) / g.cockDuration : 1f;
            float open = 0f;
            if (t >= 0f && t < 1f)
            {
                // snap open (first 40%), brief hold, then close
                open = t < 0.4f ? Mathf.SmoothStep(0, 1, t / 0.4f)
                     : t < 0.5f ? 1f
                     : Mathf.SmoothStep(1, 0, (t - 0.5f) / 0.5f);
            }
            g.LeverOpen = open;
            g.LeverAngle = open * leverOpenAngle;

            g.lever.localPosition = g.leverRestPos;
            g.lever.localRotation = g.leverRestRot;
            if (open <= 0f || g.fulcrum == null) return;
            if (g.fulcrum.parent == g.lever.parent)
            {
                // rotate in the shared parent's space so it's independent of where the gun is this frame
                Vector3 pivot = g.fulcrum.localPosition;
                Quaternion rot = Quaternion.AngleAxis(g.LeverAngle, g.fulcrum.localRotation * Vector3.forward);
                g.lever.localPosition = pivot + rot * (g.leverRestPos - pivot);
                g.lever.localRotation = rot * g.leverRestRot;
            }
            else g.lever.RotateAround(g.fulcrum.position, g.fulcrum.forward, g.LeverAngle);
        }

        /// <summary>Racks the pump Handle straight back along the barrel and forward again after each shot.</summary>
        void AnimatePump(GunVisual g)
        {
            if (g.pump == null) return;
            float t = g.cockDuration > 0f ? (Time.time - g.cockStart) / g.cockDuration : 1f;
            float open = 0f;
            if (t >= 0f && t < 1f)
            {
                // snap back (first 40%), brief hold, then shove forward
                open = t < 0.4f ? Mathf.SmoothStep(0, 1, t / 0.4f)
                     : t < 0.5f ? 1f
                     : Mathf.SmoothStep(1, 0, (t - 0.5f) / 0.5f);
            }
            g.PumpOpen = open;
            Vector3 backLocal = g.pump.parent != null
                ? g.pump.parent.InverseTransformVector(-g.root.forward * pumpTravel)
                : -g.root.forward * pumpTravel;
            g.pump.localPosition = g.pumpRest + backLocal * open;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var f = FindDeep(c, name);
                if (f != null) return f;
            }
            return null;
        }
    }
}
