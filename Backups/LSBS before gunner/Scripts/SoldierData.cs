using System;
using UnityEngine;

namespace LSBS
{
    /// <summary>Baked by "LSBS > Rebuild Soldiers": meshes wearing their kit, and every pose as bone matrices.</summary>
    public class SoldierData : ScriptableObject
    {
        [Serializable]
        public struct ClipInfo
        {
            public string name;
            public int start, frames;
            public bool loop;
            public float seconds;
        }

        public const int Fps = 30;
        /// <summary>Extra bones after the body's own.</summary>
        public int bodyBones;
        public int Rifle => bodyBones;
        public int Lever => bodyBones + 1;
        public int Helmet => bodyBones + 2;
        public int Pump => bodyBones + 3;
        public int BoneCount => bodyBones + 4;
        public bool IsWeapon(int bone) => bone == Rifle || bone == Lever || bone == Pump;

        public string[] boneNames;
        public Matrix4x4[] bindposes;      // BoneCount (identity for the extras)
        public bool[] upper;               // bends with the aim
        public int chestBone;
        public Vector3 chestPivot;         // chest joint in bind space
        public ClipInfo[] clips;
        /// <summary>frames * BoneCount rows of three float4 (3x4 matrices, model space, feet at the origin).</summary>
        public Vector4[] poses;
        public Mesh[] near = new Mesh[2];  // a team's soldier, all kit
        public Mesh[] far = new Mesh[2];   // the same from a distance
        public Mesh[] torn = new Mesh[2];  // cut through at the joints, for bodies blown apart
        public Mesh grenade;
        public GameObject ragdoll;         // rigidbodies on the bones, plus "RifleBody" and "HelmetBody"
        public Vector3 muzzle;             // from the rifle bone

        public int Bone(string name) => Array.IndexOf(boneNames, name);

        public int Frame(int clip, float time, out int next, out float t)
        {
            var c = clips[clip];
            float f = time * Fps;
            if (c.loop) f = Mathf.Repeat(f, c.frames);
            else f = Mathf.Clamp(f, 0, c.frames - 1);
            int i = Mathf.Min((int)f, c.frames - 1);
            t = f - i;
            next = c.start + (c.loop ? (i + 1) % c.frames : Mathf.Min(i + 1, c.frames - 1));
            return c.start + i;
        }

        public Matrix4x4 Pose(int frame, int bone)
        {
            int k = (frame * BoneCount + bone) * 3;
            var m = new Matrix4x4();
            m.SetRow(0, poses[k]); m.SetRow(1, poses[k + 1]); m.SetRow(2, poses[k + 2]);
            m.SetRow(3, new Vector4(0, 0, 0, 1));
            return m;
        }
    }
}
