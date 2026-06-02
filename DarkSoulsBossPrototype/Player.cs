using DarkSoulsBossPrototype;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;

namespace DarkSoulsBossPrototype
{
    // =========================================================================
    //  Player  v3
    //
    //  Animation engine rewritten from scratch based on measured sprite sheets:
    //
    //  Sheet       Grid      Frame px   Total frames   Layout
    //  --------    -------   --------   ------------   ------
    //  Idle        2 x 4     128 x 64        8         multi-row grid
    //  Run         2 x 4     128 x 64        8         multi-row grid
    //  Death       2 x 2     128 x 64        4         multi-row grid
    //  Hurt        2 x 2     128 x 64        4         multi-row grid
    //  Attacks     8 x 5     128 x 64       40         multi-row grid
    //
    //  All sheets store frames left-to-right, then top-to-bottom.
    //  The source rectangle is calculated as:
    //    col = linearFrame % cols
    //    row = linearFrame / cols
    //    sourceRect = (col * frameW, row * frameH, frameW, frameH)
    //
    //  The previous code assumed every sheet was a single horizontal strip
    //  (row always 0), which produced wrong frames for every multi-row sheet.
    // =========================================================================
    public class Player
    {
        // =====================================================================
        //  Physical constants
        // =====================================================================

        // Standing hitbox
        public const int WIDTH = 64;
        public const int HEIGHT = 64;    // full standing height

        // Crouching hitbox — same width, half the height, anchored to the ground
        // so Position.Y stays the same but the top of the box shifts down
        public const int CROUCH_HEIGHT = 32;

        // Draw scale applied to each sprite frame (128x64 -> 256x128 on screen)
        private const float DRAW_SCALE = 2.0f;

        // Jump physics
        private const float JUMP_VELOCITY = -520f;   // px/s upward (negative = up in screen space)
        private const float GRAVITY = 980f;   // px/s^2 downward acceleration
        private const float MAX_FALL_SPEED = 900f;   // terminal velocity cap

        // =====================================================================
        //  Skill points and stats
        // =====================================================================
        public int SkillPoints { get; set; }

        // Position = top-left corner of the STANDING hitbox in world space.
        // When crouching the hitbox shrinks upward from the bottom, so Position.Y
        // advances by (HEIGHT - CROUCH_HEIGHT) to keep the feet on the ground.
        public Vector2 Position { get; set; }

        // ── Physics state ─────────────────────────────────────────────────────
        // Vertical velocity in px/s. Negative = moving up, positive = moving down.
        private float _velocityY = 0f;

        // True when the player is standing on the ground (not airborne).
        // Gates the jump so double-jumping is impossible.
        private bool _isGrounded = true;

        // The Y coordinate of the ground line (bottom of the standing hitbox).
        // Set once from the spawn position and never changes, so the player
        // always returns to exactly the same floor level after a jump.
        private float _groundY;

        // ── Dynamic hitbox ────────────────────────────────────────────────────
        // Returns the correct Rectangle depending on whether the player is crouching.
        // Crouch keeps the BOTTOM of the box at the same world position as standing
        // (feet stay on the ground), so only the top edge moves down.
        public Rectangle Bounds
        {
            get
            {
                bool crouching = CurrentAnimState == PlayerAnimationState.Crouching;
                int h = crouching ? CROUCH_HEIGHT : HEIGHT;
                // When crouching, shift Position.Y down so the bottom stays grounded
                int yOffset = crouching ? HEIGHT - CROUCH_HEIGHT : 0;
                return new Rectangle((int)Position.X, (int)Position.Y + yOffset, WIDTH, h);
            }
        }

        // Centre of the current (possibly crouched) hitbox
        public Vector2 Centre
        {
            get
            {
                Rectangle b = Bounds;
                return new Vector2(b.X + b.Width * 0.5f, b.Y + b.Height * 0.5f);
            }
        }

        public float CurrentHP { get; set; }
        public bool IsDead => CurrentHP <= 0f;

        // Base stats (before skill-tree modifiers)
        private float baseMaxHP = 100f;
        private float baseMoveSpeed = 200f;
        private float baseAttackDamage = 15f;
        private float baseDamageReduction = 0.0f;
        private float baseArmorPenetration = 0.0f;

        // Active stats (recalculated by RecalculateStats)
        public float MaxHP { get; private set; }
        public float MoveSpeed { get; private set; }
        public float AttackDamage { get; private set; }
        public float DamageReduction { get; private set; }
        public float ArmorPenetration { get; private set; }

