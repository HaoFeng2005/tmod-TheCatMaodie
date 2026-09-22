using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Projectiles
{
    // 音波信标:二阶段大狗"剑模式"里由 boss 从嘴里吐出的音波。
    // 它像一只"飞行小怪"(无敌的那种)一样活动, 全程只用速度移动 —— 没有任何一帧直接改写位置,
    // 所以不会出现"被强行拉到某处"的突兀感:
    //   从大狗嘴里飞出 → 用原版飞行怪的转向逻辑追向玩家 → 保持在玩家周围的环上边绕边漂
    //   → 朝玩家架起一束激光(预警线跟着玩家走)→ 光束越来越粗越来越亮 → 直接点火并继续弱追踪 → 消失
    // 两只音波各自独立开火(不做交叉 X), 从各自所在的方位射向玩家。
    //
    // 三段生命周期(phase, 实例字段):
    //   0 接近/环绕   飞向玩家并保持在 StationRadius 环上, 边绕边漂(画本体)
    //   1 预瞄         激光预警线实时指着玩家扫动, 从细到粗、从暗到亮地增强, 全程不冻结
    //                  —— 这一段就是躲避窗口(80 帧 = 1.33 秒)
    //   2 发射         直接点火: 光束向前延伸 + 炮口星芒 + 伤害, 同时继续弱追踪玩家 → 结束
    //
    // 位置语义: Projectile.Center = 音波自己在哪(它在飞、在漂), 光束方向由它指向玩家
    public class BigDogEcho : ModProjectile
    {
        public override string Texture => "TheCatMaodie/Projectiles/BigDogSpit";   // 声波环贴图(本体)

        // 借月球领主死亡射线的贴图当激光核心条带。它在磁盘上是 LZX 压缩的 .xnb,
        // 离线读不出长宽/朝向, 所以绘制时做自适应(见 DrawBorrowedCore), 两种情况都不会画糊
        private const string BeamTexturePath = "Terraria/Images/Projectile_455";

        // ── 四段节奏(公共常量: boss 的音波对节拍引用 TotalTime) ──
        public const float SetupFrames = 45f;
        // 预瞄段: 光束实时指着玩家扫 —— 这一段是"瞄准中", 玩家还不知道该往哪躲
        public const float AimFrames = 50f;
        // 锁线段: 方向定死在锁定那一刻的玩家位置, 线不再跟人; 这段时间光束继续增粗增亮。
        // ★ 这才是真正的躲避窗口: 看到线定住不动了, 就横向跑出那条线
        public const float LockFrames = 30f;
        public const float FireFrames = 25f;
        // 第一只发射之后, 第二只等这么多帧才开始预瞄 → 两束激光拉开约 2 秒的间隔
        public const float StaggerFrames = 120f;

        // 总时长按"最晚的那只"算, boss 用这个排下一对的节拍, 保证两对不会叠在一起
        public static float TotalTime => SetupFrames + StaggerFrames + AimFrames + LockFrames + FireFrames;

        // ── 可调参数: 飞行(飞行小怪逻辑) ──
        protected virtual float FlySpeed => 7.5f;        // 巡航速度
        protected virtual float SteerSteps => 15f;       // 转向惯性: 越大越"黏"(原版飞行怪常用 21)
        protected virtual float StationRadius => 300f;    // 在玩家周围保持的环半径(基础值)
        protected virtual float RadiusStep => 90f;       // 每只音波半径再加这个数 × 变体号 → 两只不在同一圈上
        protected virtual float TangentialSpeed => 3.4f; // 沿环切向的漂移速度(绕圈感就靠它)
        protected virtual float RadiusPull => 0.075f;    // 回到环上的力度(太大就会像被拉过去)

        // ── 可调参数: 激光 ──
        protected virtual float BeamLength => 3400f;     // 光束全长(长到玩家飞行3秒也看不到端点)
        protected virtual float BeamThickness => 20f;    // 光束粗细(基准值, 各层按它乘系数)
        protected virtual float BeamDamageHalfWidth => 7f;   // 伤害判定半宽
        protected virtual float BeamExtendFrames => 8f;  // 发射时光束从音波处延伸到全长的帧数
        // 发射期间的弱追踪: 每帧最多把光束方向朝玩家转这么多弧度。
        // 0.005 ≈ 0.29度/帧 ≈ 17度/秒 —— 玩家在 300 像素外横向跑动的角速度约 0.033 弧度/帧(≈114度/秒),
        // 是它的 6 倍多, 所以"站着不动会被慢慢追上, 跑起来轻松甩开"
        protected virtual float BeamTrackRate => 0.005f;
        public virtual int LaserDamage => 65;

        // ── 运行状态 ──
        private int phase;
        private float phaseTimer;
        private Vector2 beamDir;        // 光束方向(从音波指向玩家), 锁定后冻结
        private Vector2 frozenWave;     // 锁定时音波的位置(= 光束起点)
        private float driftSign = 1f;   // 绕行方向(一顺一逆)
        private int variant;            // 同批第几只(0/1): 决定半径、绕行方向、开火延迟
        private float stationRadius;    // 本只的环半径(带变体偏移)

        private static Asset<Texture2D> beamTex;

        private static Texture2D GetBeamTex()
        {
            if (beamTex == null)
                beamTex = ModContent.Request<Texture2D>(BeamTexturePath, AssetRequestMode.ImmediateLoad);
            return beamTex.Value;
        }

        public override void SetStaticDefaults()
        {
            ProjectileID.Sets.DrawScreenCheckFluff[Type] = 3800;   // 光束长、本体常在屏幕边缘: 放宽剔除
        }

        public override void SetDefaults()
        {
            Projectile.width = 28;
            Projectile.height = 28;
            Projectile.friendly = false;
            Projectile.hostile = true;
            Projectile.penetrate = -1;        // 打不烂(飞行小怪那种"无敌"装置)
            Projectile.timeLeft = 900;        // 兜底: 真正的结束由 phase 走到 3 之后主动 Kill
            Projectile.tileCollide = false;
            Projectile.light = 0.3f;

            // ★ 走"Boss 免疫通道"。默认值是 -1(General), 会和普通弹幕共用玩家的受伤无敌帧 ——
            //   剑雨或另一只音波刚打中玩家时, 这条激光的整个伤害窗口都会落在无敌期里, 一点伤害都没有,
            //   表现出来就是"激光穿过了却没伤害"。原版的月球领主死亡射线等光束用的就是 Bosses 通道
            //   (见 Projectile.Damage() 里 case 455 那一段), 模组弹幕要自己声明
            CooldownSlot = ImmunityCooldownID.Bosses;
        }

        // 找最近的活玩家(飞行与瞄准的锚点)
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
            Projectile.ai[1]++;
            phaseTimer++;
            float t = Projectile.ai[1];

            Player p = Anchor();
            if (p == null)
            {
                Projectile.Kill();
                return;
            }

            // 第一帧: 读出生时给的变体号(0/1), 一次把三处差异定死 ——
            //   环半径不同 → 两只不在同一圈上, 不会贴在一起飞
            //   绕行方向相反 → 一顺一逆
            //   相位时长不同(见 case 0 的判定)→ 开火一先一后
            if (t == 1f)
            {
                variant = (int)Projectile.ai[2];
                driftSign = (variant % 2 == 0) ? 1f : -1f;
                stationRadius = StationRadius + variant * RadiusStep;
            }

            switch (phase)
            {
                // ── 0 接近 + 环绕站位 ──
                // 期望速度 = 径向回环的修正 + 切向漂移。全程走速度插值, 所以是"绕过去"而不是"被拉过去"
                case 0:
                    {
                        Vector2 fromPlayer = Projectile.Center - p.Center;
                        if (fromPlayer.LengthSquared() < 1f) fromPlayer = new Vector2(1f, 0f);
                        float distNow = fromPlayer.Length();
                        Vector2 radial = fromPlayer / distNow;
                        Vector2 tangential = radial.RotatedBy(MathHelper.PiOver2) * driftSign;

                        Vector2 desired = radial * ((stationRadius - distNow) * RadiusPull)
                                        + tangential * TangentialSpeed;
                        if (desired.Length() > FlySpeed) desired = Vector2.Normalize(desired) * FlySpeed;

                        Projectile.velocity = (Projectile.velocity * SteerSteps + desired) / (SteerSteps + 1f);
                        Projectile.velocity.Y += (float)Math.Sin(t * 0.13f) * 0.10f;   // 一点扑腾感

                        if (Projectile.velocity.LengthSquared() > 0.01f)
                            Projectile.rotation = Projectile.velocity.ToRotation();

                        // ★ 第 2 只(变体1)在这里多绕 StaggerFrames 帧才进预瞄 → 开火错开一先一后
                        if (phaseTimer >= SetupFrames + variant * StaggerFrames)
                        {
                            phase = 1;
                            phaseTimer = 0f;
                            Projectile.netUpdate = true;
                        }
                    }
                    break;

                // ── 1 预瞄: 激光实时指着玩家扫(这一段还在瞄准, 线跟着你走) ──
                case 1:
                    {
                        Projectile.velocity *= 0.94f;
                        Projectile.rotation = Projectile.velocity.LengthSquared() > 1f
                            ? Projectile.velocity.ToRotation()
                            : (p.Center - Projectile.Center).ToRotation();

                        beamDir = Vector2.Normalize(p.Center - Projectile.Center);   // ★ 跟着玩家

                        if (phaseTimer >= AimFrames)
                        {
                            // ★ 锁线: 方向定死在"此刻玩家所在的方向", 位置定在当前(炮台位置)。
                            //   之后线不再跟人 —— 玩家看到线定住, 就该横向跑开了
                            phase = 2;
                            phaseTimer = 0f;
                            frozenWave = Projectile.Center;
                            beamDir = Vector2.Normalize(p.Center - Projectile.Center);
                            Projectile.velocity = Vector2.Zero;
                            Projectile.netUpdate = true;
                        }
                    }
                    break;

                // ── 2 锁线蓄能: 方向与位置都冻住, 但光束继续增粗增亮(不是死停顿) ──
                case 2:
                    Projectile.Center = frozenWave;
                    if (phaseTimer >= LockFrames)
                    {
                        phase = 3;
                        phaseTimer = 0f;
                        Projectile.netUpdate = true;
                    }
                    break;

                // ── 3 发射: 沿锁定的方向点火, 附带弱追踪 ──
                // 弱追踪让"站着不动"也会被慢慢咬到, 但横向跑动(角速度约为追踪速度的6倍)轻松甩开
                default:
                    {
                        Projectile.Center = frozenWave;

                        Vector2 toPlayer = p.Center - frozenWave;
                        if (toPlayer.LengthSquared() > 1f)
                        {
                            float want = toPlayer.ToRotation();
                            float diff = MathHelper.WrapAngle(want - beamDir.ToRotation());
                            float step = MathHelper.Clamp(diff, -BeamTrackRate, BeamTrackRate);
                            beamDir = (beamDir.ToRotation() + step).ToRotationVector2();
                        }

                        if (phaseTimer >= FireFrames)
                            Projectile.Kill();
                    }
                    break;
            }
        }

        // 伤害判定: 只在发射段。单束激光从冻结的音波位置出发、朝锁定方向延伸,
        // 所以判定段 = [音波位置, 音波位置 + 方向 × 光束长], 音波背后不在判定内
        public override bool? Colliding(Rectangle projHitbox, Rectangle targetHitbox)
        {
            if (phase != 3) return false;   // 只有发射段(阶段3)有伤害

            Vector2 toTarget = targetHitbox.Center.ToVector2() - frozenWave;
            float along = Vector2.Dot(toTarget, beamDir);
            if (along < 0f || along > BeamLength) return false;

            float perp = Math.Abs(Vector2.Dot(toTarget, beamDir.RotatedBy(MathHelper.PiOver2)));
            return perp <= BeamDamageHalfWidth + 8f;   // +8: 玩家判定框的宽容度
        }

        // ── 绘制 ──
        public override bool PreDraw(ref Color lightColor)
        {
            Vector2 wavePos = Projectile.Center - Main.screenPosition;

            // 0 接近/环绕: 只画本体 + 拖尾粒子
            if (phase == 0)
            {
                if (Main.rand.NextBool(2))
                {
                    Dust d = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.Silver);
                    d.noGravity = true;
                    d.velocity *= 0.25f;
                }
                DrawWave(wavePos, lightColor);
                return false;
            }

            // 月球领主的青白色调
            Color outer = new Color(60, 160, 200);
            Color mid = new Color(130, 220, 255);
            Color core = new Color(225, 250, 255);

            float aimP = (phase == 1) ? MathHelper.Clamp(phaseTimer / AimFrames, 0f, 1f) : 1f;
            float lockP = (phase == 2) ? MathHelper.Clamp(phaseTimer / LockFrames, 0f, 1f) : 0f;
            float pulse = 0.85f + 0.15f * (float)Math.Sin(Projectile.ai[1] * 0.14f);
            float energy = Math.Max(aimP * pulse, (phase >= 2) ? 1f : 0f);

            // 1 预瞄: 细光束实时跟着玩家扫(从很细长到 0.7 倍粗细) + 本体
            if (phase == 1)
            {
                float ramp = MathHelper.Clamp(phaseTimer / AimFrames, 0f, 1f);
                float warnW = MathHelper.Lerp(5f, BeamThickness * 0.7f, ramp);
                DrawFlowingBeam(wavePos, beamDir, BeamLength, warnW,
                    mid * (0.35f + 0.40f * ramp), Main.GlobalTimeWrappedHourly, 2.6f, 0.012f);
                DrawWave(wavePos, lightColor);
                return false;
            }

            // 2 锁线蓄能: 方向已定死(线不再跟人), 光束从 0.7 倍长满到 1 倍粗细、白芯渐亮
            // 3 发射: 沿锁定方向点火
            Vector2 start = (phase == 2)
                ? wavePos + Main.rand.NextVector2Circular(1.5f * lockP, 1.5f * lockP)
                : wavePos;

            float len = BeamLength;
            if (phase == 3)
            {
                float extend = MathHelper.Clamp(phaseTimer / BeamExtendFrames, 0f, 1f);
                len = BeamLength * (0.10f + 0.90f * extend);
            }

            // ── 让光束"活"起来的一组动态项 ──
            float time = Main.GlobalTimeWrappedHourly;
            float throb = 1f + 0.10f * (float)Math.Sin(Projectile.ai[1] * 0.30f);   // 粗细脉动
            float flicker = 0.90f + 0.10f * (float)Math.Sin(Projectile.ai[1] * 1.90f); // 高频亮度抖动
            // 锁线段宽度从预瞄末端的 0.7 倍长到满粗 —— 读起来是能量充满的过程
            float wMult = (phase == 2) ? MathHelper.Lerp(0.7f, 1f, lockP) : 1f;
            float w = BeamThickness * throb * wMult;
            energy *= flicker;

            // 分层收窄: 外层柔光 1.8 倍、中层 1.0 倍、核心条带 0.6 倍。
            // 外层与中层用"沿线流动"的画法(亮度带从音波流向远处), 这是动态感的主体
            DrawFlowingBeam(start, beamDir, len, w * 1.8f, outer * (0.30f * energy), time, 2.2f, 0.010f);
            DrawFlowingBeam(start, beamDir, len, w * 1.0f, mid * (0.45f * energy), time, 3.2f, 0.014f);
            DrawBorrowedCore(start, beamDir, len, w * 0.6f, core * (0.85f * energy));

            // 沿光束跑的波纹: 分段绘制, 每段垂直偏移随"沿程 + 时间"正弦变化
            DrawRipple(start, beamDir, len, 2.6f, core * (0.7f * energy), time * 7f);

            // 束内粒子: 每帧沿光束撒几粒, 向外飘散
            SpawnBeamDust(len);

            // 锁线段: 长出一道白芯(蓄满提示); 发射段: 满亮白芯
            if (phase == 2)
                DrawOneWayBeam(start, beamDir, len, 3f + 4f * lockP, Color.White * lockP * 0.85f);
            else
                DrawOneWayBeam(start, beamDir, len, 5f, Color.White * 0.35f);

            DrawMuzzleFlash(wavePos, energy, phase == 3 ? phaseTimer : 0f);
            return false;    // 发射段本体已经"变成"光束, 不再画声波环
        }

        // 借来的月球领主死亡射线贴图: 作为光束的核心条带。
        // 自适应两种可能: 横向长条 → 沿光束拉伸; 不是长条(方形光斑) → 只当炮口闪光, 不拉伸
        private void DrawBorrowedCore(Vector2 start, Vector2 dir, float length, float thickness, Color color)
        {
            Texture2D tex = GetBeamTex();
            if (tex == null) return;

            if (tex.Width < tex.Height)
            {
                // 不是长条: 拉到光束粗细画在炮口, 当作能量核心的起点光斑
                float s = thickness / tex.Height;
                Main.spriteBatch.Draw(tex, start +
                    new Vector2(0f, 0f), null, color, dir.ToRotation(),
                    new Vector2(0f, tex.Height * 0.5f),
                    new Vector2(s, s), SpriteEffects.None, 0f);
                return;
            }

            // 横向长条: 把贴图整幅映射到 长度 × 粗细 上
            Main.spriteBatch.Draw(
                tex,
                start,
                null,
                color,
                dir.ToRotation(),
                new Vector2(0f, tex.Height * 0.5f),
                new Vector2(length / tex.Width, thickness / tex.Height),
                SpriteEffects.None,
                0f);
        }

        // 音波本体: 声波环贴图, 轻微脉动
        private void DrawWave(Vector2 screenPos, Color lightColor)
        {
            Texture2D tex = ModContent.Request<Texture2D>(Texture).Value;
            float scale = 1f + 0.12f * (float)Math.Sin(Projectile.ai[1] * 0.15f);
            Main.spriteBatch.Draw(
                tex,
                screenPos,
                null,
                Color.Lerp(lightColor, Color.White, 0.4f),
                Projectile.rotation + (float)Math.Sin(Projectile.ai[1] * 0.05f) * 0.2f,
                tex.Size() / 2f,
                scale,
                SpriteEffects.None,
                0f);
        }

        // 炮口闪光: 发射那一刻最亮, 之后衰减
        private void DrawMuzzleFlash(Vector2 center, float energy, float since)
        {
            float flash = (since > 0f) ? MathHelper.Clamp(1f - since / (FireFrames * 0.6f), 0f, 1f) : 0f;
            float size = 40f + 48f * flash;
            float a = 0.25f * energy + 0.65f * flash;
            if (a <= 0.01f) return;

            // 星芒随时间缓慢旋转 + 长度呼吸, 不再是一个固定的十字
            float spin = Main.GlobalTimeWrappedHourly * 0.9f;
            float breathe = 1f + 0.12f * (float)Math.Sin(Main.GlobalTimeWrappedHourly * 5.5f);
            for (int i = 0; i < 4; i++)
            {
                Vector2 d = (MathHelper.PiOver4 * i + spin).ToRotationVector2();
                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    center,
                    new Rectangle(0, 0, 1, 1),
                    new Color(200, 245, 255) * a,
                    d.ToRotation(),
                    new Vector2(0.5f, 0.5f),
                    new Vector2(size * breathe, 4f + 5f * flash),
                    SpriteEffects.None,
                    0f);
            }

            // 交叉的细芒(和主芒错开45度), 让中心更像一颗星
            for (int i = 0; i < 4; i++)
            {
                Vector2 d = (MathHelper.PiOver4 * i + spin + MathHelper.PiOver4 * 0.5f).ToRotationVector2();
                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    center,
                    new Rectangle(0, 0, 1, 1),
                    new Color(255, 255, 255) * (a * 0.5f),
                    d.ToRotation(),
                    new Vector2(0.5f, 0.5f),
                    new Vector2(size * 0.62f * breathe, 2.5f),
                    SpriteEffects.None,
                    0f);
            }
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

        // 流动光束: 把线拆成 N 段, 每段亮度按"沿程位置 + 时间"的正弦滚动 ——
        // 亮带沿线跑动(从音波流向远处), 这是能量感的主体。
        // (灾厄的流光预警线也是这个手法: increment = 沿程比 + 时间×速度)
        private static void DrawFlowingBeam(Vector2 start, Vector2 dir, float length, float width, Color color,
            float time, float flowSpeed, float freq)
        {
            const int Segs = 40;                          // 分段够密, 亮带才连续
            float segLen = length / Segs;
            Vector2 step = dir * segLen;
            float rot = dir.ToRotation();
            for (int i = 0; i < Segs; i++)
            {
                float along = (i + 0.5f) * segLen;
                float wave = 0.55f + 0.45f * (float)Math.Sin(time * flowSpeed - along * freq);
                Color c = color * wave;
                if (c.R + c.G + c.B < 6) continue;        // 全暗的段不画
                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    start + step * i,
                    new Rectangle(0, 0, 1, 1),
                    c,
                    rot,
                    new Vector2(0f, 0.5f),
                    new Vector2(segLen + 2f, width),      // +2: 段间无缝
                    SpriteEffects.None,
                    0f);
            }
        }

        // 沿光束跑的波纹: 分段绘制, 每段垂直偏移 = sin(沿程 - 时间×速度)。
        // 读起来像一道能量脉冲顺着光束窜出去(比亮度滚动更有"动"的感觉)
        private static void DrawRipple(Vector2 start, Vector2 dir, float length, float width, Color color, float phase)
        {
            const int Segs = 26;
            float segLen = length / Segs;
            Vector2 perp = dir.RotatedBy(MathHelper.PiOver2);
            float rot = dir.ToRotation();
            for (int i = 0; i < Segs; i++)
            {
                float along = (i + 0.5f) * segLen;
                float off = (float)Math.Sin(along * 0.011f - phase) * 5.5f;
                Main.spriteBatch.Draw(
                    TextureAssets.MagicPixel.Value,
                    start + dir * (segLen * i) + perp * off,
                    new Rectangle(0, 0, 1, 1),
                    color,
                    rot,
                    new Vector2(0f, 0.5f),
                    new Vector2(segLen + 2f, width),
                    SpriteEffects.None,
                    0f);
            }
        }

        // 束内粒子: 每帧沿光束随机取点撒几粒, 带一点垂直漂移 —— 让光束周围有"烧灼"的碎屑感
        private void SpawnBeamDust(float len)
        {
            if (Main.netMode == NetmodeID.Server) return;      // 纯观感, 服务器不用跑
            Vector2 perp = beamDir.RotatedBy(MathHelper.PiOver2);
            int n = 2;
            for (int i = 0; i < n; i++)
            {
                float along = Main.rand.NextFloat() * len;
                Vector2 pos = Projectile.Center + beamDir * along
                            + perp * Main.rand.NextFloat(-BeamThickness * 0.9f, BeamThickness * 0.9f);
                Dust d = Dust.NewDustDirect(pos, 2, 2, DustID.Silver);
                d.noGravity = true;
                d.velocity = perp * Main.rand.NextFloat(-1.4f, 1.4f)
                           + beamDir * Main.rand.NextFloat(0.5f, 2.8f);
                d.scale = Main.rand.NextFloat(0.7f, 1.3f);
            }
        }
    }
}
