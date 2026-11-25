using Unity.Netcode;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// A stationary target using the player model. Shoot it, it ragdolls like a player,
    /// then respawns where it was placed. Add one via Gunbound > Add Test Dummy.
    /// </summary>
    public class TestDummy : Combatant
    {
        public override Vector3 ViewVelocity => Vector3.zero;
        public override float ViewYaw => transform.eulerAngles.y;
        public override float ViewPitch => 0f;
        public override byte ViewFlags => NetState.Grounded;
        public override string FeedName => "Test Dummy";

        Vector3 _homePos;
        Quaternion _homeRot;

        protected override void Awake()
        {
            base.Awake();
            _homePos = transform.position;
            _homeRot = transform.rotation;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            SetVisible(!Dead.Value);
        }

        protected override void ServerRespawn() => RespawnRpc();

        [Rpc(SendTo.Everyone)]
        void RespawnRpc()
        {
            transform.SetPositionAndRotation(_homePos, _homeRot);
            SetVisible(true);
        }

        public override void SetVisible(bool on)
        {
            base.SetVisible(on);
            foreach (var c in GetComponents<Collider>()) c.enabled = on;
        }
    }
}
