using UnityEngine;

namespace LSBS
{
    /// <summary>
    /// The machines, made over from the blockouts: angled armour in gunmetal, panels in their side's colour (drawn separately,
    /// tinted), lines of light along their edges. Ground at y 0 (a transport: its middle), front along +z.
    /// </summary>
    public class VehicleModels
    {
        public const int Phases = 8;

        /// <summary>A tank kind's parts: hull and its painted panels, one track at each stage of going round, turret, gun, machine gun.</summary>
        public class Tracked
        {
            public Mesh Hull, Paint, Turret, TurretPaint, Gun, MachineGun;
            public readonly Mesh[] Track = new Mesh[Phases];
            public float TrackX;
        }

        /// <summary>A transport: its body and painted panels, and a fan (one, drawn at each of its four places).</summary>
        public class Flying
        {
            public Mesh Body, Paint, Fan;
            public Vector3[] Fans;
        }

        public readonly Tracked Tank, Destroyer;
        public readonly Flying Troop, Lifter;

        static Color C(int hex) => MeshBuilder.Col(hex);
        static Color Metal(int hex) => MeshBuilder.Col(hex, MeshBuilder.Metal);
        static Color Glow(int hex) => MeshBuilder.Col(hex, MeshBuilder.Glow);
        static readonly Color Armour = Metal(0x343a41), ArmourDark = Metal(0x22272c), Plate = Metal(0x4d565f), Black = C(0x111316),
            Paint = C(0xdcdcdc), Light = Glow(0x7fe6ff), Hot = Glow(0xff8a3a), Lens = Glow(0xc8f4ff);

        public VehicleModels()
        {
            Tank = MakeTank();
            Destroyer = MakeDestroyer();
            Troop = MakeTransport(false);
            Lifter = MakeTransport(true);
        }

        // ------------------------------------------------------------------ the tank

        Tracked MakeTank()
        {
            var t = new Tracked { TrackX = 2.4f };
            var mb = new MeshBuilder();
            var paint = new MeshBuilder();
            // the hull: a low wedge between the tracks, its glacis raked well back
            mb.Box(new Vector3(0, 1.45f, -0.1f), new Vector3(3.8f, 1.5f, 5.6f), Quaternion.identity, Armour, 0.92f);
            mb.Box(new Vector3(0, 1.75f, 2.95f), new Vector3(3.6f, 0.9f, 1.3f), Quaternion.Euler(-28f, 0, 0), ArmourDark);
            mb.Box(new Vector3(0, 0.95f, 3.0f), new Vector3(3.7f, 0.7f, 0.9f), Quaternion.Euler(30f, 0, 0), ArmourDark);
            mb.Box(new Vector3(0, 1.6f, -3.0f), new Vector3(3.6f, 1.2f, 0.6f), Quaternion.identity, ArmourDark);
            // the deck: a plate with vents behind the turret, and a strip of light down each side
            paint.Box(new Vector3(0, 2.22f, -0.3f), new Vector3(3.5f, 0.08f, 3.4f), Quaternion.identity, Paint);
            for (int i = 0; i < 4; i++) mb.Box(new Vector3(0, 2.24f, -2.0f - i * 0.28f), new Vector3(2.4f, 0.06f, 0.14f), Quaternion.identity, Black);
            for (int s = -1; s <= 1; s += 2)
            {
                // armoured skirts over the tracks, with a light along them
                mb.Box(new Vector3(s * 2.4f, 1.55f, 0), new Vector3(1.45f, 0.9f, 7.2f), Quaternion.identity, Plate, 0.94f);
                paint.Box(new Vector3(s * 3.14f, 1.55f, 0.4f), new Vector3(0.06f, 0.5f, 4.6f), Quaternion.identity, Paint);
                mb.Box(new Vector3(s * 3.15f, 1.18f, 0), new Vector3(0.05f, 0.08f, 6.6f), Quaternion.identity, Light);
                // headlights
                mb.Box(new Vector3(s * 1.4f, 1.55f, 3.42f), new Vector3(0.35f, 0.12f, 0.08f), Quaternion.identity, Lens);
            }
            // the machine gun's ball mount in the nose
            mb.Tube(TankShape.GunPort - new Vector3(0, 0, 0.3f), TankShape.GunPort + new Vector3(0, 0, 0.05f), 0.32f, 0.28f, 10, ArmourDark);
            t.Hull = mb.Build("Tank hull");
            t.Paint = paint.Build("Tank paint");

            // the turret, about its own pivot: a faceted wedge, sensors on top
            mb = new MeshBuilder(); paint = new MeshBuilder();
            mb.Box(new Vector3(0, 0.75f, 0), new Vector3(2.9f, 1.4f, 3.0f), Quaternion.identity, Armour, 0.78f);
            mb.Box(new Vector3(0, 0.85f, 1.45f), new Vector3(2.2f, 1.0f, 0.8f), Quaternion.Euler(-22f, 0, 0), ArmourDark);
            mb.Box(new Vector3(0, 0.6f, -1.75f), new Vector3(2.6f, 1.1f, 0.7f), Quaternion.identity, Plate);
            paint.Box(new Vector3(0, 1.47f, -0.2f), new Vector3(2.1f, 0.06f, 2.2f), Quaternion.identity, Paint);
            for (int s = -1; s <= 1; s += 2) paint.Box(new Vector3(s * 1.33f, 0.85f, 0.1f), new Vector3(0.06f, 0.5f, 2.2f), Quaternion.identity, Paint);
            mb.Box(new Vector3(0.7f, 1.65f, -0.5f), new Vector3(0.5f, 0.35f, 0.6f), Quaternion.identity, ArmourDark);
            mb.Box(new Vector3(0.7f, 1.67f, -0.18f), new Vector3(0.38f, 0.16f, 0.04f), Quaternion.identity, Lens);
            mb.Tube(new Vector3(-0.8f, 1.45f, -1.0f), new Vector3(-0.8f, 2.4f, -1.0f), 0.04f, 0.03f, 5, Black);
            mb.Box(new Vector3(0, 0.15f, 0), new Vector3(3.0f, 0.06f, 3.1f), Quaternion.identity, Light);
            t.Turret = mb.Build("Tank turret");
            t.TurretPaint = paint.Build("Tank turret paint");

            // the gun, from its trunnion along +z
            t.Gun = Barrel(TankShape.CannonLength(TankKind.Tank), 0.2f, 0.42f);
            t.MachineGun = SmallGun();
            Tracks(t, 3.65f, 1.2f, 1.25f);
            return t;
        }

