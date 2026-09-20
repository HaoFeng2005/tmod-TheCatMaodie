using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using ReLogic.Content;

namespace TheCatMaodie.Projectiles
{
    // boss"扇形掷刃"用的刀刃弹幕。
    // 和音波弹幕的区别:它不追踪玩家,就是一把被甩出去的刀——直线飞行、刀尖朝飞行方向、
    // 碰到方块就扎进去消失。贴图来自 blade.jpg(黑底抠图 + 自动转正,见 Art/build_bigdog_sprites.ps1)
    public class BigDogBlade : ModProjectile
    {
        // 自转速度(弧度/帧),0.25 约等于每 25 帧翻一圈(0.42秒)
        private const float RollSpeed = 0.25f;

        private Asset<Texture2D> blade;   // 贴图缓存(第一次用到时才加载)

        private Asset<Texture2D> GetBlade()
        {
            if (blade == null)
                blade = ModContent.Request<Texture2D>(Texture, AssetRequestMode.ImmediateLoad);
            return blade;
        }

        public override void SetDefaults()
        {
            // 刃是"绕长轴翻滚"的(见 PreDraw),视觉上的占位就是贴图本身:长56、高22。
            // 判定箱比贴图略收一点(44x26),旋转/翻面时不会出现"看着没碰到却被判中"
            Projectile.width = 44;
            Projectile.height = 26;
            Projectile.friendly = false;   // 敌对弹幕:打玩家,不打NPC
            Projectile.hostile = true;
            Projectile.DamageType = DamageClass.Default;

            Projectile.penetrate = 1;      // 打中1个玩家就消失
            Projectile.timeLeft = 240;     // 4秒:扇形里朝斜上方飞的要飞一会儿
            Projectile.tileCollide = true; // 碰到方块就消失(刃扎进地形)
            Projectile.ignoreWater = true;
        }

        public override void AI()
        {
            // 长轴始终对准飞行方向——"翻滚"不是靠旋转贴图,而是靠 PreDraw 里的纵向缩放
            Projectile.rotation = Projectile.velocity.ToRotation();
            Projectile.localAI[1] += RollSpeed;   // 翻滚相位

            // 淡淡的银色拖尾:扇形里飞得快,没有拖尾容易看不清
            if (Main.rand.NextBool(3))
            {
                Dust trail = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.Silver);
                trail.noGravity = true;
                trail.velocity *= 0.2f;
                trail.scale = 0.9f;
            }
        }

        // 自己画:绕"横向长轴"翻滚。
        // 2D 里没有真正的绕轴旋转,标准做法是用纵向缩放来演:正对镜头时是全高,
        // 转到侧对镜头时被压成一条薄边;翻到背面时再竖直翻转一下,读起来就是硬币在桌上滚
        public override bool PreDraw(ref Color lightColor)
        {
            Texture2D tex = GetBlade().Value;
            float phase = Projectile.localAI[1];
            float flat = Math.Abs((float)Math.Cos(phase));
            flat = Math.Max(flat, 0.18f);          // 留一点厚度,不然会有"整帧消失"的闪烁感
            bool backSide = Math.Sin(phase) < 0f;  // 翻到背面 → 竖直翻转

            Main.spriteBatch.Draw(
                tex,
                Projectile.Center - Main.screenPosition,
                null,
                lightColor,
                Projectile.rotation,
                tex.Size() / 2f,
                new Vector2(1f, flat),
                backSide ? SpriteEffects.FlipVertically : SpriteEffects.None,
                0f
            );
            return false;
        }

        // 消失(打中玩家 / 扎进方块 / 超时)时溅一圈银屑
        public override void OnKill(int timeLeft)
        {
            for (int i = 0; i < 8; i++)
            {
                Dust burst = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.Silver);
                burst.noGravity = true;
                burst.velocity = Main.rand.NextVector2Circular(4f, 4f);
                burst.scale = 1.1f;
            }
        }
    }
}
