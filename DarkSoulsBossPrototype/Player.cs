using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;


namespace DarkSoulsBossPrototype
{

    public class Player
    {
        private int skillPoints;
        private List<SkillNode> skillNodes;
        private VisualSkillTree skillTree;

        public Player(VisualSkillTree skillTree)
        {
            this.skillTree = skillTree;
            this.skillPoints = 0;
            this.skillNodes = new List<SkillNode>();
        }

        public int SkillPoints
        {
            get { return skillPoints; }
            set { skillPoints = value; }
        }

        public List<SkillNode> SkillNodes
        {
            get { return skillNodes; }
        }

        public void AddSkillNode(SkillNode node)
        {
            skillNodes.Add(node);
            skillPoints -= node.Cost;
        }

        //public SkillNode GetHoveredNode()
        //{
        //    return skillTree.HoveredNode;
        //}
    }
}
