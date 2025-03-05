using System;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;

namespace TarodevController
{
    [RequireComponent(typeof(Rigidbody2D), typeof(Collider2D))]
    public class PlayerController : MonoBehaviour, IPlayerController
    {
        [SerializeField] private InputActionAsset inputActions;
        private InputAction moveAction;
        private InputAction jumpAction;
        private InputAction dashAction;
        private InputAction grabAction;
        
        [SerializeField] private ScriptableStats _stats;
        private Rigidbody2D _rb;
        private Animator _anim;
        private CapsuleCollider2D _col;
        private FrameInput _frameInput;
        private Vector2 _frameVelocity;
        private bool _cachedQueryStartInColliders;

        public static event Action<float> OnVerticalVelocityChanged;

        [SerializeField] private Transform wallCheck;
        [SerializeField] private float wallCheckDistance;
        [SerializeField] private LayerMask WhatIsGround;
        [SerializeField] private LayerMask PushableLayer;
        private bool isWallDetected;
        private bool isWallDetectedLeft;
        
        private bool isWallSliding;
        private bool isGrabbingAndSliding;
        private bool isSliding;
        
        private bool isObjectDetected;
        private bool isObjectDetectedLeft;
        
        [SerializeField] private GameObject leftWallDetector; 
        [SerializeField] private GameObject rightWallDetector; 
        
        // LedgeClimb
        [SerializeField] private Vector2 offset1Right; 
        [SerializeField] private Vector2 offset2Right; 
        [SerializeField] private Vector2 offset1Left;  
        [SerializeField] private Vector2 offset2Left;  
        private Vector2 climbBegunPosition;
        private Vector2 climbOverPosition;
        private bool canGrabLedge = true;
        public bool ledgeDetectedLeft;
        public bool ledgeDetected;

        
        #region Interface

        public Vector2 FrameInput => _frameInput.Move;
        public Vector2 Velocity => _rb.linearVelocity;
        public event Action<bool, float> GroundedChanged;
        public event Action Jumped;
        public event Action WallJumped;
        public bool IsGrounded => _grounded;
        public bool IsDashing => _isDashing;
        public bool IsGrabbingWall => _isGrabbingWall;
        public bool IsWallSliding => isWallSliding;
        public bool IsSliding => isSliding;
        public bool canClimb { get; private set; }
        public float CurrentClimbSpeed => _currentClimbSpeed;

        #endregion

        
        private float _time;

        private SpriteRenderer _spriteRenderer;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _col = GetComponent<CapsuleCollider2D>();
            _spriteRenderer = GetComponent<SpriteRenderer>(); 
            _anim = GetComponent<Animator>();

            _cachedQueryStartInColliders = Physics2D.queriesStartInColliders;

            var playerControls = inputActions.FindActionMap("Player");
            moveAction = playerControls.FindAction("Move");
            jumpAction = playerControls.FindAction("Jump");
            dashAction = playerControls.FindAction("Dash");
            grabAction = playerControls.FindAction("Grab");

            moveAction.Enable();
            jumpAction.Enable();
            dashAction.Enable();
            grabAction.Enable();
        }

        private void OnEnable()
        {
            jumpAction.Enable();
            dashAction.Enable();
            grabAction.Enable();
        }

        private void OnDisable()
        {
            jumpAction.Disable();
            dashAction.Disable();
            grabAction.Disable();
        }

        private void OnDestroy()
        {
            moveAction.Disable();
            jumpAction.Disable();
            dashAction.Disable();
            grabAction.Disable();
        }

                private void Update()
                {
                    _time += Time.deltaTime;
                    GatherInput();

                    float verticalVelocity = _rb.linearVelocity.y;
                    OnVerticalVelocityChanged?.Invoke(verticalVelocity);

                    // Don't touch this unless you hate yourself.
                    HandleWallGrab();
                }