        // Modifier accumulators
        private float _totalMaxHPBonus = 0f;
        private float _totalMoveSpeedPct = 0f;
        private float _totalAttackDamagePct = 0f;
        private float _totalDamageReduction = 0f;
        private float _totalArmorPenetration = 0f;

        // Per-node modifier constants
        private const float HP_BONUS_PER_NODE = 20f;
        private const float MOVE_SPEED_PCT_PER_NODE = 0.10f;
        private const float ATTACK_DAMAGE_PCT_PER_NODE = 0.15f;
        private const float DAMAGE_REDUCTION_PER_NODE = 0.08f;
        private const float ARMOR_PEN_PER_NODE = 0.12f;
        private const float MAX_DAMAGE_REDUCTION = 0.90f;

        // =====================================================================
        //  Animation state
        // =====================================================================

        public enum PlayerAnimationState
        {
            Idle,
            Running,
            LightAttack1,   // LMB first click  — upswing    L0-L7
            LightAttack2,   // LMB combo click  — followup   L8-L15
            Stab,           // Q key            — stab       L8-L15
            HeavyAttack,    // Z key            — downswing  L16-L23
            Hurt,
            Dead,
            Crouching,
            Jumping
        }
        public PlayerAnimationState CurrentAnimState { get; private set; } = PlayerAnimationState.Idle;

        // ── Combo window ──────────────────────────────────────────────────────
        // After LightAttack1 finishes, a combo is available for COMBO_WINDOW seconds.
        // If the player clicks LMB within that window, LightAttack2 plays.
        private const float COMBO_WINDOW = 0.35f;  // seconds to input the combo
        private bool _comboWindowOpen = false;  // true while window is live
        private float _comboWindowTimer = 0f;     // counts down from COMBO_WINDOW
        private bool _comboPending = false;  // LMB was pressed during window

        // ── Sprite sheet references ───────────────────────────────────────────
        private Texture2D _sheetIdle;
        private Texture2D _sheetRun;
        private Texture2D _sheetAttack;
        private Texture2D _sheetHurt;
        private Texture2D _sheetDeath;
        private Texture2D _sheetCrouch;   // crouch_idle.png  — 2x4 grid, 128x64, 8 frames
        private Texture2D _sheetJump;     // Jump.png         — 2x4 grid, 128x64, 8 frames

        // ── Active sheet + grid description ──────────────────────────────────
        // Updated every time SetAnimation() is called.
        private Texture2D _activeSheet;
        private int _frameCols;      // number of columns in the active sheet
        private int _frameRows;      // number of rows in the active sheet
        private int _frameW;         // pixel width of one frame cell
        private int _frameH;         // pixel height of one frame cell
        private int _totalFrames;    // cols * rows

        // ── Playback state ────────────────────────────────────────────────────
        private int _linearFrame = 0;   // 0 .. totalFrames-1, read left-to-right then down
        private float _frameTimer = 0f;
        private float _frameDuration = 0.10f;   // seconds per frame

        // ── Direction ────────────────────────────────────────────────────────
        private bool _facingLeft = false;

        // ── One-shot animation flags ──────────────────────────────────────────
        // Hurt and Attack play once then return to Idle/Run automatically.
        private bool _playingOneShot = false;

        // ── Input edge detection ──────────────────────────────────────────────
        private KeyboardState _prevKb;
        private MouseState _prevMs;   // previous frame mouse state for LMB edge detect

        // ── 1x1 pixel fallback texture (created in LoadContent) ───────────────
        private Texture2D _pixel;

        // =====================================================================
        //  Sheet descriptor  — grid layout for non-attack sheets
        // =====================================================================
        private readonly struct SheetInfo
        {
            public readonly int Cols;
            public readonly int Rows;
            public readonly int FrameW;
            public readonly int FrameH;
            public readonly float Duration;   // seconds per frame (0 = use 0.10f default)
            public readonly bool Loop;

            public SheetInfo(int cols, int rows, int fw, int fh,
                             float duration = 0f, bool loop = true)
            {
                Cols = cols; Rows = rows; FrameW = fw; FrameH = fh;
                Duration = duration; Loop = loop;
            }
        }

