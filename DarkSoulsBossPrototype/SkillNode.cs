using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DarkSoulsBossPrototype
{
    // =========================================================================
    // SkillType  — the five gameplay branches + the hub root
    // =========================================================================
    public enum SkillType
    {
        Hub,
        Healing,
        MovementSpeed,
        DamageBoost,
        Armor,
        ArmorPenetration
    }

    // =========================================================================
    // SkillNode  v5
    //
    // Properties are now { get; set; } so the tree constructor can write them
    // directly.  Everything else (ScreenPosition, graph pointers, unlock state)
    // remains immutable from outside the assembly.
    // =========================================================================
    public class SkillNode
    {
        // ── Identity (set once at construction, read anywhere) ────────────────
        public string ID { get; }
        public SkillType SkillType { get; }

        // ── Mutable UI data — populated in BuildTree() ────────────────────────
        public string Name { get; set; }
        public string Description { get; set; }
        public int Cost { get; set; }

        // ── Progression ───────────────────────────────────────────────────────
        public bool IsUnlocked { get; private set; }

        // ── Layout ────────────────────────────────────────────────────────────
        /// <summary>Centre of this node in screen-space pixels.  Never changes.</summary>
        public Vector2 ScreenPosition { get; }

        // ── Graph pointers ────────────────────────────────────────────────────
        /// <summary>Back-pointer set by VisualSkillTree.Link().</summary>
        public SkillNode Parent { get; internal set; }
        public List<SkillNode> Children { get; } = new();

        // ── Construction ──────────────────────────────────────────────────────
        public SkillNode(string id, SkillType type, Vector2 screenPosition,
                         bool startsUnlocked = false)
        {
            ID = id;
            SkillType = type;
            ScreenPosition = screenPosition;
            IsUnlocked = startsUnlocked;

            // Defaults — always overwritten by BuildTree
            Name = id;
            Description = string.Empty;
            Cost = 0;
        }

        // ── Internal state mutation ───────────────────────────────────────────
        internal void Unlock() => IsUnlocked = true;
        internal void Relock() => IsUnlocked = false;

        // ── Hit-test: centred rectangle, 40×40 px (spec: half = 20) ──────────
        public bool HitTest(MouseState ms, int halfSize = 20)
        {
            var area = new Microsoft.Xna.Framework.Rectangle(
                (int)ScreenPosition.X - halfSize,
                (int)ScreenPosition.Y - halfSize,
                halfSize * 2,
                halfSize * 2);
            return area.Contains(ms.X, ms.Y);
        }

        // Distance-based overload kept for any legacy callers
        public bool HitTest(Vector2 mousePos, float radius = 24f)
            => Vector2.Distance(mousePos, ScreenPosition) <= radius;
    }
}