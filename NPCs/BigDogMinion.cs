using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.NPCs
{
    // 大狗boss召唤的小怪。
    // 它不重写任何行为——整个AI都是从 BigDogBoss 继承来的同一套代码,
    // 这里只覆盖基类那组"可调参数",把自己削弱成小怪。好处是以后调boss的手感时,
    // 小怪会自动跟着变,不会出现两套AI各自跑偏。
    //
    // 相对boss的差异:
    //   跑动速度、冲刺速度 → 砍一半
    //   跳跃高度 → 砍一半(高度 ∝ 初速²,所以初速乘 0.707,不是乘 0.5)
    //   激光占比更高(近身70% / 中距85%),激光伤害 14 → 8
    //   大跳不会越到玩家身后,而是差一段落在玩家身前
    //   大跳的触发距离也跟着射程缩到1/4(基类里自动按 JumpPower² 算)
    public class BigDogMinion : BigDogBoss
    {
        // 贴图:不写 Texture 就是默认路径 NPCs/BigDogMinion.png(现在是自己的美术,单帧,
        // 尺寸按"和原来借用的僵尸一样大"来做,所以判定箱/体积没变)。小狗暂时没有动作

        protected override float RunSpeedMax => 2.3f;        // 4.6 的一半
        protected override float JumpPower => 0.7071f;       // 初速×0.707 → 跳跃高度正好是一半
        protected override float DashSpeedBase => 6f;        // 12 的一半
        protected override float DashSpeedRage => 1.5f;      // 3 的一半
        protected override int SpitDamage => 8;              // boss 是 14
        protected override float SpitSpeed => 8f;            // boss 是 9:小怪也提速(原来只有7),但比boss慢一点
        protected override float SpitHoming => 0f;           // 小怪暂时不给追踪(boss 是 0.025)
        protected override float SpitGrowMax => 0f;          // 小怪的音波不变大(boss 是 1.3)
        // 扇形掷刃:小怪也用,但刀更少更慢更轻,免得三只小怪一起甩出和boss同级的火力
        protected override int BladeCount => 2;              // boss 是 9:小怪只甩两把
        protected override float BladeSpreadDeg => 10f;      // boss 是 120:小怪两把刃几乎平行甩出去
                                                             // (本来飞出去后距离就拉得很快,张角一大两把就分太开了)
        protected override float BladeSpeed => 12f;          // boss 是 18
        protected override int BladeDamage => 6;             // boss 是 12
        protected override bool ShowsBladeWarning => false;  // 小怪不画预警线(boss 才画)
        protected override bool DashAppliesDebuff => false;  // 小怪的冲刺不上"野性"
        protected override float BigJumpGap => -140f;        // 负值 = 提前140像素落地,停在玩家身前,不贴脸
        protected override float StillRangedResist => 1f;    // 关掉"站定远程减伤"——那是boss专属的特性
        protected override int FrameSourceNPC => -1;   // -1 = 用自己的整套帧(这里是单帧贴图);>=0 = 借原版图只画第1帧

        public override void SetStaticDefaults()
        {
            base.SetStaticDefaults();       // 残影缓存等设置照旧(基类里配的)
            Main.npcFrameCount[Type] = 1;   // 借来的原版贴图只画第1帧,不做帧动画
        }

        // 动作配比(20份制):激光占比比boss更高
        protected override int WeightPounceJump => 4;        // 近身:小跳 20%
        protected override int WeightNearDash => 2;          // 近身:冲刺 10%(其余70%是激光)
        protected override int WeightMidDash => 3;           // 中距:冲刺 15%(其余85%是激光)

        // 小怪不再召唤小怪
        protected override bool SummonsMinions => false;

        // 帧数由基类统一设成1(只画贴图第1帧),这里不用重复

        // 小怪必须显式把自己的boss头像槽设成 -1(无):
        // 地图绘制那段是遍历"所有NPC"、只看槽号是不是 -1 的(代码里没有 boss 过滤),
        // 所以继承了基类的克眼头像,会让地图上每只小怪都冒出一个眼球图标
        public override void BossHeadSlot(ref int index)
        {
            index = -1;
        }

        public override void SetDefaults()
        {
            NPC.width = 34;                 // 和借来的僵尸贴图差不多大
            NPC.height = 42;
            NPC.damage = 16;                // 接触伤害(boss 是 22)
            NPC.defense = 15;
            NPC.lifeMax = 350;
            NPC.knockBackResist = 0.6f;     // 抗击退60%
            NPC.aiStyle = -1;               // 和boss一样,AI 全自己写(继承来的那套)
            NPC.boss = false;               // 小怪不显示boss血条
            NPC.HitSound = SoundID.NPCHit1;
            NPC.DeathSound = SoundID.NPCDeath1;
        }
    }
}
