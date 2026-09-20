using Microsoft.Xna.Framework;
using TheCatMaodie.Projectiles;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Items
{
    public class bigdog : ModItem
    {
        public override void SetDefaults()
        {
            // 尺寸与判定
            Item.width = 40;
            Item.height = 40;
            Item.scale = 1f;

            // 战斗数值
            // 站桩DPS账(实测口径):每发弹幕伤害 = 武器伤害 + 弹药伤害(火枪弹7),
            // 一次齐射3弧、每秒4轮 = 12发/秒,要落在150档 → 每发≈12 → 武器本体取5。
            // (想让它面板好看点,可以把 useTime 调大到20帧,那时武器伤害可以取到9)
            Item.damage = 5;
            Item.DamageType = DamageClass.Ranged;   // 关键：远程
            Item.knockBack = 3f;
            Item.crit = 0;                            // 额外暴击率(%)

            // 使用方式(远程通常用 Shoot 或 Holdout)
            Item.useTime = 15;        // 每 15 帧能用一次
            Item.useAnimation = 15;   // 挥动动画时长
            Item.useStyle = ItemUseStyleID.Shoot; // 枪械姿势
            Item.autoReuse = true;    // 按住持续发射
            Item.noMelee = true;      // 近战判定关掉(远程武器必加)

            // 消耗弹药(远程核心机制)
            Item.useAmmo = AmmoID.Bullet;   // 使用"子弹"类弹药
            Item.UseSound = SoundID.Item11; // 开枪音效

            // 稀有度和价值
            Item.rare = ItemRarityID.Blue;
            Item.value = Item.buyPrice(silver: 50);

            // 发射设置:注意!消耗弹药的武器,实际弹幕会被"弹药"覆盖
            // 这里指定的弹幕主要作用是激活下面的 Shoot 钩子(必须非空钩子才会触发)
            Item.shoot = ModContent.ProjectileType<BigDogShot>();
            Item.shootSpeed = 12f;   // 弹速(会和弹药自身速度叠加)
        }

        // 贴图:若你放了同名 png 则不需要写下面这行
        public override string Texture => "Terraria/Images/Item_1314";

        // 关键修复:消耗弹药的武器,实际射出的弹幕由"弹药"决定(火枪子弹就是原版灰色弹),
        // 会覆盖武器上的 Item.shoot。所以必须在这里拦截,一次射出三条平行弧线。
        public override bool Shoot(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 position, Vector2 velocity, int type, int damage, float knockback)
        {
            // damage 参数已包含 武器伤害+子弹伤害加成+玩家远程加成,直接用即可
            Vector2 dir = velocity.SafeNormalize(Vector2.Zero);
            Vector2 perp = new Vector2(-dir.Y, dir.X); // 垂直于飞行方向的轴,用来把三条弧线排开

            for (int i = 0; i < 3; i++)
            {
                // 三条弧线平行间隔18像素
                // ai[0]=画哪张弧线  ai[1]=出场延迟(12帧,错开无敌帧)  ai[2]/ai[3]=暂存起飞初速X/Y
                Vector2 spawnPos = position + perp * (i - 1) * 18f;
                Projectile.NewProjectile(source, spawnPos, velocity, ModContent.ProjectileType<BigDogShot>(), damage, knockback, player.whoAmI, i, i * 12f);
            }

            return false; // false = 拦下弹药原本要发射的原版弹幕,只发射上面这三条
        }

        public override void AddRecipes()
        {
            Recipe recipe = CreateRecipe();
            recipe.AddIngredient(ItemID.DirtBlock, 10);
            recipe.AddTile(TileID.WorkBenches);
            recipe.Register();
        }
    }
}