        // ------------------------------------------------------------------ the destroyer

        Tracked MakeDestroyer()
        {
            var t = new Tracked { TrackX = 2.9f };
            var mb = new MeshBuilder();
            var paint = new MeshBuilder();
            // a long casemate, its front sloped steeply, the gun low in its face
            mb.Box(new Vector3(0, 1.7f, -0.6f), new Vector3(4.6f, 2.2f, 9.6f), Quaternion.identity, Armour, 0.9f);
            mb.Box(new Vector3(0, 2.9f, -1.0f), new Vector3(3.8f, 1.4f, 6.6f), Quaternion.identity, ArmourDark, 0.85f);
            mb.Box(new Vector3(0, 2.4f, 3.95f), new Vector3(4.2f, 2.4f, 1.6f), Quaternion.Euler(-35f, 0, 0), ArmourDark);
            mb.Box(new Vector3(0, 1.1f, 4.5f), new Vector3(4.4f, 0.9f, 1.0f), Quaternion.Euler(30f, 0, 0), Armour);
            mb.Box(new Vector3(0, 2.65f, 4.45f), new Vector3(1.5f, 1.4f, 0.6f), Quaternion.identity, Plate);
            paint.Box(new Vector3(0, 3.62f, -1.2f), new Vector3(3.4f, 0.06f, 5.2f), Quaternion.identity, Paint);
            for (int s = -1; s <= 1; s += 2)
            {
                mb.Box(new Vector3(s * 2.9f, 1.75f, 0), new Vector3(1.5f, 1.3f, 10.8f), Quaternion.identity, Plate, 0.95f);
                paint.Box(new Vector3(s * 3.66f, 1.9f, 0.5f), new Vector3(0.06f, 0.7f, 6.8f), Quaternion.identity, Paint);
                mb.Box(new Vector3(s * 3.67f, 1.2f, 0), new Vector3(0.05f, 0.08f, 10.2f), Quaternion.identity, Light);
                mb.Box(new Vector3(s * 1.7f, 1.4f, 5.0f), new Vector3(0.4f, 0.12f, 0.08f), Quaternion.identity, Lens);
                // exhausts at the back
                mb.Tube(new Vector3(s * 1.2f, 2.5f, -5.4f), new Vector3(s * 1.2f, 2.5f, -5.9f), 0.25f, 0.25f, 8, Black);
                mb.Tube(new Vector3(s * 1.2f, 2.5f, -5.85f), new Vector3(s * 1.2f, 2.5f, -5.95f), 0.2f, 0.2f, 8, Hot);
            }
            mb.Box(new Vector3(-1.0f, 3.75f, -0.4f), new Vector3(0.6f, 0.4f, 0.7f), Quaternion.identity, ArmourDark);
            mb.Box(new Vector3(-1.0f, 3.78f, -0.04f), new Vector3(0.44f, 0.18f, 0.04f), Quaternion.identity, Lens);
            t.Hull = mb.Build("Destroyer hull");
            t.Paint = paint.Build("Destroyer paint");
            t.Gun = Barrel(TankShape.CannonLength(TankKind.Destroyer), 0.24f, 0.55f);
            Tracks(t, 5.5f, 1.25f, 1.3f);
            return t;
        }

