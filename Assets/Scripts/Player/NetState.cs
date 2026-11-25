using System;
using Unity.Netcode;
using UnityEngine;

namespace Gunbound
{
    /// <summary>Owner-authoritative movement snapshot replicated to everyone.</summary>
    public struct NetState : INetworkSerializable, IEquatable<NetState>
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Hook;  // grapple hook tip (valid when HookOut)
        public float Yaw;
        public float Pitch;
        public byte Flags;
        public byte Teleport; // increments on respawn so remotes snap instead of interpolating

        public const byte Grounded = 1, Sliding = 2, Crouching = 4, WallLeft = 8, WallRight = 16, Reloading = 32, HookOut = 64, HookOnPlayer = 128;

        public bool Has(byte f) => (Flags & f) != 0;
        public int WallSide => Has(WallLeft) ? -1 : Has(WallRight) ? 1 : 0;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Position);
            s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Hook);
            s.SerializeValue(ref Yaw);
            s.SerializeValue(ref Pitch);
            s.SerializeValue(ref Flags);
            s.SerializeValue(ref Teleport);
        }

        public bool Equals(NetState o) =>
            Position == o.Position && Velocity == o.Velocity && Hook == o.Hook && Mathf.Approximately(Yaw, o.Yaw) &&
            Mathf.Approximately(Pitch, o.Pitch) && Flags == o.Flags && Teleport == o.Teleport;

        public override bool Equals(object obj) => obj is NetState o && Equals(o);
        public override int GetHashCode() => Position.GetHashCode() ^ (Flags << 8);
    }
}
