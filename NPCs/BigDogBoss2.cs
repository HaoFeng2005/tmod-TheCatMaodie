using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.NPCs
{
    // 二阶段大狗 —— 飞行型。
    //
    // 一组出手共三拍(血越少越快):
    //   第1拍  走位到玩家左下/右下 → 上方生成虚影 → 双发射大弹幕(X 型交叉) → 爆开成 12 发小弹幕
    //   第2拍  同上, 换一边
    //   第3拍  boss 和虚影飞到玩家同一水平线合并 → 蓄力 → 朝玩家高速冲撞(锁定方向, 可躲)
    //
    // 大弹幕: 直线飞行, 越过"发射时锁定的玩家位置+260px"后原地爆开(爆点发射时定死, 走位能甩开)。
    // 虚影: 不是真的第二个NPC, 只是 PreDraw 里多画的一层半透明本体, 位置由 AI 计算 ——
    //       所以它没有判定、打不着、不消耗NPC槽位, 行为天然和 boss 完全同步
    //
    // 状态机(NPC.ai[0]):
    //   0 = 悬停观察   1 = 合并冲撞的蓄力   2 = 冲撞(有伤害)   3 = 收招
    //   5 = X 型双发(走位 + 虚影 + 预警线 + 发射, 一气呵成)
    //
    // 便签分工:
    //   ai[0]=状态   ai[1]=状态内计时器   ai[2]=出手冷却   ai[3]=冲撞角度(冲出去那帧定死)
    //   localAI[0]=悬停点X偏移  localAI[1]=悬停点Y偏移  localAI[2]=重选悬停点倒计时
    //   localAI[3]=本组已完成的攻击拍数(0/1=下一拍X型, 2=下一拍合并冲撞; 冲撞收招后清零)
    //   虚影位置不能用 localAI 了 —— localAI 只有 4 个槽(0~3), 之前写 localAI[4/5] 运行期直接越界
    //   (日志里的 IndexOutOfRangeException 就是它)。改用实例字段 ghostPos 等, 联机同步走 SendExtraAI
    //
    // 贴图: NPCs/BigDogBoss2.png, 8 帧"叼刀奔跑"循环(Art/build_bigdog2_blade.py 生成)。
    // 单帧 232x100, 狗身 122x100 水平居中, 判定箱 104x95 正好落在狗身上。
    [AutoloadBossHead]
    public class BigDogBoss2 : ModNPC
    {
        // ── 飞行 ──
        protected virtual float MoveSpeed => 11f;             // 悬停飞行限速(这次整体提速)
        protected virtual float MoveSmoothing => 0.09f;       // 越大加速越"硬"
        protected virtual float HoverDistance => 460f;        // 悬停时与玩家的水平距离(320 太贴脸, 拉远)
        protected virtual float HoverRandomX => 120f;
        protected virtual float HoverRandomY => 70f;          // 竖直抖动也收一点, 别忽近忽远
        protected virtual float HoverRepickFrames => 55f;

        // ── 朝向 ──
        protected virtual float TurnRate => 0.10f;
        protected virtual float DashTurnRate => 0.35f;
        protected virtual float FlipHysteresis => 0.12f;

        // ── 出手节奏 ──
        // 收尾大招之间的间隔(留足喘息, 因为它们是重招)
        protected virtual float AttackIntervalBase => 150f;
        protected virtual float AttackIntervalRage => 70f;    // 残血再缩短
        // 前两拍 X 型之间的间隔: 明显比收尾短 —— X 型是"连拍", 打完一发不该在原地晃半天
        protected virtual float XBeatInterval => 60f;
        protected virtual float XBeatIntervalRage => 30f;
        protected virtual float AttackRange => 1100f;

        // ── X 型双发(状态5, 屏角版) ──
        protected virtual float OrbWindup => 45f;             // 预警线出现的时长(0.75秒)
        protected virtual int OrbDamage => 45;                // 大弹幕本体伤害
        protected virtual float OrbFlySpeed => 11f;           // 大弹幕飞行速度
        // 判定"到位"的距离: 离屏角这么近就算站好了
        protected virtual float ArriveRadius => 70f;
        // 本体飞向屏角的兜底上限: 玩家一直全速跑、本体追不上时, 到点也得往下走(否则卡死)
        protected virtual float ApproachTimeoutMax => 240f;
        // 虚影的移动速度: 比本体快得多 —— 它是"分身", 分出来就是要迅速就位的。
        // 太慢会影响观感(还在飞的时候本体已经射完了), 详见 XPattern 分段2 的三重发射条件
        protected virtual float GhostMoveSpeed => MoveSpeed * 1.9f;
        // 虚影等待的兜底上限: 极端情况下它迟迟到不了, 也不能让这一拍永远卡住
        protected virtual float GhostWaitMax => 180f;
        // 预警线长度: 要长到"玩家满速飞行 3 秒也看不到线的端点"。
        // 依据: 满速飞行约 10 像素/帧, 3 秒 = 180 帧 ≈ 1800 像素; 屏幕对角线约 1900 像素
        // (可视区 1655×931) → 端点至少要在起点 3000+ 像素外, 这里给 4000 留足余量
        protected virtual float AimLineLength => 4000f;
        // 本体射完之后, 虚影隔多少帧再射(不同时: 先本体后虚影, 两条弹道交错着来)
        protected virtual float GhostShotDelay => 14f;

        // ── 合并冲撞(状态1/2/3) ──
        protected virtual float WindupFrames => 26f;          // 合并后的蓄力(比之前 38 快)
        // 起跑距离: 合并完成后先退到这里之外才起跑冲刺。屏幕边缘离玩家只有约半个屏宽(≈730px),
        // 直接冲起步就贴脸; 拉到这个距离外, 玩家才有走位反应窗口
        protected virtual float DashStartDist => 520f;
        protected virtual float DashSpeed => 24f;             // 冲撞速度(之前 21)
        protected virtual float DashFrames => 32f;
        protected virtual float RecoverFrames => 20f;         // 收招(之前 45, 太拖)

        // ── 三段冲刺(状态7) ──
        protected virtual float TripleChargeFrames => 32f;    // 每段蓄力(预警线时长)
        protected virtual float TripleDashLength => 48f;      // 每段冲刺持续帧数(48×27 ≈ 1300px)
        protected virtual float TriplePauseFrames => 18f;     // 段间急停喘息(间隔不变)
        protected virtual float TripleDashSpeed => 27f;       // 每段冲刺速度(比合并冲刺 24 还快一点)

        // ── 剑模式(状态8): 三段冲刺结束后有概率进入 ──
        // 召唤大飞剑(自己把"剑"丢了, 切到没叼剑的站立贴图) → 成对发射音波(5~6 对,
        // 每对在玩家身上交叉出 X 光束) → 结束: 撤走飞剑, 换回叼刀贴图
        // 三个收尾大招的基准权重(动态平衡的起点, 见 PickEnder):
        // 想更常看到哪一招就把它调高
        protected virtual float EnderWeightMerge => 1f;       // 合并冲刺
        protected virtual float EnderWeightTriple => 1f;      // 三段冲刺
        protected virtual float EnderWeightSword => 1.6f;     // 剑模式(基准给高, 让它更常出现)
        protected virtual float SwordPairGap => 45f;          // 两对音波之间的间隔

        // ── 动画换帧间隔 ──
        protected virtual int FramesPerIndexHover => 12;
        protected virtual int FramesPerIndexWindup => 18;
        protected virtual int FramesPerIndexDash => 3;
        protected virtual int FramesPerIndexRecover => 12;
        protected virtual int FramesPerIndexOrb => 8;         // X 型期间的动画速度

        // ── 残影 ──
        protected virtual float TrailAlphaNormal => 0.22f;
        protected virtual float TrailAlphaDash => 0.55f;
        protected virtual int TrailFramesNormal => 6;

        protected virtual int DespawnFrames => 90;

        private Asset<Texture2D> skin;
        private Asset<Texture2D> standingSkin;   // 没叼剑的站立贴图(剑模式专用)

        // 贴图形态: true = 叼刀奔跑(默认), false = 空手站立(把剑"扔出去"当了大飞剑)。
        // 两套是不同的贴图文件和帧数(8帧/11帧), 切换时帧游标要归零
        private bool formBlade = true;

        private Asset<Texture2D> GetSkin()
        {
            if (skin == null)
                skin = ModContent.Request<Texture2D>(Texture, AssetRequestMode.ImmediateLoad);
            return skin;
        }

        private Asset<Texture2D> GetStanding()
        {
            if (standingSkin == null)
                standingSkin = ModContent.Request<Texture2D>(Texture + "_Standing", AssetRequestMode.ImmediateLoad);
            return standingSkin;
        }

        // 当前形态用的贴图与帧数(两套帧数不同: 8 / 11)
        private int FrameCount => formBlade ? 8 : 11;

        private void SetForm(bool blade)
        {
            if (formBlade == blade) return;
            formBlade = blade;
            NPC.frame.Y = 0;          // 换贴图后帧游标必须归零, 否则按旧帧高取帧会越界
            NPC.frameCounter = 0;
        }

        // 翻面状态必须自己记着: 翻面那一刻要做旋转补偿(见 FaceAngle),
        // 而"上一帧是什么状态"没法从 NPC.rotation 反推
        private bool faceRight;
        private bool facingReady;   // 首帧只登记状态, 不做补偿

        // 虚影/屏角状态。localAI 只有 4 个槽放不下, 用实例字段
        // (引擎给每个 NPC 单独建一个 ModNPC 实例, 所以字段天然按NPC隔离)
        private Vector2 ghostPos;     // 虚影的世界坐标(每帧由 AI 维护)
        private Vector2 ghostVel;     // 虚影的速度(虚影不是NPC, 位置靠这套速度自己积分)
        private bool ghostFlying;     // 虚影是否处于"飞行中"(走位/合并滑行), 影响绘制的透明度
        private float cornerX;        // 本体当前占据的屏角: -1=左列 +1=右列
        private float cornerY;        // 本体当前占据的屏角: -1=上半 +1=下半(本体永远在下半)
        private float mergeSide;      // 合并阶段本体贴哪一侧的屏幕边缘: -1=左 +1=右
        // 三个收尾大招各自放过多少次(0=合并冲刺 1=三段冲刺 2=剑模式)。
        // 动态平衡用: 权重 = 基准 ÷ (1+次数), 放过的招权重下降, 没放的相对更高
        private int[] enderCounts = new int[3];

        // X 型(状态5)的分段状态。原来的"固定帧数推进"改成"按到位情况推进", 所以需要显式状态:
        //   0 = 本体飞向屏角(虚影还不存在)  1 = 虚影飞对角 + 本体蓄力  2 = 本体已射, 等虚影到位再射
        private int xPhase;
        private float xCharge;        // 本体到位后的蓄力帧数
        private float xShotDelay;     // 距本体发射过了多少帧

        public override void SetStaticDefaults()
        {
            Main.npcFrameCount[Type] = 8;
            NPCID.Sets.TrailingMode[Type] = 1;
            NPCID.Sets.TrailCacheLength[Type] = 10;
        }

        public override void SetDefaults()
        {
            NPC.width = 104;
            NPC.height = 95;
            NPC.damage = 60;
            NPC.defense = 40;
            NPC.lifeMax = 18000;          // 测试值, 想打久一点就调高
            NPC.knockBackResist = 0f;
            NPC.aiStyle = -1;
            NPC.boss = true;
            NPC.noGravity = true;
            NPC.noTileCollide = true;
            NPC.HitSound = SoundID.NPCHit1;
            NPC.DeathSound = SoundID.NPCDeath1;
        }

        private static readonly SoundStyle SfxDash = new SoundStyle("TheCatMaodie/Sounds/Dash") { Volume = 0.8f };

        public override void SendExtraAI(System.IO.BinaryWriter writer)
        {
            writer.Write(NPC.ai[3]);
            for (int i = 0; i < 4; i++) writer.Write(NPC.localAI[i]);
            writer.Write(ghostPos.X);
            writer.Write(ghostPos.Y);
            writer.Write(ghostFlying);
            writer.Write(cornerX);
            writer.Write(cornerY);
            writer.Write(mergeSide);
            writer.Write(tripleDashAngle);
            for (int i = 0; i < 3; i++) writer.Write(enderCounts[i]);
            writer.Write(xPhase);
            writer.Write(xCharge);
            writer.Write(xShotDelay);
            writer.Write(formBlade);
            writer.Write(faceRight);
            writer.Write(facingReady);
        }

        public override void ReceiveExtraAI(System.IO.BinaryReader reader)
        {
            NPC.ai[3] = reader.ReadSingle();
            for (int i = 0; i < 4; i++) NPC.localAI[i] = reader.ReadSingle();
            ghostPos.X = reader.ReadSingle();
            ghostPos.Y = reader.ReadSingle();
            ghostFlying = reader.ReadBoolean();
            cornerX = reader.ReadSingle();
            cornerY = reader.ReadSingle();
            mergeSide = reader.ReadSingle();
            tripleDashAngle = reader.ReadSingle();
            for (int i = 0; i < 3; i++) enderCounts[i] = reader.ReadInt32();
            xPhase = reader.ReadInt32();
            xCharge = reader.ReadSingle();
            xShotDelay = reader.ReadSingle();
            formBlade = reader.ReadBoolean();
            faceRight = reader.ReadBoolean();
            facingReady = reader.ReadBoolean();
        }

        public override void AI()
        {
            NPC.TargetClosest(false);   // 只锁目标, 朝向完全由 FaceAngle 接管

            if (NPC.target < 0 || NPC.target >= Main.maxPlayers)
            {
                Disengage();
                return;
            }

            Player player = Main.player[NPC.target];

            // 玩家死了必须自己判: TargetClosest 在无人可锁时不清 target, 会留着尸体索引
            if (!player.active || player.dead)
            {
                Disengage();
                return;
            }

            disengageTicks = 0;

            float aggression = 1f - NPC.life / (float)NPC.lifeMax;
            if (NPC.ai[2] > 0f) NPC.ai[2]--;

            switch ((int)NPC.ai[0])
            {
                case 0: HoverObserve(player, aggression); break;
                case 1: MergeWindup(player); break;
                case 2: Dash(); break;
                case 5: case 6: XPattern(player); break;   // 5/6 都是 X 型, 选边由拍数决定
                case 7: TripleDash(player); break;         // 三段冲刺(与合并冲刺交替)
                case 8: SwordMode(player); break;          // 剑模式: 大飞剑 + 音波X激光
                default: Recover(); break;
            }
        }

        // ── 状态0: 悬停观察 ──
        // 悬停点分两种: X 型刚打完的那一拍(localAI[3] 是 1 或 2), 拉到屏幕外围候场 ——
        // 下一拍 X 型从屏角就近起手, 弹幕永远不会从玩家脸上发射;
        // 合并冲刺打完(本组结束, localAI[3] 清零)才回到玩家两侧的正常悬停, 接近感来自这里
        private void HoverObserve(Player player, float aggression)
        {
            NPC.damage = 0;

            NPC.localAI[2]--;
            if (NPC.localAI[2] <= 0f)
            {
                NPC.localAI[2] = HoverRepickFrames;
                if (NPC.localAI[3] >= 1f)
                {
                    // 候场: 贴在屏幕下半的左/右缘外侧(和 X 型同侧的屏角附近), 等下一拍
                    float side = (NPC.localAI[3] == 1f) ? 1f : -1f;   // 第1拍打完在左下 → 候场挪到右下, 第2拍正好换边
                    Vector2 waitCorner = ScreenCornerWorld(player, side, 1f);
                    NPC.localAI[0] = waitCorner.X - player.Center.X;
                    NPC.localAI[1] = waitCorner.Y - player.Center.Y;
                }
                else
                {
                    // 正常悬停: 玩家两侧一段距离, 随机抖动
                    float side = Main.rand.NextBool() ? -1f : 1f;
                    NPC.localAI[0] = side * HoverDistance + Main.rand.Next(-(int)HoverRandomX, (int)HoverRandomX + 1);
                    NPC.localAI[1] = Main.rand.Next(-(int)HoverRandomY, (int)HoverRandomY + 1);
                }
                NPC.netUpdate = true;
            }

            Vector2 hoverPoint = player.Center + new Vector2(NPC.localAI[0], NPC.localAI[1]);

            // 换边绕行: 新悬停点和当前位置分居玩家两侧时, 直线追过去会贴着玩家身边掠过。
            // 目标点距离玩家近身圈以内时, 插一个中转点 —— 从玩家上方/下方绕大弧过去, 不穿越近身区
            Vector2 seekPoint = hoverPoint;
            Vector2 toTarget = hoverPoint - NPC.Center;
            float nearRadius = HoverDistance * 0.55f;          // 近身圈: 悬停距离的一半左右
            bool crossesPlayer = (Math.Sign(hoverPoint.X - player.Center.X) != Math.Sign(NPC.Center.X - player.Center.X));
            if (crossesPlayer &&
                Vector2.Distance(hoverPoint, player.Center) < nearRadius &&
                Vector2.Distance(NPC.Center, player.Center) < nearRadius * 2f)
            {
                // 绕行点: 玩家正上/正下方(选离当前Y更近的一侧)再往外推到近身圈外
                float upOrDown = (NPC.Center.Y < player.Center.Y) ? -1f : 1f;
                seekPoint = player.Center + new Vector2(NPC.localAI[0] * 0.4f, upOrDown * nearRadius * 1.5f);
            }

            FlyToward(seekPoint);
            FaceTowards(player.Center, TurnRate);

            // 冷却好 → 进入下一拍。前两拍 X 型, 第三拍是收尾大招(动态平衡挑选)
            NPC.ai[1]++;
            bool orbsDone = NPC.localAI[3] >= 2;
            // X 型是连拍, 用更短的间隔; 收尾大招之间留足喘息
            float interval = orbsDone
                ? AttackIntervalBase - AttackIntervalRage * aggression
                : XBeatInterval - XBeatIntervalRage * aggression;
            if (NPC.ai[1] >= interval && Vector2.Distance(NPC.Center, player.Center) <= AttackRange)
            {
                if (orbsDone)
                {
                    PickEnder();          // 三个收尾大招按"用过就降权"的方式挑一个
                }
                else
                {
                    NPC.ai[0] = 5f;       // 本组第 1、2 拍固定是 X 型
                    ResetXBeat();         // ★ 当帧就要清, 不能等下一帧进 XPattern 才清
                }
                NPC.ai[1] = 0f;
                NPC.netUpdate = true;
            }
        }

        // 清空"X 型这一拍"的分段状态。
        // 必须在这里(决定招式的当帧)就调用, 原因是个一帧的脏数据泄漏:
        //   AI() 先跑、PreDraw() 后跑。悬停阶段在这里把 ai[0] 设成 5, 但 xPhase 还残留着
        //   上一拍结束时的值(2), 同一帧的 PreDraw 就会误判"虚影已经分出来了", 把上一拍遗留的
        //   ghostPos 和虚影预警线画出来 —— 下一帧 XPattern 清零后它们又消失, 看起来就是
        //   "放大弹幕前虚影和预警线闪一下"。
        private void ResetXBeat()
        {
            xPhase = 0;
            xCharge = 0f;
            xShotDelay = 0f;
            ghostFlying = false;
        }

        // ── 收尾大招的动态平衡挑选 ──
        // 权重 = 基准权重 ÷ (1 + 已放次数): 放得越多权重越低, 没放过的权重相对就高 ——
        // 连放同一个大招的概率被压下去, 三个招会自然轮换(但不会归零, 永远留着随机性)。
        // 剑模式基准权重给得高一些, 因为它是最想让你看到的那一招
        private void PickEnder()
        {
            float w0 = EnderWeightMerge / (1f + enderCounts[0]);
            float w1 = EnderWeightTriple / (1f + enderCounts[1]);
            float w2 = EnderWeightSword / (1f + enderCounts[2]);

            float roll = Main.rand.NextFloat() * (w0 + w1 + w2);
            int pick = (roll < w0) ? 0 : (roll < w0 + w1) ? 1 : 2;
            enderCounts[pick]++;
            ResetXBeat();     // 同样要当帧清: 合并冲刺(状态1)的绘制也看 ghostFlying, 不清会闪一下虚影

            switch (pick)
            {
                case 0:
                    NPC.ai[0] = 1f;                          // 合并冲刺
                    break;
                case 1:
                    NPC.ai[0] = 7f;                          // 三段冲刺
                    break;
                default:
                    NPC.ai[0] = 8f;                          // 剑模式
                    NPC.ai[3] = Main.rand.Next(5, 7);        // 本模式发 5~6 对音波(存便签)
                    break;
            }
        }

        // ── 状态5: X 型双发(屏角版) ──
        // 全程"贴"在玩家屏幕的一个角上, 与玩家保持相对静止。推进不看固定帧数, 看"到位没有"(xPhase):
        //   分段0  本体自己自然飞向屏角(惯性飞行, 不瞬移不硬拽); 此时还没有虚影
        //   分段1  离屏角够近(ArriveRadius) → 虚影才从"已经站好的角落"分出, 飞向同侧对角;
        //          本体同时贴角蓄力, 蓄满 OrbWindup 帧就发射(它先射)
        //   分段2  本体已射 → 等虚影自己到位, 再等 GhostShotDelay 帧, 虚影才射
        //          (三重条件: 本体已发射 + 虚影已到位 + 延迟到点, 绝不抢在飞行途中开火)
        //   然后回悬停, 下一拍换一个角; 两拍打完由 PickEnder 挑收尾大招
        //
        // "贴屏幕边缘"的实现: 屏幕是屏幕坐标, 世界是世界坐标, 两者差一个 Main.screenPosition。
        // 不能直接把 boss 钉在屏幕角的屏幕坐标上(那是绘制层)。灾厄双胞胎的做法是以玩家为锚点取偏移 ——
        // 镜头跟着玩家走, 所以"玩家 + 屏幕半宽的偏移"在世界里就近似等于"屏幕的角",
        // 玩家走动时 boss 跟着平移但相对玩家(也就是相对屏幕)静止。
        // "自然飞过去"的实现: 全程用 FlyToward(带惯性的期望速度插值), 到位靠 slowRadius 自然减速,
        // 没有任何一帧直接改写 NPC.Center —— 所以不会出现"被拉过去"的观感
        private void XPattern(Player player)
        {
            NPC.damage = 0;
            NPC.ai[1]++;

            // 起手第一帧: 选角。分段状态在决定招式那一帧(HoverObserve/PickEnder)就已经清过了
            if (NPC.ai[1] == 1f)
            {
                cornerX = (NPC.localAI[3] == 0f) ? -1f : 1f;      // -1=左, +1=右
                cornerY = 1f;                                     // 1=下半屏(boss 永远在下半, 虚影在上半)
                ResetXBeat();                                     // 双保险: 联机/异常路径下也保证从干净状态开始
                NPC.netUpdate = true;
            }

            Vector2 selfCorner = ScreenCornerWorld(player, cornerX, cornerY);
            Vector2 ghostCorner = ScreenCornerWorld(player, cornerX, -cornerY);

            switch (xPhase)
            {
                // ── 分段0: 本体自己飞向屏角 ──
                // ★ 虚影必须等本体真正到位才生成。之前是"第51帧无条件生成", 于是本体换边
                //   还在半路时, 虚影就从靠近玩家的中途位置冒出来 —— 它再快也已经站在坏起点上了,
                //   结果就是"虚影贴着玩家放大弹幕"。现在从源头保证它的起点永远在屏幕角落
                case 0:
                    FlyToward(selfCorner);
                    FaceTowards(player.Center, TurnRate);
                    if (Vector2.Distance(NPC.Center, selfCorner) <= ArriveRadius ||
                        NPC.ai[1] >= ApproachTimeoutMax)          // 兜底: 玩家一直跑也总得往下走
                    {
                        xPhase = 1;
                        ghostPos = NPC.Center;    // 从"已经站好的角落"分出
                        ghostVel = Vector2.Zero;  // 从静止开始加速
                        ghostFlying = true;
                        xCharge = 0f;
                        NPC.netUpdate = true;
                    }
                    return;

                // ── 分段1: 虚影飞向对角 + 本体贴角蓄力 ──
                case 1:
                    FlyToward(selfCorner, tight: true);
                    MoveGhostToward(ghostCorner, tight: false);
                    FaceTowards(player.Center, TurnRate);
                    xCharge++;
                    if (NPC.ai[1] % 4f == 0f)
                        Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);

                    if (xCharge >= OrbWindup)
                    {
                        FireOrb(MouthPosition(), player);   // 本体先射
                        xPhase = 2;
                        xShotDelay = 0f;
                        ghostFlying = false;                // 已经就位/在收尾, 按"分身已稳"绘制
                        NPC.netUpdate = true;
                    }
                    return;

                // ── 分段2: 本体已射 → 等虚影到位, 再等 GhostShotDelay 帧, 它才射 ──
                // 三重条件: 本体已发射 + 虚影自己已到位 + 距本体发射够久。缺一不发 —— 绝不抢在飞行途中开火
                default:
                    FlyToward(selfCorner, tight: true);
                    bool ghostReady = Vector2.Distance(ghostPos, ghostCorner) <= ArriveRadius;
                    MoveGhostToward(ghostCorner, tight: ghostReady);
                    FaceTowards(player.Center, TurnRate);
                    xShotDelay++;

                    if ((ghostReady && xShotDelay >= GhostShotDelay) || xShotDelay >= GhostWaitMax)
                    {
                        // 虚影后射: 从它现在的位置朝玩家打(此时它一定在它的角上, 除非兜底超时)
                        Vector2 gDir = player.Center - ghostPos;
                        if (gDir.LengthSquared() < 1f) gDir = new Vector2(NPC.direction, 0f);
                        gDir = Vector2.Normalize(gDir);
                        FireOrb(ghostPos + gDir * (NPC.width * 0.5f + 25f), player);

                        ghostFlying = false;
                        NPC.localAI[3]++;               // 完成一拍
                        NPC.ai[0] = 0f;
                        NPC.ai[1] = 0f;
                        NPC.ai[2] = 25f;                // 拍间小间隔(和 XBeatInterval 一起构成两次X之间的空档)
                        NPC.netUpdate = true;
                    }
                    return;
            }
        }

        // (贴角也走速度: 见 FlyToward 的 tight 参数 —— 没有任何直接改写位置的代码)

        // 发一枚大弹幕(朝玩家当前位置, 爆点 = 玩家位置 + 发射方向延伸)
        private void FireOrb(Vector2 muzzle, Player player)
        {
            Vector2 dir = player.Center - muzzle;
            if (dir.LengthSquared() < 1f) dir = new Vector2(NPC.direction, 0f);
            dir = Vector2.Normalize(dir);

            if (Main.netMode != NetmodeID.MultiplayerClient)
                SpawnOrb(muzzle, dir);

            SoundEngine.PlaySound(SfxDash, muzzle);
        }

        private void SpawnOrb(Vector2 pos, Vector2 dir)
        {
            // 锁定爆破点: 玩家位置 + 沿发射方向延伸 260px(走位能甩开)
            Vector2 lockPos = Main.player[NPC.target].Center + dir * 260f;
            int p = Projectile.NewProjectile(NPC.GetSource_FromAI(), pos, dir * OrbFlySpeed,
                ModContent.ProjectileType<Projectiles.BigDogOrb>(), OrbDamage, 0f,
                Main.myPlayer, 0f, NPC.target);
            if (Main.projectile.IndexInRange(p) && Main.projectile[p].ModProjectile is Projectiles.BigDogOrb orb)
            {
                orb.ProjLocalAI0 = lockPos.X;
                orb.ProjLocalAI1 = lockPos.Y;
                orb.ProjNetUpdate();
            }
        }

        // 屏幕左/右缘的世界坐标(合并阶段用)。
        // side ∈ {-1,+1} 决定左/右缘; progress ∈ [0,1] 是从上缘滑到玩家高度的进度
        private static Vector2 ScreenEdgeWorld(Player player, float side, float progress)
        {
            float viewW = Main.ViewSize.X;
            float viewH = Main.ViewSize.Y;
            if (float.IsNaN(viewW) || viewW < 400f || viewW > 20000f) viewW = 1600f;
            if (float.IsNaN(viewH) || viewH < 300f || viewH > 20000f) viewH = 900f;

            float x = player.Center.X + side * viewW * 0.44f;              // 0.44: 贴着边缘但不卡出屏
            float y = player.Center.Y + (viewH * 0.42f) * (1f - progress); // 从屏幕上缘一路滑到玩家高度
            return new Vector2(x, y);
        }

        // 屏幕角的世界坐标: 玩家为锚点, 偏移 = 可视世界尺寸 × (±比例)。
        // cornerX/cornerY ∈ {-1, +1}。用 Main.ViewSize(可见世界宽度, 已折算分辨率和缩放),
        // 玩家站正中时这个点就落在屏幕角附近; 玩家靠边时镜头被世界边界夹住, 会有些许偏差 —— 可接受
        private static Vector2 ScreenCornerWorld(Player player, float cx, float cy)
        {
            float viewW = Main.ViewSize.X;
            float viewH = Main.ViewSize.Y;
            if (float.IsNaN(viewW) || viewW < 400f || viewW > 20000f) viewW = 1600f;   // 世界没加载时的兜底
            if (float.IsNaN(viewH) || viewH < 300f || viewH > 20000f) viewH = 900f;

            // 0.42: 稍微收进一点, 别让狗一半身子卡出屏幕外
            return player.Center + new Vector2(cx * viewW * 0.42f, cy * viewH * 0.40f);
        }

        // 虚影位置(世界坐标)。分身飞向对角的过程中每帧由 AI 更新
        private Vector2 GhostPosition(Player player)
        {
            return ghostPos;
        }

        // ── 状态1: 合并冲撞 ──
        // 全程仍贴在屏幕边缘: 本体沿屏幕边缘水平滑到玩家正左/正右侧(与玩家同一水平线),
        // 虚影同时沿边缘从对角滑到另一侧 → 两端合拢(虚影被吸进本体) → 后撤拉开起跑距离
        // → 蓄力 → 锁定方向冲撞。
        // 后撤这一段不能省: 合并点在屏幕边缘, 离玩家已经只剩半个屏宽, 直接冲的话起步就贴脸,
        // 玩家根本来不及反应。拉到 DashStartDist 之外再起跑, 冲刺才有"从远处扑过来"的读法
        private void MergeWindup(Player player)
        {
            NPC.damage = 0;
            NPC.ai[1]++;

            // 起手决定合并侧: 沿用当前所在的左右(在左边就贴左缘, 虚影走右缘)
            if (NPC.ai[1] == 1f)
            {
                mergeSide = (cornerX >= 0f) ? -1f : 1f;   // 在右下角 → 从左侧合并; 在左下角 → 从右侧
                // 虚影也从本体当前位置分出(和 X 型一致): 不指定起点的话它会从上一拍遗留的
                // 位置开始滑, 看起来像"凭空从别处飘过来"
                ghostPos = NPC.Center;
                ghostVel = Vector2.Zero;
                ghostFlying = true;
                NPC.netUpdate = true;
            }

            float mergeFrames = 55f;                      // 贴边滑行的时长
            float pullbackFrames = 30f;                   // 合并后向后拉开的时长

            if (NPC.ai[1] <= mergeFrames)
            {
                // 本体: 沿屏幕边缘滑到玩家高度; 虚影: 对称滑到另一缘。
                // 位置全部由速度积分产生(没有直接改写 Center 的代码), 观感是"滑过去"而不是"被挪过去"
                float slide = NPC.ai[1] / mergeFrames;    // 0 → 1
                Vector2 selfTarget = ScreenEdgeWorld(player, mergeSide, slide);
                Vector2 ghostTarget = ScreenEdgeWorld(player, -mergeSide, slide);

                FlyToward(selfTarget);
                MoveGhostToward(ghostTarget, tight: false);
                FaceTowards(player.Center, TurnRate);

                if (NPC.ai[1] == mergeFrames)
                {
                    // 合并: 虚影吸进本体(接下来不再绘制)
                    ghostFlying = false;
                    NPC.netUpdate = true;
                }
                return;
            }

            // 合并完成 → 后撤拉开起跑距离: 朝玩家反方向(略偏上)退到 DashStartDist 之外。
            // 同样是速度行为(期望速度指向"远离子弹道"的退位点), 不是瞬移
            float sinceMerged = NPC.ai[1] - mergeFrames;
            if (sinceMerged <= pullbackFrames)
            {
                Vector2 away = NPC.Center - player.Center;
                if (away.LengthSquared() < 1f) away = new Vector2(mergeSide, 0f);
                away = Vector2.Normalize(away);

                // 退到起跑距离之外就停(期望速度归零), 否则继续退
                float distNow = Vector2.Distance(NPC.Center, player.Center);
                Vector2 desired = (distNow < DashStartDist)
                    ? away * MoveSpeed * 1.6f
                    : Vector2.Zero;
                NPC.velocity = Vector2.Lerp(NPC.velocity, desired, 0.15f);
                FaceTowards(player.Center, TurnRate);

                if (NPC.ai[1] % 4f == 0f)
                    Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);
                return;
            }

            // 后撤完成: 蓄力(此时离玩家至少 DashStartDist, 冲刺有完整的加速可视距离)
            FlyToward(player.Center + Vector2.Normalize(NPC.Center - player.Center) * DashStartDist, tight: true);
            FaceTowards(player.Center, TurnRate);

            if (NPC.ai[1] % 4f == 0f)
                Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);

            if (NPC.ai[1] < mergeFrames + pullbackFrames + WindupFrames) return;

            // 冲出去: 方向按这一刻锁定, 之后不再修正
            Vector2 aim = player.Center - NPC.Center;
            float side = Math.Sign(aim.X);
            if (side == 0f) side = 1f;
            if (aim.LengthSquared() < 1f) aim = new Vector2(side, 0f);

            NPC.ai[3] = aim.ToRotation();
            NPC.velocity = Vector2.Normalize(aim) * DashSpeed;
            FaceAngle(NPC.ai[3], 1f);

            SoundEngine.PlaySound(SfxDash, NPC.Center);
            NPC.ai[0] = 2f;
            NPC.ai[1] = 0f;
            NPC.netUpdate = true;
        }

        // ── 状态2: 冲撞 ──
        private void Dash()
        {
            NPC.damage = NPC.defDamage;    // 只有冲撞有接触伤害
            NPC.ai[1]++;

            Vector2 dashDir = NPC.ai[3].ToRotationVector2();
            NPC.velocity = dashDir * DashSpeed;
            FaceAngle(NPC.ai[3], DashTurnRate);

            if (NPC.ai[1] % 3f == 0f)
                Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);

            if (NPC.ai[1] < DashFrames) return;

            NPC.ai[0] = 3f;
            NPC.ai[1] = 0f;
            NPC.netUpdate = true;
        }

        // ── 状态3: 收招 ──
        private void Recover()
        {
            NPC.damage = 0;
            NPC.velocity *= 0.9f;
            NPC.ai[1]++;

            if (NPC.ai[1] < RecoverFrames) return;

            NPC.ai[0] = 0f;
            NPC.ai[1] = 0f;
            NPC.localAI[2] = 0f;
            NPC.localAI[3] = 0f;           // 一组结束, 下组从 X 型重新开始
            NPC.netUpdate = true;
        }

        // ── 状态7: 三段冲刺 ──
        // 蓄力 → 冲 → 急停 × 3, 每一段都重新瞄准再锁方向(玩家每段都有独立的躲避窗口)。
        // 段内: 预警线闪断(和 X 型同款), 冲刺开始线消失, 冲刺途中身后撒白色粒子;
        // 三段全部冲完粒子自然停止(没有需要专门清理的东西 —— 粒子跟着冲刺行为走)
        //
        // 内部阶段用 ai[1] 的时间轴切, 段号用 localAI[3] 的余数……不行, localAI[3] 是组进度。
        // 段号用 ai[3] 记(此状态里不存冲撞角, 角度改用实例字段 tripleDashAngle):
        //   ai[3] = 已完成的段数(0~2), 冲完第 3 段收招
        private float tripleDashAngle;      // 当前段的锁定冲角(实例字段, 联机走 SendExtraAI)

        private void TripleDash(Player player)
        {
            // 本状态的节拍表(全部可调):
            float charge = TripleChargeFrames;     // 每段蓄力(预警线)
            float dash = TripleDashLength;         // 每段冲刺
            float pause = TriplePauseFrames;       // 段间急停喘息

            NPC.ai[1]++;
            float t = NPC.ai[1] - 1f;              // 本状态第 t 帧(0 起)

            // 段内时间轴: [0, charge) 蓄力 | [charge, charge+dash) 冲刺 | 之后急停, 直到本段总长走完
            float segLen = charge + dash + pause;
            int seg = (int)(t / segLen);           // 当前第几段(0 起)
            float inSeg = t % segLen;              // 段内帧

            if (seg >= 3)
            {
                // 三段全部完成 → 收招(剑模式现在是独立收尾大招, 由 PickEnder 挑选, 不再挂在这里)
                NPC.damage = 0;
                NPC.velocity *= 0.85f;
                NPC.ai[1] = 0f;
                NPC.ai[0] = 3f;                     // 复用收招状态, 它会把 localAI[3] 清零开新组
                NPC.netUpdate = true;
                return;
            }

            if (inSeg < charge)
            {
                // 蓄力: 急停 + 头跟着玩家转 + 预警线实时跟转(绘制层画的是"此刻指向玩家的方向")
                NPC.damage = 0;
                NPC.velocity *= 0.82f;
                FaceTowards(player.Center, TurnRate * 1.5f);
                if (NPC.ai[1] % 4f == 0f)
                    Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);

                // 蓄力结束的那一帧: 锁定本段方向
                if (inSeg == charge - 1f)
                {
                    Vector2 aim = player.Center - NPC.Center;
                    float sideX = Math.Sign(aim.X);
                    if (sideX == 0f) sideX = 1f;
                    if (aim.LengthSquared() < 1f) aim = new Vector2(sideX, 0f);
                    tripleDashAngle = aim.ToRotation();
                    FaceAngle(tripleDashAngle, 1f);
                    NPC.netUpdate = true;
                }
                return;
            }

            if (inSeg < charge + dash)
            {
                // 冲刺: 只有这段有接触伤害; 速度每帧补满(方向锁死); 身后撒白色粒子
                NPC.damage = NPC.defDamage;
                NPC.velocity = tripleDashAngle.ToRotationVector2() * TripleDashSpeed;
                FaceAngle(tripleDashAngle, DashTurnRate);

                // 白色粒子轨迹: 每帧 2 粒, 无重力、微缩小、位置带一点随机散布
                for (int i = 0; i < 2; i++)
                {
                    Dust trail = Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);
                    trail.noGravity = true;
                    trail.velocity = Main.rand.NextVector2Circular(1.2f, 1.2f);
                    trail.scale = Main.rand.NextFloat(0.8f, 1.3f);
                }
                return;
            }

            // 急停喘息: 无伤害, 减速
            NPC.damage = 0;
            NPC.velocity *= 0.78f;
        }

        // ── 状态8: 剑模式 ──
        // 开场: 召唤大飞剑(飞到自己头上方活动)+ 切到没叼剑的站立贴图(剑"给出去了")
        // 中段: 安静悬停, 每隔一阵发射一对音波(BigDogEcho × 2, 共 ai[3] 对 = 5~6 对);
        //       每对音波在玩家身上交叉出 X 光束, 发完自动消失
        // 结束: 撤走大飞剑, 换回叼刀贴图, 复用收招状态开新组
        private void SwordMode(Player player)
        {
            NPC.damage = 0;
            NPC.ai[1]++;
            float t = NPC.ai[1];

            // 开场帧: 召剑 + 换贴图 + 选个悬停偏移
            if (t == 1f)
            {
                if (Main.netMode != NetmodeID.MultiplayerClient)
                {
                    float viewH = Main.ViewSize.Y;
                    if (float.IsNaN(viewH) || viewH < 300f || viewH > 20000f) viewH = 900f;
                    int idx = NPC.NewNPC(NPC.GetSource_FromAI(),
                        (int)player.Center.X, (int)(player.Center.Y - viewH * 0.4f),
                        ModContent.NPCType<BigDogFlySword>());
                    if (idx >= 0 && idx < Main.maxNPCs)
                        Main.npc[idx].netUpdate = true;
                }
                SetForm(false);       // 切到站立(空手)贴图
                NPC.localAI[0] = Main.rand.NextBool() ? -HoverDistance : HoverDistance;
                NPC.localAI[1] = Main.rand.Next(-(int)HoverRandomY, (int)HoverRandomY + 1);
                NPC.netUpdate = true;
            }

            // 安静悬停: 保持距离, 不做别的动作
            Vector2 hoverPoint = player.Center + new Vector2(NPC.localAI[0], NPC.localAI[1]);
            FlyToward(hoverPoint);
            FaceTowards(player.Center, TurnRate);

            // ── 音波对节拍 ──
            // 一对音波的生命周期 = BigDogEcho 的四段(接近/环绕 + 预瞄 + 蓄力 + 发射, 含第二只的延迟);
            // boss 按"周期 + 间隔"推进
            float echoTotal = Projectiles.BigDogEcho.TotalTime;
            float pairLen = echoTotal + SwordPairGap;
            if (t >= 2f)
            {
                float since = t - 2f;
                int pairIndex = (int)(since / pairLen);    // 第几对(0 起)
                bool pairStart = since % pairLen == 0f;

                if (pairStart && pairIndex < NPC.ai[3] && Main.netMode != NetmodeID.MultiplayerClient)
                {
                    // 每对的整体角度转一点, 让两只音波每次都从不同方位过来
                    float baseAngle = -2.5f + pairIndex * 0.35f;
                    SpawnEcho(player, baseAngle, 0);              // 第 1 只
                    SpawnEcho(player, baseAngle + 2.1f, 1);       // 第 2 只: 换半径/反向绕/晚开火
                    SoundEngine.PlaySound(SfxDash, NPC.Center);
                }

                // 全部对数发完 + 缓冲 → 收尾
                if (pairIndex >= NPC.ai[3] && since >= NPC.ai[3] * pairLen + 30f)
                {
                    if (Main.netMode != NetmodeID.MultiplayerClient)
                        DespawnFlySword();
                    SetForm(true);                          // 剑收回, 换回叼刀贴图
                    NPC.ai[0] = 3f;
                    NPC.ai[1] = 0f;
                    NPC.netUpdate = true;
                }
            }
        }

        // 吐出一只音波信标: 从大狗的嘴部飞出, 之后像飞行小怪一样追到玩家周围环绕。
        // θ = 它的出生方位(决定它从哪个方向进场); variant = 同批第几只(0/1):
        //   决定环半径(错开)、绕行方向(一顺一逆)、以及开火延迟(一先一后)
        private void SpawnEcho(Player player, float theta, int variant)
        {
            Vector2 mouth = MouthPosition();
            // 初速给一个"朝玩家 + 侧向散开"的方向: 两只同帧从同一个嘴飞出,
            // 不散开的话飞行段会完全重叠在一起
            Vector2 toPlayer = player.Center - mouth;
            if (toPlayer.LengthSquared() < 1f) toPlayer = new Vector2(NPC.direction, 0f);
            Vector2 dir = Vector2.Normalize(toPlayer).RotatedBy((variant == 0 ? -1f : 1f)
                * Main.rand.NextFloat(0.25f, 0.55f));

            int p = Projectile.NewProjectile(NPC.GetSource_FromAI(), mouth, dir * 6f,
                ModContent.ProjectileType<Projectiles.BigDogEcho>(), 65, 0f,
                Main.myPlayer, theta, 0f, variant);
            if (Main.projectile.IndexInRange(p))
                Main.projectile[p].netUpdate = true;
        }

        // 撤走场上所有大飞剑(一圈白粒子后消失)
        private void DespawnFlySword()
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC n = Main.npc[i];
                if (n.active && n.ModNPC is BigDogFlySword)
                {
                    for (int k = 0; k < 14; k++)
                    {
                        Dust d = Dust.NewDustDirect(n.position, n.width, n.height, DustID.Silver);
                        d.noGravity = true;
                        d.velocity = Main.rand.NextVector2Circular(4f, 4f);
                    }
                    n.active = false;
                    n.netUpdate = true;
                }
            }
        }

        // 朝目标点飞行: 位置完全由速度积分产生 —— 这是"自然"的根本,
        // 任何一帧直接改写 NPC.Center 都会表现为"被拽"。
        // tight=false: 巡航(大 slowRadius, 松的跟随), tight=true: 贴角(小 slowRadius, 收紧后几乎钉在点上,
        // 但玩家带着锚点移动时 boss 仍是平滑追过去, 不会跳)
        private void FlyToward(Vector2 target, bool tight = false)
        {
            Vector2 toTarget = target - NPC.Center;
            float dist = toTarget.Length();

            Vector2 desired = Vector2.Zero;
            if (dist > 1f)
            {
                desired = toTarget / dist * MoveSpeed;
                float slowRadius = tight ? 60f : 140f;
                if (dist < slowRadius) desired *= dist / slowRadius;
            }

            NPC.velocity = Vector2.Lerp(NPC.velocity, desired, MoveSmoothing);
        }

        // 虚影的二阶跟踪: 虚影不是 NPC, 没有引擎帮它积分速度, 所以自己维护一套
        // "速度 → 位置"的运动(ghostVel 每帧朝期望速度插值, 位置每帧由速度推进)。
        // 速度上限用 GhostMoveSpeed(比本体快得多), 加速也更利落 —— 分出来就要迅速就位
        private void MoveGhostToward(Vector2 target, bool tight)
        {
            Vector2 toTarget = target - ghostPos;
            float dist = toTarget.Length();

            Vector2 desired = Vector2.Zero;
            if (dist > 1f)
            {
                desired = toTarget / dist * GhostMoveSpeed;
                float slowRadius = tight ? 60f : 150f;
                if (dist < slowRadius) desired *= dist / slowRadius;
            }

            ghostVel = Vector2.Lerp(ghostVel, desired, tight ? 0.16f : 0.11f);
            ghostPos += ghostVel;
        }

        // ── 朝向 ──
        // "头指向" = rotation + (翻面 ? 0 : π)。翻面那一刻把 rotation 同步补上 π,
        // 头指向保持不变, 只有身体镜像 —— 否则翻面会被 AngleTowards 拖成一个大甩头。
        // 带滞回: 玩家停在正上/正下方时不会左右闪
        private void FaceAngle(float angle, float turnRate)
        {
            float cosAim = (float)Math.Cos(angle);
            bool wantRight = faceRight
                ? cosAim > -FlipHysteresis
                : cosAim > FlipHysteresis;

            if (!facingReady)
            {
                facingReady = true;
                faceRight = wantRight;
            }
            else if (wantRight != faceRight)
            {
                NPC.rotation = MathHelper.WrapAngle(
                    NPC.rotation + (wantRight ? MathHelper.Pi : -MathHelper.Pi));
                faceRight = wantRight;
            }

            NPC.direction = faceRight ? 1 : -1;

            float target = faceRight ? angle : MathHelper.WrapAngle(angle - MathHelper.Pi);
            NPC.rotation = AngleTowards(NPC.rotation, target, turnRate);
        }

        private void FaceTowards(Vector2 point, float turnRate)
        {
            FaceAngle((point - NPC.Center).ToRotation(), turnRate);
        }

        private static float AngleTowards(float current, float target, float maxChange)
        {
            float diff = MathHelper.WrapAngle(target - current);
            if (Math.Abs(diff) <= maxChange) return target;
            return current + Math.Sign(diff) * maxChange;
        }

        // 嘴部位置: 判定箱中心沿"头朝向"外探。预警线/发射点共用, 保证线指哪儿弹往哪儿飞
        private Vector2 MouthPosition()
        {
            Vector2 dir = new Vector2((float)Math.Cos(NPC.rotation), (float)Math.Sin(NPC.rotation));
            if (NPC.direction < 0) dir = -dir;
            return NPC.Center + dir * (NPC.width * 0.5f + 25f);
        }

        // ── 脱战 ──
        private int disengageTicks;

        private void Disengage()
        {
            NPC.velocity *= 0.92f;
            if (Main.rand.NextBool(2))
                Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);

            disengageTicks++;
            if (disengageTicks < DespawnFrames) return;

            for (int i = 0; i < 16; i++)
                Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);

            NPC.active = false;
            if (Main.netMode == NetmodeID.Server)
            {
                NPC.netSkip = -1;
                NPC.life = 0;
                NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, NPC.whoAmI);
            }
        }

        public override bool CheckActive() => false;

        private int CurrentFrameRate()
        {
            switch ((int)NPC.ai[0])
            {
                case 1: return FramesPerIndexWindup;
                case 2: return FramesPerIndexDash;
                case 3: return FramesPerIndexRecover;
                case 5: case 6: return FramesPerIndexOrb;
                default: return FramesPerIndexHover;
            }
        }

        public override void FindFrame(int frameHeight)
        {
            // 两套贴图帧数不同(叼刀 8 帧 / 站立 11 帧), 不能用引擎按 npcFrameCount 算的
            // frameHeight —— 自己按当前形态的贴图高度算
            int frames = FrameCount;
            if (frames <= 1)
            {
                NPC.frame.Y = 0;
                return;
            }

            int texH = (formBlade ? GetSkin() : GetStanding()).Value.Height;
            int fh = Math.Max(1, texH / frames);

            NPC.frameCounter++;
            if (NPC.frameCounter < CurrentFrameRate()) return;

            NPC.frameCounter = 0;
            int index = NPC.frame.Y / fh + 1;
            if (index >= frames) index = 0;
            NPC.frame.Y = index * fh;
        }

        // ── 绘制 ──
        // 预警线: 阿尔忒弥斯式的"闪断"线 —— 出现后快速淡入, 以 8Hz 高频闪烁, 消失的那一帧正好发射。
        // 长度必出屏(1700px)。本体线在发射帧消失; 虚影线多亮 GhostShotDelay 帧, 到虚影发射才消失。
        // 状态7(三段冲刺)的每段蓄力也复用这条线 —— 指向本段即将冲出去的方向(段内锁定, 不跟人转)
        private void DrawAimLines(SpriteBatch spriteBatch, Player player)
        {
            // ── 状态7: 每段的蓄力段画线 ──
            // 蓄力期间线实时跟着玩家转(预瞄), 冲出去那一刻才固定 —— 画的角度直接用
            // "嘴部指向玩家"的实时方向, 和 boss 头的朝向同步; 锁定后玩家看到的线就是实际弹道
            if (NPC.ai[0] == 7f)
            {
                float t = NPC.ai[1] - 1f;
                float segLen = TripleChargeFrames + TripleDashLength + TriplePauseFrames;
                float inSeg = t % segLen;
                if (inSeg < TripleChargeFrames)
                {
                    float progress = inSeg / TripleChargeFrames;
                    float fadein = MathHelper.Clamp(inSeg / 8f, 0f, 1f);
                    float blink = (Main.GlobalTimeWrappedHourly * 8f) % 1f < 0.5f ? 1f : 0.35f;
                    Color c = new Color(220, 220, 255) * (0.5f + 0.5f * progress) * fadein * blink;   // 白偏蓝, 和冲刺粒子同色系

                    // 蓄力中: 线 = 实时指向玩家(预瞄); 冲刺/急停段: 线已固定(不再画, 但保留计算避免边界闪线)
                    Vector2 aimDir = player.Center - MouthPosition();
                    if (aimDir.LengthSquared() < 1f) aimDir = new Vector2(NPC.direction, 0f);
                    aimDir = Vector2.Normalize(aimDir);
                    DrawSimpleLine(spriteBatch, MouthPosition() - Main.screenPosition, aimDir, AimLineLength, c, 3f);
                }
                return;
            }

            // ── 状态5: X 型双发 ──
            // 线的出现/消失直接跟分段状态走:
            //   分段1(虚影已分出、本体蓄力中) → 两条线都在
            //   分段2(本体已射、等虚影到位)   → 只剩虚影那条("本体已经打了, 虚影还没")
            if (NPC.ai[0] != 5f || xPhase == 0) return;

            // 阿尔忒弥斯的淡入: 前 8 帧从透明到全亮(GetLerpValue(0,8,timeLeft) 的等价)
            float xFadein = MathHelper.Clamp(xCharge / 8f, 0f, 1f);
            // 高频闪烁(约8Hz): 灾厄的线用着色器做流光, 这里用亮度方波近似"闪断"感
            float xBlink = (Main.GlobalTimeWrappedHourly * 8f) % 1f < 0.5f ? 1f : 0.35f;
            float alpha = xFadein * xBlink;

            // 本体线: 蓄力期间显示, 它发射(转入分段2)就消失
            if (xPhase == 1)
            {
                float progress = MathHelper.Clamp(xCharge / OrbWindup, 0f, 1f);
                Color lineColor = new Color(255, 90, 70) * (0.55f + 0.45f * progress) * alpha;
                Vector2 dirSelf = Vector2.Normalize(player.Center - MouthPosition());
                DrawSimpleLine(spriteBatch, MouthPosition() - Main.screenPosition, dirSelf, AimLineLength, lineColor, 3f);
            }

            // 虚影线: 从它的角落指向玩家, 一直画到它自己发射那一刻
            {
                Vector2 gp = GhostPosition(player);
                Vector2 dirGhost = Vector2.Normalize(player.Center - gp);
                DrawSimpleLine(spriteBatch, gp - Main.screenPosition, dirGhost, AimLineLength,
                    new Color(255, 90, 70) * 0.85f * alpha, 3f);
            }
        }

        private static void DrawSimpleLine(SpriteBatch spriteBatch, Vector2 start, Vector2 dir, float length, Color color, float width)
        {
            spriteBatch.Draw(
                TextureAssets.MagicPixel.Value,
                start,
                new Rectangle(0, 0, 1, 1),
                color,
                dir.ToRotation(),
                new Vector2(0f, 0.5f),
                new Vector2(length, width),
                SpriteEffects.None,
                0f
            );
        }

        public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            Player player = Main.player[NPC.target];

            // 预警线最底层
            DrawAimLines(spriteBatch, player);

            // 当前形态的贴图与帧(叼刀 8 帧 / 站立 11 帧, 两套独立文件)
            Texture2D tex = (formBlade ? GetSkin() : GetStanding()).Value;
            int frames = Math.Max(1, FrameCount);
            int frameHeight = Math.Max(1, tex.Height / frames);
            int index = Math.Clamp(NPC.frame.Y / frameHeight, 0, frames - 1);
            Rectangle frame = new Rectangle(0, index * frameHeight, tex.Width, frameHeight);
            Vector2 origin = new Vector2(frame.Width, frame.Height) / 2f;

            SpriteEffects flip = NPC.direction > 0
                ? SpriteEffects.FlipHorizontally
                : SpriteEffects.None;

            // ── 虚影: 半透明的本体, 在对角的屏角上 ──
            // 可见区间: X 型从"本体到位、虚影分出"(xPhase≥1)起, 到本拍结束; 合并滑行阶段也画。
            // 分段0(本体自己飞向屏角)时还没有虚影 —— 它是本体站好之后才分出来的
            bool ghostActive =
                (NPC.ai[0] == 5f && xPhase >= 1) ||
                (NPC.ai[0] == 1f && NPC.ai[1] <= 55f && ghostFlying);
            if (ghostActive)
            {
                Vector2 gp = GhostPosition(player);
                // 虚影面朝玩家: 按与玩家的水平关系定翻转(虚影不旋转, 始终水平朝向玩家)
                SpriteEffects ghostFlip = (player.Center.X > gp.X)
                    ? SpriteEffects.FlipHorizontally
                    : SpriteEffects.None;
                // 飞行中更淡(它是"刚分出去的"), 落位后呼吸式透明度
                float baseAlpha = ghostFlying ? 0.28f : 0.42f;
                float ghostAlpha = baseAlpha + 0.10f * (float)Math.Sin(Main.GlobalTimeWrappedHourly * 6f);
                spriteBatch.Draw(
                    tex,
                    gp - screenPos,
                    frame,
                    drawColor * ghostAlpha,
                    0f,
                    origin,
                    1f,
                    ghostFlip,
                    0f
                );
            }

            // ── 残影 ──
            bool dashing = NPC.ai[0] == 2f;
            int trailFrames = dashing ? NPC.oldPos.Length : Math.Min(TrailFramesNormal, NPC.oldPos.Length);
            float trailAlpha = dashing ? TrailAlphaDash : TrailAlphaNormal;
            Color ghostColor = new Color(255, 130, 100);

            for (int i = 1; i < trailFrames; i++)
            {
                if (NPC.oldPos[i] == Vector2.Zero) continue;
                float fade = 1f - i / (float)trailFrames;
                Vector2 ghostTrailPos = NPC.oldPos[i] + new Vector2(NPC.width, NPC.height) / 2f - screenPos;
                spriteBatch.Draw(
                    tex,
                    ghostTrailPos,
                    frame,
                    ghostColor * (fade * trailAlpha),
                    NPC.rotation,
                    origin,
                    1f,
                    flip,
                    0f
                );
            }

            // ── 本体 ──
            spriteBatch.Draw(
                tex,
                NPC.Center - screenPos,
                frame,
                drawColor,
                NPC.rotation,
                origin,
                1f,
                flip,
                0f
            );
            return false;
        }
    }
}