        private void GatherInput()
        {
            Vector2 move = moveAction.ReadValue<Vector2>();
            _frameInput = new FrameInput
            {
                JumpDown = jumpAction.triggered, 
                JumpHeld = jumpAction.ReadValue<float>() > 0, 
                Move = move,
                GrabDown = grabAction.triggered,
                GrabHeld = grabAction.ReadValue<float>() > 0
            };

            // Why here?
            bool dashPressed = dashAction.triggered;
            if (dashPressed && _canDash && !_anim.GetCurrentAnimatorStateInfo(0).IsName("LedgeClimb"))
            {
                if (_isGrabbingWall)
                {
                    _isGrabbingWall = false;
                    _rb.gravityScale = 1f;
                }
                StartDash();
            }

            if (jumpAction.triggered) 
            {
                _jumpToConsume = true;
                _timeJumpWasPressed = _time;
                _timeWallJumpWasPressed = _time;
            }

            if (_stats.SnapInput)
            {
                _frameInput.Move.x = Mathf.Abs(_frameInput.Move.x) < _stats.HorizontalDeadZoneThreshold ? 0 : Mathf.Sign(_frameInput.Move.x);
                _frameInput.Move.y = Mathf.Abs(_frameInput.Move.y) < _stats.VerticalDeadZoneThreshold ? 0 : Mathf.Sign(_frameInput.Move.y);
            }

            if (_frameInput.JumpDown)
            {
                _jumpToConsume = true;
                _timeJumpWasPressed = _time;
            }

            if (!_frameInput.JumpHeld && _rb.linearVelocity.y > 0)
            {
                _endedJumpEarly = true;
            }
        }

        private void FixedUpdate()
        {
            if (canClimb)
            {
                _rb.linearVelocity = Vector2.zero; 
                transform.position = climbBegunPosition; 
                return; 
            }

            if (!_isGrabbingWall && (ledgeDetected || ledgeDetectedLeft))
            {
                ResetLedgeDetection();
            }

            if (_isGrabbingWall && _frameInput.Move.y > 0)
            {
               CheckForLedge();
            }
            
            CheckCollisions();
            HandleJump();
            HandleDirection();
            HandleGravity();
            HandleDash();
            HandleWallMovement();
            HandleWallSliding();
            HandlePushableObject();
            
            ApplyMovement();
        }

        #region Collisions
        
        private float _frameLeftGrounded = float.MinValue;
        public PhysicsMaterial2D zeroFrictionMaterial;
        public PhysicsMaterial2D FrictionMaterial;
        private bool _forceZeroFriction = false;
        private bool _grounded;

        private void CheckCollisions()
        {
            Physics2D.queriesStartInColliders = false;

            // Ground and Ceiling
            bool groundHit = Physics2D.CapsuleCast(_col.bounds.center, _col.size, _col.direction, 0, Vector2.down, _stats.GrounderDistance, ~_stats.PlayerLayer);
            bool ceilingHit = Physics2D.CapsuleCast(_col.bounds.center, _col.size, _col.direction, 0, Vector2.up, _stats.GrounderDistance, ~_stats.PlayerLayer);

            // Wall detection
            RaycastHit2D hitLeft = Physics2D.Raycast(new Vector2(_col.bounds.min.x, _col.bounds.center.y), Vector2.left, _stats.GrounderDistance, ~_stats.PlayerLayer);
            RaycastHit2D hitRight = Physics2D.Raycast(new Vector2(_col.bounds.max.x, _col.bounds.center.y), Vector2.right, _stats.GrounderDistance, ~_stats.PlayerLayer);

            if ((hitLeft.collider != null || hitRight.collider != null))
            {
                _canWallJump = true; 
                _wallNormal = hitLeft.collider != null ? hitLeft.normal : hitRight.normal;
            }
            else
            {
                _canWallJump = false; 
            }

            // Hit a Ceiling
            if (ceilingHit) _frameVelocity.y = Mathf.Min(0, _frameVelocity.y);

            // Landed on the Ground
            if (!_grounded && groundHit)
            {
                _grounded = true;
                _coyoteUsable = true;
                _bufferedJumpUsable = true;
                _endedJumpEarly = false;
                GroundedChanged?.Invoke(true, Mathf.Abs(_frameVelocity.y));
            }
            // Left the Ground
            else if (_grounded && !groundHit)
            {
                _grounded = false;
                _frameLeftGrounded = _time;
                GroundedChanged?.Invoke(false, 0);
            }

            Physics2D.queriesStartInColliders = _cachedQueryStartInColliders;
        }
 
