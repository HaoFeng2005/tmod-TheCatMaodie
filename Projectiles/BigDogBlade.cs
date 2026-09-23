using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using ReLogic.Content;

namespace TheCatMaodie.Projectiles
{
    // boss 用的刀刃弹幕。两种模式(用 ai[0] 区分, 和一期 BigDogSpit 用 ai 参数区分 boss/小怪是同一手法):
    //   模式0(默认) 一期的"扇形掷刃": 直线飞行、刀尖朝飞行方向、碰到方块就扎进去消失
    //   模式1       二阶段"悬停旋刃": 先在原地缓缓自转一下, 然后朝玩家飞出去 ——
    //               初速慢、带弱追踪、并且越来越快(前段好躲, 拖久了反而更难躲)
    // 贴图来自 blade.jpg(黑底抠图 + 自动转正, 见 Art/build_bigdog_sprites.ps1)
    //
    // 模式1 的便签: ai[1] = 原地旋转的帧数  ai[2] = 起飞方向(弧度)
    //               localAI[0] = 已走帧数(用来切阶段)
    public class BigDogBlade : ModProjectile
    {
        // 自转速度(弧度/帧),0.25 约等于每 25 帧翻一圈(0.42秒)
        private const float RollSpeed = 0.25f;

        // ── 模式1(悬停旋刃)的参数 ──
        private const float SpinTurnSpeed = 0.030f;   // 原地旋转时长轴每帧转多少(缓慢)
        private const float SpinRollSpeed = 0.10f;    // 原地旋转时翻滚相位的推进(比飞行时慢)
        private const float LaunchStartSpeed = 6f;    // 起飞初速(比一期掷刃的 18 慢得多 → "速度削减")
        private const float LaunchAccel = 0.22f;      // 每帧加速(越飞越快)
        private const float LaunchMaxSpeed = 17f;     // 加速上限
        private const float LaunchTurnRate = 0.012f;  // 弱追踪: 每帧最多把方向朝玩家转这么多弧度

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

        // 模式1 用:找最近的活玩家(弱追踪的目标)
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

        public override void AI()
        {
            bool volley = Projectile.ai[0] == 1f;
            Projectile.localAI[0]++;                     // 已走帧数(模式1切阶段用)

            // ── 模式1 的阶段A: 原地缓缓自转, 还没飞出去 ──
            if (volley && Projectile.localAI[0] <= Projectile.ai[1])
            {
                Projectile.velocity = Vector2.Zero;                    // 钉在原地
                Projectile.rotation += SpinTurnSpeed;                  // 长轴慢慢转
                Projectile.localAI[1] += SpinRollSpeed;                // 翻滚也慢
                if (Main.rand.NextBool(4))
                {
                    Dust d = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.Silver);
                    d.noGravity = true;
                    d.velocity *= 0.15f;
                    d.scale = 0.8f;
                }
                return;
            }

            // ── 模式1 的阶段B: 朝玩家飞出去, 弱追踪 + 越来越快 ──
            if (volley)
            {
                if (Projectile.velocity.LengthSquared() < 0.01f)
                {
                    // 起飞那一帧: 方向用 boss 传进来的扇角(保住"扇形散开"的起手)
                    Projectile.velocity = Projectile.ai[2].ToRotationVector2() * LaunchStartSpeed;
                }
                else
                {
                    // 弱追踪: 每帧最多把速度方向朝玩家转 LaunchTurnRate 弧度
                    Player p = Anchor();
                    if (p != null)
                    {
                        float speed = Projectile.velocity.Length();
                        float want = (p.Center - Projectile.Center).ToRotation();
                        float diff = MathHelper.WrapAngle(want - Projectile.velocity.ToRotation());
                        float step = MathHelper.Clamp(diff, -LaunchTurnRate, LaunchTurnRate);
                        Projectile.velocity = (Projectile.velocity.ToRotation() + step).ToRotationVector2() * speed;
                    }

                    // 越飞越快(有上限)
                    float s = Math.Min(Projectile.velocity.Length() + LaunchAccel, LaunchMaxSpeed);
                    Projectile.velocity = Vector2.Normalize(Projectile.velocity) * s;
                }
            }

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
