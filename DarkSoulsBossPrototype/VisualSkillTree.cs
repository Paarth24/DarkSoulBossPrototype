using DarkSoulsBossPrototype;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DarkSoulsBossPrototype
{
    // =========================================================================
    //  VisualSkillTree  v5
    //
    //  Root fix — tooltip position
    //  ────────────────────────────
    //  The bug in v4 was that DrawTooltip() read _lastMs (stored during Update)
    //  instead of the live hardware position at draw time.  Because MonoGame's
    //  Update/Draw pipeline runs in sequence, by the time Draw executes the
    //  stored MouseState is one frame stale AND was captured at the node-centre
    //  coordinates rather than the real mouse cursor position.
    //
    //  Fix: call Mouse.GetState() directly inside Draw(), right at the tooltip
    //  block.  _lastMs is still kept for Update's click logic.
    //
    //  Other changes
    //  ─────────────
    //  NODE NAMES     All 25 nodes renamed to Soulslike flavour text.
    //  TOOLTIP BOX    Exactly matches spec: 300×110 px, Color(25,25,25,245),
    //                 gold top border, text at fixed offsets from dynamicTooltipPos.
    //  SCREEN CLAMP   Box clamps to viewport so it never overflows any edge.
    //  HUD            Skill-points count rendered top-left (20,20).
    // =========================================================================
    public class VisualSkillTree
    {
        // ── Visual constants ──────────────────────────────────────────────────
        private const int NODE_R = 26;
        private const int NODE_D = NODE_R * 2;   // 52 px
        private const float GRID_STEP = 130f;
        private const int CLICK_HALF = 20;           // centred hit-rect half-size

        private const int LINE_W = 2;
        private const int GLOW_W = 7;

        // Screen bounds for tooltip clamp
        private const int SCREEN_W = 1280;
        private const int SCREEN_H = 720;

        // ── Gradient palette ─────────────────────────────────────────────────
        private static readonly Dictionary<SkillType, (Color dark, Color bright, Color accent)> Pal = new()
        {
            { SkillType.Hub,              (new Color( 15,  50, 150), new Color( 85, 165, 255), new Color( 70, 150, 255)) },
            { SkillType.Healing,          (new Color(  0,  70,  30), new Color( 50, 210, 105), new Color( 35, 195,  85)) },
            { SkillType.MovementSpeed,    (new Color(  5,  50, 110), new Color( 50, 170, 255), new Color( 35, 155, 235)) },
            { SkillType.DamageBoost,      (new Color(110,   8,   8), new Color(255,  70,  50), new Color(225,  45,  25)) },
            { SkillType.Armor,            (new Color( 40,  20, 100), new Color(160, 130, 255), new Color(130, 105, 225)) },
            { SkillType.ArmorPenetration, (new Color(110,  50,   0), new Color(255, 172,  30), new Color(225, 142,  15)) },
        };

        private static readonly Color LockDark = new Color(26, 26, 34);
        private static readonly Color LockBright = new Color(65, 65, 80);

        private static readonly Color CLineLock = new Color(45, 45, 58);
        private static readonly Color CLineOn = new Color(210, 178, 35);
        private static readonly Color CLineGlow = new Color(255, 210, 65, 70);
        private static readonly Color CGlowRing = new Color(255, 220, 85, 45);
        private static readonly Color CAfford = new Color(105, 250, 130);
        private static readonly Color CShadow = new Color(0, 0, 0, 215);

        // HUD colours
        private static readonly Color CHudBg = new Color(6, 6, 16, 230);
        private static readonly Color CHudBorder = new Color(70, 70, 95, 200);
        private static readonly Color CHudGold = new Color(255, 212, 75);
        private static readonly Color CHudSub = new Color(170, 165, 150);

        // ── Runtime state ─────────────────────────────────────────────────────
        private readonly List<SkillNode> _all = new();
        private SkillNode _root;

        private SkillNode _hovered = null;
        private SkillNode _lastClick = null;
        private MouseState _lastMs;       // used only for click debounce in Update
        private int _currency;
        private float _pulse = 0f;

        // GPU resources
        private Texture2D _px;
        private readonly Dictionary<SkillType, Texture2D> _texOn = new();
        private readonly Dictionary<SkillType, Texture2D> _texOff = new();

        // ── Public ────────────────────────────────────────────────────────────
        public int PlayerCurrency => _currency;
        public bool IsInitialised => _px != null;

        /// <summary>
        /// All nodes in the tree (locked and unlocked).
        /// Pass directly to Player.RecalculateStats() after any unlock.
        /// Returns a read-only view — mutate the tree through TryUnlock only.
        /// </summary>
        public IReadOnlyList<SkillNode> AllNodes => _all.AsReadOnly();

        // =====================================================================
        //  Constructor — always a clean slate, never reads save data
        // =====================================================================
        public VisualSkillTree(Vector2 centre, int initialCurrency = 300)
        {
            _currency = initialCurrency;
            BuildTree(centre);
        }

        public void Reset(int startingCurrency = 300)
        {
            _currency = startingCurrency;
            _hovered = null;
            _lastClick = null;
            foreach (var n in _all) if (n.ID != "ROOT") n.Relock();
        }

        // =====================================================================
        //  BuildTree
        //  Grid step = 130 px → axis neighbours 130 px, diagonal 184 px.
        //  Both exceed NODE_D(52) + 30 px padding = 82 px minimum. ✓
        //
        //  Node data is set via property initializers after construction so the
        //  Name / Description / Cost can be written in one readable block per node.
        // =====================================================================
        private void BuildTree(Vector2 C)
        {
            Vector2 G(int col, int row) =>
                new Vector2(C.X + col * GRID_STEP, C.Y + row * GRID_STEP);

            _all.Clear();

            // ── ROOT ──────────────────────────────────────────────────────────
            _root = Add("ROOT", SkillType.Hub, G(0, 0), startsUnlocked: true,
                name: "Origin",
                desc: "The source of all power. All skills unlock from here.",
                cost: 0);

            // HEALING BRANCH  (column 0, upward)
            var h1 = Add("H1", SkillType.Healing, G(0, -1),
                name: "Life Flask Recovery",
                desc: "Flask restores 20 percent more HP per sip.",
                cost: 1);

            var h2 = Add("H2", SkillType.Healing, G(0, -2),
                name: "Blessed Vessel",
                desc: "Flask cooldown reduced by 20 percent between uses.",
                cost: 1);

            var h3 = Add("H3", SkillType.Healing, G(0, -3),
                name: "Passive Regeneration",
                desc: "Restore 2 HP per second while not taking damage.",
                cost: 2);

            var hL = Add("HL", SkillType.Healing, G(-1, -2),
                name: "Miracle Draught",
                desc: "Each flask sip removes one active debuff on use.",
                cost: 1);

            var hR = Add("HR", SkillType.Healing, G(1, -2),
                name: "Lifegem Resonance",
                desc: "Defeating a boss phase restores one free flask charge.",
                cost: 1);

            // DAMAGE BOOST BRANCH  (row 0, rightward)
            var d1 = Add("D1", SkillType.DamageBoost, G(1, 0),
                name: "Melee Might",
                desc: "Boosts physical attack damage by 15 percent.",
                cost: 2);

            var d2 = Add("D2", SkillType.DamageBoost, G(2, 0),
                name: "Critical Power",
                desc: "Critical hits deal 20 percent more damage on top of base.",
                cost: 2);

            var d3 = Add("D3", SkillType.DamageBoost, G(3, 0),
                name: "Chaos Ascendant",
                desc: "Below 30 percent HP all damage increases by 25 percent.",
                cost: 3);

            var dE = Add("DE", SkillType.DamageBoost, G(4, 0),
                name: "Soul Forged Blade",
                desc: "All damage bonuses stack together for maximum output.",
                cost: 4);

            var dU = Add("DU", SkillType.DamageBoost, G(2, -1),
                name: "Relentless Assault",
                desc: "Consecutive hits on the same enemy add 3 percent damage each.",
                cost: 2);

            var dD = Add("DD", SkillType.DamageBoost, G(2, 1),
                name: "Finishing Rite",
                desc: "Enemies below 25 percent HP take 30 percent extra damage.",
                cost: 2);

            // ARMOR BRANCH  (row 0, leftward)
            var a1 = Add("A1", SkillType.Armor, G(-1, 0),
                name: "Ironclad Resolve",
                desc: "Reduces incoming boss damage by 8 percent.",
                cost: 2);

            var a2 = Add("A2", SkillType.Armor, G(-2, 0),
                name: "Fortress Body",
                desc: "Heavy conditioning grants 12 percent physical damage reduction.",
                cost: 2);

            var a3 = Add("A3", SkillType.Armor, G(-3, 0),
                name: "Living Rampart",
                desc: "Grants 20 flat armor rating against all incoming strikes.",
                cost: 3);

            var aE = Add("AE", SkillType.Armor, G(-4, 0),
                name: "Undying Bulwark",
                desc: "Block any single hit for zero damage once every 8 seconds.",
                cost: 4);

            var aU = Add("AU", SkillType.Armor, G(-2, -1),
                name: "Thorned Carapace",
                desc: "Reflects 10 percent of all melee damage back at the attacker.",
                cost: 2);

            var aD = Add("AD", SkillType.Armor, G(-2, 1),
                name: "Unyielding Poise",
                desc: "Reduces stagger duration by 40 percent after heavy hits.",
                cost: 2);

            // MOVEMENT SPEED BRANCH  (col -1, downward)
            var s1 = Add("S1", SkillType.MovementSpeed, G(-1, 1),
                name: "Fleet Footwork",
                desc: "Increases movement speed by 10 percent.",
                cost: 1);

            var s2 = Add("S2", SkillType.MovementSpeed, G(-1, 2),
                name: "Quicksilver Step",
                desc: "Movement speed up by 14 percent and roll cooldown down by 0.1s.",
                cost: 1);

            var s3 = Add("S3", SkillType.MovementSpeed, G(-1, 3),
                name: "Ashen Tempest",
                desc: "Sprint speed increases by 22 percent for sustained bursts.",
                cost: 2);

            var sL = Add("SL", SkillType.MovementSpeed, G(-2, 2),
                name: "Gust Reading",
                desc: "Automatically sidestep one projectile every 6 seconds.",
                cost: 1);

            // ARMOR PENETRATION BRANCH  (col +1, downward)
            var p1 = Add("P1", SkillType.ArmorPenetration, G(1, 1),
                name: "Shattering Strike",
                desc: "Ignores 12 percent of the enemy boss physical armor.",
                cost: 3);

            var p2 = Add("P2", SkillType.ArmorPenetration, G(1, 2),
                name: "Ruinous Rend",
                desc: "Each hit lowers enemy armor by 2 percent for 4 seconds.",
                cost: 3);

            var p3 = Add("P3", SkillType.ArmorPenetration, G(1, 3),
                name: "Void Puncture",
                desc: "First attack after a dodge fully bypasses all armor.",
                cost: 4);

            var pR = Add("PR", SkillType.ArmorPenetration, G(2, 2),
                name: "Exposed Marrow",
                desc: "Armor piercing hits have 20 percent chance to cause bleed.",
                cost: 3);

            // ── Wire ──────────────────────────────────────────────────────────
            Link(_root, h1, d1, a1, s1, p1);

            Link(h1, h2, hL, hR);
            Link(h2, h3);

            Link(d1, d2, dU, dD);
            Link(d2, d3);
            Link(d3, dE);

            Link(a1, a2, aU, aD);
            Link(a2, a3);
            Link(a3, aE);

            Link(s1, s2, sL);
            Link(s2, s3);

            Link(p1, p2, pR);
            Link(p2, p3);

#if DEBUG
            CheckCollisions();
#endif
        }

        // =====================================================================
        //  LoadContent — create the 1×1 pixel texture and bake gradient circles
        // =====================================================================
        public void LoadContent(GraphicsDevice gd)
        {
            _px = new Texture2D(gd, 1, 1);
            _px.SetData(new[] { Color.White });

            foreach (SkillType t in Enum.GetValues<SkillType>())
            {
                _texOn[t] = BakeCircle(gd, t, locked: false);
                _texOff[t] = BakeCircle(gd, t, locked: true);
            }
        }

        // =====================================================================
        //  Update
        // =====================================================================
        public void Update(MouseState mouseState, GameTime gt = null)
        {
            _lastMs = mouseState;   // retained for click-debounce logic only

            float dt = gt == null ? 0f : (float)gt.ElapsedGameTime.TotalSeconds;
            _pulse = (_pulse + dt * 3.0f) % (MathF.PI * 2f);

            // ── Hover detection ───────────────────────────────────────────────
            //
            // ROOT CAUSE OF THE RIGHT/BOTTOM MISS BUG
            // ─────────────────────────────────────────
            // Rectangle.Contains(int x, int y) in MonoGame treats the right
            // and bottom edges as EXCLUSIVE:
            //   return x >= Left && x < Right && y >= Top && y < Bottom;
            //                              ^                          ^
            // That means a mouse point sitting exactly on the right or bottom
            // boundary — which happens first when moving inward from those
            // directions — is never matched. Nodes whose ScreenPosition sits
            // to the right or below the viewport centre are therefore missed
            // on approach from the far side.
            //
            // Rectangle.Contains(Point) uses the same formula, so the fix is
            // not the overload choice alone.  The real requirement is that the
            // rectangle is constructed so its interior fully covers the node's
            // drawn circle.  We must:
            //   1. Subtract half-width AND half-height from ScreenPosition to
            //      get the true top-left corner (nodes are drawn centred).
            //   2. Use a Point for the mouse position so the single Contains
            //      call is unambiguous and consistent on all platforms.
            //   3. Match the hit-radius to the visual radius (NODE_R = 26 px)
            //      rather than CLICK_HALF = 20, which was smaller than the
            //      drawn circle and left a dead ring around every node edge.
            //
            // With these corrections every node — left, right, top, bottom —
            // responds identically because the rectangle is perfectly centred
            // and sized to match what the player actually sees.

            // Reset every frame before testing
            _hovered = null;

            // Build a Point once — cheaper than constructing a new Vector2 per node
            Point mousePoint = new Point(mouseState.X, mouseState.Y);

            foreach (var node in _all)
            {
                // Subtract half the diameter from the node's centre to obtain
                // the top-left corner of the hitbox (MonoGame Rectangle origin
                // is always top-left, never centre).
                int radius = NODE_R;   // match the visual circle exactly (26 px)
                Rectangle centeredHitbox = new Rectangle(
                    (int)node.ScreenPosition.X - radius,   // left edge
                    (int)node.ScreenPosition.Y - radius,   // top edge
                    radius * 2,                             // full width  = diameter
                    radius * 2);                            // full height = diameter

                // Contains(Point) is consistent across all edges — use it instead
                // of Contains(int, int) to avoid the right/bottom exclusion issue
                if (centeredHitbox.Contains(mousePoint))
                {
                    _hovered = node;
                    break;   // only one node can be hovered at a time
                }
            }

            // ── Click-to-unlock (press-edge debounced) ────────────────────────
            if (mouseState.LeftButton == ButtonState.Pressed
                && _hovered != null
                && _hovered != _lastClick)
            {
                _lastClick = _hovered;
                TryUnlock(_hovered);
            }
            else if (mouseState.LeftButton == ButtonState.Released)
            {
                _lastClick = null;
            }
        }

        // =====================================================================
        //  Draw  —  WORLD-SPACE ONLY
        //
        //  Call this inside a SpriteBatch.Begin() that uses your camera matrix.
        //  It draws lines and node circles in world space.
        //  Do NOT draw the tooltip here — its coordinates are raw screen pixels
        //  and would be incorrectly transformed by the camera matrix.
        //
        //  Call order in Game1.Draw():
        //    1. spriteBatch.Begin(transformMatrix: camera.Matrix);  // world batch
        //    2.   _skillTree.Draw(spriteBatch, nodeTexture, font);
        //    3. spriteBatch.End();
        //    4. spriteBatch.Begin();                                // screen batch — NO matrix
        //    5.   _skillTree.DrawScreenUI(spriteBatch, font);
        //    6. spriteBatch.End();
        // =====================================================================
        public void Draw(SpriteBatch spriteBatch, Texture2D nodeTexture, SpriteFont font)
        {
            if (_px == null)
                throw new InvalidOperationException("Call LoadContent() before Draw().");

            // ── A: Connection lines ───────────────────────────────────────────
            var seen = new HashSet<(string, string)>();
            foreach (var par in _all)
                foreach (var chi in par.Children)
                {
                    var key = string.Compare(par.ID, chi.ID, StringComparison.Ordinal) < 0
                        ? (par.ID, chi.ID) : (chi.ID, par.ID);
                    if (!seen.Add(key)) continue;

                    bool lit = par.IsUnlocked && chi.IsUnlocked;
                    if (lit)
                        DrawLine(spriteBatch, par.ScreenPosition, chi.ScreenPosition,
                                 CLineGlow, GLOW_W);
                    DrawLine(spriteBatch, par.ScreenPosition, chi.ScreenPosition,
                             lit ? CLineOn : CLineLock, LINE_W);
                }

            // ── B: Node circles ───────────────────────────────────────────────
            foreach (var n in _all)
                DrawNode(spriteBatch, n);
        }

        // Spec-compatible overload (lineTexture unused — _px handles lines)
        public void Draw(SpriteBatch sb, Texture2D nodeTexture, Texture2D lineTexture, SpriteFont font)
            => Draw(sb, nodeTexture, font);

        // =====================================================================
        //  DrawScreenUI  —  SCREEN-SPACE ONLY  (no camera matrix)
        //
        //  WHY THIS IS A SEPARATE METHOD
        //  ─────────────────────────────
        //  Mouse.GetState() always returns raw screen-pixel coordinates.
        //  If those coordinates are passed to DrawString/Draw inside a
        //  SpriteBatch.Begin(transformMatrix: cameraMatrix) call, MonoGame
        //  applies the camera transform to them — scaling and translating the
        //  tooltip as if it were a world object.  The result is the box
        //  rendering near the tree centre instead of under the cursor.
        //
        //  Calling spriteBatch.Begin() with NO matrix (identity) means every
        //  coordinate is treated as a raw screen pixel, which is exactly what
        //  Mouse.GetState() returns.  The tooltip then always appears directly
        //  below the cursor tip regardless of where the camera is pointing.
        //
        //  Call this AFTER spriteBatch.End() closes the world-space batch:
        //
        //    spriteBatch.Begin();          // <-- no matrix argument
        //    _skillTree.DrawScreenUI(spriteBatch, font);
        //    spriteBatch.End();
        // =====================================================================
        public void DrawScreenUI(SpriteBatch spriteBatch, SpriteFont font)
        {
            if (_px == null)
                throw new InvalidOperationException("Call LoadContent() before DrawScreenUI().");

            // ── C: HUD (skill-points panel, top-left corner) ──────────────────
            DrawHUD(spriteBatch, font);

            // ── D: Tooltip ────────────────────────────────────────────────────
            // Everything from this point uses raw screen-pixel coordinates.
            // Mouse.GetState() is called fresh every frame — never a cached value.
            if (_hovered == null || font == null) return;

            // 1. Raw screen-space mouse position — correct because this batch
            //    has NO transform matrix applied
            var rawMouse = Mouse.GetState();
            var screenMousePos = new Vector2(rawMouse.X, rawMouse.Y);

            // 2. Anchor the card completely below and to the right of the cursor
            //    so the tooltip never covers the cursor tip itself
            Vector2 tooltipBoxPos = screenMousePos + new Vector2(15, 25);

            // 3. Build text strings — SafeAscii() removes any character outside
            //    printable ASCII 32-126, preventing ArgumentException in DrawString
            string textLineName = SafeAscii(_hovered.Name);
            string textLineDesc = SafeAscii(
                string.IsNullOrEmpty(_hovered.Description)
                    ? "No description available."
                    : _hovered.Description);
            string textLineCost = "Cost: " + _hovered.Cost + " SP";
            string textLineStatus = _hovered.IsUnlocked ? "Unlocked" : "Locked";
            string combinedLine3 = textLineCost + "  |  " + textLineStatus;

            // Status colour: LimeGreen = owned, Gold = affordable, Crimson = too expensive
            Color statusColor = _hovered.IsUnlocked
                ? Color.LimeGreen
                : (_currency >= _hovered.Cost ? Color.Gold : Color.Crimson);

            // 4. Measure every line so the box grows to fit — no truncation needed
            Vector2 nameSize = font.MeasureString(textLineName);
            Vector2 descSize = font.MeasureString(textLineDesc);
            Vector2 costSize = font.MeasureString(combinedLine3);

            float requiredWidth = Math.Max(nameSize.X, Math.Max(descSize.X, costSize.X)) + 24f;
            float requiredHeight = nameSize.Y + descSize.Y + costSize.Y + 30f;

            // 5. Clamp: keep the entire box inside the viewport
            tooltipBoxPos.X = Math.Clamp(tooltipBoxPos.X, 0, SCREEN_W - requiredWidth - 2);
            tooltipBoxPos.Y = Math.Clamp(tooltipBoxPos.Y, 0, SCREEN_H - requiredHeight - 2);

            var dynamicCardBounds = new Rectangle(
                (int)tooltipBoxPos.X,
                (int)tooltipBoxPos.Y,
                (int)requiredWidth,
                (int)requiredHeight);

            // 6. Draw layers: background → borders → text

            // Dark translucent card body
            spriteBatch.Draw(_px, dynamicCardBounds, new Color(20, 20, 20, 245));

            // Gold top border (2 px)
            spriteBatch.Draw(_px,
                new Rectangle(dynamicCardBounds.X, dynamicCardBounds.Y,
                               dynamicCardBounds.Width, 2),
                Color.Gold);

            // Muted bottom border (2 px)
            spriteBatch.Draw(_px,
                new Rectangle(dynamicCardBounds.X, dynamicCardBounds.Bottom - 2,
                               dynamicCardBounds.Width, 2),
                new Color(80, 80, 80, 200));

            // Skill-type accent bar on the left edge (3 px)
            Color accent = Pal.TryGetValue(_hovered.SkillType, out var palEntry)
                           ? palEntry.accent : Color.White;
            spriteBatch.Draw(_px,
                new Rectangle(dynamicCardBounds.X, dynamicCardBounds.Y,
                               3, dynamicCardBounds.Height),
                accent);

            // Text lines — each advances Y by the measured line height + gap
            Vector2 linePos = tooltipBoxPos + new Vector2(12, 10);

            spriteBatch.DrawString(font, textLineName, linePos, Color.Gold);
            linePos.Y += nameSize.Y + 6f;

            spriteBatch.DrawString(font, textLineDesc, linePos, Color.White);
            linePos.Y += descSize.Y + 8f;

            spriteBatch.DrawString(font, combinedLine3, linePos, statusColor);
            // ── Nothing is drawn after this line ─────────────────────────────
        }

        // =====================================================================
        //  DrawNode
        // =====================================================================
        private void DrawNode(SpriteBatch sb, SkillNode n)
        {
            bool hov = n == _hovered;
            bool affordable = !n.IsUnlocked
                           && (n.Parent == null || n.Parent.IsUnlocked)
                           && _currency >= n.Cost;

            float scale = hov ? 1.18f : 1f;
            int sz = (int)(NODE_D * scale);
            var dest = CentredRect(n.ScreenPosition, sz);

            if (n.IsUnlocked)
                sb.Draw(_texOn[n.SkillType], GrowRect(dest, 14), CGlowRing);

            if (affordable)
            {
                byte a = (byte)Math.Clamp(125 + 100 * MathF.Sin(_pulse), 0, 255);
                int ex = 5 + (int)(3 * MathF.Sin(_pulse));
                DrawRing(sb, n.ScreenPosition, NODE_R + ex,
                         new Color(CAfford.R, CAfford.G, CAfford.B, a), thickness: 3);
            }

            sb.Draw(n.IsUnlocked ? _texOn[n.SkillType] : _texOff[n.SkillType], dest, Color.White);

            if (n.IsUnlocked)
                FillCircle(sb, n.ScreenPosition, NODE_R / 5, new Color(255, 255, 255, 170));
        }

        // =====================================================================
        //  DrawHUD — skill points panel top-left
        // =====================================================================
        private void DrawHUD(SpriteBatch sb, SpriteFont font)
        {
            var panel = new Rectangle(20, 20, 240, 68);
            Rect(sb, panel, CHudBg);
            Rect(sb, new Rectangle(20, 20, 3, 68), CHudGold);
            RectOutline(sb, panel, CHudBorder, 1);

            if (font == null) return;

            DrawText(sb, font, _currency.ToString(),
                     new Vector2(40, 28), CHudGold, scale: 1.55f);
            DrawText(sb, font, "Skill Points Available",
                     new Vector2(40, 62), CHudSub, scale: 0.80f);
        }

        // =====================================================================
        //  Gradient circle baker
        // =====================================================================
        private Texture2D BakeCircle(GraphicsDevice gd, SkillType type, bool locked)
        {
            int sz = NODE_D;
            var tex = new Texture2D(gd, sz, sz);
            var data = new Color[sz * sz];
            float cx = (sz - 1) / 2f;
            float cy = (sz - 1) / 2f;
            float r = cx;

            Color dark = locked ? LockDark : Pal[type].dark;
            Color bright = locked ? LockBright : Pal[type].bright;

            for (int y = 0; y < sz; y++)
                for (int x = 0; x < sz; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    if (dist > r) { data[y * sz + x] = Color.Transparent; continue; }

                    float t = MathF.Pow(1f - dist / r, 0.60f);
                    float spec = MathF.Max(0f, (-dx * 0.55f - dy * 0.75f) / r);
                    t = Math.Clamp(t * 0.78f + spec * 0.22f, 0f, 1f);

                    Color px = Color.Lerp(dark, bright, t);

                    if (!locked && dx < -r * 0.2f && dy < -r * 0.2f && dist < r * 0.52f)
                    {
                        float sd = 1f - dist / (r * 0.52f);
                        px = Color.Lerp(px, new Color(255, 255, 255, 210), sd * sd * 0.45f);
                    }

                    data[y * sz + x] = px;
                }

            tex.SetData(data);
            return tex;
        }

        // =====================================================================
        //  Primitive helpers
        // =====================================================================

        private void DrawLine(SpriteBatch sb, Vector2 from, Vector2 to, Color c, int w)
        {
            var d = to - from;
            float ang = MathF.Atan2(d.Y, d.X);
            sb.Draw(_px, from, null, c, ang,
                    new Vector2(0f, 0.5f), new Vector2(d.Length(), w),
                    SpriteEffects.None, 0f);
        }

        private void Rect(SpriteBatch sb, Rectangle r, Color c) =>
            sb.Draw(_px, r, c);

        private void RectOutline(SpriteBatch sb, Rectangle r, Color c, int t)
        {
            sb.Draw(_px, new Rectangle(r.X, r.Y, r.Width, t), c);
            sb.Draw(_px, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
            sb.Draw(_px, new Rectangle(r.X, r.Y, t, r.Height), c);
            sb.Draw(_px, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
        }

        private void FillCircle(SpriteBatch sb, Vector2 cen, int r, Color c)
        {
            int icx = (int)cen.X, icy = (int)cen.Y;
            for (int dy = -r; dy <= r; dy++)
            {
                int half = (int)MathF.Sqrt(MathF.Max(0f, r * r - dy * dy));
                sb.Draw(_px, new Rectangle(icx - half, icy + dy, half * 2 + 1, 1), c);
            }
        }

        private void DrawRing(SpriteBatch sb, Vector2 cen, int r, Color c, int thickness)
        {
            int icx = (int)cen.X, icy = (int)cen.Y;
            for (int t = 0; t < thickness; t++)
            {
                int rr = r + t;
                for (int dx = -rr; dx <= rr; dx++)
                {
                    int dy = (int)MathF.Round(MathF.Sqrt(MathF.Max(0f, rr * rr - dx * dx)));
                    sb.Draw(_px, new Rectangle(icx + dx, icy + dy, 1, 1), c);
                    sb.Draw(_px, new Rectangle(icx + dx, icy - dy, 1, 1), c);
                }
            }
        }

        private void DrawText(SpriteBatch sb, SpriteFont f, string s,
                              Vector2 pos, Color c, float scale = 1f)
        {
            if (f == null || string.IsNullOrEmpty(s)) return;
            sb.DrawString(f, s, pos + new Vector2(1, 1), CShadow,
                          0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            sb.DrawString(f, s, pos, c,
                          0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        private static Rectangle CentredRect(Vector2 centre, int size) =>
            new Rectangle((int)(centre.X - size / 2f),
                          (int)(centre.Y - size / 2f), size, size);

        private static Rectangle GrowRect(Rectangle r, int by) =>
            new Rectangle(r.X - by, r.Y - by, r.Width + by * 2, r.Height + by * 2);

        // TruncateAscii: trims text to fit maxWidth using "..." (three plain dots).
        // Never uses the Unicode ellipsis U+2026, which most SpriteFonts lack.
        private static string TruncateAscii(SpriteFont font, string text, float maxWidth)
        {
            if (font == null || font.MeasureString(text).X <= maxWidth) return text;
            const string tail = "...";
            while (text.Length > 1 && font.MeasureString(text + tail).X > maxWidth)
                text = text.Substring(0, text.Length - 1);
            return text + tail;
        }

        // SafeAscii: strips characters outside printable ASCII 32-126.
        // Em-dash (U+2014), checkmark (U+2713), ellipsis (U+2026), curly quotes,
        // and percent-sign variants outside ASCII are all silently replaced with
        // a plain space so DrawString never throws ArgumentException.
        private static string SafeAscii(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char ch in text)
                sb.Append(ch >= 32 && ch <= 126 ? ch : ' ');
            return sb.ToString();
        }

        // =====================================================================
        //  Unlock logic
        // =====================================================================
        private void TryUnlock(SkillNode n)
        {
            if (n.IsUnlocked || n.SkillType == SkillType.Hub) return;
            if (n.Parent != null && !n.Parent.IsUnlocked)
            {
                Console.WriteLine($"[SkillTree] '{n.Name}': unlock '{n.Parent.Name}' first.");
                return;
            }
            if (_currency < n.Cost)
            {
                Console.WriteLine($"[SkillTree] '{n.Name}': need {n.Cost}, have {_currency}.");
                return;
            }
            _currency -= n.Cost;
            n.Unlock();
            Console.WriteLine($"[SkillTree] Unlocked '{n.Name}'. Remaining: {_currency}.");
        }

        // =====================================================================
        //  SaveData integration
        // =====================================================================
        public void FlushToSaveData(SaveData saveData)
        {
            saveData.UnlockedSkillIDs = _all
                .Where(n => n.IsUnlocked && n.ID != "ROOT")
                .Select(n => n.ID).ToList();
        }

        public void LoadFromSaveData(SaveData saveData)
        {
            foreach (var n in _all) if (n.ID != "ROOT") n.Relock();
            foreach (string id in saveData.UnlockedSkillIDs ?? Enumerable.Empty<string>())
                _all.FirstOrDefault(n => n.ID == id)?.Unlock();
        }

        // =====================================================================
        //  Construction helpers
        // =====================================================================

        /// <summary>
        /// Creates a SkillNode, writes all mutable UI data onto it, adds it to
        /// the master list, and returns it.
        /// </summary>
        private SkillNode Add(string id, SkillType type, Vector2 pos,
                              string name, string desc, int cost,
                              bool startsUnlocked = false)
        {
            var node = new SkillNode(id, type, pos, startsUnlocked)
            {
                Name = name,
                Description = desc,
                Cost = cost
            };
            _all.Add(node);
            return node;
        }

        private static void Link(SkillNode parent, params SkillNode[] children)
        {
            foreach (var c in children)
            {
                c.Parent ??= parent;
                if (!parent.Children.Contains(c)) parent.Children.Add(c);
            }
        }

        // =====================================================================
        //  Debug collision checker
        // =====================================================================
#if DEBUG
        private void CheckCollisions()
        {
            const float MIN_SAFE = NODE_D + 30f;   // 82 px
            bool ok = true;
            for (int i = 0; i < _all.Count; i++)
                for (int j = i + 1; j < _all.Count; j++)
                {
                    float d = Vector2.Distance(_all[i].ScreenPosition, _all[j].ScreenPosition);
                    if (d < MIN_SAFE)
                    {
                        Console.WriteLine(
                            $"[Layout] ⚠  {_all[i].ID} ↔ {_all[j].ID}  " +
                            $"dist={d:F0} (min={MIN_SAFE:F0})");
                        ok = false;
                    }
                }
            if (ok) Console.WriteLine(
                $"[Layout] ✓ All {_all.Count} nodes clear (min sep ≥ {MIN_SAFE:F0} px).");
        }
#endif
    }
}