        private void HandleWallSliding()
        {
            // Wall detection (Slide)
            isWallDetected = Physics2D.Raycast(wallCheck.position, Vector2.right, wallCheckDistance, WhatIsGround);
            isWallDetectedLeft = Physics2D.Raycast(wallCheck.position, Vector2.left, wallCheckDistance, WhatIsGround);

            // Nouvelle condition d'input vers le mur
            bool isMovingTowardsWall = false;

            bool isFacingRight = !_spriteRenderer.flipX;

            // Lire l'input horizontal avec le Input System
            float horizontalInput = moveAction.ReadValue<Vector2>().x;  

            // Vérifie si l'input est dirigé vers le mur détecté
            if (isWallDetected && horizontalInput > 0 && isFacingRight)
            {
                isMovingTowardsWall = true;
            }
            else if (isWallDetectedLeft && horizontalInput < 0 && !isFacingRight)
            {
                isMovingTowardsWall = true;
            }

            // Appliquer le matériau en fonction de la direction, sauf si zeroFriction est forcé
            if (_col != null)
            {
                if (_forceZeroFriction)
                {
                    _col.sharedMaterial = zeroFrictionMaterial;
                }
                else
                {
                    _col.sharedMaterial = isMovingTowardsWall ? FrictionMaterial : zeroFrictionMaterial;
                }
            }

            // Determine if sliding is active
            isWallSliding = !_isDashing && !_isGrabbingWall && !_grounded && _rb.linearVelocity.y < 0 && isMovingTowardsWall; // Without Grab
            isGrabbingAndSliding = _isGrabbingWall && _rb.linearVelocity.y < -1f && !canClimb; // With Grab

            isSliding = isWallSliding || isGrabbingAndSliding;

            // Set animation state
            _anim.SetBool("isWallSliding", isWallSliding);
        }

        private void HandlePushableObject()
        {
            // Object/Wall detection (Slide)
            isObjectDetected = Physics2D.Raycast(wallCheck.position, Vector2.right, wallCheckDistance, WhatIsGround | PushableLayer);
            isObjectDetectedLeft = Physics2D.Raycast(wallCheck.position, Vector2.left, wallCheckDistance, WhatIsGround | PushableLayer);

            // condition d'input vers le mur
            bool isMovingTowardsPushable = false;

            bool isFacingRight = !_spriteRenderer.flipX;

            // Lire l'input horizontal avec le Input System
            float horizontalInput = moveAction.ReadValue<Vector2>().x;  

            // Vérifie si l'input est dirigé vers le mur détecté
            if (isObjectDetected && horizontalInput > 0 && isFacingRight)
            {
                isMovingTowardsPushable = true;
            }
            else if (isObjectDetectedLeft && horizontalInput < 0 && !isFacingRight)
            {
                isMovingTowardsPushable = true;
            }

            // Determine if Pushing is active
            bool isPushingWall = isMovingTowardsPushable && _grounded && !isWallSliding && !_isGrabbingWall;

            // Set animation state
            _anim.SetBool("isPushingWall", isPushingWall);
        }

        
        #endregion

       #region Jumping

        private bool _jumpToConsume;
        private bool _bufferedJumpUsable;
        private bool _endedJumpEarly;
        private bool _coyoteUsable;
        private float _timeJumpWasPressed;
        private bool HasBufferedJump => _bufferedJumpUsable && _time < _timeJumpWasPressed + _stats.JumpBuffer;
        private bool CanUseCoyote => _coyoteUsable && !_grounded && _time < _frameLeftGrounded + _stats.CoyoteTime;
        
        // WallJump
        private bool _canWallJump;
        private float _timeWallJumpWasPressed; 
        private bool HasBufferedWallJump => _canWallJump && _time < _timeWallJumpWasPressed + _stats.JumpBuffer;

        // Ledge Cooldown for WallJump
        private float _ledgeClimbCooldownTime = 0.5f; 
        private float _ledgeClimbCooldownEndTime; 
        private bool IsWallJumpBlocked => Time.time < _ledgeClimbCooldownEndTime;