        // =====================================================================
        //  AttackSegment  — defines one attack's slice inside Attacks.png
        //
        //  All four attacks share a single Texture2D (_sheetAttack = Attacks.png).
        //  Each segment records where its frames start in the LINEAR frame index
        //  and how many frames it contains, so DrawSprite samples the right cells.
        //
        //  Pixel-verified layout (Attacks.png  1024x320, 8 cols x 5 rows, 128x64):
        //
        //    Attack          Key   Linear range   Count  Duration (s/frame)
        //    --------------- ---   ------------   -----  ------------------
        //    Upswing         Z      0 ..  6         7       0.07
        //    Downswing       X      7 .. 13         7       0.07
        //    Stab            C     14 .. 19         6       0.06
        //    Heavy Slash     V     20 .. 28         9       0.09
        //
        //  Left-facing is handled by SpriteEffects.FlipHorizontally on the single
        //  right-facing sheet — no mirrored sheet is needed.
        // =====================================================================
        private readonly struct AttackSegment
        {
            public readonly int StartFrame;    // first linear frame index
            public readonly int FrameCount;    // number of frames in this attack
            public readonly float Duration;      // seconds per frame for this attack

            // Last valid linear frame index (inclusive)
            public int EndFrame => StartFrame + FrameCount - 1;

            public AttackSegment(int start, int count, float duration)
            {
                StartFrame = start; FrameCount = count; Duration = duration;
            }
        }

        // Pixel-verified sheet descriptors
        private static readonly SheetInfo InfoIdle = new SheetInfo(2, 4, 128, 64, 0.12f, loop: true);
        private static readonly SheetInfo InfoRun = new SheetInfo(2, 4, 128, 64, 0.09f, loop: true);
        private static readonly SheetInfo InfoAttack = new SheetInfo(8, 5, 128, 64, 0.06f, loop: false);
        private static readonly SheetInfo InfoHurt = new SheetInfo(2, 2, 128, 64, 0.10f, loop: false);
        private static readonly SheetInfo InfoDeath = new SheetInfo(2, 2, 128, 64, 0.14f, loop: false);
        private static readonly SheetInfo InfoCrouch = new SheetInfo(2, 4, 128, 64, 0.12f, loop: true);
        private static readonly SheetInfo InfoJump = new SheetInfo(2, 4, 128, 64, 0.09f, loop: false);

        // Pixel-verified attack segments — slices inside Attacks.png (8 cols x 5 rows, 128x64)
        //
        //   Input   Animation       Linear range   Frames  s/frame
        //   ------  --------------- ------------   ------  -------
        //   LMB 1   Light upswing   L0  - L6          8    0.07
        //   LMB 2   Light followup  L7  - L9         8    0.07   (combo after hit 1)
        //   Q       Stab            L8  - L15         8    0.06   (same row as followup)
        //   Z       Heavy downswing L14 - L19         8    0.09
        //
        //   All rows are right-facing. FlipHorizontally handles left-facing.
        private static readonly AttackSegment SegLightSwing = new AttackSegment(0, 7, 0.07f);
        private static readonly AttackSegment SegLightCombo = new AttackSegment(6, 4, 0.07f);
        private static readonly AttackSegment SegStab = new AttackSegment(9, 5, 0.06f);
        private static readonly AttackSegment SegHeavy = new AttackSegment(14, 6, 0.09f);

        // Active segment — valid whenever CurrentAnimState is an attack state
        private AttackSegment _activeSegment;

        // =====================================================================
        //  Constructor
        // =====================================================================
        public Player(Vector2 startPosition, int startingSkillPoints = 5)
        {
            Position = startPosition;
            SkillPoints = startingSkillPoints;
            _prevKb = Keyboard.GetState();
            _prevMs = Mouse.GetState();

            _groundY = startPosition.Y;
            _isGrounded = true;
            _velocityY = 0f;

            RecalculateStats(new List<SkillNode>());
            CurrentHP = MaxHP;
        }

