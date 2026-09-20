using TheCatMaodie.Projectiles;
using Terraria.DataStructures;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Items
{
    public class QiSword : ModItem
    {
        public override void SetDefaults()
        {
            Item.width = 40;
            Item.height = 40;
            Item.damage = 45;
            Item.DamageType = DamageClass.Melee;
            Item.useTime = 20;
            Item.useAnimation = 20;
            Item.useStyle = ItemUseStyleID.Swing;
            Item.knockBack = 6;
            Item.value = Item.buyPrice(gold: 1);
            Item.rare = ItemRarityID.LightRed;
            Item.UseSound = SoundID.Item1;
            Item.autoReuse = true;

            // 关键！随便填一个弹幕ID用来激活Shoot钩子
            Item.shoot = ProjectileID.Bullet;
            Item.shootSpeed = 10f;
        }

        public override string Texture => "Terraria/Images/Item_1506";

public override bool Shoot(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 position, Vector2 velocity, int type, int damage, float knockback)
{
    int beamDamage = (int)(Item.damage * 0.6f);
    Vector2 dir = velocity.SafeNormalize(Vector2.Zero);
    float step = 45f;
    int count = 6;

    // ========== 向右生成一整条剑气 ==========
    for (int i = 0; i < count; i++)
    {
        Vector2 offset = dir * step * i;
        Projectile.NewProjectile(
            source,
            position + offset,
            velocity,
            ModContent.ProjectileType<DecayBeam>(),
            beamDamage,
            knockback,
            player.whoAmI
        );
    }

    // ========== 向左生成一整条剑气 ==========
    Vector2 leftVel = -velocity;
    Vector2 leftDir = leftVel.SafeNormalize(Vector2.Zero);
    for (int i = 0; i < count; i++)
    {
        Vector2 offset = leftDir * step * i;
        Projectile.NewProjectile(
            source,
            position + offset,
            leftVel,
            ModContent.ProjectileType<DecayBeam>(),
            beamDamage,
            knockback,
            player.whoAmI
        );
    }

    return false;
}


private void FireBeamLine(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 startPos, Vector2 dir, int damage, float knockback)
{
    if (dir == Vector2.Zero)
        return;

    dir = dir.SafeNormalize(Vector2.Zero);

    // 单条剑气链的速度：和主方向一致
    Vector2 beamVel = dir * 10f;

    // 每道剑气间距
    float step = 45f;

    // 每条线生成几道剑气
    int count = 6;

    for (int i = 0; i < count; i++)
    {
        Vector2 offset = dir * step * i;
        Projectile.NewProjectile(
            source,
            startPos + offset,
            beamVel,
            ModContent.ProjectileType<DecayBeam>(),
            damage,
            knockback,
            player.whoAmI
        );
    }
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
