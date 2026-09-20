using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using TheCatMaodie.Projectiles;

namespace TheCatMaodie.NPCs
{
    // 一阶段boss:地面系大狗。
    // 这个类同时是"小怪"的基类——BigDogMinion 继承它,只覆盖下面那组可调参数,
    // 所以boss和小怪永远共用同一套行为代码,不会各自演化出两套。
    //
    // 设计核心:跑动是常态,跳跃/冲刺/喷吐是"主动作",由跑动状态按距离和冷却挑选。
    //
    // 状态机(ai[0]):
    //   0 = 跑动追击(默认):朝玩家跑;跑够一段时间后挑一个主动作(近身偏小跳/激光,
    //       中距离偏冲刺);玩家拉开半个屏幕以上或站上高台时,立刻改用冲刺或超级大跳扑上去,
    //       不受冷却限制——大跳落点在玩家身后一点,专治放风筝
    //   1 = 跳跃:先蹲身蓄力(预告),再起跳。玩家远 → 长距离大跳扑过去;
    //       玩家近 → 短促下压跳(压杀),滞空下坠时额外加速砸下来;
    //       飞扑(ai[3]=3)= 冲刺的第二段,蓄力更长,按抛射弧线朝玩家扑过去、自然落下
    //   2 = 冲刺第一段:站定蓄力约0.33秒 → 水平高速地面冲撞(17~20,克眼二阶段量级);
    //       路上撞到矮台阶会跳过去(最多3次)。冲完接一次飞扑,两段合成一次进攻
    //   3 = 喷吐:先停顿1秒(嘴部聚气预告),再连发4发小弧弹幕(大狗武器0号弧的敌对版),吐完回跑动
    //   4 = 站定休息:原地歇1秒(占10%的出手份额)。属于"假动作"——让人以为要出招,结果什么都不做;
    //       因为判定远距离那段在它之前,所以只会发生在近中距离
    //   5 = 扇形掷刃:站定蓄力约0.8秒(嘴部银色聚气预告),然后一次性把5把刀以自己为中心、
    //       朝玩家方向张开120度的扇形甩出去;刀直线飞行、不追踪、碰到方块就消失
    //   6 = 入场:刚被召唤时站着不动3秒(期间无敌),说完开场白再开始行动
    //   另外:血量掉到80% / 50% / 30%时各召唤一波小怪(只有boss做,玩家屏幕两侧共3只),
    //        召唤不占状态位,在任何状态里都会触发
    //
    // 便签分工:
    //   ai[0]=状态   ai[1]=状态内计时器   ai[2]=主动作冷却(跑动时必须为0才允许发起新动作)
    //   ai[3]=跳跃类型(状态1: 0=远跳追人 1=压杀跳 2=脱困跳) / 已喷吐数量(状态3)
    //          冲刺中(状态2)=已翻越台阶的次数
    //   localAI[0]=下穿平台剩余帧数  localAI[1]=上一帧的X坐标(卡住检测用)  localAI[2]=卡住计数
    //   localAI[3]=已召唤小怪的波数(0~3,每档只触发一次)
    [AutoloadBossHead]   // 引擎会自动把 NPCs/BigDogBoss_Head_Boss.png 当成血条/地图上的头像
    public class BigDogBoss : ModNPC
    {
        // 贴图:不写 Texture 就是默认路径 NPCs/BigDogBoss.png(现在是我们自己的美术:
        // 帧0=站立,帧1~8=跑步循环,由 FindFrame 驱动)

        // ── 可调参数 ──
        // 小怪(BigDogMinion)继承这个类,只覆盖下面这些值来削弱自己。
        // 想单独调boss就改这里的默认值;想调小怪就改 BigDogMinion.cs 里的覆盖值。
        protected virtual float RunSpeedMax => 4.6f;          // 跑动限速(追人的地面速度上限)
        protected virtual float JumpPower => 1f;              // 跳跃初速倍率(跳跃高度 ∝ 初速²)
        protected virtual float JumpSpeedCap => 14f;          // 大跳的水平初速上限
        protected virtual float AirFramesPerPower => 73.3f;   // 初速为1时的滞空帧数(滞空时间 ∝ 初速)
        protected virtual float DashSpeedBase => 17f;         // 冲刺速度基础值(克眼二阶段冲刺量级)
        protected virtual float DashSpeedRage => 3f;          // 冲刺速度随血量增加的部分
        protected virtual int SpitDamage => 14;               // 喷吐弹幕的单发伤害
        protected virtual float SpitSpeed => 11f;             // 喷吐弹幕的飞行速度(7→9→11,越来越难躲)
        protected virtual float SpitHoming => 0.025f;         // 喷吐弹幕的追踪强度(0=不追踪;越大拐弯越急)
        protected virtual float SpitGrowMax => 1.3f;          // 音波弹幕的最大放大倍率(0=不变大)
        protected virtual int SpitProjectile => ModContent.ProjectileType<BigDogSpit>();   // 喷吐的弹幕(小弧,敌对版)
        // 扇形掷刃(状态5)
        protected virtual int BladeProjectile => ModContent.ProjectileType<BigDogBlade>(); // 刀刃弹幕
        protected virtual int BladeCount => 9;                // 一次甩几把刀(120度里铺9把 ≈ 每15度一把)
        protected virtual float BladeSpreadDeg => 120f;       // 扇形张角(度):以"朝向玩家"为中心,左右各分一半
        protected virtual float BladeSpeed => 18f;            // 刀刃飞行速度(原来9太慢,直接翻倍)
        protected virtual int BladeDamage => 12;              // 单把刀刃的伤害
        protected virtual float BladeWindup => 50f;           // 掷刃前的蓄力帧数(预警线出现的时长)
        protected virtual float BladeLeadIn => 30f;           // 预警线出现之前的前摇(30帧=0.5秒):音效在这段时间先响
        protected virtual bool ShowsBladeWarning => true;     // 掷刃蓄力时画预警线(小怪关掉,免得满屏都是线)
        protected virtual bool DashAppliesDebuff => true;     // 冲刺撞到玩家时给"野性"(小怪关掉)
        protected virtual float BigJumpGap => 60f;            // 大跳落点与玩家的间距:正=越到身后,负=差一段(留在身前)
        protected virtual float PounceSpeed => 16f;           // 飞扑(冲刺第二段)的初速:固定值,不随距离变(力度恒定)
        protected virtual float PounceLift => 7f;             // 飞扑额外的上抬初速:决定弧线高度和滞空(7≈0.78秒/82像素高)
        protected virtual float StillRangedResist => 0.7f;    // 站定不动时受远程伤害的倍率(0.7 = 减30%;1 = 不启用)
        protected virtual float StillSpeed => 0.5f;           // 横向速度低于这个值就算"站定不动"
        protected virtual int FrameSourceNPC => -1;   // 贴图来源:-1=用我们自己的整套帧;>=0=借那张原版图,只画它的第1帧
        // 主动作配比(20份制):近身小跳 / 近身冲刺 / 中距冲刺,剩下的都归激光
        protected virtual int WeightPounceJump => 6;          // 近身:小跳(压杀跳)
        protected virtual int WeightNearDash => 2;            // 近身:冲刺
        protected virtual int WeightMidDash => 7;             // 中距:冲刺
        protected virtual int WeightStandStill => 2;          // 站定休息:占 2/20 = 10%(想改5%就填1,15%填3)
        protected virtual int WeightBladeFan => 3;            // 扇形掷刃:常态占 3/20 = 15%
        protected virtual int WeightBladeFanRage => 6;        // 残血(默认50%以下)时提高到 6/20 = 30%
        protected virtual float BladeFanRageLife => 0.5f;     // 血量低于这个比例就算"残血"
        protected virtual float RestFrames => 60f;            // 站定休息持续帧数(60 = 1秒)
        // 召唤小怪(默认是boss的行为;小怪自己把这个覆盖成 false,免得小怪也召唤小怪)
        protected virtual bool SummonsMinions => true;
        protected virtual int MinionType => ModContent.NPCType<BigDogMinion>();

