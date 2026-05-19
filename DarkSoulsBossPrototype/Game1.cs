using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace DarkSoulsBossPrototype
{
    // -----------------------------------------------------------------------
    // Example: Wiring VisualSkillTree into Game1.cs
    // -----------------------------------------------------------------------

    public class Game1 : Game
    {
        private GraphicsDeviceManager _graphics;
        private SpriteBatch _spriteBatch;

        //Player
        private int _playerSkillPoints;
        private SkillNode _hoveredNode = null;

        // Skill tree
        private VisualSkillTree _skillTree;

        // Assets — load these from your Content pipeline
        private Texture2D _nodeTexture;   // 48×48 circular node sprite (or null for fallback squares)
        private SpriteFont _uiFont;        // any Content-pipeline SpriteFont

        // Save system
        private SaveData _save;

        public Game1()
        {
            _graphics = new GraphicsDeviceManager(this);
            _graphics.PreferredBackBufferWidth = 1920;
            _graphics.PreferredBackBufferHeight = 1080;
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
        }

        protected override void Initialize()
        {
            // Screen centre for the hub node
            var centre = new Vector2(
                _graphics.PreferredBackBufferWidth / 2f,
                _graphics.PreferredBackBufferHeight / 2f);

            _skillTree = new VisualSkillTree(centre, initialCurrency: 500);

            // Restore previously unlocked skills from disk
            _save = SaveSystem.Load();
            _skillTree.LoadFromSaveData(_save);

            base.Initialize();
        }

        protected override void LoadContent()
        {
            _spriteBatch = new SpriteBatch(GraphicsDevice);

            // IMPORTANT: pass GraphicsDevice here so the tree can create its 1×1 pixel texture
            _skillTree.LoadContent(GraphicsDevice);

            // Load your content-pipeline assets:
            //_nodeTexture = Content.Load<Texture2D>("UI/skill_node");
            _uiFont = Content.Load<SpriteFont>("Arial");
            _nodeTexture = null;   // null → fallback coloured squares (works without art assets)
            //_uiFont = null;   // null → skips text rendering
        }

        protected override void Update(GameTime gameTime)
        {
            MouseState mouse = Mouse.GetState();

            _skillTree.Update(mouse);

            // Autosave whenever skills change (compare counts as a cheap proxy)
            int savedCount = _save.UnlockedSkillIDs.Count;
            _skillTree.FlushToSaveData(_save);
            if (_save.UnlockedSkillIDs.Count != savedCount)
                SaveSystem.Save(_save);

            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(new Color(8, 8, 16));   // very dark background

            _spriteBatch.Begin(
                sortMode: SpriteSortMode.Deferred,
                blendState: BlendState.AlphaBlend,
                samplerState: SamplerState.PointClamp);  // crisp pixel art if node texture is small

            _skillTree.Draw(_spriteBatch, _nodeTexture, _uiFont);

            _spriteBatch.End();

            base.Draw(gameTime);
        }
    }
}