        // =====================================================================
        //  LoadContent
        //  Call once from Game1.LoadContent() after the Content pipeline is ready.
        //
        //  Expected Content project names (add these to Content.mgcb):
        //    knight_idle    -> Idle.png
        //    knight_run     -> Run.png
        //    knight_attack  -> Attacks.png
        //    knight_hurt    -> Hurt.png
        //    knight_death   -> Death.png
        // =====================================================================
        public void LoadContent(Microsoft.Xna.Framework.Content.ContentManager content,
                                GraphicsDevice graphicsDevice)
        {
            // 1x1 white pixel for HP bar and UI fills
            _pixel = new Texture2D(graphicsDevice, 1, 1);
            _pixel.SetData(new[] { Color.White });

            // Load sprite sheets from the Content pipeline
            _sheetIdle = content.Load<Texture2D>("PlayerSpriteSheets/Idle");
            _sheetRun = content.Load<Texture2D>("PlayerSpriteSheets/Run");
            _sheetAttack = content.Load<Texture2D>("PlayerSpriteSheets/Attacks");
            _sheetHurt = content.Load<Texture2D>("PlayerSpriteSheets/Hurt");
            _sheetDeath = content.Load<Texture2D>("PlayerSpriteSheets/Death");
            _sheetCrouch = content.Load<Texture2D>("PlayerSpriteSheets/crouch_idle");
            _sheetJump = content.Load<Texture2D>("PlayerSpriteSheets/Jump");

            // Validate that every loaded sheet matches its expected dimensions
            ValidateSheet(_sheetIdle, InfoIdle, "knight_idle");
            ValidateSheet(_sheetRun, InfoRun, "knight_run");
            ValidateSheet(_sheetAttack, InfoAttack, "knight_attack");
            ValidateSheet(_sheetHurt, InfoHurt, "knight_hurt");
            ValidateSheet(_sheetDeath, InfoDeath, "knight_death");
            ValidateSheet(_sheetCrouch, InfoCrouch, "knight_crouch");
            ValidateSheet(_sheetJump, InfoJump, "knight_jump");

            // Boot into Idle animation
            SetAnimation(PlayerAnimationState.Idle);
        }

        // Overload for callers that only pass ContentManager (pixel created separately)
        public void LoadContent(Microsoft.Xna.Framework.Content.ContentManager content)
        {
            _sheetIdle = content.Load<Texture2D>("knight_idle");
            _sheetRun = content.Load<Texture2D>("knight_run");
            _sheetAttack = content.Load<Texture2D>("knight_attack");
            _sheetHurt = content.Load<Texture2D>("knight_hurt");
            _sheetDeath = content.Load<Texture2D>("knight_death");
            _sheetCrouch = content.Load<Texture2D>("knight_crouch");
            _sheetJump = content.Load<Texture2D>("knight_jump");

            ValidateSheet(_sheetIdle, InfoIdle, "knight_idle");
            ValidateSheet(_sheetRun, InfoRun, "knight_run");
            ValidateSheet(_sheetAttack, InfoAttack, "knight_attack");
            ValidateSheet(_sheetHurt, InfoHurt, "knight_hurt");
            ValidateSheet(_sheetDeath, InfoDeath, "knight_death");
            ValidateSheet(_sheetCrouch, InfoCrouch, "knight_crouch");
            ValidateSheet(_sheetJump, InfoJump, "knight_jump");

            SetAnimation(PlayerAnimationState.Idle);
        }

        // Prints a warning if the loaded sheet does not match the expected grid
        private static void ValidateSheet(Texture2D sheet, SheetInfo info, string name)
        {
            if (sheet == null) return;
            int expectedW = info.Cols * info.FrameW;
            int expectedH = info.Rows * info.FrameH;
            if (sheet.Width != expectedW || sheet.Height != expectedH)
            {
                Console.WriteLine(
                    "[Player] WARNING: " + name +
                    " expected " + expectedW + "x" + expectedH +
                    " but got " + sheet.Width + "x" + sheet.Height +
                    ". Animation may be offset.");
            }
        }