        private Asset<Texture2D> skin;   // 贴图缓存(第一次用到时才加载)

        private Asset<Texture2D> GetSkin()
        {
            if (skin == null)
                skin = ModContent.Request<Texture2D>(Texture, AssetRequestMode.ImmediateLoad);
            return skin;
        }

        public override void SetStaticDefaults()
        {
            // 显示名"大狗"配置在 Localization/en-US_Mods.TheCatMaodie.hjson 里
            Main.npcFrameCount[Type] = 11; // 帧0=站立,1~8=跑步循环,9=吐音波,10=掷刃(见 FindFrame)

            // 冲刺残影用引擎自带的"过去位置"缓存:TrailingMode=1 = 每帧记一次位置(不记旋转),
            // TrailCacheLength = 保留多少帧。然后在 PreDraw 里把这些位置各画一份。
            // 注意:TrailingMode 千万别设回 0——模式0下原版会拿 localAI[3] 当残影计时器,
            // 而 localAI[3] 我们用来记"已召唤小怪的波数",会被原版每帧改掉(召唤会重复触发)
            NPCID.Sets.TrailingMode[Type] = 1;
            NPCID.Sets.TrailCacheLength[Type] = 10;
        }

        public override void SetDefaults()
        {
            NPC.width = 98;
            NPC.height = 84;
            NPC.damage = 22;              // 接触伤害
            NPC.defense = 10;
            NPC.lifeMax = 5000;           // 肉山前最终boss的定位(留出二阶段空间,原来7000)
            NPC.knockBackResist = 0f;     // boss 不吃击退
            NPC.aiStyle = -1;             // 不用原版剧本,AI 全自己写
            NPC.boss = true;              // 显示 boss 血条
            NPC.HitSound = SoundID.NPCHit1;
            NPC.DeathSound = SoundID.NPCDeath1;
        }

        // 冲刺/飞扑撞到玩家 → 给"野性"。DashingNow() 同时覆盖冲刺第一段和飞扑第二段,
        // 所以"被冲刺命中"两种情况都算;平时走路撞到不给
        public override void OnHitPlayer(Player target, Player.HurtInfo hurtInfo)
        {
            if (!DashAppliesDebuff) return;   // 小怪的冲刺不给(见 BigDogMinion 的覆盖)
            if (!DashingNow()) return;        // 不是冲刺中撞到的

            int wild = ModContent.BuffType<Buffs.Wildness>();
            if (target.HasBuff(wild)) return;   // 已经有了就不刷新(否则整场都在野性状态)
            target.AddBuff(wild, 40 * 60);      // 40秒
        }

        // 站定不动时吃远程伤害减半:防止它站着喷激光的那两秒被远程武器集火打空血。
        // "站定"直接看横向速度,不需要额外记状态——冲刺/跳跃时速度远大于阈值,自然不会吃到减伤
        public override void ModifyIncomingHit(ref NPC.HitModifiers modifiers)
        {
            if (StillRangedResist >= 1f) return;                                   // 参数设成1就是不启用
            if (!IsStandingStill()) return;                                        // 正在移动
            if (!modifiers.DamageType.CountsAsClass(DamageClass.Ranged)) return;   // 只管远程伤害
            modifiers.FinalDamage *= StillRangedResist;
        }

        // 现在算不算"站定"。
        // 速度判定能覆盖绝大多数情况(蓄力站桩时速度会衰减到接近0),但起手那几帧它还在减速,
        // 所以把三个"整段都站着"的状态直接列出来,让减伤从起手第一帧就生效
        private bool IsStandingStill()
        {
            if (Math.Abs(NPC.velocity.X) <= StillSpeed) return true;
            return NPC.ai[0] == 3f    // 喷吐(声波)
                || NPC.ai[0] == 4f    // 站定休息
                || NPC.ai[0] == 5f;   // 扇形掷刃
        }

