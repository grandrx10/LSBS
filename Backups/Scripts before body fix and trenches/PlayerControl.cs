using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LSBS
{
    /// <summary>
    /// The person playing: flying over the battle, or inside one of the soldiers. Taking a soldier over makes them their
    /// squad's captain; the rest of the squad follows.
    /// </summary>
    public unsafe class PlayerControl
    {
        public enum Mode { Spectate, Soldier, Dead }

        readonly Battle _b;
        readonly Camera _cam;
        BattleSystem Sim => _b.Sim;

        public Mode Now = Mode.Spectate;
        public int Slot = -1;
        int _gen;
        float _yaw, _pitch, _fov = 70f, _kick, _bobT;
        Vector3 _pos;
        bool _crouch, _look, _menuWas;
        float2 _vel;
        float _kneelK, _aimK, _ground;
        public float HitMark, HurtFlash;
        public float LastHealth = 100f;
        public int AimedSlot = -1, AimedTank = -1; // what spectating would take over
        public bool Aiming { get; private set; }
        public SoldierClass Class { get; private set; }
        float _height = 1f;

        public PlayerControl(Battle b, Camera cam)
        {
            _b = b; _cam = cam;
            _pos = new Vector3(-70f, 26f, -60f);
            _yaw = 55f; _pitch = 18f;
        }

        public int HideHead => Now == Mode.Soldier ? Slot : -1;
        public bool InSoldier => Now == Mode.Soldier;

        public void Place(Vector3 pos, float yaw, float pitch) { _pos = pos; _yaw = yaw; _pitch = pitch; }

        Entity E => Sim.SlotEntities[Slot];
        EntityManager Em => Sim.EntityManager;
        bool Alive => Slot >= 0 && Sim.Slots[Slot].Alive && Sim.Slots[Slot].Gen == _gen && Em.Exists(E);

        // ------------------------------------------------------------------ before the battle's frame

        public void Before(float dt)
        {
            if (Now == Mode.Soldier && !Alive) Leave(true);
            if (Sim.PlayerDeployedSlot >= 0)
            {
                int s = Sim.PlayerDeployedSlot;
                Sim.PlayerDeployedSlot = -1;
                if (Sim.Slots[s].Alive) { if (Now == Mode.Soldier) Leave(false); Take(s); }
            }
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (_b.MenuOpen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _menuWas = true;
                return;
            }
            // the click that closed the menu isn't a shot
            if (_menuWas) { _menuWas = false; _look = true; return; }
            if (mouse.leftButton.wasPressedThisFrame && !_look && !_b.PanelHasMouse) _look = true;
            bool menu = _b.PanelOpen;
            bool locked = _look && !menu;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
            if (locked)
            {
                float sens = 0.09f * Options.Sensitivity * (_fov / 70f);
                Vector2 d = mouse.delta.ReadValue() * sens;
                _yaw += d.x;
                _pitch = Mathf.Clamp(_pitch - d.y, -80f, 80f);
            }
            HitMark = Mathf.Max(0f, HitMark - dt * 3.5f);
            HurtFlash = Mathf.Max(0f, HurtFlash - dt * 1.4f);
            _kick = Mathf.Lerp(_kick, 0f, 1f - Mathf.Exp(-9f * dt));

            if (InTankNow) { }
            else if (kb.digit1Key.wasPressedThisFrame) Sim.PlayerDeploy = Sim.PlayerDeploy == Team.Green ? -1 : Team.Green;
            if (!InTankNow && kb.digit2Key.wasPressedThisFrame) Sim.PlayerDeploy = Sim.PlayerDeploy == Team.Grey ? -1 : Team.Grey;

            if (Now == Mode.Dead) Dead(dt, kb);
            else if (Now == Mode.Spectate) Spectate(dt, kb, mouse);
            else Soldier(dt, kb, mouse);
        }

        void Spectate(float dt, Keyboard kb, Mouse mouse)
        {
            Vector3 move = Vector3.zero;
            var rot = Quaternion.Euler(_pitch, _yaw, 0);
            if (kb.wKey.isPressed) move += rot * Vector3.forward;
            if (kb.sKey.isPressed) move -= rot * Vector3.forward;
            if (kb.dKey.isPressed) move += rot * Vector3.right;
            if (kb.aKey.isPressed) move -= rot * Vector3.right;
            if (kb.spaceKey.isPressed) move += Vector3.up;
            if (kb.leftCtrlKey.isPressed) move -= Vector3.up;
            float speed = kb.leftShiftKey.isPressed ? 60f : 20f;
            _pos += move * (speed * dt);
            _pos.x = Mathf.Clamp(_pos.x, -260f, 260f);
            _pos.z = Mathf.Clamp(_pos.z, -150f, 150f);
            float ground = Sim.Map.Height(new float2(_pos.x, _pos.z));
            _pos.y = Mathf.Clamp(_pos.y, ground + 1.2f, 140f);

            // whoever's nearest the middle of the screen
            AimedSlot = -1;
            AimedTank = -1;
            Vector3 fwd = rot * Vector3.forward;
            float best = 0.985f;
            for (int i = 0; i < Sim.Tanks.Count; i++)
            {
                var t = Sim.Tanks[i];
                if (t.Wreck || t.Crew.Count == 0) continue;
                Vector3 to = (Vector3)t.Pos + Vector3.up * 1.5f - _pos;
                float len = to.magnitude;
                if (len > 180f) continue;
                // it's a big thing to look at
                float wide = Mathf.Cos(Mathf.Atan2(3.2f, len));
                float dot = Vector3.Dot(to / len, fwd);
                if (dot > Mathf.Min(wide, 0.995f)) { AimedTank = i; best = Mathf.Max(best, 0.9995f); }
            }
            for (int i = 0; i < Sim.HighSlot; i++)
            {
                var s = Sim.Slots[i];
                if (!s.Alive || s.Inside) continue;
                Vector3 to = (Vector3)s.Pos + Vector3.up - _pos;
                float len = to.magnitude;
                if (len > 140f || len < 0.5f) continue;
                float dot = Vector3.Dot(to / len, fwd) - len * 0.00002f;
                if (dot > best) { best = dot; AimedSlot = i; }
            }
            if (AimedSlot >= 0) AimedTank = -1;
            if (kb.fKey.wasPressedThisFrame && AimedTank >= 0) { Take(Sim.Tanks[AimedTank].Crew[0].slot); return; }
            if (kb.fKey.wasPressedThisFrame && AimedSlot >= 0) Take(AimedSlot);
        }

        // ------------------------------------------------------------------ being a soldier

        public void Take(int slot)
        {
            var st = Sim.Slots[slot];
            if (!st.Alive) return;
            Slot = slot;
            _gen = st.Gen;
            var e = Sim.SlotEntities[slot];
            var s = Em.GetComponentData<Soldier>(e);
            s.Player = true;
            Class = s.Class;
            _height = s.Height;
            Em.SetComponentData(e, s);
            st.Player = true;
            Sim.Slots[slot] = st;
            var q = Sim.Squads[s.Squad];
            q.Captain = s.Member;
            q.PlayerLed = true;
            q.PlayerHold = false;
            Sim.Squads[s.Squad] = q;
            Sim.PlayerSquad = s.Squad;
            _yaw = st.Yaw * Mathf.Rad2Deg;
            _pitch = 0f;
            _vel = 0;
            _crouch = false;
            LastHealth = Em.GetComponentData<Health>(e).Value;
            Now = Mode.Soldier;
            _look = true;
            Roster.Clear();
            unsafe
            {
                for (int m = 0; m < Squad.Size; m++)
                {
                    int mate = q.Members[m];
                    if (mate >= 0 && mate != slot) Roster.Add((mate, Sim.Slots[mate].Gen, Sim.Slots[mate].Class));
                }
            }
            brainOff(e);
            int tank = Em.GetComponentData<Ability>(e).Tank;
            if (tank >= 0 && tank < Sim.Tanks.Count) { Sim.Tanks[tank].Player = slot; Sim.Tanks[tank].Seat = 0; _tankYaw = Sim.Tanks[tank].Yaw; }
        }

        public void Leave(bool died)
        {
            Progress = -1f;
            NearMaxim = -1;
            if (Slot >= 0 && !died && Alive)
            {
                var s = Em.GetComponentData<Soldier>(E);
                s.Player = false;
                Em.SetComponentData(E, s);
                var q = Sim.Squads[s.Squad];
                q.PlayerLed = false;
                Sim.Squads[s.Squad] = q;
            }
            Sim.PlayerSquad = -1;
            _pos = _cam.transform.position + Vector3.up * (died ? 0.6f : 1.2f);
            Slot = -1;
            AimedSlot = -1;
            Now = Mode.Spectate;
            if (!died) return;
            HurtFlash = 1f;
            Now = Mode.Dead;
            _deadFor = 0f;
            // back from where they fell, and a little up
            _deadAt = _b.CorpsePos(out bool any);
            if (!any) _deadAt = _pos;
            Vector3 back = Quaternion.Euler(0, _yaw, 0) * new Vector3(0.6f, 0f, -3.4f);
            _pos = _deadAt + back + Vector3.up * 2.1f;
        }

        public const float DeadTime = 4f;
        float _deadFor;
        Vector3 _deadAt;
        public bool IsDead => Now == Mode.Dead;

        /// <summary>Looking on at what's left.</summary>
        void Dead(float dt, Keyboard kb)
        {
            _deadFor += dt;
            var target = _b.CorpsePos(out bool any);
            if (any) _deadAt = Vector3.Lerp(_deadAt, target, 1f - Mathf.Exp(-8f * dt));
            // keep it in reach if it's been thrown
            Vector3 off = _pos - _deadAt;
            off.y = 0f;
            if (off.magnitude > 6f) _pos += (_deadAt + off.normalized * 6f + Vector3.up * 2.2f - _pos) * (1f - Mathf.Exp(-3f * dt));
            _pos.y = Mathf.Max(_pos.y, Sim.Map.Height(new float2(_pos.x, _pos.z)) + 0.7f);
            var look = Quaternion.LookRotation(_deadAt + Vector3.up * 0.2f - _pos).eulerAngles;
            _yaw = look.y;
            _pitch = look.x > 180f ? look.x - 360f : look.x;
            if (_deadFor < DeadTime && !(_deadFor > 1f && kb.spaceKey.wasPressedThisFrame)) return;
            Now = Mode.Spectate;
        }

        void Soldier(float dt, Keyboard kb, Mouse mouse)
        {
            if (kb.qKey.wasPressedThisFrame) { Leave(false); return; }
            var e = E;
            var body = Em.GetComponentData<Body>(e);
            var brain = Em.GetComponentData<Brain>(e);
            var gun = Em.GetComponentData<Gun>(e);
            var anim = Em.GetComponentData<Anim>(e);
            var ability = Em.GetComponentData<Ability>(e);
            bool busy = ability.Act != Act.None;
            Progress = ability.Act == Act.Setup || ability.Act == Act.Pack ? Mathf.Clamp01(ability.Timer / Ability.Length(ability.Act)) : -1f;
            float health = Em.GetComponentData<Health>(e).Value;
            if (health < LastHealth) HurtFlash = 1f;
            LastHealth = health;

            if (kb.cKey.wasPressedThisFrame || kb.leftCtrlKey.wasPressedThisFrame) _crouch = !_crouch;
            if (kb.gKey.wasPressedThisFrame)
            {
                var q = Sim.Squads[Sim.Slots[Slot].Squad];
                q.PlayerHold = !q.PlayerHold;
                Sim.Squads[Sim.Slots[Slot].Squad] = q;
            }
            Aiming = _look && mouse.rightButton.isPressed && gun.Phase != GunPhase.Reloading;
            _fov = Mathf.Lerp(_fov, Aiming ? Options.Fov * 0.57f : Options.Fov, 1f - Mathf.Exp(-12f * dt));

            if (ability.Tank >= 0 && ability.Tank < Sim.Tanks.Count) { InTank(dt, kb, mouse, ability.Tank); return; }
            if (ability.Maxim >= 0 && brain.Mounted) { OnMaxim(dt, kb, mouse, e, ability, body); return; }
            float2 wish = 0;
            if (ability.Act == Act.Setup || ability.Act == Act.Pack) { }
            else if (kb.wKey.isPressed) wish.y += 1;
            if (kb.sKey.isPressed && !busyHands(ability)) wish.y -= 1;
            if (kb.dKey.isPressed && !busyHands(ability)) wish.x += 1;
            if (kb.aKey.isPressed && !busyHands(ability)) wish.x -= 1;
            wish = math.normalizesafe(wish);
            bool sprint = kb.leftShiftKey.isPressed && wish.y > 0 && !Aiming && !_crouch;
            float speed = _crouch ? 1.9f : Aiming ? 2.0f : sprint ? 6.2f : 4.2f;
            float ry = _yaw * Mathf.Deg2Rad;
            float2 fwd = new(math.sin(ry), math.cos(ry)), right = new(fwd.y, -fwd.x);
            float2 want = (fwd * wish.y + right * wish.x) * speed;
            float2 p = body.Pos.xz;
            if (math.lengthsq(want) > 0.01f)
            {
                float slope = (Sim.Map.Height(p + math.normalize(want) * 0.6f) - body.Pos.y) / 0.6f;
                want /= 1f + math.max(0f, slope) * 0.7f + Sim.Map.Rough[MapView.Index(p)] * 0.3f;
            }
            _vel = math.lerp(_vel, want, 1f - math.exp(-11f * dt));
            if (math.lengthsq(_vel) < 0.02f) _vel = 0;
            float2 next = p + _vel * dt;
            if (Sim.Map.IsBlocked(next))
            {
                float2 nx = new(next.x, p.y), ny = new(p.x, next.y);
                next = !Sim.Map.IsBlocked(nx) ? nx : !Sim.Map.IsBlocked(ny) ? ny : Sim.Map.IsBlocked(p) ? next : p;
            }
            next = MapView.Clamp(next);
            body.Pos = new float3(next.x, Sim.Map.Height(next), next.y);
            body.Vel = _vel;
            body.Yaw = ry;
            body.Pitch = math.lerp(body.Pitch, _pitch * Mathf.Deg2Rad, 1f - math.exp(-30f * dt));
            brain.Kneel = _crouch;

            _kneelK = Mathf.MoveTowards(_kneelK, _crouch ? 1f : 0f, dt * 5f);
            _aimK = Mathf.Lerp(_aimK, Aiming ? 1f : 0f, 1f - Mathf.Exp(-14f * dt));
            float moving = math.length(_vel);
            _bobT += moving * dt * 1.7f;
            float bob = Mathf.Sin(_bobT * 2f) * 0.018f * Mathf.Clamp01(moving / 4f) * (Aiming ? 0.3f : 1f);
            // settle on the ground smoothly over the metre grid
            _ground = Mathf.Abs(_ground - body.Pos.y) > 1f ? body.Pos.y : Mathf.Lerp(_ground, body.Pos.y, 1f - Mathf.Exp(-30f * dt));
            _pos = new Vector3(body.Pos.x, _ground + bob, body.Pos.z);

            if (kb.rKey.wasPressedThisFrame && !busy && gun.Phase == GunPhase.Ready && gun.Ammo < Gun.Magazine(Class))
            {
                gun.Phase = GunPhase.Reloading;
                gun.Timer = Class == SoldierClass.Gunner ? 1.5f : 0.5f * (Gun.Magazine(Class) - gun.Ammo);
            }
            // a machine gun to hand, then grenades, calling the guns, setting one up
            NearMaxim = busy ? -1 : Sim.FreeMaxim(p, Sim.Slots[Slot].Team, 2.2f);
            if (!busy && gun.Phase != GunPhase.Firing)
            {
                if (NearMaxim >= 0 && kb.eKey.wasPressedThisFrame)
                {
                    Em.SetComponentData(e, body);
                    Em.SetComponentData(e, brain);
                    Sim.Mount(Slot, NearMaxim);
                    _yaw = Sim.Maxims[NearMaxim].Yaw * Mathf.Rad2Deg;
                    _pitch = 0f;
                    _vel = 0;
                    _crouch = false;
                    return;
                }
                NearTank = NearMaxim >= 0 ? -1 : Sim.TankBeside(p, Sim.Slots[Slot].Team, 2.2f);
                if (NearTank >= 0 && kb.eKey.wasPressedThisFrame)
                {
                    Em.SetComponentData(e, body);
                    Em.SetComponentData(e, brain);
                    Sim.PlayerBoards(Slot, NearTank, 0);
                    _tankYaw = Sim.Tanks[NearTank].Yaw;
                    _yaw = _tankYaw * Mathf.Rad2Deg;
                    _pitch = 8f;
                    _vel = 0;
                    _crouch = false;
                    NearTank = -1;
                    return;
                }
                if (NearMaxim >= 0 && Class == SoldierClass.Gunner && !ability.Carrying && kb.xKey.wasPressedThisFrame)
                {
                    Sim.Mount(Slot, NearMaxim);
                    ability = Em.GetComponentData<Ability>(e);
                    ability.Act = Act.Pack; ability.Timer = 0f; ability.Done = false; ability.At = Sim.Maxims[NearMaxim].Pos;
                    Em.SetComponentData(e, ability);
                    return;
                }
                if (Class == SoldierClass.Sweeper && kb.eKey.wasPressedThisFrame && ability.Grenades > 0)
                {
                    ability.Act = Act.Throw; ability.Timer = 0f; ability.Done = false;
                    ability.At = LookedAt(36f, 30f);
                    busy = true;
                }
                if (Class == SoldierClass.Communicator && kb.eKey.wasPressedThisFrame && CanCall)
                {
                    float3 at = LookedAt(Sim.T.callRange, -1f);
                    if (math.any(at)) { ability.Act = Act.Call; ability.Timer = 0f; ability.Done = false; ability.At = at; busy = true; }
                }
                if (Class == SoldierClass.Gunner && ability.Carrying && kb.eKey.wasPressedThisFrame)
                {
                    Sim.Site(p, ry, out var site, out bool tall);
                    float2 site2 = p + fwd * Sim.MaximBack;
                    bool roofed = (Sim.Map.Cover[MapView.Index(p)] & MapView.Roofed) != 0;
                    if (math.abs(site.y - body.Pos.y) < 0.6f && (roofed || !Sim.Map.IsBlocked(site2)))
                    {
                        ability.Act = Act.Setup; ability.Timer = 0f; ability.Done = false; ability.At = site; ability.Yaw = ry;
                        // kneeling sets it low, standing sets it high
                        ability.Tall = !_crouch;
                        busy = true;
                    }
                }
            }
            if (_look && !busy && mouse.leftButton.wasPressedThisFrame && gun.Phase == GunPhase.Ready)
            {
                if (gun.Ammo > 0)
                {
                    Shoot(body, moving);
                    gun.Ammo--;
                    gun.Phase = GunPhase.Firing;
                    gun.Timer = Gun.FireTime(Class);
                    anim.Time = 0f;
                }
                else { gun.Phase = GunPhase.Reloading; gun.Timer = Gun.ReloadTime(Class); }
            }
            Em.SetComponentData(e, body);
            Em.SetComponentData(e, brain);
            Em.SetComponentData(e, gun);
            Em.SetComponentData(e, anim);
            Em.SetComponentData(e, ability);
        }

        static bool busyHands(in Ability a) => a.Act == Act.Setup || a.Act == Act.Pack;

        /// <summary>A machine gun in reach with nobody on it, or -1; how far through setting one up (-1 not at it).</summary>
        public int NearMaxim = -1;
        public float Progress = -1f;
        public bool OnGun => Slot >= 0 && Sim.Slots[Slot].Mounted;
        float _mg;

        public int NearTank = -1;
        float _tankYaw;
        public Tank MyTank
        {
            get
            {
                if (Now != Mode.Soldier || !Alive) return null;
                int t = Em.GetComponentData<Ability>(E).Tank;
                return t >= 0 && t < Sim.Tanks.Count ? Sim.Tanks[t] : null;
            }
        }
        public bool InTankNow => MyTank != null;

        /// <summary>One of a tank's places: driving it and its main gun, or either side gun.</summary>
        void InTank(float dt, Keyboard kb, Mouse mouse, int index)
        {
            var t = Sim.Tanks[index];
            NearMaxim = NearTank = -1;
            if (t.Player != Slot) t.Player = Slot;
            if (kb.eKey.wasPressedThisFrame)
            {
                t.Throttle = t.Steer = 0f;
                t.Trigger = false;
                Sim.Alight(Slot);
                _vel = 0;
                _pos = Sim.Slots[Slot].Pos;
                _ground = _pos.y;
                _pitch = 0f;
                return;
            }
            int seat = t.Seat;
            if (kb.digit1Key.wasPressedThisFrame) seat = 0;
            if (kb.digit2Key.wasPressedThisFrame) seat = 1;
            if (kb.digit3Key.wasPressedThisFrame) seat = 2;
            if (seat != t.Seat)
            {
                t.Seat = seat;
                t.Throttle = t.Steer = 0f;
                t.Trigger = false;
                _yaw = t.Yaw * Mathf.Rad2Deg + (seat == 1 ? -90f : seat == 2 ? 90f : 0f);
                _pitch = seat == 0 ? 8f : 0f;
            }
            float turned = Mathf.DeltaAngle(_tankYaw * Mathf.Rad2Deg, t.Yaw * Mathf.Rad2Deg);
            _tankYaw = t.Yaw;
            Aiming = _look && mouse.rightButton.isPressed;
            _fov = Mathf.Lerp(_fov, Aiming ? Options.Fov * 0.6f : Options.Fov, 1f - Mathf.Exp(-12f * dt));
            if (t.Seat == 0)
            {
                t.Throttle = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
                t.Steer = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
                _pitch = Mathf.Clamp(_pitch, -14f, 40f);
                // lay the gun on whatever's in the middle of the view
                Vector3 from = _cam.transform.position, dir = Quaternion.Euler(_pitch, _yaw, 0) * Vector3.forward;
                float reach = 14f + Reach(from + dir * 14f, dir, 230f, out _);
                Sim.Elevate(t, from + dir * reach);
                t.Trigger = _look && mouse.leftButton.isPressed && t.Reload <= 0f;
            }
            else
            {
                int side = t.Seat - 1;
                var g = t.Guns[side];
                // round with the tank, and no further than the gun goes
                _yaw += turned;
                float off = Mathf.DeltaAngle(t.Yaw * Mathf.Rad2Deg + (side == 0 ? -90f : 90f), _yaw);
                float most = Tank.SideArc * Mathf.Rad2Deg;
                _yaw += Mathf.Clamp(off, -most, most) - off;
                _pitch = Mathf.Clamp(_pitch, -19f, 16f);
                Vector3 l = Quaternion.Inverse(t.Rotation * Quaternion.Euler(0, side == 0 ? -90f : 90f, 0)) * (Quaternion.Euler(_pitch, _yaw, 0) * Vector3.forward);
                g.WantYaw = Mathf.Atan2(l.x, l.z);
                g.WantPitch = -Mathf.Atan2(l.y, new Vector2(l.x, l.z).magnitude);
                _mg -= dt;
                if (kb.rKey.wasPressedThisFrame && g.Reloading <= 0f && g.Ammo < Maxim.Clip) g.Reloading = Maxim.Reload;
                else if (_look && mouse.leftButton.isPressed && g.Reloading <= 0f)
                {
                    if (_mg < -0.1f) _mg = 0f;
                    while (_mg <= 0f && g.Ammo > 0)
                    {
                        _mg += 60f / Maxim.Rpm;
                        g.Ammo--;
                        var aim = t.GunRotation(side);
                        Fire(ShotKind.Maxim, aim, t.GunAt(side) + aim * Vector3.forward * 0.6f, 0.008f);
                        _kick = Mathf.Min(0.45f, _kick + 0.16f);
                    }
                }
            }
        }

        /// <summary>Behind the machine gun: no walking, only bringing it round.</summary>
        void OnMaxim(float dt, Keyboard kb, Mouse mouse, Entity e, Ability ability, Body body)
        {
            int at = ability.Maxim;
            var m = Sim.Maxims[at];
            NearMaxim = -1;
            float off = Mathf.DeltaAngle(m.BaseYaw * Mathf.Rad2Deg, _yaw);
            float most = Maxim.Arc * Mathf.Rad2Deg;
            _yaw = m.BaseYaw * Mathf.Rad2Deg + Mathf.Clamp(off, -most, most);
            _pitch = Mathf.Clamp(_pitch, -16f, 14f);
            m.WantYaw = _yaw * Mathf.Deg2Rad;
            m.Pitch = Mathf.Lerp(m.Pitch, _pitch * Mathf.Deg2Rad, 1f - Mathf.Exp(-14f * dt));
            _mg -= dt;
            bool ready = ability.Act == Act.None && m.Reloading <= 0f;
            if (ready && kb.rKey.wasPressedThisFrame && m.Ammo < Maxim.Clip) m.Reloading = Maxim.Reload;
            else if (ready && _look && mouse.leftButton.isPressed)
            {
                if (_mg < -0.1f) _mg = 0f;
                while (_mg <= 0f && m.Ammo > 0)
                {
                    _mg += 60f / Maxim.Rpm;
                    m.Ammo--;
                    Vector3 muzzle = (Vector3)(m.Pivot + m.Forward * Sim.MaximMuzzle) + Vector3.up * 0.05f;
                    Fire(ShotKind.Maxim, Quaternion.Euler(m.Pitch * Mathf.Rad2Deg, m.Yaw * Mathf.Rad2Deg, 0), muzzle, 0.008f);
                    _kick = Mathf.Min(0.45f, _kick + 0.16f);
                }
            }
            Sim.Maxims[at] = m;
            if (ability.Act == Act.None && kb.eKey.wasPressedThisFrame) { Sim.Dismount(Slot); return; }
            if (ability.Act == Act.None && Class == SoldierClass.Gunner && !ability.Carrying && kb.xKey.wasPressedThisFrame)
            {
                ability.Act = Act.Pack; ability.Timer = 0f; ability.Done = false; ability.At = m.Pos;
                Em.SetComponentData(e, ability);
            }
        }

        public readonly System.Collections.Generic.List<(int slot, int gen, SoldierClass cls)> Roster = new();

        /// <summary>Whoever's taken over has no bunker to mind.</summary>
        void brainOff(Entity e)
        {
            var brain = Em.GetComponentData<Brain>(e);
            brain.OnPost = false;
            Em.SetComponentData(e, brain);
        }

        /// <summary>Who the middle of the screen is on.</summary>
        public int Hovered(float reach)
        {
            Vector3 from = _cam.transform.position, dir = _cam.transform.forward;
            int best = -1;
            float bestOff = 1f;
            for (int i = 0; i < Sim.HighSlot; i++)
            {
                var s = Sim.Slots[i];
                if (!s.Alive || s.Inside || i == Slot) continue;
                Vector3 to = (Vector3)s.Pos + Vector3.up * ((s.Kneel ? 0.85f : 1.1f) * s.Height) - from;
                float along = Vector3.Dot(to, dir);
                if (along < 0.5f || along > reach) continue;
                Vector3 off = to - dir * along;
                float wide = new Vector2(off.x, off.z).magnitude / 0.5f, tall = Mathf.Abs(off.y) / 1.1f;
                float k = Mathf.Max(wide, tall) / (1f + along * 0.012f) + along * 0.0004f;
                if (k < bestOff) { bestOff = k; best = i; }
            }
            if (best < 0) return -1;
            var b = Sim.Slots[best];
            return Sim.Map.Sees(from, b.Pos + new float3(0, 1.6f * b.Height, 0)) ? best : -1;
        }

        public bool CanCall => Slot >= 0 && !Sim.IsCalling(Slot) && Sim.Teams[Sim.Slots[Slot].Team].GunsBusy < Sim.GunsFor(Sim.Slots[Slot].Team);

        /// <summary>The ground along the view, out to reach; if there's none, that far along it (otherwise: nowhere).</summary>
        float3 LookedAt(float reach, float otherwise)
        {
            Vector3 from = _cam.transform.position, dir = Quaternion.Euler(_pitch, _yaw, 0) * Vector3.forward;
            float d = Reach(from, dir, reach, out bool hit);
            if (hit) return from + dir * d;
            if (otherwise < 0f) return 0;
            float3 p = from + dir * otherwise;
            p.y = Sim.Map.Height(p.xz);
            return p;
        }

        float Reach(Vector3 from, Vector3 dir, float reach, out bool hit)
        {
            hit = false;
            for (float d = 0.5f; d < reach; d += 0.35f)
            {
                float3 q = from + dir * d;
                if (math.abs(q.x) > MapView.HalfW || math.abs(q.z) > MapView.HalfH) return d;
                if (q.y < Sim.Map.Height(q.xz) || q.y < Sim.Map.PropTop[MapView.Index(q.xz)]) { hit = true; return d; }
            }
            return reach;
        }

        /// <summary>A real shot: wherever the barrel's pointing, against bodies as they stand. Nine of them, for a shotgun.</summary>
        void Shoot(in Body body, float moving)
        {
            var kind = Class == SoldierClass.Sweeper ? ShotKind.Shotgun : Class == SoldierClass.Gunner ? ShotKind.Pistol : ShotKind.Rifle;
            var aim = Quaternion.Euler(_pitch, _yaw, 0);
            float spread = kind == ShotKind.Shotgun ? 0.045f : (Aiming ? 0.0008f : 0.014f) + moving * (Aiming ? 0.0015f : 0.004f);
            Fire(kind, aim, _cam.transform.position + aim * new Vector3(0.1f, -0.1f, 1.0f), spread);
            _kick = kind == ShotKind.Pistol ? 0.6f : 1f;
            _pitch -= kind == ShotKind.Shotgun ? 3f : kind == ShotKind.Pistol ? 1.6f : 2.2f;
            _yaw += UnityEngine.Random.Range(-0.4f, 0.4f);
        }

        void Fire(ShotKind kind, Quaternion aim, Vector3 muzzle, float spread)
        {
            bool shotgun = kind == ShotKind.Shotgun;
            var t = Sim.T;
            // a machine gun shoots down its own barrel
            Vector3 from = kind == ShotKind.Maxim ? muzzle - aim * Vector3.forward * 0.5f : _cam.transform.position;
            byte team = Sim.Slots[Slot].Team;
            var hits = new System.Collections.Generic.Dictionary<int, ShotEvent>();
            Vector3 end = from;
            int rounds = shotgun ? t.pellets : 1;
            for (int n = 0; n < rounds; n++)
            {
                Vector3 dir = aim * (Vector3.forward + (Vector3)UnityEngine.Random.insideUnitCircle * spread);
                dir.Normalize();
                float reach = Reach(from, dir, shotgun ? 70f : 260f, out _);
                int hit = -1;
                byte region = 0;
                for (int i = 0; i < Sim.HighSlot; i++)
                {
                    var s = Sim.Slots[i];
                    if (!s.Alive || s.Inside || s.Team == team || i == Slot) continue;
                    Vector3 feet = s.Pos;
                    if ((feet - from).sqrMagnitude > (reach + 2f) * (reach + 2f)) continue;
                    float drop = s.Kneel ? 0.42f : 0f, h = s.Height;
                    Test(feet + new Vector3(0, (1.84f - drop) * h, 0), feet + new Vector3(0, (1.98f - drop) * h, 0), 0.16f, 1, i);
                    Test(feet + new Vector3(0, (1.05f - drop) * h, 0), feet + new Vector3(0, (1.58f - drop) * h, 0), 0.25f, 0, i);
                    Test(feet + new Vector3(0, 0.1f, 0), feet + new Vector3(0, (0.95f - drop) * h, 0), 0.2f, 2, i);
                }

                void Test(Vector3 a, Vector3 b, float r, byte reg, int slot)
                {
                    // nearest approach of the shot to the segment
                    Vector3 u = dir, v = b - a, w = from - a;
                    float bb = Vector3.Dot(u, v), c = Vector3.Dot(v, v), d = Vector3.Dot(u, w), e = Vector3.Dot(v, w);
                    float den = c - bb * bb;
                    float s = den < 1e-6f ? 0f : (bb * e - c * d) / den;
                    float q = Mathf.Clamp01((e + bb * s) / c);
                    s = Mathf.Max(0f, Vector3.Dot(a + v * q - from, u));
                    if (s >= reach || (from + u * s - (a + v * q)).sqrMagnitude > r * r) return;
                    reach = s; hit = slot; region = reg;
                }

                end = from + dir * reach;
                if (hit < 0) continue;
                float damage = (shotgun ? t.pelletDamage : kind == ShotKind.Pistol ? t.pistolDamage : t.damage) * (region == 1 ? t.headMultiplier : region == 2 ? t.limbMultiplier : 1f);
                hits.TryGetValue(hit, out var ev);
                ev = new ShotEvent
                {
                    Shooter = Slot, ShooterGen = _gen, From = muzzle, To = end, Team = team, Target = hit, TargetGen = Sim.Slots[hit].Gen, Hit = true,
                    Region = region == 1 || ev.Region == 1 ? (byte)1 : region, Damage = ev.Damage + damage, Pellets = (byte)(ev.Pellets + 1), Kind = kind,
                };
                hits[hit] = ev;
            }
            if (hits.Count == 0) Sim.Shots.Enqueue(new ShotEvent { Shooter = Slot, ShooterGen = _gen, Target = -1, From = muzzle, To = end, Team = team, Kind = kind });
            else HitMark = 1f;
            foreach (var ev in hits.Values) Sim.Shots.Enqueue(ev);
        }

        // ------------------------------------------------------------------ after it

        Vector3 PivotOf(int clip) => _b.soldiers.Pose(_b.soldiers.clips[clip].start, _b.soldiers.chestBone).MultiplyPoint3x4(_b.soldiers.chestPivot);
        /// <summary>The rifle is posed from the eye, so the eye is found from the rifle.</summary>
        Vector3 EyeOf(int clip) => (Vector3)_b.soldiers.Pose(_b.soldiers.clips[clip].start, _b.soldiers.Rifle).GetColumn(3) - new Vector3(0.11f, -0.16f, 0.36f);

        public void After()
        {
            if (Now == Mode.Soldier && !Alive) Leave(true);
            _cam.fieldOfView = Now == Mode.Soldier ? _fov : Mathf.Max(50f, Options.Fov - 14f);
            _cam.nearClipPlane = Now == Mode.Soldier ? 0.05f : 0.3f;
            var rot = Quaternion.Euler(_pitch - (Now == Mode.Soldier ? _kick * 2f : 0f), _yaw, 0);
            Vector3 at = _pos;
            float shake = Options.Shake ? _b.Shake : 0f;
            if (shake > 0.01f) rot *= Quaternion.Euler(Mathf.PerlinNoise(Time.time * 23f, 1f) * shake * 3f - shake * 1.5f, Mathf.PerlinNoise(2f, Time.time * 21f) * shake * 3f - shake * 1.5f, Mathf.PerlinNoise(Time.time * 19f, 5f) * shake * 2f - shake);
            var mine = MyTank;
            if (mine != null)
            {
                if (mine.Seat == 0)
                {
                    // out behind it, looking over the top
                    Vector3 mid = (Vector3)mine.Pos + Vector3.up * 3.4f;
                    at = mid - rot * Vector3.forward * 10.5f + Vector3.up * 0.8f;
                    float floor = Sim.Map.Height(new float2(at.x, at.z)) + 0.6f;
                    if (at.y < floor) at.y = floor;
                }
                else
                {
                    var swing = mine.GunRotation(mine.Seat - 1);
                    at = mine.GunAt(mine.Seat - 1) + swing * new Vector3(0f, 0.27f, -0.42f);
                    rot = swing * Quaternion.Euler(-_kick * 0.8f, 0, 0);
                }
                _cam.nearClipPlane = mine.Seat == 0 ? 0.3f : 0.12f;
            }
            else if (Now == Mode.Soldier && OnGun && Em.GetComponentData<Ability>(E).Maxim is int mg && mg >= 0 && mg < Sim.Maxims.Length)
            {
                // over the breech, looking down the barrel
                var m = Sim.Maxims[mg];
                var swing = Quaternion.Euler(m.Pitch * Mathf.Rad2Deg, m.Yaw * Mathf.Rad2Deg, 0);
                at = (Vector3)m.Pivot + swing * new Vector3(0f, 0.30f, -Sim.MaximBack + 0.28f) + (rot * Vector3.forward) * (-0.02f * _kick);
                rot = swing * Quaternion.Euler(-_kick * 0.8f, 0, 0);
            }
            else if (Now == Mode.Soldier)
            {
                // where this body's eye is: carried round the chest by the aim, exactly as the body and rifle are
                float k = Mathf.SmoothStep(0, 1, _kneelK);
                Vector3 eye = Vector3.Lerp(EyeOf(Clip.StandAim), EyeOf(Clip.KneelAim), k), pivot = Vector3.Lerp(PivotOf(Clip.StandAim), PivotOf(Clip.KneelAim), k);
                var yaw = Quaternion.Euler(0, _yaw, 0);
                at += yaw * (pivot + Quaternion.Euler(_pitch, 0, 0) * (eye - pivot)) * _height;
                // down the sights, the eye comes in over the rifle
                at += rot * Vector3.Lerp(Vector3.zero, Class == SoldierClass.Gunner ? new Vector3(0.05f, -0.07f, 0.02f) : new Vector3(0.11f, -0.066f, -0.02f), _aimK);
            }
            _cam.transform.SetPositionAndRotation(at, rot);
        }
    }
}