        // =====================================================================
        //  SetAnimation
        // =====================================================================
        private void SetAnimation(PlayerAnimationState newState, bool forceRestart = false)
        {
            if (CurrentAnimState == newState && !forceRestart && _activeSheet != null)
                return;

            CurrentAnimState = newState;
            _frameTimer = 0f;
            _playingOneShot = false;

            bool isAttack = newState == PlayerAnimationState.LightAttack1
                         || newState == PlayerAnimationState.LightAttack2
                         || newState == PlayerAnimationState.Stab
                         || newState == PlayerAnimationState.HeavyAttack;

            if (isAttack)
            {
                _activeSheet = _sheetAttack;
                _frameCols = InfoAttack.Cols;
                _frameRows = InfoAttack.Rows;
                _frameW = InfoAttack.FrameW;
                _frameH = InfoAttack.FrameH;
                _totalFrames = InfoAttack.Cols * InfoAttack.Rows;

                switch (newState)
                {
                    case PlayerAnimationState.LightAttack1:
                        _activeSegment = SegLightSwing; break;
                    case PlayerAnimationState.LightAttack2:
                        _activeSegment = SegLightCombo; break;
                    case PlayerAnimationState.Stab:
                        _activeSegment = SegStab; break;
                    default: // HeavyAttack
                        _activeSegment = SegHeavy; break;
                }

                _linearFrame = _activeSegment.StartFrame;
                _frameDuration = _activeSegment.Duration;
                _playingOneShot = true;
                return;
            }

            _linearFrame = 0;

            SheetInfo info;
            switch (newState)
            {
                case PlayerAnimationState.Idle:
                    _activeSheet = _sheetIdle; info = InfoIdle; break;
                case PlayerAnimationState.Running:
                    _activeSheet = _sheetRun; info = InfoRun; break;
                case PlayerAnimationState.Hurt:
                    _activeSheet = _sheetHurt; info = InfoHurt;
                    _playingOneShot = true; break;
                case PlayerAnimationState.Dead:
                    _activeSheet = _sheetDeath; info = InfoDeath; break;
                case PlayerAnimationState.Crouching:
                    _activeSheet = _sheetCrouch; info = InfoCrouch; break;
                case PlayerAnimationState.Jumping:
                    _activeSheet = _sheetJump; info = InfoJump;
                    _playingOneShot = true; break;
                default:
                    _activeSheet = _sheetIdle; info = InfoIdle; break;
            }

            _frameCols = info.Cols;
            _frameRows = info.Rows;
            _frameW = info.FrameW;
            _frameH = info.FrameH;
            _totalFrames = info.Cols * info.Rows;
            _frameDuration = info.Duration > 0f ? info.Duration : 0.10f;
        }

        private bool IsAttacking =>
            CurrentAnimState == PlayerAnimationState.LightAttack1 ||
            CurrentAnimState == PlayerAnimationState.LightAttack2 ||
            CurrentAnimState == PlayerAnimationState.Stab ||
            CurrentAnimState == PlayerAnimationState.HeavyAttack;

