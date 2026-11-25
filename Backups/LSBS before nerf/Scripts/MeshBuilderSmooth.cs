using UnityEngine;

namespace LSBS
{
    /// <summary>The rounded shapes: the same builder, with normals that run smoothly over them.</summary>
    public partial class MeshBuilder
    {
        /// <summary>How finely round things are divided: 1 is as asked for.</summary>
        public static float Roundness = 0.55f;
        static int Div(int n, int least) => Mathf.Max(least, Mathf.RoundToInt(n * Roundness));

        int Vert(Vector3 p, Vector3 n, Color col)
        {
            V.Add(M.MultiplyPoint3x4(p));
            N.Add(M.MultiplyVector(n).normalized);
            C.Add(col);
            B.Add(Bone);
            return V.Count - 1;
        }

        void Face(int a, int b, int c)
        {
            if ((V[a] - V[b]).sqrMagnitude < 1e-12f || (V[b] - V[c]).sqrMagnitude < 1e-12f || (V[a] - V[c]).sqrMagnitude < 1e-12f) return;
            I.Add(a); I.Add(b); I.Add(c);
        }

        /// <summary>A shape turned round the y axis: (radius, height) from bottom to top.</summary>
        public void SmoothLathe(Vector3 centre, Vector2[] profile, int sides, Color col, float stretchZ = 1f, bool inside = false)
        {
            sides = Div(sides, 6);
            int rings = profile.Length, start = V.Count;
            for (int r = 0; r < rings; r++)
            {
                Vector2 along = profile[Mathf.Min(r + 1, rings - 1)] - profile[Mathf.Max(r - 1, 0)];
                Vector2 outward = new Vector2(along.y, -along.x).normalized;
                if (inside) outward = -outward;
                for (int i = 0; i <= sides; i++)
                {
                    float t = i * Mathf.PI * 2f / sides;
                    float s = Mathf.Sin(t), c = Mathf.Cos(t);
                    Vert(centre + new Vector3(s * profile[r].x, profile[r].y, c * profile[r].x * stretchZ), new Vector3(s * outward.x, outward.y, c * outward.x / stretchZ), col);
                }
            }
            int w = sides + 1;
            for (int r = 0; r + 1 < rings; r++)
            for (int i = 0; i < sides; i++)
            {
                int a = start + r * w + i, b = a + 1, c = a + w, d = c + 1;
                if (inside) { Face(b, c, d); Face(b, a, c); }
                else { Face(b, d, c); Face(b, c, a); }
            }
        }

        /// <summary>An ellipsoid.</summary>
        public void Ball(Vector3 centre, Vector3 radii, Color col, int sides = 12, int rings = 8)
        {
            sides = Div(sides, 5); rings = Div(rings, 4);
            int start = V.Count, w = sides + 1;
            for (int r = 0; r <= rings; r++)
            {
                float p = Mathf.PI * (r / (float)rings - 0.5f);
                for (int i = 0; i <= sides; i++)
                {
                    float t = i * Mathf.PI * 2f / sides;
                    Vector3 d = new(Mathf.Sin(t) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(t) * Mathf.Cos(p));
                    Vert(centre + Vector3.Scale(d, radii), new Vector3(d.x / radii.x, d.y / radii.y, d.z / radii.z), col);
                }
            }
            for (int r = 0; r < rings; r++)
            for (int i = 0; i < sides; i++)
            {
                int a = start + r * w + i, b = a + 1, c = a + w, e = c + 1;
                Face(b, e, c); Face(b, c, a);
            }
        }