        /// <summary>A gun barrel: a mantlet, a long tube, rings of light near the muzzle, and a brake at the end.</summary>
        static Mesh Barrel(float length, float r, float mantlet)
        {
            var mb = new MeshBuilder();
            mb.Box(new Vector3(0, 0, 0.1f), new Vector3(mantlet * 2.2f, mantlet * 1.8f, 0.7f), Quaternion.identity, ArmourDark, 0.85f);
            mb.Tube(new Vector3(0, 0, 0.3f), new Vector3(0, 0, length * 0.45f), r * 1.35f, r * 1.2f, 12, Armour);
            mb.Tube(new Vector3(0, 0, length * 0.45f), new Vector3(0, 0, length - 0.4f), r, r * 0.95f, 12, Armour);
            for (int i = 0; i < 3; i++)
            {
                float z = length * (0.6f + i * 0.1f);
                mb.Tube(new Vector3(0, 0, z), new Vector3(0, 0, z + 0.06f), r * 1.12f, r * 1.12f, 12, Light);
            }
            mb.Box(new Vector3(0, 0, length - 0.2f), new Vector3(r * 3.2f, r * 1.8f, 0.45f), Quaternion.identity, ArmourDark);
            mb.Tube(new Vector3(0, 0, length - 0.42f), new Vector3(0, 0, length + 0.02f), r * 0.6f, r * 0.6f, 10, Black);
            return mb.Build("Barrel");
        }

        static Mesh SmallGun()
        {
            var mb = new MeshBuilder();
            mb.Box(new Vector3(0, 0, -0.1f), new Vector3(0.22f, 0.22f, 0.5f), Quaternion.identity, ArmourDark);
            mb.Tube(new Vector3(0, 0, 0.1f), new Vector3(0, 0, 1.0f), 0.06f, 0.05f, 8, Black);
            mb.Tube(new Vector3(0, 0, 0.98f), new Vector3(0, 0, 1.02f), 0.07f, 0.07f, 8, Light);
            return mb.Build("Machine gun");
        }

        /// <summary>One track's belt round its wheels, at each stage of going round: plates with a lit seam between.</summary>
        static void Tracks(Tracked t, float halfLong, float wide, float tall)
        {
            for (int p = 0; p < Phases; p++)
            {
                var mb = new MeshBuilder();
                // wheels
                for (float z = -halfLong + 0.55f; z <= halfLong - 0.5f; z += 1.05f)
                    mb.Tube(new Vector3(-wide * 0.5f, 0.55f, z), new Vector3(wide * 0.5f, 0.55f, z), 0.45f, 0.45f, 10, ArmourDark);
                // the belt: plates along the bottom and top, a step on with each phase
                const float link = 0.4f;
                float shift = link * p / Phases;
                for (float z = -halfLong + shift; z < halfLong; z += link)
                {
                    mb.Box(new Vector3(0, 0.06f, z), new Vector3(wide, 0.12f, link * 0.8f), Quaternion.identity, Black);
                    mb.Box(new Vector3(0, tall, -z), new Vector3(wide, 0.1f, link * 0.8f), Quaternion.identity, Black);
                }
                // round the ends
                for (int e = -1; e <= 1; e += 2)
                    for (int k = 0; k < 5; k++)
                    {
                        float a = (k + (float)p / Phases) / 5f * Mathf.PI;
                        float y = tall * 0.5f - Mathf.Cos(a) * tall * 0.5f, z = e * (halfLong + Mathf.Sin(a) * 0.35f);
                        mb.Box(new Vector3(0, y, z), new Vector3(wide, 0.1f, link * 0.7f), Quaternion.Euler(e * Mathf.Rad2Deg * a, 0, 0), Black);
                    }
                t.Track[p] = mb.Build("Track");
            }
        }

        // ------------------------------------------------------------------ the transports

