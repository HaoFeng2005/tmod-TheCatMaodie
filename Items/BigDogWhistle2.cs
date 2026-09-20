using TheCatMaodie.NPCs;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Items
{
    // 二阶段大狗的召唤物(测试用)。
    // 贴图走默认路径 Items/BigDogWhistle2.png —— 现在是自动生成的泥块占位图标,
    // 美术做好之后直接覆盖那个同名文件就行, 不用改代码。
    public class BigDogWhistle2 : ModItem
    {
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
            // 生成NPC只在服务器/单机做;场上已经有一只二阶段大狗就不再召唤
            if (Main.netMode != NetmodeID.MultiplayerClient && !NPC.AnyNPCs(ModContent.NPCType<BigDogBoss2>()))
            {
                NPC.SpawnOnPlayer(player.whoAmI, ModContent.NPCType<BigDogBoss2>());
            }
            return true;
        }

        public override void AddRecipes()
        {
            // 测试用配方:10个泥土在工作台合成(一阶段哨子是5个泥块,这里多给点好区分)
            Recipe recipe = CreateRecipe();
            recipe.AddIngredient(ItemID.DirtBlock, 10);
            recipe.AddTile(TileID.WorkBenches);
            recipe.Register();
        }
    }
}