        public override void AI()
        {
            NPC.TargetClosest();                       // 锁定最近玩家(顺带让它面朝玩家)

            // 先挡掉非法目标索引,再去取 Main.player[target]——顺序反了就是数组越界崩溃
            if (NPC.target < 0 || NPC.target >= Main.maxPlayers)
            {
                Disengage();
                return;
            }

            Player player = Main.player[NPC.target];

            // 目标玩家死了 / 不在了 → 脱战:停手、起烟,过一小会儿消失。
            // 原版boss都是这个套路(克眼会直接飞走)。没有这一段的话它会一直追着尸体打,
            // 而且因为"场上已经有一只大狗",玩家重新吹哨召唤还会被拦下
            if (!player.active || player.dead)
            {
                Disengage();
                return;
            }

            float aggression = 1f - NPC.life / (float)NPC.lifeMax;   // 血越少越凶(0~1)
            float distX = Math.Abs(player.Center.X - NPC.Center.X);  // 水平距离
            float above = NPC.Center.Y - player.Center.Y;            // >0 说明玩家在boss上方
            float dir = Math.Sign(player.Center.X - NPC.Center.X);
            if (dir == 0f) dir = 1f;

            if (NPC.ai[2] > 0f) NPC.ai[2]--;   // 主动作冷却递减

            // 正常情况始终开启地形碰撞;只有下穿平台那几帧临时打开 noTileCollide,
            // 每帧先重置一次,避免任何异常路径下卡在"穿墙"状态
            NPC.noTileCollide = false;

            // 台词冷却用的单调时钟(每帧+1)
            lifeTicks++;

            // 入场:每只boss第一次运行时先站着不动3秒(180帧,见 IntroFrames;期间无敌),并说开场白。
            // 用实例字段记"已入场"——新召唤的boss是新实例,会自动重新走一遍
            if (NPC.boss && !introStarted)
            {
                introStarted = true;
                NPC.ai[0] = 6f;      // 入场状态
                NPC.ai[1] = 0f;
                SayLine("Intro");
                SoundEngine.PlaySound(SfxIntro, NPC.Center);
            }

            // 入场期间无敌(每帧按状态重置,离开入场自动恢复)
            NPC.dontTakeDamage = NPC.ai[0] == 6f;

            // 血量台词(各说一次)
            if (NPC.boss)
            {
                if (!saidAt30 && NPC.life <= NPC.lifeMax * 0.3f)
                {
                    saidAt30 = true;
                    SayLine("At30");
                }
                if (!saidAt5 && NPC.life <= NPC.lifeMax * 0.05f)
                {
                    saidAt5 = true;
                    at5SecondDelay = 0;
                    SayLine("At5a");
                }
                // 第二句隔1.5秒再接,读起来像一口气说完,而不是两行同时刷出来
                if (saidAt5 && !saidAt5Second && ++at5SecondDelay >= 90)
                {
                    saidAt5Second = true;
                    SayLine("At5b");
                }
            }

            // 召唤小怪:血量掉到 80% / 50% / 30% 时各来一波(不占状态位,哪个状态里都会触发)
            if (SummonsMinions)
                CheckSummonWaves(player);

            switch ((int)NPC.ai[0])
            {
                // ── 状态0:跑动追击(常态) ──
                case 0:
                {
                    // 下穿平台进行中:快速往下穿,穿过去立刻恢复碰撞
                    // (原来照抄玩家按S下平台那套:每帧只沉2像素,慢得像卡了一下——
                    //  玩家才需要那种慢动作做提示,boss应该像史莱姆王那样直接穿过去)
                    if (NPC.localAI[0] > 0f)
                    {
                        NPC.localAI[0]--;
                        NPC.noTileCollide = true;
                        NPC.velocity.Y = Math.Max(NPC.velocity.Y, 7f);   // 快速下落
                        NPC.velocity.X *= 0.95f;
                        // 脚下已经不是平台了 = 已经穿过去了 → 立刻收手,免得继续往下穿地板
                        if (!StandingOnPlatform()) NPC.localAI[0] = 0f;
                        break;
                    }

                    NPC.ai[1]++;
                    bool grounded = NPC.collideY;

                    // 玩家在下方(自己站在更高的平台上):
                    //   脚下是平台 → 直接下穿,一步到位(不用绕路找边缘)
                    //   脚下是实心地面 → 朝最近的边缘走过去掉下来(石头没法穿,这是唯一的自然下法)
                    if (grounded && above < -64f)
                    {
                        if (StandingOnPlatform())
                        {
                            NPC.localAI[0] = 14f;   // 14帧足够穿下一层平台
                            NPC.noTileCollide = true;
                            NPC.velocity.Y = 2f;
                            break;
                        }

                        float leftDist = LedgeDistance(-1f);
                        float rightDist = LedgeDistance(1f);
                        float edgeDir = (leftDist <= rightDist) ? -1f : 1f;
                        // 朝玩家那一侧如果很快就是边缘,就直接朝玩家跑,顺路掉下去
                        if ((dir < 0f ? leftDist : rightDist) <= 96f) edgeDir = dir;

                        NPC.velocity.X += edgeDir * 0.22f;
                        NPC.velocity.X = Math.Clamp(NPC.velocity.X, -RunSpeedMax, RunSpeedMax);
                        break;
                    }

                    NPC.velocity.X += dir * 0.22f;
                    NPC.velocity.X = Math.Clamp(NPC.velocity.X, -RunSpeedMax, RunSpeedMax);

                    // 卡住检测:想朝玩家走,但位置几乎没动(被墙/坑沿挡住) → 起跳脱困
                    // (原版僵尸被方块挡住时也是跳一下翻过去,不然它会贴着墙一直发呆)
                    if (Math.Abs(NPC.Center.X - NPC.localAI[1]) < 0.5f && distX > 48f)
                        NPC.localAI[2]++;
                    else
                        NPC.localAI[2] = 0f;
                    NPC.localAI[1] = NPC.Center.X;

                    if (NPC.localAI[2] >= 14f)
                    {
                        NPC.localAI[2] = 0f;
                        NPC.ai[0] = 1f;
                        NPC.ai[1] = 0f;
                        NPC.ai[3] = 2f;              // 脱困跳(固定跳得够高)
                        break;
                    }

                    // "远"按当前分辨率和缩放动态算:Main.ViewSize 是游戏自己给出的"可见世界宽度"
                    // (Main.screenWidth 是不含缩放的渲染宽度,直接拿它当屏幕宽会算错)。
                    // 本机是 1920宽 + 116%缩放 → 1655,半个屏幕约 828 像素
                    float viewW = Main.ViewSize.X;
                    if (float.IsNaN(viewW) || viewW < 400f || viewW > 20000f) viewW = 1600f;   // 兜底:世界还没加载时

                    // "拉开就扑"的触发距离 = 半个屏幕,但不能超过"一次大跳真正够得到的距离",
                    // 否则触发出来的跳只会摔在半路。射程 ≈ 水平初速上限 × 滞空帧数,两者都随
                    // JumpPower 变,所以射程 ∝ JumpPower²(小怪跳一半高,射程只剩1/4)
                    float jumpReach = JumpSpeedCap * AirFramesPerPower * JumpPower * JumpPower;
                    float farX = MathHelper.Clamp(viewW * 0.5f, 520f, 1000f);
                    farX = Math.Min(farX, jumpReach - BigJumpGap);

                    bool playerFar = distX > farX;     // 拉开半个屏幕以上(且一次大跳够得着)
                    bool playerHigh = above > 120f;    // 站在高台上

                    // 玩家拉开半个屏幕、或站上高台 → 立刻用"拉近距离"的动作去追(不吃冷却)。
                    // 这一段专门治放风筝:光靠跑永远追不上带靴子的玩家(它跑动限速4.6),
                    // 必须靠跳跃/冲刺这种大位移手段才追得回来
                    if (grounded && (playerFar || playerHigh))
                    {
                        NPC.ai[1] = 0f;

                        if (playerFar && Main.rand.NextBool(2))
                        {
                            // 远距离的一半概率:直接水平冲刺扑过去,比跳更快贴脸,也不怕地形起伏
                            NPC.ai[0] = 2f;
                            NPC.ai[3] = 0f;
                        }
                        else
                        {
                            // 其余:超级大跳,落点瞄在玩家身后一点(远/高 → 大跳;否则短促压杀跳)
                            NPC.ai[0] = 1f;
                            NPC.ai[3] = (playerFar || above > 220f) ? 0f : 1f;
                        }
                        break;
                    }

                    // 跑够一段时间 + 冷却好了 → 挑一个主动作(20份,方便细配比例)
                    float runTime = 96f - 46f * aggression;   // 血越少,出手越频繁
                    if (grounded && NPC.ai[1] >= runTime && NPC.ai[2] <= 0f)
                    {
                        NPC.ai[1] = 0f;
                        int roll = Main.rand.Next(20);

                        // 方案A:抽签最前面单独留两档(站定休息 / 扇形掷刃),
                        // 剩下的份额仍按原来的相对比例分配,所以已有的手感基本不变。
                        // 远距离追扑那段在上面就 break 了,所以它们只会在近中距离发生
                        if (roll < WeightStandStill)
                        {
                            NPC.ai[0] = 4f;              // 站定休息
                            NPC.ai[3] = 0f;
                        }
                        else if (roll < WeightStandStill + CurrentBladeFanWeight)
                        {
                            NPC.ai[0] = 5f;              // 扇形掷刃
                            NPC.ai[3] = 0f;
                        }
                        else if (distX < 300f)
                        {
                            // 近身(默认配比):小跳30% / 冲刺10% / 激光60%
                            // 贴身不用大跳(大跳只在"拉开半屏"或"高台"时触发);
                            // 冲刺也压得很低——贴着玩家往一个方向冲,反而会把自己冲到玩家身后拉开距离,
                            // 所以近身主要靠小跳和激光压着打,只保留一点冲刺概率
                            if (roll < WeightPounceJump) { NPC.ai[0] = 1f; NPC.ai[3] = 1f; }                        // 压杀跳(小跳)
                            else if (roll < WeightPounceJump + WeightNearDash) { NPC.ai[0] = 2f; NPC.ai[3] = 0f; } // 蓄力冲刺
                            else { NPC.ai[0] = 3f; NPC.ai[3] = 0f; }                                                // 喷吐(激光)
                        }
                        else
                        {
                            // 中距离(300~半屏)(默认配比):冲刺35% / 激光65%
                            // 这个档位是唯一还能真正拉近距离的,所以冲刺给得比近身高
                            if (roll < WeightMidDash) { NPC.ai[0] = 2f; NPC.ai[3] = 0f; }   // 蓄力冲刺
                            else { NPC.ai[0] = 3f; NPC.ai[3] = 0f; }                        // 喷吐(激光)
                        }
                    }
                    break;
                }

                // ── 状态1:跳跃(远跳追人 / 近身压杀 / 飞扑) ──
                // ai[3] 决定用哪种:0=远跳追人  1=近身压杀  2=脱困  3=飞扑(冲刺第二段)
                case 1:
                {
                    NPC.ai[1]++;

                    // 飞扑是冲刺的第二段,给更长的蓄力做预告;普通跳跃沿用原来的8帧
                    bool pounceJump = NPC.ai[3] == 3f;
                    float jumpWindup = pounceJump ? 20f : 8f;

                    if (NPC.ai[1] <= jumpWindup)
                    {
                        // 蹲身蓄力(预告:给玩家反应时间,顺便刹车)
                        NPC.velocity.X *= 0.82f;
                        if (Main.rand.NextBool(2))
                            Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Grass);
                    }
                    else if (NPC.ai[1] == jumpWindup + 1f)
                    {
                        // 起跳:高度和水平距离都按玩家位置实时算
                        bool longJump = NPC.ai[3] == 0f;      // 远跳(追人)
                        bool escapeJump = NPC.ai[3] == 2f;    // 脱困跳(翻出坑/越过墙)
                        float reach = MathHelper.Clamp(above, 0f, 420f);   // 玩家比boss高多少

                        // 滞空帧数:起跳初速为v时约为 2v/重力(这个版本实测重力=0.3),所以它和初速成正比。
                        // 水平初速 = 要飞的距离 / 滞空帧数 —— 分母必须跟着 JumpPower 缩放,
                        // 否则小怪(跳得矮、滞空短)会算出一个偏大的初速,每次都冲过头
                        float airFrames = AirFramesPerPower * JumpPower;

                        if (escapeJump)
                        {
                            // 脱困:固定跳得够高,能越过坑沿或墙;水平朝玩家方向冲出去
                            NPC.velocity.Y = -13.5f * JumpPower;
                            NPC.velocity.X = dir * 4.5f;
                        }
                        else if (pounceJump)
                        {
                            // 飞扑(冲刺第二段):力度恒定——只瞄"方向",初速大小固定,完全不按距离缩放。
                            // 所以它每次扑出的距离是固定的:你离得近、它角度又偏了,就直接从你旁边冲过去;
                            // 离得远则扑不到,得再扑一次。这才是"扑"的手感
                            // (前两版都是按距离反推速度,近身就必然变成慢慢挪,方向上是错的)
                            Vector2 toTarget = player.Center - NPC.Center;
                            if (toTarget.LengthSquared() < 1f) toTarget = new Vector2(dir, -1f);   // 重合时的兜底方向
                            NPC.velocity = Vector2.Normalize(toTarget) * (PounceSpeed * JumpPower);
                            // 再额外抬一个固定量:轨迹就成了抛物线(之后下落全交给重力)
                            NPC.velocity.Y -= PounceLift * JumpPower;
                        }
                        else if (longJump)
                        {
                            // 超级大跳:水平初速按"要飞的距离 ÷ 滞空帧数"反推。
                            // BigJumpGap 是落点与玩家的间距:正值 = 越过这么多(落到玩家身后,堵住退路);
                            // 负值 = 差一段(落在玩家身前,小怪用它来避免贴脸)
                            float travel = Math.Max(distX + BigJumpGap, 80f);   // 至少往前飞80像素,免得原地起跳
                            float jumpCap = JumpSpeedCap * JumpPower;
                            NPC.velocity.Y = (-11f - reach * 0.011f) * JumpPower;
                            NPC.velocity.X = MathHelper.Clamp(dir * travel / airFrames, -jumpCap, jumpCap);
                        }
                        else
                        {
                            // 近身压杀跳:短促下压,贴着玩家砸下来
                            NPC.velocity.Y = (-11.5f - reach * 0.011f) * JumpPower;
                            NPC.velocity.X = MathHelper.Clamp(dir * distX / 50f, -8.5f * JumpPower, 8.5f * JumpPower);
                        }
                    }
                    else if (!NPC.collideY)
                    {
                        // 飞扑(ai[3]=3)不做横向修正:方向和力度在起跳那一刻就锁死了,
                        // 所以玩家靠走位能骗掉它、距离不对它就从旁边冲过去——这个"扑空"是刻意保留的。
                        // (其它跳跃仍然要修正:追人跳必须真的落到玩家附近才有意义)
                        if (NPC.ai[3] != 3f)
                        {
                            // 空中微调:朝玩家轻推,保证能落到平台上的玩家附近。
                            // 上限必须跟大跳的初速上限一致,否则大跳刚起跳就被这里削回去,照样飞不远
                            NPC.velocity.X += dir * 0.10f;
                            NPC.velocity.X = Math.Clamp(NPC.velocity.X, -JumpSpeedCap * JumpPower, JumpSpeedCap * JumpPower);
                        }

                        // 自适应跳高:上升途中如果还没到玩家所在高度,就继续补力。
                        // 这样不管实际重力是多少、玩家站多高的平台,都能跳得上去;到位后自动收力
                        // (飞扑也吃这一条,它才够得到站在高平台上的玩家)
                        float targetY = player.Center.Y - 40f;   // 想跳到玩家略下方的高度
                        if (NPC.velocity.Y < 0f && NPC.Center.Y > targetY + 40f)
                            NPC.velocity.Y -= 0.15f;

                        // 压杀跳:确认玩家在下方且自己正在下坠时,额外加速砸下去
                        // (飞扑不做这个加速:它要"自然落下",加速砸下去就不像抛射了)
                        if (NPC.ai[3] == 1f && NPC.velocity.Y > 0f && player.Center.Y > NPC.Center.Y)
                            NPC.velocity.Y += 0.28f;

                        // 飞扑(冲刺第二段)也要记"有没有被躲开"
                        if (NPC.ai[3] == 3f) CheckDashDodged();
                    }
                    else if (NPC.ai[1] > jumpWindup + 4f)
                    {
                        // 落地:扬一圈灰,回到跑动
                        for (int i = 0; i < 10; i++)
                            Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Grass);
                        // 飞扑落地 = 这一整套冲刺结束了,结算躲闪台词
                        if (NPC.ai[3] == 3f) FlushDodgeLine();
                        NPC.ai[0] = 0f;
                        NPC.ai[1] = 0f;
                        NPC.ai[2] = 40f - 15f * aggression;
                    }
                    break;
                }

