using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Projectiles
{
    // 音波信标:二阶段大狗"剑模式"里由 boss 成对发射的音波。
    // 两只音波在"以玩家为圆心"的同一个圆上公转, 各自朝圆心方向(也就是朝玩家)射出光束,
    // 两束在玩家身上交叉成 X。光束是单方向的: 从音波出发、穿过圆心、向对侧延伸 ——
    // 音波背向圆心的那一侧(圆外)永远没有光束。
    //
    // 四段时间轴(ai[1]):
    //   [0, Orbit)                音波沿圆公转(本体可见, 细瞄准线跟着扫)
    //   [Orbit, +Aim)             宽紫 X 预热: 光束跟着公转扫动 —— "动态锁定"的观感
    //   [+, +Charge)              锁定蓄力: 圆心与角度冻结(= 此刻的位置), 光束逐渐变亮 —— 真正的躲避窗口
    //   [+, +Fire)                发射: 光束从音波处向圆心方向快速延伸出来, 高亮 + 伤害
    //   结束                       消失(boss 会发下一对)
    //
    // 联机同步: 环绕角 φ = ai[0] + 计时×角速度, 全部由同步槽(ai[0]/ai[1]) + 弹幕自身位置推导,
    // 锁定后的音波本体位置和光束角度两端算出同一个值, 不需要额外通道
    public class BigDogEcho : ModProjectile
    {
        public override string Texture => "TheCatMaodie/Projectiles/BigDogSpit";   // 复用一期声波环贴图

        // ── 四段节奏(公共常量: boss 的音波对节拍引用 TotalTime) ──
        public const float OrbitFrames = 45f;
        public const float AimFrames = 45f;
        public const float ChargeFrames = 35f;
        public const float FireFrames = 25f;

        public static float TotalTime => OrbitFrames + AimFrames + ChargeFrames + FireFrames;

        // ── 可调参数 ──
        protected virtual float HoverRadius => 250f;      // 音波绕行的半径
        protected virtual float OrbitSpeed => 0.014f;     // 环绕角速度(弧度/帧, ≈0.84 rad/s)
        protected virtual float BeamLength => 2300f;      // 光束全长
        protected virtual float BeamDamageHalfWidth => 13f;  // 伤害判定半宽
        protected virtual float BeamExtendFrames => 8f;   // 发射时光束从音波处延伸到全长的帧数
        public virtual int LaserDamage => 65;

        public override void SetStaticDefaults()
        {
            ProjectileID.Sets.DrawScreenCheckFluff[Type] = 2600;   // 光束长、本体常在屏幕边缘: 放宽剔除
        }

        public override void SetDefaults()
        {
            Projectile.width = 28;
            Projectile.height = 28;
            Projectile.friendly = false;
            Projectile.hostile = true;
            Projectile.penetrate = -1;
            Projectile.timeLeft = (int)TotalTime + 5;
            Projectile.tileCollide = false;
            Projectile.light = 0.3f;
        }

        // 找最近的活玩家(锚点)。单机就是那一个玩家; 联机取最近, 两端算出同一个
        private Player Anchor()
        {
            Player best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                if (!p.active || p.dead) continue;
                float d = Vector2.DistanceSquared(p.Center, Projectile.Center);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        // 环绕角: 由同步槽推导, 两端一致
        private float Phi => Projectile.ai[0] + Projectile.ai[1] * OrbitSpeed;

        // 音波本体的位置 = 光束中心 + 环绕角方向的半径偏移(锁定后中心冻结, 本体也跟着冻结)
        private Vector2 WavePos => Projectile.Center + Phi.ToRotationVector2() * HoverRadius;

        public override void AI()
        {
            Projectile.ai[1]++;
            float t = Projectile.ai[1];
            float lockT = OrbitFrames + AimFrames;

            Player p = Anchor();
            if (p == null)
            {
                Projectile.Kill();
                return;
            }

            // 锁定之前: 锚点 = 玩家中心(光束中心), 每帧跟着玩家
            if (t <= lockT)
                Projectile.Center = p.Center;

            // 伤害窗口结束就消失
            if (t > TotalTime)
                Projectile.Kill();
        }

        // 伤害判定: 只在发射窗口。光束是单方向的: 从音波位置出发、朝圆心方向延伸,
        // 所以判定段 = [音波位置, 音波位置 + 朝内方向 × 光束长], 音波背后不在判定内
        public override bool? Colliding(Rectangle projHitbox, Rectangle targetHitbox)
        {
            float t = Projectile.ai[1];
            if (t <= OrbitFrames + AimFrames + ChargeFrames || t > TotalTime) return false;

            Vector2 start = WavePos;                          // 冻结后的音波位置
            Vector2 inward = -Phi.ToRotationVector2();        // 朝圆心的方向
            Vector2 toTarget = targetHitbox.Center.ToVector2() - start;
            float along = Vector2.Dot(toTarget, inward);
            if (along < 0f || along > BeamLength) return false;   // 在音波背后 / 超出光束长 → 无判定

            float perp = Math.Abs(Vector2.Dot(toTarget, inward.RotatedBy(MathHelper.PiOver2)));
            return perp <= BeamDamageHalfWidth + 8f;   // +8: 玩家判定框的宽容度
        }

        // ── 绘制 ──
        // 所有光束都是单方向: 以音波本体为起点、朝圆心(玩家)方向延伸, 音波背后没有光
        public override bool PreDraw(ref Color lightColor)
        {
            float t = Projectile.ai[1];
            float lockT = OrbitFrames + AimFrames;
            Vector2 inward = -Phi.ToRotationVector2();          // 从音波指向圆心(玩家)
            Vector2 wavePos = WavePos - Main.screenPosition;    // 音波本体(光束的起点)

            // ── 环绕段: 细瞄准线(流动版) + 本体 ──
            if (t <= OrbitFrames)
            {
                DrawFlowingBeam(wavePos, inward, BeamLength, 3f, new Color(190, 120, 255) * 0.45f,
                    Main.GlobalTimeWrappedHourly, 3.0f, 0.012f);
                DrawWave(wavePos, t, lightColor);
                return false;
            }

            float fireT = t - lockT - ChargeFrames;   // ≥0 = 已进入发射窗口

            // ── 预热/蓄力: 宽紫光束(跟着公转扫, 蓄力段冻结) ──
            float aimP = MathHelper.Clamp((t - OrbitFrames) / AimFrames, 0f, 1f);
            float chargeP = MathHelper.Clamp((t - lockT) / ChargeFrames, 0f, 1f);
            // 预热期缓脉动; 越接近锁定越亮; 蓄力期继续爬亮
            float pulse = 0.82f + 0.18f * (float)Math.Sin(t * 0.12f);
            float energy = Math.Max(aimP * pulse, chargeP);

            Color outer = new Color(120, 55, 200) * (0.32f * energy);
            Color mid = new Color(165, 85, 255) * (0.48f * energy);
            Color core = new Color(210, 150, 255) * (0.75f * energy);

            // 蓄力段光束带一点"颤抖"(±1.5px 抖动), 读起来是蓄满前的压迫感
            Vector2 jitter = Vector2.Zero;
            if (t > lockT)
                jitter = Main.rand.NextVector2Circular(1.5f, 1.5f);

            // 预热/蓄力段的宽紫光束用"流动"画法: 亮度带沿线从音波流向圆心(灵动, 参照灾厄的流光预警线)
            float now = Main.GlobalTimeWrappedHourly;
            DrawFlowingBeam(wavePos + jitter, inward, BeamLength, 30f, outer, now, 2.6f, 0.010f);
            DrawFlowingBeam(wavePos + jitter, inward, BeamLength, 18f, mid, now, 3.4f, 0.014f);
            DrawFlowingBeam(wavePos + jitter, inward, BeamLength, 8f, core, now, 4.2f, 0.018f);

            // 蓄力完成度高的白芯(将射未射)
            if (t > lockT)
                DrawOneWayBeam(wavePos + jitter, inward, BeamLength, 4f, new Color(255, 240, 255) * chargeP * 0.9f);

            // ── 发射: 光束从音波处向圆心方向快速延伸出来, + 交叉点星芒 + 白芯 ──
            if (fireT >= 0f)
            {
                float extend = MathHelper.Clamp(fireT / BeamExtendFrames, 0f, 1f);
                float len = BeamLength * (0.12f + 0.88f * extend);
                DrawOneWayBeam(wavePos, inward, len, 30f, new Color(120, 55, 200) * 0.55f);
                DrawOneWayBeam(wavePos, inward, len, 18f, new Color(185, 110, 255) * 0.75f);
                DrawOneWayBeam(wavePos, inward, len, 8f, new Color(230, 190, 255) * 0.95f);
                DrawOneWayBeam(wavePos, inward, len, 4f, new Color(255, 245, 255) * 0.95f);
                DrawStar(Projectile.Center - Main.screenPosition, fireT);
                return false;
            }

            // 未发射: 画音波本体(发射后本体"变成"光束, 不再画)
            DrawWave(wavePos, t, lightColor);
            return false;
        }

        // 音波本体: 声波环贴图, 轻微脉动
        private void DrawWave(Vector2 screenPos, float t, Color lightColor)
        {
            Texture2D tex = ModContent.Request<Texture2D>(Texture).Value;
            float scale = 1f + 0.12f * (float)Math.Sin(t * 0.15f);
            Main.spriteBatch.Draw(
                tex,
                screenPos,
                null,
                Color.Lerp(lightColor, Color.White, 0.35f),
                Phi + (float)Math.Sin(t * 0.05f) * 0.2f,
                tex.Size() / 2f,
                scale,
                SpriteEffects.None,
                0f);
        }

        // 单方向光束: 从 start 出发、沿 dir 延伸 length(起点处没有"另一半")
        private static void DrawOneWayBeam(Vector2 start, Vector2 dir, float length, float width, Color color)
        {
            Main.spriteBatch.Draw(
                TextureAssets.MagicPixel.Value,
                start,
                new Rectangle(0, 0, 1, 1),
                color,
                dir.ToRotation(),
                new Vector2(0f, 0.5f),            // 以左端中点为轴 → 线段只向 dir 一侧延伸
                new Vector2(length, width),
                SpriteEffects.None,
                0f);
        }

        // 流动光束: 把线拆成 N 段, 每段的亮度按"沿程位置 + 时间"的正弦滚动 ——
        // 亮带沿线跑动(从音波流向圆心), 这是灾厄流光预警线(如阿忒弥斯的
        // increment = 沿程比 + 时间×速度)的魔素线平替: 没有着色器也有"活"的观感
        private static void DrawFlowingBeam(Vector2 start, Vector2 dir, float length, float width, Color color,
            float time, float flowSpeed, float freq)
        {
            const int Segs = 14;
            float segLen = length / Segs;
            Vector2 step = dir * segLen;
            float rot = dir.ToRotation();
            for (int i = 0; i < Segs; i++)
            {
                float along = (i + 0.5f) * segLen;
                // 波峰随时间沿线推进: 时间项为正 → 图案向 along 增大的方向跑(即流向圆心)
                float wave = 0.55f + 0.45f * (float)Math.Sin(time * flowSpeed - along * freq);
                Color c = color * wave;
                if (c.R + c.G + c.B < 6) continue;    // 全暗的段不画, 省一次 draw
                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    start + step * i,
                    new Rectangle(0, 0, 1, 1),
                    c,
                    rot,
                    new Vector2(0f, 0.5f),
                    new Vector2(segLen + 2f, width),   // +2: 段间无缝
                    SpriteEffects.None,
                    0f);
            }
        }

        // 交叉点星芒(发射瞬间最亮, 随后衰减)
        private void DrawStar(Vector2 center, float fireT)
        {
            float flash = MathHelper.Clamp(1f - fireT / (FireFrames * 0.6f), 0f, 1f);
            float size = 44f + 30f * flash;
            for (int i = 0; i < 4; i++)
            {
                Vector2 d = (MathHelper.PiOver4 * i + 0.3f).ToRotationVector2();
                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    center,
                    new Rectangle(0, 0, 1, 1),
                    new Color(255, 230, 180) * (0.5f * flash + 0.3f),
                    d.ToRotation(),
                    new Vector2(0.5f, 0.5f),
                    new Vector2(size, 4f + 4f * flash),
                    SpriteEffects.None,
                    0f);
            }
        }
    }
}