        // =====================================================================
        //  Update
        //
        //  Controls
        //  --------
        //  A / Left     Move left
        //  D / Right    Move right
        //  W / Space    Jump (grounded only, no double jump)
        //  S / Down     Crouch (grounded only)
        //  LMB          Light attack 1 — upswing (L0-L7)
        //               If pressed again within COMBO_WINDOW after hit 1 finishes,
        //               automatically chains into Light attack 2 — followup (L8-L15)
        //  Q            Stab (L8-L15) — plays independently, no combo
        //  Z            Heavy downswing (L16-L23)
        //
        //  Combo logic
        //  -----------
        //  When LightAttack1 finishes, a COMBO_WINDOW timer starts.
        //  If LMB is pressed while _comboWindowOpen == true, _comboPending is set.
        //  On the next Update the combo window check fires LightAttack2.
        //  If the window expires without a click, the combo is lost.
        // =====================================================================
        public void Update(GameTime gameTime, Rectangle worldBounds)
        {
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            var kb = Keyboard.GetState();
            var ms = Mouse.GetState();

            // ── 1. Read input (edge-detect: fires once per press) ─────────────
            bool jumpPressed = (kb.IsKeyDown(Keys.W) && _prevKb.IsKeyUp(Keys.W))
                            || (kb.IsKeyDown(Keys.Space) && _prevKb.IsKeyUp(Keys.Space));
            bool crouchHeld = kb.IsKeyDown(Keys.S) || kb.IsKeyDown(Keys.Down);

            // Left mouse button — edge detect
            bool lmbPressed = ms.LeftButton == ButtonState.Pressed
                            && _prevMs.LeftButton == ButtonState.Released;

            // Q = stab, Z = heavy — edge detect
            bool stabPressed = kb.IsKeyDown(Keys.Q) && _prevKb.IsKeyUp(Keys.Q);
            bool heavyPressed = kb.IsKeyDown(Keys.Z) && _prevKb.IsKeyUp(Keys.Z);

            float horizInput = 0f;
            if (!IsDead)
            {
                if (kb.IsKeyDown(Keys.A) || kb.IsKeyDown(Keys.Left)) horizInput -= 1f;
                if (kb.IsKeyDown(Keys.D) || kb.IsKeyDown(Keys.Right)) horizInput += 1f;
            }

            // ── 2. Combo window timer ─────────────────────────────────────────
            // Counts down whenever the window is open.
            // A LMB press during the window sets _comboPending so the combo
            // fires at the top of the state machine on the next Update.
            if (_comboWindowOpen)
            {
                _comboWindowTimer -= dt;

                if (lmbPressed)
                    _comboPending = true;

                if (_comboWindowTimer <= 0f)
                {
                    _comboWindowOpen = false;
                    _comboWindowTimer = 0f;
                    _comboPending = false;   // window expired, combo lost
                }
            }

            // ── 3. Jump — grounded only, no double jump ───────────────────────
            if (jumpPressed && _isGrounded && !IsDead && !_playingOneShot)
            {
                _velocityY = JUMP_VELOCITY;
                _isGrounded = false;
            }

            // ── 4. Gravity ────────────────────────────────────────────────────
            if (!_isGrounded)
            {
                _velocityY += GRAVITY * dt;
                _velocityY = Math.Min(_velocityY, MAX_FALL_SPEED);
            }

            // ── 5. Vertical integration ───────────────────────────────────────
            float newY = Position.Y + _velocityY * dt;
            if (newY >= _groundY) { newY = _groundY; _velocityY = 0f; _isGrounded = true; }
            if (newY < worldBounds.Top) { newY = worldBounds.Top; _velocityY = 0f; }

            // ── 6. Horizontal integration ─────────────────────────────────────
            float newX = Position.X + horizInput * MoveSpeed * dt;
            newX = Math.Clamp(newX, worldBounds.Left, worldBounds.Right - WIDTH);
            Position = new Vector2(newX, newY);

            if (horizInput < 0f) _facingLeft = true;
            if (horizInput > 0f) _facingLeft = false;

            // ── 7. Animation state machine ────────────────────────────────────
            // Priority: Dead > one-shot in progress > combo pending
            //           > new attack input > airborne > crouch > run > idle
            if (!IsDead && !_playingOneShot)
            {
                if (_comboPending)
                {
                    // Player clicked LMB during the combo window after LightAttack1
                    _comboPending = false;
                    _comboWindowOpen = false;
                    SetAnimation(PlayerAnimationState.LightAttack2);
                }
                else if (lmbPressed)
                {
                    // Fresh LMB press — always starts LightAttack1
                    SetAnimation(PlayerAnimationState.LightAttack1);
                }
                else if (stabPressed)
                {
                    SetAnimation(PlayerAnimationState.Stab);
                }
                else if (heavyPressed)
                {
                    SetAnimation(PlayerAnimationState.HeavyAttack);
                }
                else if (!_isGrounded)
                {
                    SetAnimation(PlayerAnimationState.Jumping);
                }
                else if (crouchHeld)
                {
                    SetAnimation(PlayerAnimationState.Crouching);
                }
                else if (horizInput != 0f)
                {
                    SetAnimation(PlayerAnimationState.Running);
                }
                else
                {
                    SetAnimation(PlayerAnimationState.Idle);
                }
            }

            // ── 8. Advance frame counter ──────────────────────────────────────
            if (_activeSheet != null)
            {
                _frameTimer += dt;
                if (_frameTimer >= _frameDuration)
                {
                    _frameTimer -= _frameDuration;
                    _linearFrame++;

                    if (IsAttacking)
                    {
                        // Each attack only plays its own segment slice.
                        // When the last frame of the segment is passed, finish.
                        if (_linearFrame > _activeSegment.EndFrame)
                        {
                            _playingOneShot = false;

                            // LightAttack1 ending opens the combo window
                            if (CurrentAnimState == PlayerAnimationState.LightAttack1)
                            {
                                _comboWindowOpen = true;
                                _comboWindowTimer = COMBO_WINDOW;
                                _comboPending = false;
                            }

                            // Return to correct follow-up state
                            if (!_isGrounded) SetAnimation(PlayerAnimationState.Jumping);
                            else if (crouchHeld) SetAnimation(PlayerAnimationState.Crouching);
                            else if (horizInput != 0f) SetAnimation(PlayerAnimationState.Running);
                            else SetAnimation(PlayerAnimationState.Idle);
                        }
                    }
                    else if (CurrentAnimState == PlayerAnimationState.Dead)
                    {
                        _linearFrame = _totalFrames - 1;   // hold last frame forever
                    }
                    else if (_playingOneShot)
                    {
                        // Hurt or Jump finished
                        if (_linearFrame >= _totalFrames)
                        {
                            _playingOneShot = false;
                            if (!_isGrounded) SetAnimation(PlayerAnimationState.Jumping);
                            else if (crouchHeld) SetAnimation(PlayerAnimationState.Crouching);
                            else if (horizInput != 0f) SetAnimation(PlayerAnimationState.Running);
                            else SetAnimation(PlayerAnimationState.Idle);
                        }
                    }
                    else
                    {
                        // Looping states: Idle, Run, Crouch
                        if (_linearFrame >= _totalFrames)
                            _linearFrame = 0;
                    }
                }
            }

            _prevKb = kb;
            _prevMs = ms;
        }

