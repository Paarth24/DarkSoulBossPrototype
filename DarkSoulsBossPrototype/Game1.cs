using DarkSoulsBossPrototype;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;

namespace DarkSoulsBossPrototype
{
    // -----------------------------------------------------------------------
    //  Game1.cs
    //  Full working integration of Player (rectangle + WASD), VisualSkillTree,
    //  SaveSystem, and a 1x1 pixel texture for all solid drawing.
    // -----------------------------------------------------------------------

    public class Game1 : Game
    {
        // ── MonoGame boilerplate ──────────────────────────────────────────────
        private GraphicsDeviceManager _graphics;
        private SpriteBatch _spriteBatch;

        // ── Core systems ──────────────────────────────────────────────────────
        private Player _player;
        private VisualSkillTree _skillTree;
        private SaveData _save;

        // ── Shared 1x1 pixel texture used for all solid rectangle drawing ─────
        // Created once in LoadContent; passed to Player.Draw() every frame.
        private Texture2D _pixel;

        // ── Optional assets (leave null to use fallbacks) ─────────────────────
        private SpriteFont _font;        // Content.Load<SpriteFont>("Fonts/UIFont")
        private Texture2D _nodeTexture; // Content.Load<Texture2D>("UI/skill_node")

        // ── State ─────────────────────────────────────────────────────────────
        private int _prevUnlockCount = 0;
        private bool _showSkillTree = false;
        private KeyboardState _prevKb;

        // Screen dimensions — change these to match your window size
        private const int SCREEN_W = 1280;
        private const int SCREEN_H = 720;

        public Game1()
        {
            _graphics = new GraphicsDeviceManager(this);
            _graphics.PreferredBackBufferWidth = SCREEN_W;
            _graphics.PreferredBackBufferHeight = SCREEN_H;
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
        }

        // =====================================================================
        //  Initialize
        // =====================================================================
        protected override void Initialize()
        {
            var screenCentre = new Vector2(SCREEN_W / 2f, SCREEN_H / 2f);

            // Load or create save data
            _save = SaveSystem.Load();

            // Build the skill tree fresh every session
            _skillTree = new VisualSkillTree(
                screenCentre,
                initialCurrency: _save.PlayerDeathsToBoss * 2 + 5);

            // Create the player.
            // Position is the TOP-LEFT corner of the rectangle (not the centre),
            // so subtract half the box size to start centred on screen.
            var startPos = new Vector2(
                SCREEN_W / 2f - Player.WIDTH / 2f,
                SCREEN_H / 2f - Player.HEIGHT / 2f);

            _player = new Player(startPosition: startPos, startingSkillPoints: 5);
            _player.RecalculateStats(new List<SkillNode>(_skillTree.AllNodes));

            _prevUnlockCount = CountUnlocked();
            _prevKb = Keyboard.GetState();

            base.Initialize();
        }

        // =====================================================================
        //  LoadContent
        // =====================================================================
        protected override void LoadContent()
        {
            _spriteBatch = new SpriteBatch(GraphicsDevice);
            _skillTree.LoadContent(GraphicsDevice);

            // Shared 1x1 white pixel — for HP bar, UI panels, skill tree lines
            _pixel = new Texture2D(GraphicsDevice, 1, 1);
            _pixel.SetData(new[] { Color.White });

            // Load all five sprite sheets + create the internal pixel texture.
            // Requires these entries in Content.mgcb:
            //   /Content/knight_idle.png    -> Build Action: Texture
            //   /Content/knight_run.png
            //   /Content/knight_attack.png
            //   /Content/knight_hurt.png
            //   /Content/knight_death.png
            _player.LoadContent(Content, GraphicsDevice);

            // _font = Content.Load<SpriteFont>("Fonts/UIFont");
            _font = null;
            _nodeTexture = null;
        }