                // ── 状态2:冲刺(撕咬)第一段:水平地面冲撞 ──
                // 这一段冲完不会回跑动,而是接一次"飞扑"(状态1的飞扑类型)——两段合成一次进攻。
                // 第二段没有单独的状态号:它本质是一次抛射,直接复用跳跃状态那边已经调好的
                // 弹道/空中微调/落地逻辑,不用再写一套(之前那版自己写直线飞行,看起来是平着飞过去)
                case 2:
                {
                    DashStep(aggression, dir);
                    break;
                }

                // ── 状态4:站定休息(歇一拍) ──
                // 作用是"假动作":玩家看它站住会以为要吐激光或起跳,结果它什么都不做,
                // 这个空拍能打断节奏;顺便让狗有个喘气的样子(用站立那一帧)。
                // 注意它也会吃到"站定不动时远程减伤50%"——因为那个判定就是看横向速度的
                case 4:
                {
                    NPC.velocity.X *= 0.85f;    // 站定
                    NPC.ai[1]++;
                    if (NPC.ai[1] >= RestFrames)
                    {
                        NPC.ai[0] = 0f;
                        NPC.ai[1] = 0f;
                        NPC.ai[2] = 60f - 20f * aggression;   // 歇完还要等一小会儿才出下一招
                    }
                    break;
                }

                // ── 状态6:入场(站着不动3秒,期间无敌) ──
                // 作用是给玩家一个"它来了"的仪式感,顺便把开场白说完;无敌避免被开场偷袭
                case 6:
                {
                    NPC.velocity.X *= 0.8f;   // 站定
                    NPC.ai[1]++;
                    if (NPC.ai[1] >= IntroFrames)
                    {
                        NPC.ai[0] = 0f;
                        NPC.ai[1] = 0f;
                        NPC.ai[2] = 30f;      // 入场结束先缓半秒再出手
                    }
                    break;
                }

