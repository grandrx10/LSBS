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
        public enum Mode { Spectate, Soldier }

        readonly Battle _b;
        readonly Camera _cam;
        BattleSystem Sim => _b.Sim;

        public Mode Now = Mode.Spectate;
        public int Slot = -1;
        int _gen;
        float _yaw, _pitch, _fov = 70f, _kick, _bobT;
        Vector3 _pos;
        bool _crouch, _look;
        float2 _vel;
        float _kneelK, _aimK, _ground;
        public float HitMark, HurtFlash;
        public float LastHealth = 100f;
        public int AimedSlot = -1; // what spectating would take over
        public bool Aiming { get; private set; }

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

            if (kb.escapeKey.wasPressedThisFrame) _look = false;
            else if (mouse.leftButton.wasPressedThisFrame && !_look && !_b.PanelHasMouse) _look = true;
            bool menu = _b.PanelOpen;
            bool locked = _look && !menu;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
            if (locked)
            {
                float sens = 0.09f * (_fov / 70f);
                Vector2 d = mouse.delta.ReadValue() * sens;
                _yaw += d.x;
                _pitch = Mathf.Clamp(_pitch - d.y, -80f, 80f);
            }
            HitMark = Mathf.Max(0f, HitMark - dt * 3.5f);
            HurtFlash = Mathf.Max(0f, HurtFlash - dt * 1.4f);
            _kick = Mathf.Lerp(_kick, 0f, 1f - Mathf.Exp(-9f * dt));

            if (kb.digit1Key.wasPressedThisFrame) Sim.PlayerDeploy = Sim.PlayerDeploy == Team.Green ? -1 : Team.Green;
            if (kb.digit2Key.wasPressedThisFrame) Sim.PlayerDeploy = Sim.PlayerDeploy == Team.Grey ? -1 : Team.Grey;

            if (Now == Mode.Spectate) Spectate(dt, kb, mouse);
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
            if (died) HurtFlash = 1f;
            Sim.PlayerSquad = -1;
            _pos = _cam.transform.position + Vector3.up * 1.2f;
            Slot = -1;
            Now = Mode.Spectate;
            AimedSlot = -1;
        }

        void Soldier(float dt, Keyboard kb, Mouse mouse)
        {
            if (kb.qKey.wasPressedThisFrame) { Leave(false); return; }
            var e = E;
            var body = Em.GetComponentData<Body>(e);
            var brain = Em.GetComponentData<Brain>(e);
            var gun = Em.GetComponentData<Gun>(e);
            var anim = Em.GetComponentData<Anim>(e);
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
            _fov = Mathf.Lerp(_fov, Aiming ? 42f : 74f, 1f - Mathf.Exp(-12f * dt));

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

            if (kb.rKey.wasPressedThisFrame && gun.Phase == GunPhase.Ready && gun.Ammo < Gun.Magazine)
            {
                gun.Phase = GunPhase.Reloading;
                gun.Timer = 0.5f * (Gun.Magazine - gun.Ammo);
            }
            if (_look && mouse.leftButton.wasPressedThisFrame && gun.Phase == GunPhase.Ready)
            {
                if (gun.Ammo > 0)
                {
                    Shoot(body, moving);
                    gun.Ammo--;
                    gun.Phase = GunPhase.Firing;
                    gun.Timer = Gun.FireTime;
                    anim.Time = 0f;
                }
                else { gun.Phase = GunPhase.Reloading; gun.Timer = Gun.ReloadTime; }
            }
            Em.SetComponentData(e, body);
            Em.SetComponentData(e, brain);
            Em.SetComponentData(e, gun);
            Em.SetComponentData(e, anim);
        }

        /// <summary>A real shot: wherever the barrel's pointing, against bodies as they stand.</summary>
        void Shoot(in Body body, float moving)
        {
            float spread = (Aiming ? 0.0008f : 0.014f) + moving * (Aiming ? 0.0015f : 0.004f);
            Vector3 dir = Quaternion.Euler(_pitch, _yaw, 0) * (Vector3.forward + (Vector3)UnityEngine.Random.insideUnitCircle * spread);
            dir.Normalize();
            Vector3 from = _cam.transform.position;
            float reach = 260f;
            // the ground and everything on it
            for (float d = 0.5f; d < reach; d += 0.35f)
            {
                float3 q = from + dir * d;
                if (math.abs(q.x) > MapView.HalfW || math.abs(q.z) > MapView.HalfH) { reach = d; break; }
                if (q.y < Sim.Map.Height(q.xz) || q.y < Sim.Map.PropTop[MapView.Index(q.xz)]) { reach = d; break; }
            }
            int hit = -1;
            byte region = 0;
            byte team = Sim.Slots[Slot].Team;
            for (int i = 0; i < Sim.HighSlot; i++)
            {
                var s = Sim.Slots[i];
                if (!s.Alive || s.Team == team || i == Slot) continue;
                Vector3 feet = s.Pos;
                if ((feet - from).sqrMagnitude > (reach + 2f) * (reach + 2f)) continue;
                float drop = s.Kneel ? 0.42f : 0f;
                Test(feet + new Vector3(0, 1.84f - drop, 0), feet + new Vector3(0, 1.98f - drop, 0), 0.16f, 1, i);
                Test(feet + new Vector3(0, 1.05f - drop, 0), feet + new Vector3(0, 1.58f - drop, 0), 0.25f, 0, i);
                Test(feet + new Vector3(0, 0.1f, 0), feet + new Vector3(0, 0.95f - drop, 0), 0.2f, 2, i);
            }

            void Test(Vector3 a, Vector3 b, float r, byte reg, int slot)
            {
                // nearest approach of the shot to the segment
                Vector3 u = dir, v = b - a, w = from - a;
                float bb = Vector3.Dot(u, v), c = Vector3.Dot(v, v), d = Vector3.Dot(u, w), e = Vector3.Dot(v, w);
                float den = c - bb * bb;
                float s = den < 1e-6f ? 0f : (bb * e - c * d) / den;
                float t = Mathf.Clamp01((e + bb * s) / c);
                s = Mathf.Max(0f, Vector3.Dot(a + v * t - from, u));
                if (s >= reach || (from + u * s - (a + v * t)).sqrMagnitude > r * r) return;
                reach = s; hit = slot; region = reg;
            }

            Vector3 muzzle = from + Quaternion.Euler(_pitch, _yaw, 0) * new Vector3(0.1f, -0.1f, 1.0f);
            var ev = new ShotEvent { Shooter = Slot, ShooterGen = _gen, Target = -1, From = muzzle, To = from + dir * reach, Team = team };
            if (hit >= 0)
            {
                var t = Sim.T;
                ev.Target = hit; ev.TargetGen = Sim.Slots[hit].Gen; ev.Hit = true; ev.Region = region;
                ev.Damage = region == 2 ? t.damage * t.limbMultiplier : t.damage;
                HitMark = 1f;
            }
            Sim.Shots.Enqueue(ev);
            _kick = 1f;
            _pitch -= 2.2f;
            _yaw += UnityEngine.Random.Range(-0.4f, 0.4f);
        }

        // ------------------------------------------------------------------ after it

        Vector3 PivotOf(int clip) => _b.soldiers.Pose(_b.soldiers.clips[clip].start, _b.soldiers.chestBone).MultiplyPoint3x4(_b.soldiers.chestPivot);
        /// <summary>The rifle is posed from the eye, so the eye is found from the rifle.</summary>
        Vector3 EyeOf(int clip) => (Vector3)_b.soldiers.Pose(_b.soldiers.clips[clip].start, _b.soldiers.Rifle).GetColumn(3) - new Vector3(0.11f, -0.16f, 0.36f);

        public void After()
        {
            if (Now == Mode.Soldier && !Alive) Leave(true);
            _cam.fieldOfView = Now == Mode.Soldier ? _fov : 60f;
            _cam.nearClipPlane = Now == Mode.Soldier ? 0.05f : 0.3f;
            var rot = Quaternion.Euler(_pitch - (Now == Mode.Soldier ? _kick * 2f : 0f), _yaw, 0);
            Vector3 at = _pos;
            if (Now == Mode.Soldier)
            {
                // where this body's eye is: carried round the chest by the aim, exactly as the body and rifle are
                float k = Mathf.SmoothStep(0, 1, _kneelK);
                Vector3 eye = Vector3.Lerp(EyeOf(Clip.StandAim), EyeOf(Clip.KneelAim), k), pivot = Vector3.Lerp(PivotOf(Clip.StandAim), PivotOf(Clip.KneelAim), k);
                var yaw = Quaternion.Euler(0, _yaw, 0);
                at += yaw * (pivot + Quaternion.Euler(_pitch, 0, 0) * (eye - pivot));
                // down the sights, the eye comes in over the rifle
                at += rot * Vector3.Lerp(Vector3.zero, new Vector3(0.11f, -0.066f, -0.02f), _aimK);
            }
            _cam.transform.SetPositionAndRotation(at, rot);
        }
    }
}