        // =====================================================================
        //  TriggerHurt
        //  Call from boss hit detection instead of TakeDamage directly.
        //  Applies damage then plays the correct animation (Hurt or Dead).
        //  If an attack is currently playing it is interrupted by damage.
        // =====================================================================
        public void TriggerHurt(float rawDamage)
        {
            TakeDamage(rawDamage);
            if (!IsDead)
                SetAnimation(PlayerAnimationState.Hurt, forceRestart: true);
            else
                SetAnimation(PlayerAnimationState.Dead, forceRestart: true);
        }

        // =====================================================================
        //  Draw
        //  Renders the current animation frame using the correct grid-based
        //  source rectangle.
        //
        //  Source rect formula:
        //    col = _linearFrame % _frameCols
        //    row = _linearFrame / _frameCols
        //    src = (col * _frameW, row * _frameH, _frameW, _frameH)
        //
        //  The sprite is drawn centred on the hitbox centre (Position + half
        //  WIDTH/HEIGHT), scaled by DRAW_SCALE, with horizontal flip for left
        //  facing.  The HP bar is always drawn above the hitbox.
        // =====================================================================
        public void Draw(SpriteBatch spriteBatch, Texture2D pixelOverride = null)
        {
            // Resolve pixel texture: use override, or fall back to our own
            Texture2D px = pixelOverride ?? _pixel;

            if (_activeSheet != null)
                DrawSprite(spriteBatch);
            else if (px != null)
                DrawFallbackRect(spriteBatch, px);

            if (px != null)
                DrawHPBar(spriteBatch, px);
        }

        // ── Core sprite renderer ──────────────────────────────────────────────
        private void DrawSprite(SpriteBatch sb)
        {
            // For attack states clamp within the segment's own frame range.
            // For all other states clamp within 0.._totalFrames-1.
            int frame;
            if (IsAttacking)
                frame = Math.Clamp(_linearFrame,
                                   _activeSegment.StartFrame,
                                   _activeSegment.EndFrame);
            else
                frame = Math.Clamp(_linearFrame, 0, _totalFrames - 1);

            // Convert linear index to 2D grid coordinates
            int col = frame % _frameCols;
            int row = frame / _frameCols;

            // Source rectangle: one cell in the multi-row grid
            var src = new Rectangle(col * _frameW, row * _frameH, _frameW, _frameH);

            // Draw position: centre of the hitbox
            Vector2 drawPos = Centre;

            // Origin at the centre of the source frame cell so flipping and
            // scaling both pivot around the sprite's own visual centre
            var origin = new Vector2(_frameW * 0.5f, _frameH * 0.5f);

            SpriteEffects flip = _facingLeft
                ? SpriteEffects.FlipHorizontally
                : SpriteEffects.None;

            sb.Draw(
                texture: _activeSheet,
                position: drawPos,
                sourceRectangle: src,
                color: Color.White,
                rotation: 0f,
                origin: origin,
                scale: new Vector2(DRAW_SCALE),
                effects: flip,
                layerDepth: 0f);
        }

        // ── Fallback coloured box (shown before LoadContent is called) ─────────
        private void DrawFallbackRect(SpriteBatch sb, Texture2D px)
        {
            sb.Draw(px, Bounds, new Color(100, 180, 255));
            const int B = 3;
            sb.Draw(px,
                new Rectangle(Bounds.X + B, Bounds.Y + B,
                               Bounds.Width - B * 2, Bounds.Height - B * 2),
                new Color(160, 220, 255));
        }