                // ── 状态5:扇形掷刃 ──
                // 和喷吐一样先站定蓄力(银白色聚气做预告),蓄力结束那帧一次性把整个扇形甩出去:
                // 以自己为中心、以"朝向玩家"为中轴,在 BladeSpreadDeg(默认120度)内均匀铺开
                case 5:
                {
                    NPC.velocity.X *= 0.85f;    // 站定
                    NPC.ai[1]++;

                    // 起手:前摇开始的那一帧就放音效(比预警线早 0.5 秒),顺便说"切割..."
                    if (NPC.ai[1] == 1f && NPC.boss)
                    {
                        SoundEngine.PlaySound(SfxBlade, NPC.Center);
                        SayLine("BladeFan");
                    }

                    GetBladeFanOrigin(out Vector2 muzzle, out float aim);   // 和预警线共用同一份几何

                    // 前摇结束后才是"预警线阶段":嘴部聚银色光点(预警线在 PreDraw 里画)
                    float warnFrame = NPC.ai[1] - BladeLeadIn;   // 预警阶段内的帧数(前摇期间为负)
                    if (warnFrame > 0f && warnFrame < BladeWindup && warnFrame % 5f == 0f)
                        Dust.NewDustDirect(muzzle - new Vector2(8f), 16, 16, DustID.Silver);

                    // 预警阶段走完 → 整轮扇形一次放完
                    if (NPC.ai[1] == BladeLeadIn + BladeWindup)
                    {
                        if (Main.netMode != NetmodeID.MultiplayerClient)
                        {
                            float half = MathHelper.ToRadians(BladeSpreadDeg) * 0.5f;   // 120度 → 左右各60度

                            for (int i = 0; i < BladeCount; i++)
                            {
                                Vector2 vel = (aim + BladeAngleFactor(i) * half).ToRotationVector2() * BladeSpeed;
                                Projectile.NewProjectile(NPC.GetSource_FromAI(), muzzle, vel,
                                    BladeProjectile, BladeDamage, 1f, Main.myPlayer);
                            }
                        }
                    }

                    // 甩完停一小下再收招,回到跑动并进冷却
                    if (NPC.ai[1] >= BladeLeadIn + BladeWindup + 10f)
                    {
                        NPC.ai[0] = 0f;
                        NPC.ai[1] = 0f;
                        NPC.ai[2] = 60f - 20f * aggression;
                    }
                    break;
                }

