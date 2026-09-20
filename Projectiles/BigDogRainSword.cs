using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Projectiles
{
    // 剑雨:大飞剑召唤的、从天上竖直落下的飞剑。
    // 生命周期: 在屏幕上方悬停倒计时(画竖直预警线, 缓脉动不刺闪) → 计时到 → 竖直插下去
    //   → 碰到地面/超出射程就消失。
    // 预警线画在自己所在的 x 位置、从悬停点一直到屏幕外下方 —— 玩家看到线就知道那里会落剑
    //
    // 便签: ai[0] = 预警剩余帧数(倒数)  ai[1] = 是否已开始下落(0/1)
    public class BigDogRainSword : ModProjectile
    {
        public override string Texture => "Terraria/Images/Item_" + ItemID.EnchantedSword;   // 占位: 附魔剑贴图

        // 预警时长必须是 const: 生成方(大飞剑)要把它在 NewProjectile 里塞进 ai[0] ——
        // 之前就是漏了这一步, ai[0] 出生是 0, 第一帧就"预警结束"开始下落, 预警线一次都没画过
        public const float TelegraphFrames = 40f;
        protected virtual float FallSpeed => 17f;         // 下落速度
        protected virtual float TeleLineLength => 1500f;  // 预警线长度(从悬停点往下, 必出屏)

        // 贴图朝向: 附魔剑的物品图剑尖斜朝右上(≈ -45°), 要竖直剑尖朝下(+90°)得再转 135°。
        // 预警影和下落都用这个朝向, 看上去就是"竖直往下插"
        private const float DownTilt = MathHelper.Pi * 0.75f;

        public override void SetStaticDefaults()
        {
            // 出生点在屏幕上方之外, 预警线一路伸出屏幕: 放宽剔除
            ProjectileID.Sets.DrawScreenCheckFluff[Type] = 1800;
        }

        public override void SetDefaults()
        {
            Projectile.width = 22;
            Projectile.height = 46;
            Projectile.friendly = false;
            Projectile.hostile = true;
            Projectile.penetrate = 1;
            Projectile.timeLeft = 300;
            Projectile.tileCollide = true;    // 落到地面就消失(自然收尾)
            Projectile.light = 0.25f;
        }

        public override void AI()
        {
            if (Projectile.ai[1] == 0f)
            {
                // 预警阶段: 悬停不动, 倒计时
                Projectile.ai[0]--;
                Projectile.velocity = Vector2.Zero;
                if (Projectile.ai[0] <= 0f)
                {
                    Projectile.ai[1] = 1f;                      // 开始下落
                    Projectile.velocity.Y = FallSpeed;
                }
            }
            else
            {
                // 下落: 速度恒定, 剑尖竖直朝下(带一点微摆)
                Projectile.velocity.Y = FallSpeed;
                Projectile.rotation = DownTilt + (float)Math.Sin(Projectile.timeLeft * 0.3f) * 0.05f;
            }
        }

        public override bool? Colliding(Rectangle projHitbox, Rectangle targetHitbox)
        {
            return null;   // 下落阶段用默认判定箱(22x46), 预警阶段悬在屏幕外碰不到人
        }

        public override bool OnTileCollide(Vector2 oldVelocity)
        {
            // 插进地面: 一小撮白色溅射然后消失
            for (int i = 0; i < 8; i++)
            {
                Dust d = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.Silver);
                d.noGravity = false;
                d.velocity = Main.rand.NextVector2Circular(3f, 2f);
            }
            return true;   // true = 按默认逻辑杀死弹幕
        }

        public override bool PreDraw(ref Color lightColor)
        {
            Texture2D tex = ModContent.Request<Texture2D>(Texture).Value;
            Vector2 screenPos = Projectile.Center - Main.screenPosition;

            // 预警阶段: 竖直预警线"长闪" —— 慢速明暗交替(亮得久、看得清), 半透明剑影提示落点
            if (Projectile.ai[1] == 0f)
            {
                float sinceStart = TelegraphFrames - Projectile.ai[0];
                float fadein = MathHelper.Clamp(sinceStart / 6f, 0f, 1f);
                // 长闪: 36 帧一个周期, 前 24 帧亮 / 后 12 帧暗, 明暗对比拉大
                float cycle = sinceStart % 36f;
                float blink = cycle < 24f ? 0.85f : 0.15f;
                Color c = new Color(225, 215, 255) * (0.9f * blink * fadein);

                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    screenPos,
                    new Rectangle(0, 0, 1, 1),
                    c,
                    MathHelper.PiOver2,                   // 竖直(线段默认水平, 转90度)
                    new Vector2(0.5f, 0.5f),
                    new Vector2(TeleLineLength, 4f),
                    SpriteEffects.None,
                    0f);

                // 半透明的剑本体预告: 剑尖朝下(竖直), 一直悬到落下那一刻
                Main.spriteBatch.Draw(
                    tex,
                    screenPos,
                    null,
                    lightColor * 0.5f * fadein,
                    DownTilt,
                    tex.Size() / 2f,
                    1.2f,
                    SpriteEffects.None,
                    0f);
                return false;
            }

            // 下落阶段: 正常画
            Main.spriteBatch.Draw(
                tex,
                screenPos,
                null,
                lightColor,
                Projectile.rotation,
                tex.Size() / 2f,
                1.2f,
                SpriteEffects.None,
                0f);
            return false;
        }
    }
}