        // ── HP bar above the hitbox ───────────────────────────────────────────
        private void DrawHPBar(SpriteBatch sb, Texture2D px)
        {
            const int BAR_H = 6;
            const int OFFSET = 10;

            var bg = new Rectangle(Bounds.X, Bounds.Y - OFFSET - BAR_H, WIDTH, BAR_H);
            sb.Draw(px, bg, new Color(40, 40, 40, 200));

            float frac = MaxHP > 0f ? Math.Clamp(CurrentHP / MaxHP, 0f, 1f) : 0f;
            int fill = (int)(WIDTH * frac);
            if (fill <= 0) return;

            Color barColor = frac > 0.60f ? new Color(60, 200, 60)
                           : frac > 0.30f ? new Color(220, 200, 30)
                           : new Color(220, 50, 50);

            sb.Draw(px, new Rectangle(bg.X, bg.Y, fill, BAR_H), barColor);
        }

        // =====================================================================
        //  RecalculateStats
        // =====================================================================
        public void RecalculateStats(List<SkillNode> allNodes)
        {
            _totalMaxHPBonus = 0f;
            _totalMoveSpeedPct = 0f;
            _totalAttackDamagePct = 0f;
            _totalDamageReduction = 0f;
            _totalArmorPenetration = 0f;

            if (allNodes != null)
            {
                foreach (SkillNode node in allNodes)
                {
                    if (!node.IsUnlocked || node.SkillType == SkillType.Hub) continue;
                    switch (node.SkillType)
                    {
                        case SkillType.Healing:
                            _totalMaxHPBonus += HP_BONUS_PER_NODE; break;
                        case SkillType.MovementSpeed:
                            _totalMoveSpeedPct += MOVE_SPEED_PCT_PER_NODE; break;
                        case SkillType.DamageBoost:
                            _totalAttackDamagePct += ATTACK_DAMAGE_PCT_PER_NODE; break;
                        case SkillType.Armor:
                            _totalDamageReduction += DAMAGE_REDUCTION_PER_NODE; break;
                        case SkillType.ArmorPenetration:
                            _totalArmorPenetration += ARMOR_PEN_PER_NODE; break;
                    }
                }
            }

            MaxHP = baseMaxHP + _totalMaxHPBonus;
            MoveSpeed = baseMoveSpeed * (1f + _totalMoveSpeedPct);
            AttackDamage = baseAttackDamage * (1f + _totalAttackDamagePct);
            DamageReduction = Math.Min(baseDamageReduction + _totalDamageReduction, MAX_DAMAGE_REDUCTION);
            ArmorPenetration = Math.Clamp(baseArmorPenetration + _totalArmorPenetration, 0f, 1f);
            CurrentHP = Math.Clamp(CurrentHP, 0f, MaxHP);
        }

        // =====================================================================
        //  Gameplay helpers
        // =====================================================================
        public void TakeDamage(float rawDamage)
        {
            if (rawDamage <= 0f) return;
            float final = Math.Max(rawDamage * (1f - DamageReduction), 0f);
            CurrentHP = Math.Clamp(CurrentHP - final, 0f, MaxHP);
        }

        public void Heal(float amount)
        {
            if (amount <= 0f) return;
            CurrentHP = Math.Clamp(CurrentHP + amount, 0f, MaxHP);
        }

        public void Respawn(Vector2 spawnPosition)
        {
            Position = spawnPosition;
            CurrentHP = MaxHP;
            _linearFrame = 0;
            _frameTimer = 0f;
            _playingOneShot = false;
            _velocityY = 0f;
            _isGrounded = true;
            _groundY = spawnPosition.Y;
            _comboWindowOpen = false;
            _comboWindowTimer = 0f;
            _comboPending = false;
            _prevKb = Keyboard.GetState();
            _prevMs = Mouse.GetState();
            SetAnimation(PlayerAnimationState.Idle, forceRestart: true);
        }

        public void RecordDeath(SaveData saveData)
        {
            if (saveData == null) return;
            saveData.PlayerDeathsToBoss++;
        }

        public string GetStatsSummary()
        {
            return
                "HP: " + CurrentHP.ToString("F0") + "/" + MaxHP.ToString("F0") +
                "  SPD: " + MoveSpeed.ToString("F0") +
                "  ATK: " + AttackDamage.ToString("F1") +
                "  DEF: " + (DamageReduction * 100f).ToString("F0") + "%" +
                "  PEN: " + (ArmorPenetration * 100f).ToString("F0") + "%" +
                "  SP: " + SkillPoints;
        }
    }
}