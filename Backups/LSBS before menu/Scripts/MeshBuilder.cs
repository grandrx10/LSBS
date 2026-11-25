using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LSBS
{
    /// <summary>
    /// Flat-shaded, vertex-coloured geometry. The colour's alpha says what it's made of: 0 cloth, earth and wood;
    /// 0.5 metal; 1 glowing. Uv1 (soldiers only) is bone, second bone, first bone's weight, flags (1 captain's kit only).
    /// </summary>
    public partial class MeshBuilder
    {
        public const float Matte = 0f, Metal = 0.5f, Glow = 1f;

        public readonly List<Vector3> V = new();
        public readonly List<Vector3> N = new();
        public readonly List<Color> C = new();
        public readonly List<Vector4> B = new();
        public readonly List<int> I = new();
        public Matrix4x4 M = Matrix4x4.identity;
        public Vector4 Bone;

        public static Color Col(int hex, float kind = Matte) =>
            new Color(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f, kind).WithLinear();

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            a = M.MultiplyPoint3x4(a); b = M.MultiplyPoint3x4(b); c = M.MultiplyPoint3x4(c);
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-14f) return;
            n.Normalize();
            int k = V.Count;
            V.Add(a); V.Add(b); V.Add(c);
            for (int i = 0; i < 3; i++) { N.Add(n); C.Add(col); B.Add(Bone); I.Add(k + i); }
        }

        /// <summary>Corners in clockwise order seen from the front.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
        {
            Tri(a, b, c, col);
            Tri(a, c, d, col);
        }

        public void Box(Vector3 centre, Vector3 size, Color col) => Box(centre, size, Quaternion.identity, col);

        public void Box(Vector3 centre, Vector3 size, Quaternion rot, Color col, float taper = 1f)
        {
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z)
            {
                float t = y > 0 ? taper : 1f;
                return centre + rot * new Vector3(x * h.x * t, y * h.y, z * h.z * t);
            }
            Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), col);
            Quad(P(-1, -1, 1), P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), col);
            Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), col);
            Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), col);
            Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), col);
            Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), col);
        }

        /// <summary>A cylinder or cone between two points.</summary>
        public void Tube(Vector3 a, Vector3 b, float ra, float rb, int sides, Color col, bool caps = true)
        {
            Vector3 d = b - a;
            if (d.sqrMagnitude < 1e-10f) return;
            Quaternion q = Quaternion.FromToRotation(Vector3.up, d.normalized);
            for (int i = 0; i < sides; i++)
            {
                float t0 = i * Mathf.PI * 2f / sides, t1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 u0 = q * new Vector3(Mathf.Cos(t0), 0, Mathf.Sin(t0)), u1 = q * new Vector3(Mathf.Cos(t1), 0, Mathf.Sin(t1));
                Quad(a + u0 * ra, b + u0 * rb, b + u1 * rb, a + u1 * ra, col);
                if (!caps) continue;
                if (rb > 1e-4f) Tri(b, b + u1 * rb, b + u0 * rb, col);
                if (ra > 1e-4f) Tri(a, a + u0 * ra, a + u1 * ra, col);
            }
        }

        /// <summary>A shape turned round the y axis: (radius, height) from bottom to top. Stretch squashes it front to back.</summary>
        public void Lathe(Vector3 centre, Vector2[] profile, int sides, Color col, float stretchZ = 1f, System.Func<float, int, Vector2> warp = null)
        {
            Vector3 P(int ring, int i)
            {
                float t = i * Mathf.PI * 2f / sides;
                Vector2 p = profile[ring];
                if (warp != null) p += warp(t, ring);
                return centre + new Vector3(Mathf.Sin(t) * p.x, p.y, Mathf.Cos(t) * p.x * stretchZ);
            }
            for (int r = 0; r + 1 < profile.Length; r++)
            for (int i = 0; i < sides; i++)
                Quad(P(r, i + 1), P(r + 1, i + 1), P(r + 1, i), P(r, i), col);
        }

        public void Add(Mesh mesh, Matrix4x4 m, Color col)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            for (int i = 0; i < t.Length; i += 3)
                Tri(m.MultiplyPoint3x4(v[t[i]]), m.MultiplyPoint3x4(v[t[i + 1]]), m.MultiplyPoint3x4(v[t[i + 2]]), col);
        }

        public Mesh Build(string name, bool bones = false)
        {
            var mesh = new Mesh { name = name, indexFormat = V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(V);
            mesh.SetNormals(N);
            mesh.SetColors(C);
            if (bones) mesh.SetUVs(1, B);
            mesh.SetTriangles(I, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    static class ColorExtensions
    {
        /// <summary>Vertex colours aren't converted from sRGB for us.</summary>
        public static Color WithLinear(this Color c)
        {
            var l = c.linear;
            l.a = c.a;
            return l;
        }
    }
}