        private void HandleJump()
        {
            // Si aucun saut n'est à consommer ou de saut bufferé disponible, sortir
            if (!_jumpToConsume && !HasBufferedJump && !HasBufferedWallJump) 
                return;


            // Prio de saut
            if (_isGrabbingWall && _grounded)
            {
                HandleWallJump();
            }
            
            // Saut depuis le sol ou avec le Coyote Time
            else if (_grounded || CanUseCoyote)
            {
                if (_isDashing)
                {
                    _isDashing = false; 
                    _frameVelocity.y = _stats.JumpPower; 
                    _frameVelocity.x += _dashDirection.x * _stats.DashHorizontalBoost; 
                }
                else
                {
                    ExecuteJump();
                    StartCoroutine(ApplyZeroFriction());
                }
            }
            
            // Saut depuis un mur
            else if (_canWallJump || HasBufferedWallJump)
            {
                HandleWallJump();
            }

            _jumpToConsume = false;
        }

        private void ExecuteJump()
        {
            if (_isGrabbingWall)
            {
                return;
            }
            else
            {
                _endedJumpEarly = false;
                _timeJumpWasPressed = 0;
                _bufferedJumpUsable = false;
                _coyoteUsable = false;
                _timeWallJumpWasPressed = 0;
                _frameVelocity.y = _stats.JumpPower;
                Jumped?.Invoke();
            }
        }

        private void HandleWallJump()
        {
            // Bloquer le wall jump si cooldown après ledge climb
            if (IsWallJumpBlocked)
            {
                return;
            }

            if (_isDashing)
            {
                CancelDash(); 
            }

            _endedJumpEarly = false;
            _timeWallJumpWasPressed = 0; 
            _timeJumpWasPressed = 0;
            _bufferedJumpUsable = false;
            _coyoteUsable = false;

            if (_isGrabbingWall)
            {
                StartCoroutine(ApplyZeroFriction());
                _frameVelocity = new Vector2(0, _stats.JumpPower);
                _isGrabbingWall = false; 
                _rb.gravityScale = 1f;  
                _canGrab = false;
                _grabCooldownRemaining = _grabCooldownTime;
            }
            else
            {
                _frameVelocity = new Vector2(_wallNormal.x * _stats.WallJumpPushForce, _stats.JumpPower);
            }

            Jumped?.Invoke();
            WallJumped?.Invoke();
        }

        // Coroutine pour appliquer le matériau de friction nulle
        private IEnumerator ApplyZeroFriction()
        {
            if (_col != null && zeroFrictionMaterial != null)
            {
                // Activer le flag de priorité
                _forceZeroFriction = true;

                // Appliquer le matériau avec zéro friction
                _col.sharedMaterial = zeroFrictionMaterial;

                // Attendre pendant 0.2 secondes
                yield return new WaitForSeconds(0.2f);

                // Désactiver le flag de priorité
                _forceZeroFriction = false;

                // Restaurer le matériau en fonction de la logique du Wall Sliding
                _col.sharedMaterial = isWallDetected || isWallDetectedLeft ? FrictionMaterial : zeroFrictionMaterial;
            }
        }
        
        #endregion

        #region Dash
        
        private bool _canDash = true;
        private bool _isDashing = false;
        private float _dashTimeRemaining;
        private Vector2 _dashDirection;
        private bool _canGrab = true;
        private float _grabCooldownTime = 0.2f;
        private float _grabCooldownRemaining = 0f;
        private float dashProgress;
        [SerializeField] private AnimationCurve dashDecelerationCurve = AnimationCurve.EaseInOut(0, 1, 1, 0);
        
        private void StartDash()
        {
            if (_isDashing || !_canDash) return;

            // Désactivation forcée du grab
            if (_isGrabbingWall)
            {
                _isGrabbingWall = false; 
                _currentClimbSpeed = 0; 
                _frameVelocity = Vector2.zero;
            }

            _canGrab = false;
            _isDashing = true;
            _canDash = false;
            _dashTimeRemaining = _stats.DashDuration;
            dashProgress = 0f;

            // Calculer la direction du dash (en filtrant l'entrée verticale)
            if (Mathf.Abs(_frameInput.Move.x) > Mathf.Abs(_frameInput.Move.y))
            {
                // Si le mouvement horizontal est plus important que le vertical, on prend uniquement l'axe horizontal
                _dashDirection = new Vector2(Mathf.Sign(_frameInput.Move.x), 0);
            }
            else
            {
                // Si aucune entrée horizontale, on choisit la direction par défaut (orientation du sprite)
                _dashDirection = _spriteRenderer.flipX ? Vector2.left : Vector2.right;
            }
        }