        public void SmoothTube(Vector3 a, Vector3 b, float ra, float rb, int sides, Color col, bool caps = true)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-5f) return;
            d /= len;
            sides = Div(sides, 5);
            Quaternion q = Quaternion.FromToRotation(Vector3.up, d);
            float slope = (ra - rb) / len;
            int start = V.Count, w = sides + 1;
            for (int e = 0; e < 2; e++)
                for (int i = 0; i <= sides; i++)
                {
                    float t = i * Mathf.PI * 2f / sides;
                    Vector3 u = new(Mathf.Sin(t), 0, Mathf.Cos(t));
                    Vert((e == 0 ? a : b) + q * u * (e == 0 ? ra : rb), q * new Vector3(u.x, slope, u.z), col);
                }
            for (int i = 0; i < sides; i++)
            {
                int k = start + i;
                Face(k + 1, k + 1 + w, k + w); Face(k + 1, k + w, k);
            }
            if (!caps) return;
            for (int e = 0; e < 2; e++)
            {
                Vector3 at = e == 0 ? a : b, n = e == 0 ? -d : d;
                int mid = Vert(at, n, col), first = V.Count;
                for (int i = 0; i <= sides; i++)
                {
                    float t = i * Mathf.PI * 2f / sides;
                    Vert(at + q * new Vector3(Mathf.Sin(t), 0, Mathf.Cos(t)) * (e == 0 ? ra : rb), n, col);
                }
                for (int i = 0; i < sides; i++)
                    if (e == 0) Face(mid, first + i + 1, first + i); else Face(mid, first + i, first + i + 1);
            }
        }

        public void Capsule(Vector3 a, Vector3 b, float r, int sides, Color col)
        {
            SmoothTube(a, b, r, r, sides, col, false);
            var was = M;
            M = was * Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one);
            Quaternion q = Quaternion.FromToRotation(Vector3.up, (b - a).normalized);
            foreach (var p in new[] { a, b })
            {
                M = was * Matrix4x4.TRS(p, q, Vector3.one);
                Ball(Vector3.zero, Vector3.one * r, col, sides, 8);
            }
            M = was;
        }

        /// <summary>A sleeve or leg: its thickness at even steps from a to b, rounded over at both ends.</summary>
        public void Limb(Vector3 a, Vector3 b, float[] radii, int sides, Color col)
        {
            float len = (b - a).magnitude;
            var was = M;
            M = was * Matrix4x4.TRS(a, Quaternion.FromToRotation(Vector3.up, (b - a).normalized), Vector3.one);
            int n = radii.Length;
            var profile = new Vector2[n + 4];
            profile[0] = new Vector2(0.001f, -radii[0] * 0.55f);
            profile[1] = new Vector2(radii[0] * 0.72f, -radii[0] * 0.38f);
            for (int i = 0; i < n; i++) profile[i + 2] = new Vector2(radii[i], len * i / (n - 1));
            profile[n + 2] = new Vector2(radii[n - 1] * 0.72f, len + radii[n - 1] * 0.38f);
            profile[n + 3] = new Vector2(0.001f, len + radii[n - 1] * 0.55f);
            SmoothLathe(Vector3.zero, profile, sides, col);
            M = was;
        }

        /// <summary>A ring lying flat (its axis up), or turned.</summary>
        public void Ring(Vector3 centre, float radius, float thick, int sides, Color col, Quaternion? turn = null)
        {
            Quaternion q = turn ?? Quaternion.identity;
            sides = Div(sides, 7);
            const int around = 4;
            int start = V.Count, w = around + 1;
            for (int i = 0; i <= sides; i++)
            {
                float t = i * Mathf.PI * 2f / sides;
                Vector3 u = new(Mathf.Sin(t), 0, Mathf.Cos(t));
                for (int k = 0; k <= around; k++)
                {
                    float p = k * Mathf.PI * 2f / around;
                    Vector3 n = u * Mathf.Cos(p) + Vector3.up * Mathf.Sin(p);
                    Vert(centre + q * (u * radius + n * thick), q * n, col);
                }
            }
            for (int i = 0; i < sides; i++)
            for (int k = 0; k < around; k++)
            {
                int a = start + i * w + k, b = a + 1, c = a + w, d = c + 1;
                Face(a, c, d); Face(a, d, b);
            }
        }

        /// <summary>A rounded strap through a line of points.</summary>
        public void Strap(Vector3[] pts, float halfWidth, float thick, Color col)
        {
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                Vector3 d = pts[i + 1] - pts[i];
                if (d.sqrMagnitude < 1e-8f) continue;
                var was = M;
                // flat against the body: the thin way points out from the middle of it
                Vector3 mid = (pts[i] + pts[i + 1]) * 0.5f;
                Vector3 outward = new(mid.x, 0, mid.z + 0.05f);
                outward = Vector3.ProjectOnPlane(outward, d);
                if (outward.sqrMagnitude < 1e-6f) outward = Vector3.ProjectOnPlane(Vector3.up, d);
                M = was * Matrix4x4.TRS(mid, Quaternion.LookRotation(outward.normalized, d.normalized), new Vector3(halfWidth, 1f, thick));
                float half = d.magnitude * 0.5f;
                SmoothTube(new Vector3(0, -half - 0.004f, 0), new Vector3(0, half + 0.004f, 0), 1f, 1f, 8, col);
                M = was;
            }
        }
    }
}
