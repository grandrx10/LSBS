using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Source-style movement (friction / acceleration / bunny-hop momentum) with
    /// full air control, look-directed jump lunges, wall running and sliding.
    /// Runs only for the owning client; the result is replicated by PlayerController.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMotor : MonoBehaviour
    {
        [Header("Speeds (m/s)")]
        public float runSpeed = 8f;
        public float crouchSpeed = 4f;
        public float maxSpeed = 25f;

        [Header("Ground")]
        [Tooltip("How fast you reach run speed (m/s per second).")]
        public float runAccel = 70f;
        [Tooltip("How fast you stop when letting go / pulling back (m/s per second).")]
        public float runDecel = 45f;
        [Tooltip("How quickly you can turn while carrying extra momentum (degrees per second).")]
        public float overspeedSteer = 220f;
        [Tooltip("Used by sliding friction only.")]
        public float stopSpeed = 2.5f;
        [Tooltip("After landing, no friction for this long so a quick re-jump keeps all your speed.")]
        public float landingGrace = 0.2f;
        [Tooltip("Above run speed, ground speed bleeds off linearly at this rate (m/s per second) instead of normal friction.")]
        public float overspeedDecel = 7f;

        [Header("Air")]
        [Tooltip("How strongly WASD pushes you around in the air (m/s per second). Never adds speed beyond what you had / run speed.")]
        public float airControl = 30f;
        public float gravity = 20f;

        [Header("Jump")]
        public float jumpUpSpeed = 7.2f;
        [Tooltip("Speed added on every jump / wall jump.")]
        public float jumpBoost = 1.25f;
        [Tooltip("A jump always lunges at least this fast toward the crosshair.")]
        public float minLungeSpeed = 5f;
        [Tooltip("Extra upward speed when jumping while looking up.")]
        public float lookUpJumpBonus = 3f;
        public float jumpBuffer = 0.15f;
        public float coyoteTime = 0.1f;
        [Tooltip("Holding jump re-jumps on landing (no friction frame).")]
        public bool autoBhop = true;

        [Header("Wall run")]
        public float wallCheckDistance = 0.55f;
        [Tooltip("Acceleration while holding forward on a wall.")]
        public float wallAccel = 7f;
        [Tooltip("Deceleration while holding back on a wall.")]
        public float wallBrake = 14f;
        public float wallStick = 2f;
        public float wallJumpAwayPush = 0.6f;
        public float wallReattachDelay = 0.3f;
        [Tooltip("Surfaces whose normal.y is within ±this are wall-runnable.")]
        public float maxWallNormalY = 0.35f;

        [Tooltip("Looking up/down while wall running moves you up/down the wall (1 = fully follows your look).")]
        [Range(0, 1)] public float wallClimbFactor = 1f;

        [Header("Grapple")]
        [Tooltip("How hard the rope reels you in (m/s per second).")]
        public float grapplePull = 55f;
        [Tooltip("How hard A/D (and W) push you while hooked (m/s per second).")]
        public float grappleStrafe = 22f;
        public float grappleMaxSpeed = 36f;
        [Tooltip("How hard you get dragged when someone else hooks you (m/s per second).")]
        public float hookedPull = 55f;

        [Header("Slide / crouch")]
        public float slideStartSpeed = 5f;
        public float slideStopSpeed = 2.5f;
        public float slideFriction = 0.45f;
        [Tooltip("Degrees per second you can steer while sliding.")]
        public float slideSteer = 70f;
        public float standHeight = 1.95f;
        public float crouchHeight = 1.3f;

        [Header("Look")]
        public float minPitch = -89f, maxPitch = 89f;

        // ---- public state ----
        public Vector3 Velocity;
        public bool IsGrounded { get; private set; }
        /// <summary>What we're standing on (null in the air). Lets a sinking map carry us down.</summary>
        public Collider GroundCollider { get; private set; }
        public bool IsSliding { get; private set; }
        public bool IsCrouching { get; private set; }
        /// <summary>-1 = wall on the left, +1 = wall on the right, 0 = not wall running.</summary>
        public int WallSide { get; private set; }
        public float Yaw;
        public float Pitch;
        public float RecoilPitch, RecoilYaw;
        public float CameraRoll { get; private set; }
        public float HorizontalSpeed => new Vector2(Velocity.x, Velocity.z).magnitude;
        /// <summary>Pitch/yaw including recoil (what the gun is actually pointing at).</summary>
        public float ViewPitch => Pitch - RecoilPitch;
        public float ViewYaw => Yaw + RecoilYaw;
        public Quaternion ViewRotation => Quaternion.Euler(ViewPitch, ViewYaw, CameraRoll);
        public Vector3 AimDirection => Quaternion.Euler(ViewPitch, ViewYaw, 0) * Vector3.forward;

        /// <summary>Scales run/crouch speed and the speed cap (Nimble perk). Set by PlayerController each frame.</summary>
        public float SpeedMultiplier = 1f;
        public float RunSpeed => runSpeed * SpeedMultiplier;
        public float CrouchSpeed => crouchSpeed * SpeedMultiplier;
        public float MaxSpeed => maxSpeed * SpeedMultiplier;

        float _recoilRecovery = 9f;
        CharacterController _cc;
        Vector3 _groundNormal = Vector3.up;
        float _landTime = -10f, _lastGroundedTime = -10f, _jumpPressedTime = -10f, _lastJumpTime = -10f, _wallCooldownUntil;
        Vector3 _wallNormal, _wallTangent;
        GrappleHook _grapple;
        PlayerController _hookedBy;
        float _hookedAt;

        /// <summary>Someone hooked us: we get reeled toward them until they let go.</summary>
        public void SetHookedBy(PlayerController hooker)
        {
            _hookedBy = hooker;
            _hookedAt = Time.time;
            if (WallSide != 0) DetachWall();
            if (IsGrounded) Velocity.y = Mathf.Max(Velocity.y, 4.5f); // yank off the ground
            _lastJumpTime = Time.time; // skip ground snap this moment
        }
        float _wallSpeed, _wallAttachTime;
        float _ccBottom;

        // sword lunge: driven straight along a fixed direction (no gravity) until it ends
        Vector3 _lungeDir;
        float _lungeSpeed, _lungeExitSpeed, _lungeUntil;
        public bool IsLunging => _lungeUntil > 0f && Time.time < _lungeUntil;

        /// <summary>
        /// Lunge along <paramref name="dir"/> (any direction, up and down included) for <paramref name="duration"/> seconds.
        /// Like a wall jump, ALL your current momentum is redirected that way (+<paramref name="boost"/>), never slower than
        /// <paramref name="speed"/>. When it ends you keep your redirected momentum, or <paramref name="carry"/> of the base
        /// lunge speed if that's more.
        /// </summary>
        public void Lunge(Vector3 dir, float speed, float duration, float carry, float boost)
        {
            if (dir.sqrMagnitude < 1e-4f || speed <= 0f || duration <= 0f) return;
            _lungeDir = dir.normalized;
            float current = Velocity.magnitude;
            _lungeSpeed = Mathf.Max(speed, current + boost);
            _lungeExitSpeed = Mathf.Max(speed * carry, current + boost);
            _lungeUntil = Time.time + duration;
            if (WallSide != 0) DetachWall();
        }

        void LungeMove()
        {
            IsSliding = IsCrouching = false;
            Vector3 d = _lungeDir;
            // lunging into the floor slides you along it instead
            if (IsGrounded && Vector3.Dot(d, _groundNormal) < 0f) d = Vector3.ProjectOnPlane(d, _groundNormal);
            Velocity = d * _lungeSpeed;
            if (d.y > 0.05f)
            {
                // leave the ground (and don't get snapped back onto it)
                IsGrounded = false;
                _lastJumpTime = Time.time;
            }
        }

        void EndLunge()
        {
            _lungeUntil = 0f;
            float spd = Velocity.magnitude;
            if (spd > _lungeExitSpeed) Velocity *= _lungeExitSpeed / spd;
        }

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _ccBottom = _cc.center.y - _cc.height * 0.5f;
            standHeight = _cc.height;
        }

        public void SetLook(float yaw, float pitch)
        {
            Yaw = yaw;
            Pitch = pitch;
            transform.rotation = Quaternion.Euler(0, Yaw, 0);
        }

        public void AddRecoil(float pitchUp, float yaw, float recovery)
        {
            RecoilPitch += pitchUp;
            RecoilYaw += yaw;
            _recoilRecovery = recovery;
        }

        public void Teleport(Vector3 pos, float yaw)
        {
            _cc.enabled = false;
            transform.position = pos;
            _cc.enabled = true;
            Velocity = Vector3.zero;
            IsSliding = IsCrouching = false;
            WallSide = 0;
            RecoilPitch = RecoilYaw = 0;
            SetLook(yaw, 0);
            SetHeight(standHeight, true);
            if (_grapple != null) _grapple.ResetHook();
            _hookedBy = null;
            _lungeUntil = 0f;
        }

        void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            Look(dt);

            if (InputMap.Down(GameAction.Jump)) _jumpPressedTime = Time.time;
            bool jumpQueued = Time.time - _jumpPressedTime <= jumpBuffer || (autoBhop && InputMap.Held(GameAction.Jump));

            Vector2 input = InputMap.Move;
            Vector3 fwd = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0, Yaw, 0) * Vector3.right;
            Vector3 wish = fwd * input.y + right * input.x;
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            bool wasGrounded = IsGrounded;
            GroundCheck();
            if (IsGrounded && !wasGrounded) _landTime = Time.time;
            if (IsGrounded) _lastGroundedTime = Time.time;
            if (IsGrounded && !wasGrounded && InputMap.SlideHeld && HorizontalSpeed >= slideStartSpeed)
                IsSliding = true; // land into a slide

            if (_grapple == null) _grapple = GetComponent<GrappleHook>();
            if (_hookedBy != null)
            {
                bool stillHooked = _hookedBy.IsAlive &&
                                   (Time.time - _hookedAt < 0.5f || (_hookedBy.ViewFlags & NetState.HookOnPlayer) != 0);
                if (!stillHooked) _hookedBy = null;
            }

            if (_lungeUntil > 0f && Time.time >= _lungeUntil) EndLunge();

            if (_hookedBy != null)
            {
                HookedMove(dt, input, fwd, right);
            }
            else if (IsLunging)
            {
                LungeMove();
            }
            else if (_grapple != null && _grapple.IsAttached)
            {
                GrappleMove(dt, input, fwd, right);
            }
            else if (WallSide != 0)
            {
                WallRunTick(dt, _jumpPressedTime > _wallAttachTime && Time.time - _jumpPressedTime <= jumpBuffer);
            }
            else if (IsGrounded)
            {
                if (jumpQueued) Jump(wish);
                else GroundMove(dt, wish);
            }
            else
            {
                if (jumpQueued && Time.time - _lastGroundedTime <= coyoteTime && Time.time - _lastJumpTime > 0.25f)
                    Jump(wish);
                else
                {
                    AirMove(dt, input, fwd, right);
                    TryAttachWall(fwd, right);
                }
            }

            // speed cap (horizontal)
            Vector3 h = new Vector3(Velocity.x, 0, Velocity.z);
            if (h.magnitude > MaxSpeed)
            {
                h = h.normalized * MaxSpeed;
                Velocity.x = h.x;
                Velocity.z = h.z;
            }

            // height (crouch / slide) — stand up only with headroom
            bool wantLow = IsSliding || IsCrouching;
            SetHeight(wantLow ? crouchHeight : (HasHeadroom() ? standHeight : _cc.height), false);

            bool groundedBeforeMove = IsGrounded && Time.time - _lastJumpTime > 0.1f && !(_grapple != null && _grapple.IsAttached) && _hookedBy == null;
            _cc.Move(Velocity * dt);

            // stick to the ground when running down slopes / steps
            if (groundedBeforeMove && WallSide == 0 && (_cc.collisionFlags & CollisionFlags.Below) == 0)
            {
                if (SphereDown(out var hit, _cc.stepOffset + 0.1f) && hit.normal.y > 0.6f)
                    _cc.Move(Vector3.down * hit.distance);
            }

            // camera roll for wall running
            float targetRoll = WallSide * 12f;
            CameraRoll = Mathf.Lerp(CameraRoll, targetRoll, 1f - Mathf.Exp(-10f * dt));
        }

        void Look(float dt)
        {
            Vector2 d = InputMap.LookDelta;
            Yaw = Mathf.Repeat(Yaw + d.x, 360f);
            Pitch = Mathf.Clamp(Pitch - d.y, minPitch, maxPitch);
            transform.rotation = Quaternion.Euler(0, Yaw, 0);

            float k = 1f - Mathf.Exp(-_recoilRecovery * dt);
            RecoilPitch = Mathf.Lerp(RecoilPitch, 0, k);
            RecoilYaw = Mathf.Lerp(RecoilYaw, 0, k);
        }

        // ---------------- ground ----------------

        void GroundCheck()
        {
            if (Velocity.y > 0.5f && Time.time - _lastJumpTime < 0.25f) { IsGrounded = false; GroundCollider = null; return; }
            if (SphereDown(out var hit, 0.12f) && hit.normal.y >= Mathf.Cos(_cc.slopeLimit * Mathf.Deg2Rad))
            {
                IsGrounded = true;
                _groundNormal = hit.normal;
                GroundCollider = hit.collider;
            }
            else
            {
                IsGrounded = _cc.isGrounded && Velocity.y <= 0f;
                _groundNormal = Vector3.up;
                GroundCollider = null;
            }
            if (IsGrounded && WallSide != 0) DetachWall();
        }

        bool SphereDown(out RaycastHit hit, float distance)
        {
            float r = _cc.radius * 0.95f;
            Vector3 bottomSphere = transform.position + _cc.center + Vector3.down * (_cc.height * 0.5f - _cc.radius);
            Vector3 origin = bottomSphere + Vector3.up * 0.05f;
            return Physics.SphereCast(origin, r, Vector3.down, out hit, distance + 0.05f + _cc.skinWidth,
                Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        void GroundMove(float dt, Vector3 wish)
        {
            float hs = HorizontalSpeed;
            if (!IsSliding && InputMap.SlideHeld && hs >= slideStartSpeed && InputMap.SlideDown)
                IsSliding = true;
            if (IsSliding && (!InputMap.SlideHeld || hs < slideStopSpeed))
                IsSliding = false;
            IsCrouching = !IsSliding && InputMap.SlideHeld;

            Vector3 h = new Vector3(Velocity.x, 0, Velocity.z);
            if (IsSliding)
            {
                h = ApplyFriction(h, slideFriction, dt);
                // gravity along the slope
                Vector3 slope = Vector3.ProjectOnPlane(Vector3.down * gravity, _groundNormal);
                h += new Vector3(slope.x, 0, slope.z) * dt;
                // gentle steering
                if (wish.sqrMagnitude > 0.01f && h.sqrMagnitude > 0.01f)
                {
                    Vector3 target = Vector3.RotateTowards(h, wish * h.magnitude, slideSteer * Mathf.Deg2Rad * dt, 0f);
                    h = target;
                }
            }
            else
            {
                h = RunMove(h, wish, IsCrouching ? CrouchSpeed : RunSpeed, dt);
            }
            Velocity = new Vector3(h.x, -2f, h.z);
        }

        /// <summary>
        /// Regular, responsive running. Momentum from jumps/slides/wall runs above run speed
        /// is kept and bleeds off gently (and can be steered) instead of being cut instantly.
        /// </summary>
        Vector3 RunMove(Vector3 h, Vector3 wish, float speedCap, float dt)
        {
            float hs = h.magnitude;
            bool hasInput = wish.sqrMagnitude > 0.01f;
            Vector3 wishDir = hasInput ? wish.normalized : Vector3.zero;
            bool grace = Time.time - _landTime < landingGrace;

            if (hs > speedCap + 0.05f)
            {
                // carrying extra momentum
                float decel = overspeedDecel;
                if (hasInput && Vector3.Dot(wishDir, h / hs) < -0.3f) decel = runDecel; // pulling back brakes hard
                float newSpeed = grace ? hs : Mathf.Max(speedCap, hs - decel * dt);
                Vector3 dir = h / hs;
                if (hasInput && Vector3.Dot(wishDir, dir) > -0.3f)
                    dir = Vector3.RotateTowards(dir, wishDir, overspeedSteer * Mathf.Deg2Rad * dt, 0f);
                return dir * newSpeed;
            }

            // normal running: move straight toward the target velocity (no diagonal bonus)
            Vector3 target = wishDir * speedCap;
            float rate = hasInput ? runAccel : (grace ? 0f : runDecel);
            return Vector3.MoveTowards(h, target, rate * dt);
        }

        Vector3 ApplyFriction(Vector3 h, float fric, float dt)
        {
            float speed = h.magnitude;
            if (speed < 0.001f) return Vector3.zero;
            float control = Mathf.Max(speed, stopSpeed);
            float newSpeed = Mathf.Max(speed - control * fric * dt, 0f);
            return h * (newSpeed / speed);
        }


        // ---------------- air ----------------

        /// <summary>
        /// Momentum is kept in the air (turning the camera alone doesn't redirect you).
        /// WASD pushes you around (strafe/steer); speed never grows from air input.
        /// </summary>
        void AirMove(float dt, Vector2 input, Vector3 fwd, Vector3 right)
        {
            IsSliding = false;
            IsCrouching = false;
            Velocity.y -= gravity * dt;
            if (input.sqrMagnitude < 0.01f) return;

            Vector3 h = new Vector3(Velocity.x, 0, Velocity.z);
            float before = h.magnitude;
            Vector3 wish = (fwd * input.y + right * input.x).normalized;

            // push in the input direction...
            h += wish * (airControl * dt);
            // ...but strafing can only bend your path, never add speed beyond what you had
            // (or normal run speed when you were slower than that)
            float cap = Mathf.Max(before, RunSpeed);
            if (h.magnitude > cap) h = h.normalized * cap;

            Velocity.x = h.x;
            Velocity.z = h.z;
        }

        /// <summary>Ground jump: carries you on in the direction you were already moving, plus a little speed.</summary>
        void Jump(Vector3 wish)
        {
            Vector3 h = new Vector3(Velocity.x, 0, Velocity.z);
            float spd = h.magnitude;
            if (spd > 0.5f)
                h = h / spd * Mathf.Min(spd + jumpBoost, MaxSpeed);
            else if (wish.sqrMagnitude > 0.01f)
                h = wish.normalized * Mathf.Min(RunSpeed * 0.6f + jumpBoost, MaxSpeed);
            else
                h = Vector3.zero; // standing still: straight up

            Velocity = h;
            Velocity.y = jumpUpSpeed;
            IsGrounded = false;
            IsSliding = false;
            _lastJumpTime = Time.time;
            _jumpPressedTime = -10f;
        }

        // ---------------- wall run ----------------

        void TryAttachWall(Vector3 fwd, Vector3 right)
        {
            if (Time.time < _wallCooldownUntil) return;
            Vector3 center = transform.position + _cc.center;
            float dist = _cc.radius + wallCheckDistance;
            for (int side = -1; side <= 1; side += 2)
            {
                // must consciously press toward the wall
                if (!InputMap.Held(side < 0 ? GameAction.Left : GameAction.Right)) continue;
                Vector3 dir = right * side;
                if (WallRay(center, dir, dist, out var hit) ||
                    WallRay(center, (dir + fwd * 0.6f).normalized, dist, out hit) ||
                    WallRay(center, (dir - fwd * 0.6f).normalized, dist, out hit))
                {
                    StartWallRun(side, hit);
                    return;
                }
            }
        }

        bool WallRay(Vector3 origin, Vector3 dir, float dist, out RaycastHit hit)
        {
            return Physics.Raycast(origin, dir, out hit, dist, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)
                   && Mathf.Abs(hit.normal.y) <= maxWallNormalY
                   && SurfaceRules.CanWallRun(hit.collider);
        }

        void StartWallRun(int side, RaycastHit hit)
        {
            WallSide = side;
            _wallAttachTime = Time.time;
            _wallNormal = new Vector3(hit.normal.x, 0, hit.normal.z).normalized;
            Vector3 t = Vector3.Cross(_wallNormal, Vector3.up);
            Vector3 h = new Vector3(Velocity.x, 0, Velocity.z);
            if (h.sqrMagnitude > 0.25f) { if (Vector3.Dot(t, h) < 0) t = -t; }
            else if (Vector3.Dot(t, Quaternion.Euler(0, Yaw, 0) * Vector3.forward) < 0) t = -t;
            _wallTangent = t;
            _wallSpeed = h.magnitude; // keep momentum
            Velocity.y = 0;
        }

        void WallRunTick(float dt, bool jumpQueued)
        {
            Vector3 center = transform.position + _cc.center;
            if (jumpQueued)
            {
                WallJump();
                return;
            }
            if (!WallRay(center, -_wallNormal, _cc.radius + wallCheckDistance + 0.3f, out var hit))
            {
                DetachWall(); // ran off the end of the wall
                AirMove(dt, Vector2.zero, Vector3.forward, Vector3.right);
                return;
            }
            // pressing away from the wall lets go
            if (InputMap.Held(WallSide < 0 ? GameAction.Right : GameAction.Left))
            {
                DetachWall();
                Velocity += _wallNormal * 2f;
                return;
            }

            // follow curved walls
            Vector3 n = new Vector3(hit.normal.x, 0, hit.normal.z).normalized;
            Vector3 t = Vector3.Cross(n, Vector3.up);
            if (Vector3.Dot(t, _wallTangent) < 0) t = -t;
            _wallNormal = n;
            _wallTangent = t;

            if (InputMap.Held(GameAction.Forward)) _wallSpeed += wallAccel * dt;
            if (InputMap.Held(GameAction.Back)) _wallSpeed = Mathf.Max(0f, _wallSpeed - wallBrake * dt);
            _wallSpeed = Mathf.Min(_wallSpeed, MaxSpeed);

            // look up/down to run up/down the wall
            float climb = Mathf.Clamp(-Pitch, -75f, 75f) * Mathf.Deg2Rad * wallClimbFactor;
            Velocity = _wallTangent * (_wallSpeed * Mathf.Cos(climb)) - _wallNormal * wallStick;
            Velocity.y = _wallSpeed * Mathf.Sin(climb);
            IsSliding = IsCrouching = false;
        }

        void WallJump()
        {
            Vector3 look = Quaternion.Euler(Pitch, Yaw, 0) * Vector3.forward;
            Vector3 flat = new Vector3(look.x, 0, look.z);
            flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : _wallTangent;
            // never launch into the wall
            if (Vector3.Dot(flat, _wallNormal) < 0.25f)
            {
                flat = (flat + _wallNormal * wallJumpAwayPush);
                flat.y = 0;
                flat.Normalize();
            }
            float speed = Mathf.Min(Mathf.Max(_wallSpeed + jumpBoost, minLungeSpeed), MaxSpeed);
            Velocity = flat * speed;
            Velocity.y = jumpUpSpeed + Mathf.Max(0f, look.y) * lookUpJumpBonus;
            _lastJumpTime = Time.time;
            _jumpPressedTime = -10f;
            DetachWall();
        }

        /// <summary>Hooked: reeled toward the anchor, rope stops you drifting away (swing), A/D push you sideways.</summary>
        void GrappleMove(float dt, Vector2 input, Vector3 fwd, Vector3 right)
        {
            if (WallSide != 0) DetachWall();
            IsSliding = IsCrouching = false;

            Vector3 toAnchor = _grapple.Anchor - _grapple.Origin;
            float dist = toAnchor.magnitude;
            Vector3 dir = dist > 0.001f ? toAnchor / dist : Vector3.up;

            Velocity.y -= gravity * dt;
            // reel in (eases off right at the anchor so you don't jitter)
            Velocity += dir * (grapplePull * Mathf.Clamp01((dist - 0.8f) / 2f) * dt);
            // steer: A/D sideways, W a little forward
            Velocity += (right * input.x + fwd * (Mathf.Max(0f, input.y) * 0.5f)) * (grappleStrafe * dt);

            // rope: can't get farther than its (shrinking) length -> swing
            _grapple.RopeLength = Mathf.Min(_grapple.RopeLength, dist);
            if (dist >= _grapple.RopeLength - 0.02f)
            {
                float outward = Vector3.Dot(Velocity, -dir);
                if (outward > 0f) Velocity += dir * outward;
            }

            if (Velocity.magnitude > grappleMaxSpeed) Velocity = Velocity.normalized * grappleMaxSpeed;
            IsGrounded = false;
        }

        void HookedMove(float dt, Vector2 input, Vector3 fwd, Vector3 right)
        {
            if (WallSide != 0) DetachWall();
            AirMove(dt, input, fwd, right); // gravity + some air control to struggle
            Vector3 to = _hookedBy.transform.position - transform.position;
            float dist = to.magnitude;
            if (dist > 2.2f)
            {
                Vector3 dir = to / dist;
                Velocity += dir * (hookedPull * Mathf.Clamp01((dist - 2.2f) / 2f) * dt);
            }
            if (Velocity.magnitude > grappleMaxSpeed) Velocity = Velocity.normalized * grappleMaxSpeed;
            IsGrounded = false;
            IsSliding = IsCrouching = false;
        }

        void DetachWall()
        {
            if (WallSide == 0) return;
            WallSide = 0;
            _wallCooldownUntil = Time.time + wallReattachDelay;
        }

        // ---------------- collision ----------------

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            Vector3 n = hit.normal;
            if (n.y > 0.7f) return; // floor
            float into = Vector3.Dot(Velocity, n);
            if (into < 0f) Velocity -= n * into; // Source-style clip: slide along walls, keep momentum
            if (n.y < -0.5f && Velocity.y > 0f) Velocity.y = 0f; // ceiling
        }

        void SetHeight(float h, bool instant)
        {
            float nh = instant ? h : Mathf.MoveTowards(_cc.height, h, 6f * Time.deltaTime);
            if (Mathf.Approximately(nh, _cc.height)) return;
            _cc.height = nh;
            _cc.center = new Vector3(_cc.center.x, _ccBottom + nh * 0.5f, _cc.center.z);
        }

        bool HasHeadroom()
        {
            if (_cc.height >= standHeight - 0.001f) return true;
            Vector3 top = transform.position + _cc.center + Vector3.up * (_cc.height * 0.5f - _cc.radius);
            float need = standHeight - _cc.height;
            return !Physics.SphereCast(top, _cc.radius * 0.95f, Vector3.up, out _, need, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }
    }
}