        private void HandleDash()
        {
            if (_isDashing)
            {
                StartCoroutine(ApplyZeroFriction());

                // Vérification si input > direction opposée (en ignorant la composante verticale)
                if (_frameInput.Move.x != 0)
                {
                    Vector2 newDashDirection = new Vector2(Mathf.Sign(_frameInput.Move.x), 0);

                    // Condition pour cancel : input opposé à la direction actuelle
                    if (Vector2.Dot(newDashDirection, _dashDirection) < 0)
                    {
                        CancelDash();
                        return;
                    }
                }

                // Mise à jour du temps et du progrès du dash
                dashProgress += Time.fixedDeltaTime / _stats.DashDuration;
                _dashTimeRemaining -= Time.fixedDeltaTime;

                // Application de la décélération progressive via une courbe
                float dashSpeedModifier = dashDecelerationCurve.Evaluate(dashProgress);
                _frameVelocity = _dashDirection * _stats.DashSpeed * dashSpeedModifier;

                _rb.gravityScale = 0.3f;

                // Si le temps de dash est écoulé, on arrête le dash
                if (_dashTimeRemaining <= 0)
                {
                    _isDashing = false;
                    _frameVelocity = Vector2.zero;
                    _rb.gravityScale = 1f;
                    _grabCooldownRemaining = _grabCooldownTime;
                }
            }

            // Recharge du dash au contact du sol
            if (_grounded && !_isDashing)
            {
                _canDash = true;
            }

            // Si le délai du grab est écoulé, on réactive le grab
            if (_grabCooldownRemaining > 0)
            {
                _grabCooldownRemaining -= Time.fixedDeltaTime;
            }
            else
            {
                _canGrab = true;
            }
        }

        private void CancelDash()
        {
            _isDashing = false;
            _frameVelocity = Vector2.zero;
            _rb.gravityScale = 1f;
            _grabCooldownRemaining = _grabCooldownTime;
        }
        
        #endregion



        #region Grab
        
        private bool _isTouchingWall;
        private bool _isGrabbingWall;
        private Vector2 _wallNormal;
        private float _currentClimbSpeed; 
        private bool isWallAbove;
        
