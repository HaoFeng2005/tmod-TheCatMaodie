using TheCatMaodie.NPCs;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Items
{
    // boss召唤物:吹哨子叫狗。无同名boss在场时,使用键召唤 BigDogBoss
    public class BigDogWhistle : ModItem
    {
        public override string Texture => "Terraria/Images/Item_1927"; // 借原版"狗哨"贴图(占位,ID 1927 已验证)

        public override void SetDefaults()
        {
            Item.width = 32;
            Item.height = 32;
            Item.useTime = 30;
            Item.useAnimation = 30;
            Item.useStyle = ItemUseStyleID.HoldUp;   // 举到头顶的使用动作
            Item.UseSound = SoundID.Item1;
            Item.rare = ItemRarityID.Green;
            Item.maxStack = 1;
        }

        public override bool? UseItem(Player player)
        {
            // 生成NPC只在服务器/单机做;已有一只大狗在场就不再召唤
            if (Main.netMode != NetmodeID.MultiplayerClient && !NPC.AnyNPCs(ModContent.NPCType<BigDogBoss>()))
            {
                NPC.SpawnOnPlayer(player.whoAmI, ModContent.NPCType<BigDogBoss>());
            }
            return true;
        }

        public override void AddRecipes()
        {
            // 测试用配方:5个泥块在工作台合成,方便你快速开测
            Recipe recipe = CreateRecipe();
            recipe.AddIngredient(ItemID.DirtBlock, 5);
            recipe.AddTile(TileID.WorkBenches);
            recipe.Register();
        }
    }
}
