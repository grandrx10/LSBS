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
        public int AimedSlot = -1; // what spectating would take over
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

            if (kb.digit1Key.wasPressedThisFrame) Sim.PlayerDeploy = Sim.PlayerDeploy == Team.Green ? -1 : Team.Green;
            if (kb.digit2Key.wasPressedThisFrame) Sim.PlayerDeploy = Sim.PlayerDeploy == Team.Grey ? -1 : Team.Grey;

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
            Vector3 fwd = rot * Vector3.forward;
            float best = 0.985f;
            for (int i = 0; i < Sim.HighSlot; i++)
            {
                var s = Sim.Slots[i];
                if (!s.Alive) continue;
                Vector3 to = (Vector3)s.Pos + Vector3.up - _pos;
                float len = to.magnitude;
                if (len > 140f || len < 0.5f) continue;
                float dot = Vector3.Dot(to / len, fwd) - len * 0.00002f;
                if (dot > best) { best = dot; AimedSlot = i; }
            }
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
        }

        public void Leave(bool died)
        {
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

            float2 wish = 0;
            if (kb.wKey.isPressed) wish.y += 1;
            if (kb.sKey.isPressed) wish.y -= 1;
            if (kb.dKey.isPressed) wish.x += 1;
            if (kb.aKey.isPressed) wish.x -= 1;
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
                gun.Timer = 0.5f * (Gun.Magazine(Class) - gun.Ammo);
            }
            // grenades, and calling the guns
            if (!busy && gun.Phase != GunPhase.Firing)
            {
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
                if (!s.Alive || i == Slot) continue;
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

        public bool CanCall => Slot >= 0 && !Sim.IsCalling(Slot) && Sim.Teams[Sim.Slots[Slot].Team].GunsBusy < Sim.T.gunsPerTeam;

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
            bool shotgun = Class == SoldierClass.Sweeper;
            var t = Sim.T;
            float spread = shotgun ? 0.045f : (Aiming ? 0.0008f : 0.014f) + moving * (Aiming ? 0.0015f : 0.004f);
            Vector3 from = _cam.transform.position;
            byte team = Sim.Slots[Slot].Team;
            Vector3 muzzle = from + Quaternion.Euler(_pitch, _yaw, 0) * new Vector3(0.1f, -0.1f, 1.0f);
            var hits = new System.Collections.Generic.Dictionary<int, ShotEvent>();
            Vector3 end = from;
            int rounds = shotgun ? t.pellets : 1;
            for (int n = 0; n < rounds; n++)
            {
                Vector3 dir = Quaternion.Euler(_pitch, _yaw, 0) * (Vector3.forward + (Vector3)UnityEngine.Random.insideUnitCircle * spread);
                dir.Normalize();
                float reach = Reach(from, dir, shotgun ? 70f : 260f, out _);
                int hit = -1;
                byte region = 0;
                for (int i = 0; i < Sim.HighSlot; i++)
                {
                    var s = Sim.Slots[i];
                    if (!s.Alive || s.Team == team || i == Slot) continue;
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
                float damage = (shotgun ? t.pelletDamage : t.damage) * (region == 1 ? t.headMultiplier : region == 2 ? t.limbMultiplier : 1f);
                hits.TryGetValue(hit, out var ev);
                ev = new ShotEvent
                {
                    Shooter = Slot, ShooterGen = _gen, From = muzzle, To = end, Team = team, Target = hit, TargetGen = Sim.Slots[hit].Gen, Hit = true,
                    Region = region == 1 || ev.Region == 1 ? (byte)1 : region, Damage = ev.Damage + damage, Pellets = (byte)(ev.Pellets + 1), Kind = shotgun ? ShotKind.Shotgun : ShotKind.Rifle,
                };
                hits[hit] = ev;
            }
            if (hits.Count == 0) Sim.Shots.Enqueue(new ShotEvent { Shooter = Slot, ShooterGen = _gen, Target = -1, From = muzzle, To = end, Team = team, Kind = shotgun ? ShotKind.Shotgun : ShotKind.Rifle });
            else HitMark = 1f;
            foreach (var ev in hits.Values) Sim.Shots.Enqueue(ev);
            _kick = 1f;
            _pitch -= shotgun ? 3f : 2.2f;
            _yaw += UnityEngine.Random.Range(-0.4f, 0.4f);
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
            if (Now == Mode.Soldier)
            {
                // where this body's eye is: carried round the chest by the aim, exactly as the body and rifle are
                float k = Mathf.SmoothStep(0, 1, _kneelK);
                Vector3 eye = Vector3.Lerp(EyeOf(Clip.StandAim), EyeOf(Clip.KneelAim), k), pivot = Vector3.Lerp(PivotOf(Clip.StandAim), PivotOf(Clip.KneelAim), k);
                var yaw = Quaternion.Euler(0, _yaw, 0);
                at += yaw * (pivot + Quaternion.Euler(_pitch, 0, 0) * (eye - pivot)) * _height;
                // down the sights, the eye comes in over the rifle
                at += rot * Vector3.Lerp(Vector3.zero, new Vector3(0.11f, -0.066f, -0.02f), _aimK);
            }
            _cam.transform.SetPositionAndRotation(at, rot);
        }
    }
}
