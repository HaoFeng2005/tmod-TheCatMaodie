using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Projectiles
{
    // 二阶段大狗的大弹幕(发射后被"锁定点"引爆, 炸开成 6 发小弹幕)。
    //
    // 生命周期:
    //   直线飞行(不追踪) → 沿飞行方向越过锁定点一段距离 → 原地爆开:
    //   以爆点为中心, 第一发小弹幕朝玩家"当前"位置, 其余按 60° 间隔铺满一圈
    //
    // "飞到玩家身后再一段距离才爆"的实现:
    //   发射方把"发射那一刻的玩家位置 + 260px(沿发射方向)"存进 localAI[0..1]。
    //   用飞行方向上的投影距离判断"越过了多少", 所以弹幕不管从哪儿发射、以什么角度发射都成立。
    //   玩家在飞行途中走位可以改变这个锁定点(发射瞬间已定死, 走位甩开爆破点就是玩法)
    //
    // 便签:
    //   ai[0]      = 爆开时的伤害(小弹幕用;0 = 用默认值)
    //   ai[1]      = 追踪目标的玩家序号(爆开时第一发朝谁)
    //   ai[2]      = (未用)
    //   localAI[0/1] = 锁定爆破点的 x/y
    //   localAI[2]   = 是否已经爆过(防重复)
    public class BigDogOrb : ModProjectile
    {
        // ── 可调参数 ──
        protected virtual float FlySpeed => 11f;            // 大弹幕飞行速度
        protected virtual int DetonateBeyond => 260;        // 越过锁定点多少像素后爆开
        protected virtual int OrbDamage => 40;              // 爆开时小弹幕的伤害
        protected virtual float MinibulletSpeed => 7.5f;    // 小弹幕速度
        protected virtual int MiniCount => 12;              // 爆开发几发(第一发朝玩家, 其余 +30° 间隔铺满一圈)
        protected virtual float MiniAngleStep => MathHelper.Pi / 6f;   // 30°

        private const float MaxFlyFrames = 240;             // 兜底: 最多飞4秒, 超时直接爆开

        public override string Texture => "TheCatMaodie/Projectiles/BigDogShot_1";

        public override void SetDefaults()
        {
            Projectile.width = 40;         // 大弹幕自己的判定(比贴图略小, 手感好)
            Projectile.height = 40;
            Projectile.friendly = false;
            Projectile.hostile = true;
            Projectile.penetrate = -1;     // 不因命中消失 —— 它的死法只有"爆开"或"超时"
            Projectile.timeLeft = (int)MaxFlyFrames;
            Projectile.tileCollide = false;    // 穿墙: 这是从锁定点后面炸出来的, 被地形挡住就没意义了
            Projectile.light = 0.5f;
        }

        // 爆破点通过 localAI 传入, 但 localAI 不进原版的 NPC 同步包 —— 弹幕的 localAI 联机时
        // 各客户端独立, 所以发射方(服务器)写好后要主动触发一次 netUpdate 把整个弹幕同步过去
        public void ProjNetUpdate()
        {
            Projectile.netUpdate = true;
        }

        // 给 boss 写爆破点用的通道(弹幕自己的 AI 只读不写这两个入口)
        public float ProjLocalAI0
        {
            get => Projectile.localAI[0];
            set => Projectile.localAI[0] = value;
        }

        public float ProjLocalAI1
        {
            get => Projectile.localAI[1];
            set => Projectile.localAI[1] = value;
        }

        public override void AI()
        {
            // 直线飞行: 速度由发射方定好方向后, 每帧按 FlySpeed 补满(防击退之类把速度削掉)
            Projectile.velocity = Vector2.Normalize(Projectile.velocity) * FlySpeed;
            Projectile.rotation += 0.09f;    // 自转, 纯装饰

            // 拖尾粒子
            if (Main.rand.NextBool(2))
            {
                Dust d = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.Silver);
                d.noGravity = true;
                d.velocity *= 0.3f;
            }

            if (Main.netMode == NetmodeID.MultiplayerClient) return;   // 爆炸判定只在服务器/单机做

            bool detonate = false;
            Vector2 lockPos = new Vector2(Projectile.localAI[0], Projectile.localAI[1]);

            if (Projectile.timeLeft <= 1)
            {
                detonate = true;      // 超时兜底: 到点也要炸
            }
            else if (lockPos != Vector2.Zero)
            {
                // 沿飞行方向的投影距离: 从锁定点到当前位置, 在飞行方向上走过了多少
                Vector2 dir = Vector2.Normalize(Projectile.velocity);
                float passed = Vector2.Dot(lockPos - Projectile.Center, dir);
                if (passed < -DetonateBeyond) detonate = true;   // 负值 = 已经越过锁定点这么远
            }

            if (detonate)
            {
                Projectile.Kill();
            }
        }

        // 爆开(被打掉不算 —— penetrate=-1 所以不会被打掉; 只有 detonate 和超时会走到这)
        public override void OnKill(int timeLeft)
        {
            // 爆炸粒子
            for (int i = 0; i < 18; i++)
            {
                Dust d = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.Silver);
                d.noGravity = true;
                d.velocity = Main.rand.NextVector2Circular(7f, 7f);
                d.scale = 1.4f;
            }

            // 弹幕生成只在服务器/单机做, 联机客户端各生成一份就重复了
            if (Main.netMode == NetmodeID.MultiplayerClient) return;

            int targetIndex = (int)Projectile.ai[1];
            Player target = (targetIndex >= 0 && targetIndex < Main.maxPlayers) ? Main.player[targetIndex] : null;

            Vector2 center = Projectile.Center;
            float baseAngle;
            if (target != null && target.active && !target.dead)
                baseAngle = (target.Center - center).ToRotation();      // 第一发朝玩家"现在"的位置
            else
                baseAngle = Projectile.velocity.ToRotation();           // 玩家不在了就按原方向铺

            int type = ModContent.ProjectileType<BigDogSpit>();
            for (int i = 0; i < MiniCount; i++)
            {
                float ang = baseAngle + MiniAngleStep * i;
                Vector2 vel = ang.ToRotationVector2() * MinibulletSpeed;
                // 小弹幕: ai[0]=0 不追踪, ai[2]=0 不变大也不触发诅咒
                Projectile.NewProjectile(Projectile.GetSource_FromAI(), center, vel,
                    type, OrbDamage, 0f, Main.myPlayer, 0f, -1f, 0f);
            }
        }
    }
}