        private void HandleWallGrab()
        {
            if (!_canGrab) return;
        
            // On détermine la direction du mur
            LayerMask mask = LayerMask.GetMask("Ground");
            RaycastHit2D hitLeft = Physics2D.Raycast(new Vector2(_col.bounds.min.x, _col.bounds.center.y), Vector2.left, _stats.GrounderDistance, mask);
            RaycastHit2D hitRight = Physics2D.Raycast(new Vector2(_col.bounds.max.x, _col.bounds.center.y), Vector2.right, _stats.GrounderDistance, mask);
        
            bool isTouchingLeftWall = hitLeft.collider != null;
            bool isTouchingRightWall = hitRight.collider != null;
        
            bool isTouchingWallAboveLeft = leftWallDetector.GetComponent<Collider2D>().IsTouchingLayers(LayerMask.GetMask("Ground"));
            bool isTouchingWallAboveRight = rightWallDetector.GetComponent<Collider2D>().IsTouchingLayers(LayerMask.GetMask("Ground"));
            isWallAbove = isTouchingWallAboveLeft || isTouchingWallAboveRight;
        
            // Si l'utilisateur maintient "Grab" et peut grab
            if (_frameInput.GrabHeld && isWallAbove)
            {
                bool canGrab = false;
        
                // Vérifie si un mur est présent dans la direction où le joueur regarde
                if (!_spriteRenderer.flipX && isTouchingRightWall)
                {
                    canGrab = true;
                }
                else if (_spriteRenderer.flipX && isTouchingLeftWall)
                {
                    canGrab = true;
                }
        
                // Si le joueur peut grab
                if (canGrab)
                {
                    // Annule le dash actif, s'il y en a un
                    if (_isDashing)
                    {
                        CancelDash(); 
                    }
        
                    // Active le grab 
                    if (!_isGrabbingWall)
                    {
                        _isGrabbingWall = true;
                        _frameVelocity = Vector2.zero;
                        _currentClimbSpeed = 0;
                    }
                }
            }
        
            // Logique pour maintenir ou annuler le grab
            if (_isGrabbingWall)
            {
                if (!(isTouchingLeftWall || isTouchingRightWall))
                {
                    _isGrabbingWall = false;
                    _frameVelocity.y = 0;
                }
                else
                {
                    float targetSpeed;
                    if (_frameInput.Move.y > 0) 
                    {
                        targetSpeed = _frameInput.Move.y * _stats.ClimbSpeedUp;
                    }
                    else if (_frameInput.Move.y < 0) 
                    {
                        targetSpeed = _frameInput.Move.y * _stats.ClimbSpeedDown;
                    }
                    else 
                    {
                        targetSpeed = 0;
                    }

                    if (targetSpeed != 0)
                    {
                        _currentClimbSpeed = Mathf.MoveTowards(_currentClimbSpeed, targetSpeed, _stats.ClimbAcceleration * Time.fixedDeltaTime);
                    }
                    else
                    {
                        _currentClimbSpeed = Mathf.MoveTowards(_currentClimbSpeed, 0, _stats.ClimbDeceleration * Time.fixedDeltaTime);
                    }
        
                    _frameVelocity.y = _currentClimbSpeed;
                }
            }
        
            // Si le joueur relâche "Grab", annule le grab
            if (_isGrabbingWall && !_frameInput.GrabHeld)
            {
                if (!_anim.GetCurrentAnimatorStateInfo(0).IsName("LedgeClimb"))
                {
                    _isGrabbingWall = false;
                    _frameVelocity.y = 0;
                }
            }
        }
        
        private void HandleWallMovement()
        {
            if (_isGrabbingWall)
            {
                // Empêche mouvement horizontal
                _frameVelocity.x = 0;
        
                _frameVelocity.y = _currentClimbSpeed;  
            }
        }
        
        #endregion

        #region Ledge 

        [SerializeField] private Transform lantern; 
        [SerializeField] private float lanternMoveDuration = 0.5f; 
        [SerializeField] private Vector3 lanternOriginalOffset = Vector3.zero; 
        
        private void CheckForLedge()
        {
            if ((ledgeDetected || ledgeDetectedLeft) && canGrabLedge)
            {
                canGrabLedge = false;
        
                Vector2 ledgePosition = GetComponentInChildren<LedgeDetection>().transform.position;
                if (ledgeDetected)
                {
                    climbBegunPosition = ledgePosition + offset1Right;
                    climbOverPosition = ledgePosition + offset2Right;
                }
                else if (ledgeDetectedLeft)
                {
                    climbBegunPosition = ledgePosition + offset1Left;
                    climbOverPosition = ledgePosition + offset2Left;
                }

                // Bloquer le Wall Jump temporairement après la grimpe
                _ledgeClimbCooldownEndTime = Time.time + _ledgeClimbCooldownTime;
        
                // Désactive la physique
                _rb.bodyType = RigidbodyType2D.Kinematic; 
                
                // Positionne le joueur au début de la grimpe
                transform.position = climbBegunPosition;
        
                // Lance l'animation de la lanterne
                if (lantern != null)
                {
                    StartCoroutine(MoveLantern(climbBegunPosition, climbOverPosition));
                }
        
                Invoke("ReactivatePhysics", 0.15f);
                canClimb = true;
            }
        }
        
        private void LedgeClimbOver()
        {
            canClimb = false;
            transform.position = climbOverPosition;
            ResetLedgeDetection();
            Invoke("AllowLedgeGrab", .2f);
        }
        
        private void ReactivatePhysics()
        {
            _rb.bodyType = RigidbodyType2D.Dynamic;
        }
        
        private void ResetLedgeDetection()
        {
            ledgeDetected = false;
            ledgeDetectedLeft = false;
            canClimb = false;
        }
        
