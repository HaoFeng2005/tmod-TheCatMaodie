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
        // 预警线长度: 从屏幕上方一路垂下去, 要长到玩家满速下坠 3 秒也看不到端点
        // (满速约 10 像素/帧 × 180 帧 ≈ 1800, 加上屏幕对角线约 1900 → 给 4000 留足余量)
        protected virtual float TeleLineLength => 4000f;
        protected virtual float TeleLineWidth => 3f;      // 细度: 和 boss 放 X 型时的预警线一致(3)

        // 贴图朝向: 附魔剑的物品图剑尖斜朝右上(≈ -45°), 要竖直剑尖朝下(+90°)得再转 135°。
        // 预警影和下落都用这个朝向, 看上去就是"竖直往下插"
        private const float DownTilt = MathHelper.Pi * 0.75f;

        public override void SetStaticDefaults()
        {
            // 出生点在屏幕上方之外, 预警线一路垂到屏幕下方之外: 放宽剔除范围。
            // 这个值必须比"出生点离屏距离 + 预警线长度"大, 否则玩家往下飞的时候
            // 整个投射物会被判定为"离屏"而不再绘制 —— 预警线会凭空消失。
            ProjectileID.Sets.DrawScreenCheckFluff[Type] = 4600;
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

            // 预警阶段: 竖直预警线"长闪一下"就不再变 —— 亮起后保持不动, 落剑前几帧淡出。
            // (参照阿忒弥斯的激光预警线: 它是稳定的, 只在最后几帧淡掉, 不是反复闪)
            if (Projectile.ai[1] == 0f)
            {
                float sinceStart = TelegraphFrames - Projectile.ai[0];     // 已过去多少帧
                float remain = Projectile.ai[0];                           // 还剩多少帧
                float fadein = MathHelper.Clamp(sinceStart / 6f, 0f, 1f);  // 亮起
                float fadeout = MathHelper.Clamp(remain / 8f, 0f, 1f);     // 落下前淡出
                Color c = new Color(225, 215, 255) * (0.85f * fadein * fadeout);

                // 注意轴心: 不旋转, 直接把 1x1 白点纵向拉长, 轴心取"上端中点"(0.5, 0) ——
                // 这样线是"从剑的位置单向下垂", 横跨整屏; 若用 (0.5, 0.5) 会以上下各半的方式
                // 以剑为中心展开, 剑在屏幕外, 往下就只剩一半长度(之前"长度不够"就是这个原因)
                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    screenPos,                                 // 从剑的位置起笔
                    new Rectangle(0, 0, 1, 1),
                    c,
                    0f,                                        // 竖直靠缩放实现, 不需要旋转
                    new Vector2(0.5f, 0f),                     // 轴心: 上端中点 → 单向下垂
                    new Vector2(TeleLineWidth, TeleLineLength),
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