        // =====================================================================
        //  Update
        // =====================================================================
        protected override void Update(GameTime gameTime)
        {
            var kb = Keyboard.GetState();
            var mouse = Mouse.GetState();

            // ── Toggle skill tree on Tab press (not hold) ─────────────────────
            if (kb.IsKeyDown(Keys.Tab) && _prevKb.IsKeyUp(Keys.Tab))
                _showSkillTree = !_showSkillTree;

            // ── Skill tree mode ───────────────────────────────────────────────
            if (_showSkillTree)
            {
                _skillTree.Update(mouse, gameTime);

                // Detect any new unlock and rebuild player stats
                int unlocks = CountUnlocked();
                if (unlocks != _prevUnlockCount)
                {
                    _prevUnlockCount = unlocks;
                    _player.SkillPoints = _skillTree.PlayerCurrency;
                    _player.RecalculateStats(new List<SkillNode>(_skillTree.AllNodes));

                    _skillTree.FlushToSaveData(_save);
                    SaveSystem.Save(_save);
                }
            }

            // ── Gameplay mode ────────────────────────────────────────────────
            // WASD movement is active regardless of skill tree visibility so the
            // player can still move while browsing the tree (adjust if you prefer
            // to lock movement while the tree is open).
            var worldBounds = new Rectangle(0, 0, SCREEN_W, SCREEN_H);
            _player.Update(gameTime, worldBounds);

            // Simulate boss hit on H key press — TriggerHurt plays the hurt animation
            // AND applies damage reduction, then switches to Dead if HP reaches 0
            if (kb.IsKeyDown(Keys.H) && _prevKb.IsKeyUp(Keys.H))
            {
                _player.TriggerHurt(30f);
                if (_player.IsDead)
                    _player.RecordDeath(_save);
            }

            // Heal on F key press
            if (kb.IsKeyDown(Keys.F) && _prevKb.IsKeyUp(Keys.F))
                _player.Heal(40f);

            // Respawn on R key press
            if (kb.IsKeyDown(Keys.R) && _prevKb.IsKeyUp(Keys.R))
            {
                var spawnPos = new Vector2(
                    SCREEN_W / 2f - Player.WIDTH / 2f,
                    SCREEN_H / 2f - Player.HEIGHT / 2f);
                _player.Respawn(spawnPos);
            }

            _prevKb = kb;
            base.Update(gameTime);
        }

        // =====================================================================
        //  Draw
        // =====================================================================
        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(new Color(8, 8, 16));   // near-black background

            // ── Batch 1: world-space content (skill tree nodes + player body) ──
            // If you add a camera later, pass transformMatrix: camera.Matrix here.
            _spriteBatch.Begin(
                sortMode: SpriteSortMode.Deferred,
                blendState: BlendState.AlphaBlend,
                samplerState: SamplerState.PointClamp);

            // Draw the skill tree (lines + gradient node circles)
            if (_showSkillTree)
                _skillTree.Draw(_spriteBatch, _nodeTexture, _font);

            // Draw the animated player sprite (falls back to coloured rectangle
            // automatically if LoadContent has not been called yet)
            _player.Draw(_spriteBatch, _pixel);

            _spriteBatch.End();

            // ── Batch 2: screen-space UI (no camera matrix) ───────────────────
            // HUD, tooltip, and any text drawn here use raw screen-pixel coords,
            // so Mouse.GetState() coordinates are never mistransformed.
            _spriteBatch.Begin(
                sortMode: SpriteSortMode.Deferred,
                blendState: BlendState.AlphaBlend);

            // Skill tree HUD + hover tooltip (drawn last — always on top)
            if (_showSkillTree)
                _skillTree.DrawScreenUI(_spriteBatch, _font);

            // In-game stat overlay (top-left corner, white text)
            // Requires _font to be loaded; safe to leave null during prototyping.
            if (_font != null)
            {
                string stats = _player.GetStatsSummary();
                _spriteBatch.DrawString(_font, stats, new Vector2(20, 20), Color.White);
            }

            _spriteBatch.End();

            base.Draw(gameTime);
        }

        // =====================================================================
        //  Helpers
        // =====================================================================
        private int CountUnlocked()
        {
            int count = 0;
            foreach (var n in _skillTree.AllNodes)
                if (n.IsUnlocked && n.ID != "ROOT") count++;
            return count;
        }
    }
}