        private void AllowLedgeGrab() => canGrabLedge = true;
        
        // Coroutine pour déplacer la lanterne progressivement
        private IEnumerator MoveLantern(Vector2 startPosition, Vector2 endPosition)
        {
            float elapsedTime = 0f;
        
            // Déplacement progressif de startPosition à endPosition
            while (elapsedTime < lanternMoveDuration)
            {
                lantern.position = Vector2.Lerp(startPosition, endPosition, elapsedTime / lanternMoveDuration);
                elapsedTime += Time.deltaTime;
                yield return null;
            }
        
            // S'assure que la lanterne est exactement à la position finale
            lantern.position = endPosition;
        
            // Réinitialisation de la position de la lanterne après le mouvement
            yield return new WaitForSeconds(0.2f); 
            if (lantern != null)
            {
                lantern.localPosition = lanternOriginalOffset;
            }
        }
        
        #endregion
        
        #region Horizontal

        private void HandleDirection()
        {
            if (_frameInput.Move.x == 0)
            {
                var deceleration = _grounded ? _stats.GroundDeceleration : _stats.AirDeceleration;
                _frameVelocity.x = Mathf.MoveTowards(_frameVelocity.x, 0, deceleration * Time.fixedDeltaTime);
            }
            else
            {
                // Mise à jour de la direction du personnage
                facingDirection = Mathf.Sign(_frameInput.Move.x);

                _frameVelocity.x = Mathf.MoveTowards(_frameVelocity.x, _frameInput.Move.x * _stats.MaxSpeed, _stats.Acceleration * Time.fixedDeltaTime);
            }
        }


        #endregion

        #region Gravity

        private void HandleGravity()
        {
            if (canClimb)
            {
                _rb.gravityScale = 0; 
                _frameVelocity = Vector2.zero; 
                return; 
            }
            
            if (_isGrabbingWall)
            {
                _rb.gravityScale = 0f; 
            }
            else
            {
                _rb.gravityScale = 1f;
            }
        
            // Autres logiques de gravité comme la chute
            if (_grounded && _frameVelocity.y <= 0f)
            {
                _frameVelocity.y = _stats.GroundingForce;
            }
            else
            {
                float fallSpeed = _stats.MaxFallSpeed;
                if (_frameInput.Move.y < 0)  
                {
                    fallSpeed *= _stats.FastFallMultiplier;
                }
        
                var inAirGravity = _stats.FallAcceleration;
                if (_endedJumpEarly && _frameVelocity.y > 0) inAirGravity *= _stats.JumpEndEarlyGravityModifier;
                _frameVelocity.y = Mathf.MoveTowards(_frameVelocity.y, -fallSpeed, inAirGravity * Time.fixedDeltaTime);
            }
        }


        #endregion

        private void ApplyMovement() => _rb.linearVelocity = _frameVelocity;
        
        // Cleaning ? pls
        private float facingDirection = 1f;
        private void OnDrawGizmos()
        {
            Gizmos.DrawLine(wallCheck.position, new Vector3(wallCheck.position.x + wallCheckDistance * facingDirection,
                                                           wallCheck.position.y,
                                                           wallCheck.position.z));

            Debug.DrawLine(transform.position, climbBegunPosition, Color.red, 1f);
            Debug.DrawLine(transform.position, climbOverPosition, Color.green, 1f);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_stats == null) Debug.LogWarning("Please assign a ScriptableStats asset to the Player Controller's Stats slot", this);
        }
#endif
    }

    public struct FrameInput
    {
        public bool JumpDown;
        public bool JumpHeld;
        public Vector2 Move;
        public bool DashDown;
        public bool GrabDown;
        public bool GrabHeld;
    }

    public interface IPlayerController
    {
        public event Action<bool, float> GroundedChanged;

        public event Action Jumped;
        public event Action WallJumped;
        public Vector2 FrameInput { get; }
        public Vector2 Velocity { get; }
        bool IsDashing { get; }
        public bool IsGrabbingWall { get; }
        public bool IsWallSliding { get; }
        public bool IsSliding { get; }
        bool canClimb { get; }
        float CurrentClimbSpeed { get; }
        bool IsGrounded { get; }
    }
}