                // ── 状态3:喷吐(先停顿1秒预告,频率已调低) ──
                default:
                {
                    NPC.velocity.X *= 0.9f;    // 站定输出
                    NPC.ai[1]++;

                    const float windup = 60f;   // 开火前的停顿(60帧=1秒)
                    float muzzleDist = NPC.width * 0.5f + 20f;   // 嘴到中心的距离:判定箱半宽再探出一点(boss=69,小怪=37)

                    // 停顿期间:站定 + 嘴部聚气(绿光粒子),让玩家看清它要吐了
                    if (NPC.ai[1] <= windup)
                    {
                        NPC.velocity.X *= 0.85f;
                        if (NPC.ai[1] % 6f == 0f)
                        {
                            Vector2 mouth = NPC.Center + new Vector2(NPC.direction * muzzleDist, -10f);
                            Dust.NewDustDirect(mouth - new Vector2(8f), 16, 16, DustID.GreenFairy);
                        }
                    }
                    // 停顿结束后开火:每14帧一发,共4发
                    else if ((NPC.ai[1] - windup) % 14f == 0f && NPC.ai[3] < 4f)
                    {
                        // 嘴的位置必须在判定箱之外,否则弹幕出生就撞在自己身上
                        Vector2 muzzle = NPC.Center + new Vector2(NPC.direction * muzzleDist, -10f);

                        // 第一发真正出口的这一帧响一声(前面那1秒蓄力不出声)。
                        // 声音各客户端自己播,所以放在联机判断外面。
                        // 这条小怪也要(按需求:小怪只用这一个音效,冲刺和掷刃不出声)
                        if (NPC.ai[3] == 0f) SoundEngine.PlaySound(SfxSpit, muzzle);

                        // 弹幕生成只在服务器/单机做,联机不能各客户端生成一份
                        if (Main.netMode != NetmodeID.MultiplayerClient)
                        {
                            Vector2 spit = Vector2.Normalize(player.Center - muzzle) * SpitSpeed;   // 朝玩家
                            spit = spit.RotatedByRandom(0.12);                                     // 少量散布

                            // ai[0]=追踪强度  ai[1]=追踪目标  ai[2]=最大放大倍率(0=不变大)
                            Projectile.NewProjectile(NPC.GetSource_FromAI(), muzzle, spit,
                                SpitProjectile, SpitDamage, 1f, Main.myPlayer, SpitHoming, NPC.target, SpitGrowMax);
                        }
                        NPC.ai[3]++;
                    }

                    // 吐完(最后一发在停顿后第42帧)+ 稍微喘口气 → 回跑动
                    if (NPC.ai[3] >= 4f && NPC.ai[1] >= windup + 42f + 20f)
                    {
                        NPC.ai[0] = 0f;
                        NPC.ai[1] = 0f;
                        NPC.ai[2] = 60f - 20f * aggression;
                    }
                    break;
                }
            }
        }

        // 头像(血条左边 + 地图图标)现在由类上面的 [AutoloadBossHead] 自动挂上
        // NPCs/BigDogBoss_Head_Boss.png,所以这里不再重写 BossHeadSlot——
        // 重写反而会把这个自动挂好的槽号覆盖掉(原版是先读 NPCID.Sets.BossHeadTextures,
        // 最后才调这个钩子,钩子里写什么就是什么)

        // 扇形掷刃当前的占比:残血时会提高(这个招式在低血量阶段更频繁)
        protected int CurrentBladeFanWeight =>
            (NPC.life < NPC.lifeMax * BladeFanRageLife) ? WeightBladeFanRage : WeightBladeFan;

        // ── 扇形掷刃的几何:嘴部位置 + 中轴角度(朝玩家) ──
        // 发射和预警线共用这一份,保证"线画在哪儿,刀就往哪儿飞"
        private void GetBladeFanOrigin(out Vector2 muzzle, out float aim)
        {
            Player player = Main.player[NPC.target];
            muzzle = NPC.Center + new Vector2(NPC.direction * (NPC.width * 0.5f + 20f), -10f);
            aim = (player.Center - muzzle).ToRotation();
        }

        // 第 i 把刃相对中轴的偏角倍率:从 -1(最朝上)均匀走到 +1(最朝下)
        private float BladeAngleFactor(int i)
        {
            return (BladeCount <= 1) ? 0f : (i / (float)(BladeCount - 1) * 2f - 1f);
        }

        // 扇形掷刃的预警线:蓄力期间一条条亮起来,顺序是"从下到上"。
        // 屏幕坐标 y 向下,所以角度大的那条更朝下 → 从最后一个索引往回亮
        private void DrawBladeWarningLines()
        {
            if (!ShowsBladeWarning) return;
            if (NPC.ai[0] != 5f) return;

            // 前摇期间不画线:音效已经先响了,线要等前摇走完才出现(比音效晚 BladeLeadIn 帧)
            float warnFrame = NPC.ai[1] - BladeLeadIn;
            if (warnFrame <= 0f || warnFrame > BladeWindup) return;
            if (BladeCount <= 0) return;

            GetBladeFanOrigin(out Vector2 muzzle, out float aim);
            float half = MathHelper.ToRadians(BladeSpreadDeg) * 0.5f;

            float progress = warnFrame / BladeWindup;   // 0 → 1 的预警进度

            // 蓄力进度 → 该亮几条(至少1条,免得第一帧什么都没有)
            int revealed = (int)(progress * BladeCount) + 1;
            if (revealed > BladeCount) revealed = BladeCount;

            // 线拉长到覆盖大半个屏幕(按可见世界宽度算,换分辨率/缩放都会跟着变)
            float lineLen = Math.Max(700f, Main.ViewSize.X * 0.55f);

            // 闪烁:越接近发射闪得越快(读起来是倒计时),但整体调慢一档,
            // 10弧度/秒 ≈ 1.6Hz,太快会变成刺眼的频闪
            float blinkRate = 6f + 8f * progress;
            float blink = 0.45f + 0.55f * (float)Math.Sin(Main.GlobalTimeWrappedHourly * blinkRate);
            Color lineColor = new Color(170, 225, 255) * (0.85f * blink);
            Vector2 start = muzzle - Main.screenPosition;

            for (int i = BladeCount - 1; i >= BladeCount - revealed; i--)
            {
                Vector2 dirVec = (aim + BladeAngleFactor(i) * half).ToRotationVector2();
                DrawLine(start, start + dirVec * lineLen, lineColor, 1.6f);
            }
        }

        // 用 1x1 的白点贴图拉一条线:旋转到线的方向,x 方向缩放进长度
        private static void DrawLine(Vector2 start, Vector2 end, Color color, float width)
        {
            Vector2 edge = end - start;
            float len = edge.Length();
            if (len < 1f) return;
            Main.spriteBatch.Draw(
                TextureAssets.MagicPixel.Value,
                start,
                new Rectangle(0, 0, 1, 1),
                color,
                edge.ToRotation(),
                new Vector2(0f, 0.5f),          // 以左端中点为轴心
                new Vector2(len, width),
                SpriteEffects.None,
                0f
            );
        }

        // ── 台词 / 入场 ──
        // 这些是"每只boss一份"的状态。用普通 C# 字段就行,不用去抢 ai[]/localAI[] 那 8 个
        // 已经全占满的槽位——引擎给每个NPC单独创建一个 ModNPC 实例(NPCLoader 里是
        // GetNPC(type).NewInstance(npc)),所以字段天然是按NPC隔离的,新召唤的boss会自己重来一遍
        private bool introStarted;       // 入场流程是否已启动
        private bool saidAt30;           // 30%台词是否已说
        private bool saidAt5;            // 5%第一句是否已说
        private bool saidAt5Second;      // 5%第二句是否已说
        private int at5SecondDelay;      // 第二句的延迟计时
        private bool sawDodgeThisDash;   // 这一段冲刺里玩家是否躲开了
        private int disengageTicks;      // 脱战后的帧数(自己数,不用 timeLeft——见 Disengage 注释)

        // 每句台词各自的下次可说时间(用 lifeTicks 当单调时钟)。
        // 同一句话至少隔 20~25 秒才能再说,避免连续几次冲刺都在喊同一句
        private readonly Dictionary<string, int> lineNextAllowed = new Dictionary<string, int>();
        private int lifeTicks;

        protected virtual float IntroFrames => 180f;          // 入场站桩时长(180帧 = 3秒)

        // ── 音效 ──
        // 路径是"mod内部相对路径":文件必须真的在 mod 文件夹里(TheCatMaodie/Sounds/xxx),
        // 放在 ModSources 下别的地方引擎加载不到。扩展名不写,引擎自己找。
        // 播放时机都是"真正出手那一帧",不是蓄力时——蓄力停顿期间不该出声
        private static readonly SoundStyle SfxIntro = new SoundStyle("TheCatMaodie/Sounds/Intro") { Volume = 0.85f };
        private static readonly SoundStyle SfxSpit = new SoundStyle("TheCatMaodie/Sounds/Spit") { Volume = 0.8f };
        private static readonly SoundStyle SfxBlade = new SoundStyle("TheCatMaodie/Sounds/Blade") { Volume = 0.8f };
        private static readonly SoundStyle SfxDash = new SoundStyle("TheCatMaodie/Sounds/Dash") { Volume = 0.8f };
        private static readonly SoundStyle SfxDog = new SoundStyle("TheCatMaodie/Sounds/Dog") { Volume = 0.8f };

        // 入场音频(Intro.mp3)约 2.06 秒 = 124 帧,所以战斗音乐在召唤后约 132 帧才进来,
        // 也就是"入场音频播完"之后(多留几帧让余韵散掉),而不是boss一出现就把音乐切掉
        protected virtual float IntroMusicDelay => 132f;

        // 战斗音乐可以开始了吗(给 BigDogMusic 那个 SceneEffect 判断用)
        public bool MusicReady => lifeTicks >= IntroMusicDelay;

        // 给弹幕那边调用(诅咒招狗时也要响一声)
        public static void PlayDogSound(Vector2 pos)
        {
            SoundEngine.PlaySound(SfxDog, pos);
        }

        protected virtual int DespawnFrames => 90;            // 脱战后多少帧消失(90帧 = 1.5秒)

        // 脱战:停手 + 冒烟,自己数够帧数后销毁。
        // 注意不能用 NPC.timeLeft 做倒计时:原版 NPC.Update 里对 boss 有一句
        // "if (boss) flag2 = true;",而消失的那段要求 !flag2——也就是说
        // 凡是 NPC.boss = true 的NPC,靠 timeLeft 永远等不到消失(原版boss都是自己在AI里销毁的)
        private void Disengage()
        {
            NPC.velocity.X *= 0.9f;
            if (Main.rand.NextBool(2))
                Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Grass);

            disengageTicks++;
            if (disengageTicks >= DespawnFrames)
                DespawnNow();
        }

        // 立刻销毁:照着引擎自己的消失流程写(见 NPC.Update 里 timeLeft 那一段),
        // 这样联机时的同步行为和原版一致,而不是只在本地把NPC抹掉。
        // (引擎那一段还会设 noSpawnCycle,但那个字段对外不可见,而且只是刷新周期的记账,不影响消失)
        private void DespawnNow()
        {
            for (int i = 0; i < 16; i++)
                Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Grass);

            NPC.active = false;
            if (Main.netMode == NetmodeID.Server)
            {
                NPC.netSkip = -1;
                NPC.life = 0;
                NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, NPC.whoAmI);
            }
        }

        // 说一句台词:文本放在 Localization 的 Dialogue 段里(改词不用动代码)。
        // 用聊天栏而不是对话UI——战斗中的台词不该打断操作
        public void SayLine(string key)
        {
            // 进入最后5%那两句的阶段后,不再说别的台词——临终台词不该被"切割..."之类打断。
            // 这两句自己要能说完,所以放行
            if (saidAt5 && key != "At5a" && key != "At5b") return;

            if (lineNextAllowed.TryGetValue(key, out int next) && lifeTicks < next)
                return;   // 这句还在冷却里

            // 20~25 秒之后才允许再说同一句
            lineNextAllowed[key] = lifeTicks + Main.rand.Next(20 * 60, 25 * 60 + 1);

            Main.NewText(Language.GetTextValue("Mods.TheCatMaodie.Dialogue." + key), new Color(255, 200, 120));
        }

        // 让场上那只大狗来说这句。弹幕命中时手上没有boss实例,所以从场上找一只活的
        public static void SayLineFromAnyBoss(string key)
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                if (Main.npc[i].active && Main.npc[i].ModNPC is BigDogBoss boss)
                {
                    boss.SayLine(key);
                    return;
                }
            }
        }

        // 冲刺/飞扑"擦身而过"判定:横向贴到玩家旁边(70像素内)但纵向差得远(70像素外),
        // 说明玩家是躲开的而不是被撞到。只在真的冲到玩家身边时才判定——
        // 远远没冲到的冲刺不算(否则"空放"也会触发台词)
        private void CheckDashDodged()
        {
            if (!NPC.boss) return;
            Player player = Main.player[NPC.target];
            if (Math.Abs(NPC.Center.X - player.Center.X) < 70f &&
                Math.Abs(NPC.Center.Y - player.Center.Y) > 70f)
                sawDodgeThisDash = true;
        }

        // 一段冲刺(含飞扑)结束时结算:被躲开了就说一句。同一句台词的间隔由 SayLine 自己控制
        private void FlushDodgeLine()
        {
            if (NPC.boss && sawDodgeThisDash)
                SayLine(Main.rand.NextBool() ? "Dodge1" : "Dodge2");
            sawDodgeThisDash = false;
        }

        // ── 召唤小怪 ──
        // 血量掉到 80% / 50% / 30% 时各召唤一波(每档只触发一次,用 localAI[3] 记数)。
        // 一次掉血跨过好几档时,几波会在同一帧依次放出来,不会漏
        private static readonly float[] SummonThresholds = { 0.8f, 0.5f, 0.3f };

        private void CheckSummonWaves(Player player)
        {
            for (int i = 0; i < SummonThresholds.Length; i++)
            {
                if (NPC.localAI[3] > i) continue;                     // 这一档已经放过了
                if (NPC.life > NPC.lifeMax * SummonThresholds[i]) continue;   // 血量还没掉到这一档

                // NPC生成只在服务器/单机做,联机客户端各生成一份就重复了。
                // 音效放这一层各客户端都播,而且一波只响一次(别三只各响一遍叠成噪音)
                SoundEngine.PlaySound(SfxDog, player.Center);
                if (Main.netMode != NetmodeID.MultiplayerClient)
                    SpawnMinionWave(player);

                NPC.localAI[3] = i + 1f;
            }
        }

        // 玩家屏幕某一侧的边缘X坐标,略微在屏幕外。
        // boss的召唤波和"诅咒招狗"共用这一份计算,保证两边生成位置规则一致。
        // 用"玩家 ± 半个屏幕"而不是 Main.screenPosition:单人时两者一样(镜头就在玩家身上),
        // 但专用服务器没有屏幕,screenPosition 是无效的
        public static float ScreenEdgeX(Player player, bool leftSide)
        {
            float viewW = Main.ViewSize.X;   // 可见世界宽度(已折算分辨率和缩放)
            if (float.IsNaN(viewW) || viewW < 400f || viewW > 20000f) viewW = 1600f;

            const float margin = -64f;       // 负值 = 生成点落在屏幕外一点(从画面外走进来)
            float halfW = viewW * 0.5f;
            return leftSide ? player.Center.X - halfW - margin : player.Center.X + halfW + margin;
        }

        // 在玩家屏幕两侧靠边缘的位置放3只(两侧加起来3只:随机一侧2只、另一侧1只)
        private void SpawnMinionWave(Player player)
        {
            float leftX = ScreenEdgeX(player, true);
            float rightX = ScreenEdgeX(player, false);

            int leftCount = Main.rand.NextBool() ? 2 : 1;   // 哪一侧多出一只,随机

            for (int side = 0; side < 2; side++)
            {
                int count = (side == 0) ? leftCount : 3 - leftCount;
                float edgeX = (side == 0) ? leftX : rightX;

                for (int k = 0; k < count; k++)
                {
                    // 同侧两只朝屏幕内侧错开,免得叠在一起
                    float offset = (side == 0 ? 44f : -44f) * k;
                    SpawnMinionAt(NPC.GetSource_FromAI(), new Vector2(edgeX + offset, player.Center.Y), MinionType);
                }
            }
        }

        // 在指定位置附近找一块地面,放下一只小怪。
        // 做成 public static 是为了让音波弹幕命中玩家时也能调用(减益的"再次命中招小狗"那一条),
        // 生成源由调用方传入:boss自己召唤用 NPC.GetSource_FromAI(),弹幕触发用弹幕的来源
        public static void SpawnMinionAt(IEntitySource source, Vector2 around, int minionType)
        {
            int tx = (int)(around.X / 16f);
            int ty0 = (int)(around.Y / 16f);

            float spawnY = around.Y - 160f;   // 兜底:找不到地面就从高空丢下来,靠重力落地
            for (int dy = -6; dy <= 14; dy++)
            {
                int ty = ty0 + dy;
                if (!WorldGen.InWorld(tx, ty, 10)) continue;
                Tile t = Main.tile[tx, ty];
                if (t.HasTile && Main.tileSolid[t.TileType])
                {
                    spawnY = ty * 16f - 40f;   // 方块上方略高一点,让重力把它压实到地上
                    break;
                }
            }

            int idx = NPC.NewNPC(source, (int)around.X, (int)spawnY, minionType);
            if (idx >= 0 && idx < Main.maxNPCs)
            {
                // 出场烟雾:让玩家看得见它是从哪冒出来的,不然凭空多出一只怪很突兀
                NPC minion = Main.npc[idx];
                for (int i = 0; i < 12; i++)
                    Dust.NewDustDirect(minion.position, minion.width, minion.height, DustID.Grass);
            }
        }

        // ── 冲刺第一段:水平地面冲撞 ──
        // 冲完(撞住/冲够距离)不回收,而是切到跳跃状态的"飞扑"类型,由那边负责抛射第二段
        private void DashStep(float aggression, float dir)
        {
            NPC.ai[1]++;
            const float windup = 20f;       // 蓄力帧数(预告)
            const float dashFrames = 78f;   // 最长持续帧数

            float dashSpeed = DashSpeedBase + DashSpeedRage * aggression;   // 17~20

            if (NPC.ai[1] <= windup)
            {
                NPC.velocity.X *= 0.85f;   // 急停蓄力
                if (Main.rand.NextBool(2))
                    Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Grass);
                return;
            }

            if (NPC.ai[1] == windup + 1f)
            {
                NPC.velocity.X = dir * dashSpeed;   // 方向在这一刻定死(中途不追人)
                sawDodgeThisDash = false;           // 每一段冲刺重新开始记"有没有被躲开"
                if (NPC.boss) SoundEngine.PlaySound(SfxDash, NPC.Center);   // 冲出去那一帧(小怪不响)
            }
            else
            {
                // 冲刺途中每帧把速度补回满值。横向速度本身不会自己衰减(原版对自定义AI没有
                // 地面摩擦,已查过 NPC.Collision_MoveNormal 的全部内容),但撞到方块时会被碰撞
                // 直接清零——补这一下,才能"跳上台阶后依然以冲刺速度继续冲"。
                // 方向沿用速度的正负(起冲时锁定的那个);只有被撞成0的那一帧才按玩家方位取一次
                float dashDir = Math.Sign(NPC.velocity.X);
                if (dashDir == 0f) dashDir = dir;
                NPC.velocity.X = dashDir * dashSpeed;
            }

            bool blocked = NPC.collideX && NPC.collideY;    // 贴着地被挡住(多半是矮台阶)
            bool dashOver = NPC.ai[1] > windup + dashFrames;

            CheckDashDodged();   // 贴着玩家身边掠过?记下来(见方法注释)

            if (blocked && NPC.ai[3] < 3f)
            {
                // 像原版僵尸那样跳一下越过去继续冲(地表起伏很密,不跳的话冲两三格就收招)。
                // 最多跳3次,撞上真正的高墙就不会一直原地弹
                NPC.velocity.Y = -5.5f;
                NPC.ai[3]++;
                return;
            }
            if (!blocked && !dashOver) return;   // 还在冲

            // 收招 → 接飞扑:交给跳跃状态的飞扑类型(ai[3]=3)
            NPC.ai[0] = 1f;
            NPC.ai[1] = 0f;
            NPC.ai[3] = 3f;
            // 第一段冲完就结算躲闪台词:被躲开的话这里说一句
            FlushDodgeLine();
        }

        // ── 平台边缘探测 ──
        // 脚下踩着的是不是"台面"(能站上去、且按理能穿下去的那类方块,比如木平台)
        // 判定放宽成"平台 或 任何有台面的方块",不依赖 tileSolid/tileSolidTop 的具体组合——
        // 之前就是因为我硬性要求"非实心"才一个平台都测不到
        private bool StandingOnPlatform()
        {
            int bottomY = (int)(NPC.position.Y + NPC.height);
            int tx1 = (int)(NPC.position.X / 16f);
            int tx2 = (int)((NPC.position.X + NPC.width) / 16f);

            // 脚下这一行,以及再往下一行(容忍几像素的浮点误差)
            for (int dy = 0; dy <= 1; dy++)
            {
                int ty = bottomY / 16 + dy;
                for (int tx = tx1; tx <= tx2; tx++)
                {
                    if (!WorldGen.InWorld(tx, ty, 10)) continue;
                    Tile t = Main.tile[tx, ty];
                    if (!t.HasTile) continue;
                    if (TileID.Sets.Platforms[t.TileType] || Main.tileSolidTop[t.TileType])
                        return true;
                }
            }
            return false;
        }

        // 从脚下朝指定方向逐格探,返回"还有地面"的距离(像素);探到头都还有地就返回上限
        private float LedgeDistance(float dirX, int limit = 12)
        {
            int footY = (int)((NPC.Bottom.Y + 6f) / 16f);
            int centerTX = (int)(NPC.Center.X / 16f);
            for (int i = 1; i <= limit; i++)
            {
                int tx = centerTX + (int)Math.Round(dirX * i);
                if (!HasGroundAt(tx, footY)) return i * 16f;
            }
            return limit * 16f;
        }

        // 某格往下1~2格内有没有能站的地面(实心方块或平台都算)
        private static bool HasGroundAt(int tx, int ty)
        {
            for (int y = ty; y <= ty + 1; y++)
            {
                if (!WorldGen.InWorld(tx, y, 10)) continue;
                Tile t = Main.tile[tx, y];
                if (t.HasTile && (Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType]))
                    return true;
            }
            return false;
        }

        // 动画:帧0=站立,帧1~8=跑步循环,最后一帧=吐音波的射击姿势。
        // 帧号写在 NPC.frame.Y 里(原版的惯例),PreDraw 直接按它取贴图上的那一格,不用额外占便签位
        public override void FindFrame(int frameHeight)
        {
            if (FrameSourceNPC >= 0)   // 借原版贴图的(小怪)不做动画,只画第1帧
            {
                NPC.frame.Y = 0;
                return;
            }

            int frames = Math.Max(1, Main.npcFrameCount[Type]);
            if (frames <= 1)
            {
                NPC.frame.Y = 0;
                return;
            }

            // 攻击姿势:吐音波(状态3)=倒数第二帧,扇形掷刃(状态5)=最后一帧
            if (NPC.ai[0] == 3f || NPC.ai[0] == 5f)
            {
                int pose = (NPC.ai[0] == 3f) ? frames - 2 : frames - 1;
                NPC.frame.Y = pose * frameHeight;
                NPC.frameCounter = 0f;
                return;
            }

            if (Math.Abs(NPC.velocity.X) <= 0.6f)
            {
                NPC.frame.Y = 0;          // 站住不动(含站定休息)→ 站立帧
                NPC.frameCounter = 0;
                return;
            }

            // 换帧间隔跟移速挂钩:冲刺/飞扑时腿倒得飞快(固定2帧),
            // 平时的跑动则随移速在 4~8 帧之间——跑得越快倒得越快,但整体比冲刺慢一档
            float rate;
            if (DashingNow())
            {
                rate = 2f;
            }
            else
            {
                rate = MathHelper.Clamp(8f - Math.Abs(NPC.velocity.X) * 0.4f, 4f, 8f);
            }

            NPC.frameCounter++;
            if (NPC.frameCounter >= rate)
            {
                NPC.frameCounter = 0f;
                // 跑动只在 1 ~ frames-3 之间循环:0号是站立帧,最后两帧是两种攻击姿势,都不参与循环
                int index = NPC.frame.Y / Math.Max(1, frameHeight) + 1;
                if (index < 1 || index > frames - 3) index = 1;
                NPC.frame.Y = index * frameHeight;
            }
        }

        // 残影的两档浓度(见 PreDraw):
        //   常态 —— 淡淡的拖影(像激光眼那样),只取最近几帧、浓度很低
        //   冲刺 —— 冲出去那一段用满缓存、浓度拉高,变成明显的拖尾
        private const float TrailAlphaNormal = 0.2f;    // 常态浓度(调大更明显)
        private const float TrailAlphaDash = 0.5f;      // 冲刺浓度
        private const int TrailFramesNormal = 6;        // 常态取最近几帧(冲刺时用满缓存)

        // 现在是不是正处在"冲出去"的阶段(冲刺的冲刺段 / 飞扑的空中段):残影要变浓变长
        // 用"速度够快"来判定,就不用在这儿重复抄一遍两处的蓄力帧数(蓄力时速度会衰减到接近0)
        private bool DashingNow()
        {
            bool inDashState = NPC.ai[0] == 2f || (NPC.ai[0] == 1f && NPC.ai[3] == 3f);
            return inDashState && NPC.velocity.LengthSquared() > 16f;   // 速度大于4像素/帧
        }

        // 贴图要么是我们自己的整套帧(大狗,帧号由 FindFrame 算好),要么是借来的原版
        // "竖排多帧"合体图(小怪借僵尸图:只画第1帧,不然整张都叠在身上)。
        // 帧数直接问引擎要,而不是自己猜——之前我按4帧切克眼,实际是6帧,多画了半个眼睛出来。
        public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            // 扇形掷刃的预警线画在最前面,这样线在boss身后
            DrawBladeWarningLines();

            Texture2D tex = GetSkin().Value;
            int frames = Math.Max(1, Main.npcFrameCount[FrameSourceNPC >= 0 ? FrameSourceNPC : Type]);
            int frameHeight = Math.Max(1, tex.Height / frames);
            // 借来的原版图固定画第1帧;自己的贴图按 FindFrame 写好的 NPC.frame.Y 取
            int index = FrameSourceNPC >= 0 ? 0 : Math.Clamp(NPC.frame.Y / frameHeight, 0, frames - 1);
            Rectangle frame = new Rectangle(0, index * frameHeight, tex.Width, frameHeight);
            Vector2 origin = new Vector2(frame.Width, frame.Height) / 2f;            // 以这一帧自己的中心为锚点

            // 我们的美术本身朝左,所以玩家在右边时要水平翻转。
            // 借来的原版图不翻转(原版贴图的朝向约定不一定一样,翻了会把僵尸画反)
            SpriteEffects flip = (FrameSourceNPC < 0 && NPC.direction > 0)
                ? SpriteEffects.FlipHorizontally
                : SpriteEffects.None;

            // 残影:常态就有一层很淡的拖影(像激光眼那样),冲刺/飞扑时变长变浓。
            // 站着不动时不用管——那几帧的位置和本体重合,画出来是叠在一起的,看不见
            bool dashing = DashingNow();
            int trailFrames = dashing ? NPC.oldPos.Length : Math.Min(TrailFramesNormal, NPC.oldPos.Length);
            float trailAlpha = dashing ? TrailAlphaDash : TrailAlphaNormal;

            Color ghostColor = new Color(150, 255, 180);   // 偏绿,呼应腐蚀主题(想要纯白就换成 Color.White)
            // 从1开始:oldPos[0]是上一帧,和本体几乎重合,画了看不出效果只是白费
            for (int i = 1; i < trailFrames; i++)
            {
                // 缓存刚启用那几帧还是(0,0),直接画会在地图左上角闪出一串影子
                if (NPC.oldPos[i] == Vector2.Zero) continue;

                float fade = 1f - i / (float)trailFrames;   // 1(最新) → 0(最旧)
                Vector2 ghostPos = NPC.oldPos[i] + new Vector2(NPC.width, NPC.height) / 2f - screenPos;
                Main.spriteBatch.Draw(
                    tex,
                    ghostPos,
                    frame,
                    ghostColor * (fade * trailAlpha),
                    NPC.rotation,
                    origin,
                    1f,
                    flip,
                    0f
                );
            }

            Main.spriteBatch.Draw(
                tex,
                NPC.Center - screenPos,
                frame,
                drawColor,
                NPC.rotation,
                origin,   // 以这一帧自己的中心为锚点
                1f,
                flip,
                0f
            );
            return false;   // 拦掉"整张贴图直接画上去"的默认行为
        }
    }
}
