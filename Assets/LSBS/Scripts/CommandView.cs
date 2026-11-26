using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LSBS
{
    /// <summary>
    /// The commander's view: an RTS camera over the field. Left click or drag to pick squads (any one soldier picks their
    /// whole squad), right click to send them somewhere. They make their own way there and fight what they meet.
    /// </summary>
    public unsafe class CommandView
    {
        readonly Battle _b;
        readonly Camera _cam;
        BattleSystem Sim => _b.Sim;

        /// <summary>The side being commanded.</summary>
        public byte Team;
        public readonly List<int> Selected = new();
        /// <summary>Tanks and destroyers picked.</summary>
        public readonly List<Tank> Vehicles = new();
        /// <summary>The next right click sends what's picked by air.</summary>
        public bool AirliftNext;
        /// <summary>A soldier under the cursor, or -1.</summary>
        public int Hover = -1;

        Vector3 _focus;
        float _dist = 90f, _yaw = 90f;
        bool _dragging;
        Vector2 _dragFrom, _dragTo;
        float _clickFlash;
        Vector3 _clickAt;
        bool _set;

        const float MinDist = 18f, MaxDist = 320f;

        public CommandView(Battle b, Camera cam) { _b = b; _cam = cam; }

        /// <summary>Look down on a place, from the side being commanded.</summary>
        public void LookAt(Vector3 at, float yaw)
        {
            _focus = at;
            _yaw = yaw;
            _dist = 110f;
            _set = true;
        }

        public float Pitch => Mathf.Lerp(38f, 68f, Mathf.InverseLerp(MinDist, MaxDist, _dist));
        public Quaternion Rotation => Quaternion.Euler(Pitch, _yaw, 0);
        public Vector3 Eye => _focus - Rotation * Vector3.forward * _dist;

        public void Update(float dt, Keyboard kb, Mouse mouse, out Vector3 pos, out float yaw, out float pitch)
        {
            if (!_set) LookAt(new Vector3(0, Sim.Map.Height(float2.zero), 0), 90f);
            Vector2 cursor = mouse.position.ReadValue();
            bool inside = cursor.x >= 0 && cursor.y >= 0 && cursor.x <= Screen.width && cursor.y <= Screen.height && Application.isFocused;

            // ---- moving the view ----
            float fast = kb.leftShiftKey.isPressed ? 2.4f : 1f;
            float pan = (_dist * 0.9f + 25f) * fast * dt;
            Vector3 fwd = Quaternion.Euler(0, _yaw, 0) * Vector3.forward, right = Quaternion.Euler(0, _yaw, 0) * Vector3.right;
            Vector2 move = Vector2.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += 1;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= 1;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1;
            if (inside && Options.EdgePan && !_dragging)
            {
                const float edge = 6f;
                if (cursor.x < edge) move.x -= 1; else if (cursor.x > Screen.width - edge) move.x += 1;
                if (cursor.y < edge) move.y -= 1; else if (cursor.y > Screen.height - edge) move.y += 1;
            }
            _focus += (fwd * move.y + right * move.x) * pan;
            if (kb.qKey.isPressed) _yaw -= 90f * dt;
            if (kb.eKey.isPressed) _yaw += 90f * dt;
            if (mouse.middleButton.isPressed) _yaw += mouse.delta.ReadValue().x * 0.25f;
            float scroll = mouse.scroll.ReadValue().y;
            if (inside && Mathf.Abs(scroll) > 0.01f) _dist = Mathf.Clamp(_dist * Mathf.Pow(0.88f, Mathf.Sign(scroll)), MinDist, MaxDist);
            _focus.x = Mathf.Clamp(_focus.x, -MapView.HalfW, MapView.HalfW);
            _focus.z = Mathf.Clamp(_focus.z, -MapView.HalfH, MapView.HalfH);
            _focus.y = Mathf.Lerp(_focus.y, Sim.Map.Height(new float2(_focus.x, _focus.z)), 1f - Mathf.Exp(-6f * dt));

            Vector3 eye = Eye;
            float under = Sim.Map.Height(new float2(eye.x, eye.z)) + 3f;
            if (eye.y < under) eye.y = under;
            pos = eye; yaw = _yaw; pitch = Pitch;

            // ---- picking and ordering ----
            Prune();
            _clickFlash = Mathf.Max(0f, _clickFlash - dt);
            Hover = inside && !_dragging ? SoldierAt(cursor, 16f) : -1;
            bool add = kb.leftShiftKey.isPressed || kb.leftCtrlKey.isPressed;
            if (_b.PanelHasMouse || !inside) { _dragging = false; return; }

            if (mouse.leftButton.wasPressedThisFrame) { _dragging = true; _dragFrom = _dragTo = cursor; }
            if (_dragging) _dragTo = cursor;
            if (_dragging && mouse.leftButton.wasReleasedThisFrame)
            {
                _dragging = false;
                if (!add) { Selected.Clear(); Vehicles.Clear(); }
                if ((_dragTo - _dragFrom).magnitude > 6f) BoxSelect(_dragFrom, _dragTo);
                else
                {
                    var tank = TankAt(cursor, 34f);
                    int s = tank == null ? SoldierAt(cursor, 16f) : -1;
                    if (tank != null && tank.Team == Team)
                    {
                        if (add && Vehicles.Contains(tank)) Vehicles.Remove(tank); else if (!Vehicles.Contains(tank)) Vehicles.Add(tank);
                    }
                    else if (s >= 0 && Sim.Slots[s].Team == Team)
                    {
                        int q = Sim.Slots[s].Squad;
                        if (add && Selected.Contains(q)) Selected.Remove(q); else if (!Selected.Contains(q)) Selected.Add(q);
                    }
                }
            }
            if (kb.bKey.wasPressedThisFrame && Selected.Count + Vehicles.Count > 0) AirliftNext = !AirliftNext;
            if (Selected.Count + Vehicles.Count == 0) AirliftNext = false;
            if (mouse.rightButton.wasPressedThisFrame && Selected.Count + Vehicles.Count > 0 && Ground(cursor, out Vector3 at)) Send(at);
            // back to their captains' own judgement
            if (kb.xKey.wasPressedThisFrame) foreach (int q in Selected) Sim.Release(q, Team);
            if (kb.digit0Key.wasPressedThisFrame || kb.backquoteKey.wasPressedThisFrame) SelectAll();
        }

        void Prune()
        {
            for (int i = Selected.Count - 1; i >= 0; i--)
            {
                var q = Sim.Squads[Selected[i]];
                if (!q.Active || q.Team != Team || q.Alive == 0 || q.Crew || q.Inside) Selected.RemoveAt(i);
            }
            Vehicles.RemoveAll(t => t.Wreck || t.Carried || t.Team != Team);
        }

        void SelectAll()
        {
            Selected.Clear();
            Vehicles.Clear();
            for (int i = 0; i < BattleSystem.MaxSoldiers; i++)
            {
                var q = Sim.Squads[i];
                if (q.Active && q.Team == Team && q.Alive > 0 && !q.Crew) Selected.Add(i);
            }
            foreach (var t in Sim.Tanks) if (!t.Wreck && !t.Carried && t.Team == Team) Vehicles.Add(t);
        }

        void BoxSelect(Vector2 a, Vector2 b)
        {
            var r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            for (int i = 0; i < Sim.HighSlot; i++)
            {
                var s = Sim.Slots[i];
                if (!s.Alive || s.Inside || s.Team != Team) continue;
                var v = _cam.WorldToScreenPoint((Vector3)s.Pos + Vector3.up);
                if (v.z <= 0 || !r.Contains(new Vector2(v.x, v.y))) continue;
                if (Sim.Squads[s.Squad].Crew || Selected.Contains(s.Squad)) continue;
                Selected.Add(s.Squad);
            }
            foreach (var t in Sim.Tanks)
            {
                if (t.Wreck || t.Carried || t.Team != Team || Vehicles.Contains(t)) continue;
                var v = _cam.WorldToScreenPoint((Vector3)t.Pos + Vector3.up * 1.5f);
                if (v.z > 0 && r.Contains(new Vector2(v.x, v.y))) Vehicles.Add(t);
            }
        }

        /// <summary>The nearest soldier to a point on screen, within so many pixels.</summary>
        int SoldierAt(Vector2 screen, float within)
        {
            int best = -1;
            float bestD = within * within;
            for (int i = 0; i < Sim.HighSlot; i++)
            {
                var s = Sim.Slots[i];
                if (!s.Alive || s.Inside) continue;
                var v = _cam.WorldToScreenPoint((Vector3)s.Pos + Vector3.up);
                if (v.z <= 0) continue;
                // the own side wins a near tie, being what gets clicked on
                float d = (new Vector2(v.x, v.y) - screen).sqrMagnitude * (s.Team == Team ? 0.7f : 1f);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>The tank nearest a point on screen, within so many pixels (more, close up), or null.</summary>
        Tank TankAt(Vector2 screen, float within)
        {
            Tank best = null;
            float bestD = float.MaxValue;
            foreach (var t in Sim.Tanks)
            {
                if (t.Wreck || t.Carried) continue;
                var v = _cam.WorldToScreenPoint((Vector3)t.Pos + Vector3.up * 1.5f);
                if (v.z <= 0) continue;
                float reach = within * Mathf.Clamp(60f / v.z, 0.6f, 3f);
                float d = (new Vector2(v.x, v.y) - screen).sqrMagnitude;
                if (d < reach * reach && d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        /// <summary>Where on the ground a point on screen is.</summary>
        public bool Ground(Vector2 screen, out Vector3 at)
        {
            var ray = _cam.ScreenPointToRay(screen);
            at = default;
            for (float d = 1f; d < 1400f; d += d < 200f ? 0.5f : 1.5f)
            {
                Vector3 p = ray.origin + ray.direction * d;
                if (Mathf.Abs(p.x) > MapView.HalfW + 50f || Mathf.Abs(p.z) > MapView.HalfH + 50f) { if (p.y < 0f) break; continue; }
                if (p.y <= Sim.Map.Height(new float2(p.x, p.z)))
                {
                    p.x = Mathf.Clamp(p.x, -MapView.HalfW + 3, MapView.HalfW - 3);
                    p.z = Mathf.Clamp(p.z, -MapView.HalfH + 3, MapView.HalfH - 3);
                    at = p;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Every squad picked makes for the place clicked; they find their own room about it.</summary>
        void Send(Vector3 at)
        {
            float2 to = new(at.x, at.z);
            if (AirliftNext)
            {
                // their transports come for them, and set them down there
                foreach (int q in Selected) Sim.Airlift(q, to, Team);
                foreach (var t in Vehicles) Sim.Airlift(t, to);
                AirliftNext = false;
                Selected.Clear();
                Vehicles.Clear();
            }
            else
            {
                foreach (int q in Selected) Sim.Order(q, to, to, Team);
                foreach (var t in Vehicles) Sim.OrderTank(t, to);
            }
            _clickAt = at;
            _clickFlash = 0.6f;
        }

        // ------------------------------------------------------------------ drawing

        public void Draw(Color own, Color picked)
        {
            var white = Texture2D.whiteTexture;
            void Fill(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, white); }
            bool Project(Vector3 p, out Vector2 at)
            {
                var v = _cam.WorldToScreenPoint(p);
                at = new Vector2(v.x, Screen.height - v.y);
                return v.z > 0;
            }

            // where each picked squad has been sent, and a mark over everyone in it
            foreach (int qi in Selected)
            {
                var q = Sim.Squads[qi];
                for (int m = 0; m < Squad.Size; m++)
                {
                    int s = q.Members[m];
                    if (s < 0 || !Sim.Slots[s].Alive || Sim.Slots[s].Inside) continue;
                    if (!Project((Vector3)Sim.Slots[s].Pos + Vector3.up * 2.3f, out var a)) continue;
                    bool captain = m == q.Captain;
                    float w = captain ? 7f : 5f;
                    Fill(new Rect(a.x - w * 0.5f - 1, a.y - w * 0.5f - 1, w + 2, w + 2), new Color(0, 0, 0, 0.6f));
                    Fill(new Rect(a.x - w * 0.5f, a.y - w * 0.5f, w, w), picked);
                }
                if (q.Ordered)
                {
                    var p = new Vector3(q.OrderAt.x, Sim.Map.Height(q.OrderAt) + 0.3f, q.OrderAt.y);
                    if (Project(p, out var o))
                    {
                        Fill(new Rect(o.x - 6, o.y - 1, 12, 2), picked);
                        Fill(new Rect(o.x - 1, o.y - 6, 2, 12), picked);
                    }
                }
            }
            foreach (var t in Vehicles)
            {
                if (!Project((Vector3)t.Pos + Vector3.up * 5.5f, out var a)) continue;
                Fill(new Rect(a.x - 9, a.y - 9, 18, 18), new Color(0, 0, 0, 0.6f));
                Fill(new Rect(a.x - 7, a.y - 7, 14, 14), picked);
                if (t.Ordered && Project(new Vector3(t.OrderAt.x, Sim.Map.Height(t.OrderAt) + 0.3f, t.OrderAt.y), out var o))
                {
                    Fill(new Rect(o.x - 8, o.y - 1, 16, 2), picked);
                    Fill(new Rect(o.x - 1, o.y - 8, 2, 16), picked);
                }
            }
            if (Hover >= 0 && Sim.Slots[Hover].Alive && Project((Vector3)Sim.Slots[Hover].Pos + Vector3.up * 2.3f, out var h))
            {
                var c = Sim.Slots[Hover].Team == Team ? own : new Color(0.9f, 0.3f, 0.25f);
                Fill(new Rect(h.x - 6, h.y - 6, 12, 2), c); Fill(new Rect(h.x - 6, h.y + 4, 12, 2), c);
                Fill(new Rect(h.x - 6, h.y - 6, 2, 12), c); Fill(new Rect(h.x + 4, h.y - 6, 2, 12), c);
            }
            if (_clickFlash > 0f && Project(_clickAt + Vector3.up * 0.3f, out var k))
            {
                float r = 10f + (0.6f - _clickFlash) * 30f;
                var c = new Color(picked.r, picked.g, picked.b, _clickFlash / 0.6f);
                Fill(new Rect(k.x - r, k.y - 1, r * 2, 2), c);
                Fill(new Rect(k.x - 1, k.y - r * 0.5f, 2, r), c);
            }
            if (_dragging && (_dragTo - _dragFrom).magnitude > 6f)
            {
                float x0 = Mathf.Min(_dragFrom.x, _dragTo.x), x1 = Mathf.Max(_dragFrom.x, _dragTo.x);
                float y0 = Screen.height - Mathf.Max(_dragFrom.y, _dragTo.y), y1 = Screen.height - Mathf.Min(_dragFrom.y, _dragTo.y);
                Fill(new Rect(x0, y0, x1 - x0, y1 - y0), new Color(picked.r, picked.g, picked.b, 0.12f));
                Fill(new Rect(x0, y0, x1 - x0, 1), picked); Fill(new Rect(x0, y1 - 1, x1 - x0, 1), picked);
                Fill(new Rect(x0, y0, 1, y1 - y0), picked); Fill(new Rect(x1 - 1, y0, 1, y1 - y0), picked);
            }
            GUI.color = Color.white;
        }

        public int SelectedSoldiers
        {
            get
            {
                int n = 0;
                foreach (int q in Selected) n += Sim.Squads[q].Alive;
                return n;
            }
        }
    }
}