        /// <summary>
        /// From the blockout: a hull 5 m wide and 11 long on four arms, a ducted fan at each. The troop transport has doors in
        /// its flanks; the lifter is a spine with clamps, its tank hung beneath.
        /// </summary>
        Flying MakeTransport(bool lifter)
        {
            var f = new Flying { Fans = new[] { new Vector3(-4.6f, 0.2f, -4.2f), new Vector3(4.6f, 0.2f, -4.2f), new Vector3(-4.6f, 0.2f, 3.6f), new Vector3(4.6f, 0.2f, 3.6f) } };
            var mb = new MeshBuilder();
            var paint = new MeshBuilder();
            if (!lifter)
            {
                // the hold: a long box, its belly flat, its nose raked and glazed
                mb.Box(new Vector3(0, 0, -0.6f), new Vector3(4.9f, 3.6f, 8.6f), Quaternion.identity, Armour, 0.92f);
                mb.Box(new Vector3(0, 0.3f, 4.3f), new Vector3(4.4f, 3.0f, 2.2f), Quaternion.Euler(-12f, 0, 0), Armour, 0.8f);
                mb.Box(new Vector3(0, 0.95f, 5.2f), new Vector3(3.2f, 0.9f, 0.6f), Quaternion.Euler(-35f, 0, 0), Lens);
                mb.Box(new Vector3(0, -0.6f, -5.2f), new Vector3(4.2f, 2.4f, 1.0f), Quaternion.Euler(20f, 0, 0), ArmourDark);
                for (int s = -1; s <= 1; s += 2)
                {
                    // the doors, outlined in light
                    mb.Box(new Vector3(s * 2.47f, -0.2f, 0.4f), new Vector3(0.06f, 2.6f, 2.8f), Quaternion.identity, ArmourDark);
                    mb.Box(new Vector3(s * 2.5f, -0.2f, 0.4f), new Vector3(0.04f, 2.7f, 0.06f), Quaternion.identity, Light);
                    paint.Box(new Vector3(s * 2.48f, 0.9f, -2.6f), new Vector3(0.06f, 1.0f, 3.2f), Quaternion.identity, Paint);
                }
                paint.Box(new Vector3(0, 1.83f, -0.6f), new Vector3(3.6f, 0.06f, 6.4f), Quaternion.identity, Paint);
            }
            else
            {
                // a spine over the load, and the clamps that hold it
                mb.Box(new Vector3(0, 1.2f, 0), new Vector3(4.9f, 1.6f, 10.4f), Quaternion.identity, Armour, 0.9f);
                mb.Box(new Vector3(0, 1.4f, 5.4f), new Vector3(3.8f, 1.2f, 1.4f), Quaternion.Euler(-20f, 0, 0), ArmourDark);
                mb.Box(new Vector3(0, 1.6f, 5.9f), new Vector3(2.6f, 0.5f, 0.4f), Quaternion.Euler(-30f, 0, 0), Lens);
                paint.Box(new Vector3(0, 2.03f, -0.4f), new Vector3(3.6f, 0.06f, 7.2f), Quaternion.identity, Paint);
                for (int s = -1; s <= 1; s += 2)
                    for (int e = -1; e <= 1; e += 2)
                    {
                        mb.Box(new Vector3(s * 2.0f, -0.4f, e * 3.0f), new Vector3(0.35f, 2.2f, 0.5f), Quaternion.identity, ArmourDark);
                        mb.Box(new Vector3(s * 1.8f, -1.45f, e * 3.0f), new Vector3(0.6f, 0.25f, 0.6f), Quaternion.identity, Plate);
                        mb.Box(new Vector3(s * 2.0f, 0.4f, e * 3.0f), new Vector3(0.38f, 0.06f, 0.54f), Quaternion.identity, Light);
                    }
            }
            // the arms out to the fans, and the fans' housings
            foreach (var at in f.Fans)
            {
                Vector3 root = new(Mathf.Sign(at.x) * 2.3f, lifter ? 1.2f : 0.2f, at.z * 0.85f);
                mb.Box((root + at) * 0.5f, new Vector3(Mathf.Abs(at.x - root.x) + 0.4f, 0.5f, 0.6f), Quaternion.identity, ArmourDark);
                mb.Tube(at + new Vector3(0, -0.55f, 0), at + new Vector3(0, 0.55f, 0), 1.55f, 1.6f, 16, Plate, false);
                mb.Tube(at + new Vector3(0, -0.6f, 0), at + new Vector3(0, -0.5f, 0), 1.62f, 1.62f, 16, Light, false);
                paint.Tube(at + new Vector3(0, 0.5f, 0), at + new Vector3(0, 0.6f, 0), 1.63f, 1.63f, 16, Paint, false);
                mb.Tube(at + new Vector3(0, -0.2f, 0), at + new Vector3(0, 0.2f, 0), 0.35f, 0.35f, 8, ArmourDark);
            }
            f.Body = mb.Build(lifter ? "Lifter" : "Transport");
            f.Paint = paint.Build(lifter ? "Lifter paint" : "Transport paint");
            // the blades, about the fan's middle
            var fan = new MeshBuilder();
            for (int i = 0; i < 6; i++)
                fan.Box(Quaternion.Euler(0, i * 60f, 0) * new Vector3(0, 0, 0.8f), new Vector3(0.3f, 0.04f, 1.3f), Quaternion.Euler(0, i * 60f, 18f), Black);
            f.Fan = fan.Build("Fan");
            return f;
        }
    }
}
