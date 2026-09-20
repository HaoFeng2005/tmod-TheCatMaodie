using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.NPCs
{
    // 大飞剑:二阶段大狗"剑模式"开头召唤的分身。
    // 只在玩家上方的屏幕区域活动(悬停 + 缓慢左右游走), 自己按节拍降下剑雨:
    //   每隔一阵, 在玩家附近挑 3~5 个 x 位置, 各生成一把竖直落下的飞剑(带竖直预警线)。
    // 它不可被伤害(是个"装置"而不是怪), 生命周期由 boss 管理 —— boss 的剑模式结束时把它撤走;
    // 另外玩家死亡时自己也会散掉, 不留场。
    public class BigDogFlySword : ModNPC
    {
        public override string Texture => "Terraria/Images/Item_" + ItemID.EnchantedSword;   // 占位: 附魔剑贴图

        // ── 可调参数 ──
        protected virtual float RainInterval => 170f;   // 每隔多少帧降一轮剑雨
        protected virtual int SwordsPerRain => 4;       // 每轮几把(实际 = 3~这个值随机)
        protected virtual float RainSpread => 240f;     // 剑落点在玩家水平方向的散布半径
        protected virtual float LeaveFrames => 4200f;   // 兜底寿命(70秒, 正常由 boss 撤走)

        public override void SetStaticDefaults()
        {
            // 显示名配置在 Localization/en-US_Mods.TheCatMaodie.hjson
            Main.npcFrameCount[Type] = 1;    // 单帧贴图
        }

        public override void SetDefaults()
        {
            NPC.width = 30;
            NPC.height = 30;
            NPC.scale = 1.8f;                 // 贴图是物品尺寸, 放大到"大飞剑"的体积感
            NPC.lifeMax = 1;
            NPC.dontTakeDamage = true;        // 装置, 不可伤害
            NPC.knockBackResist = 0f;
            NPC.aiStyle = -1;
            NPC.noGravity = true;
            NPC.noTileCollide = true;
            NPC.npcSlots = 0f;                // 不占刷怪上限
            NPC.friendly = false;
        }

        public override bool CheckActive() => false;   // 不因距离而消失(由 boss / 自己的寿命管理)

        public override void AI()
        {
            NPC.localAI[0]++;                     // 寿命计时
            NPC.TargetClosest(false);

            if (NPC.target < 0 || NPC.target >= Main.maxPlayers)
            {
                Leave();
                return;
            }
            Player player = Main.player[NPC.target];
            if (!player.active || player.dead || NPC.localAI[0] > LeaveFrames)
            {
                Leave();
                return;
            }

            // ── 悬停在玩家上方屏幕区域, 缓慢左右游走 ──
            float viewH = Main.ViewSize.Y;
            if (float.IsNaN(viewH) || viewH < 300f || viewH > 20000f) viewH = 900f;
            float drift = (float)Math.Sin(NPC.localAI[0] * 0.008f) * 200f;   // 慢速游走

            Vector2 target = player.Center + new Vector2(drift, -viewH * 0.34f);
            Vector2 toTarget = target - NPC.Center;
            float dist = toTarget.Length();
            Vector2 desired = Vector2.Zero;
            if (dist > 1f)
            {
                desired = toTarget / dist * 6.5f;
                if (dist < 120f) desired *= dist / 120f;
            }
            NPC.velocity = Vector2.Lerp(NPC.velocity, desired, 0.06f);

            // 剑尖朝下, 轻微摆动
            NPC.rotation = MathHelper.Pi + (float)Math.Sin(NPC.localAI[0] * 0.05f) * 0.18f;

            // ── 剑雨节拍 ──
            NPC.localAI[1]++;
            if (NPC.localAI[1] >= RainInterval)
            {
                NPC.localAI[1] = 0f;
                DropRain(player);
            }
        }

        // 一轮剑雨: 在玩家附近挑几个 x 位置(其中一个尽量正对玩家), 生成预警+落剑
        private void DropRain(Player player)
        {
            if (Main.netMode == NetmodeID.MultiplayerClient) return;   // 生成只在服务器/单机

            int count = Main.rand.Next(3, SwordsPerRain + 1);
            for (int i = 0; i < count; i++)
            {
                // 第 0 把尽量正对玩家, 其余散布; 有一把稍偏远, 逼玩家不能只盯脚下
                float x = (i == 0)
                    ? player.Center.X + Main.rand.NextFloat(-30f, 30f)
                    : player.Center.X + Main.rand.NextFloat(-RainSpread, RainSpread);

                float viewH = Main.ViewSize.Y;
                if (float.IsNaN(viewH) || viewH < 300f || viewH > 20000f) viewH = 900f;
                Vector2 spawn = new Vector2(x, player.Center.Y - viewH * 0.62f);   // 屏幕上方之外

                // ★ 必须把预警帧数塞进 ai[0]: 弹幕的预警倒计时用的就是它,
                //    漏传的话 ai[0] 出生是 0, 第一帧就开始下落, 预警线永远不会出现(踩过的坑)
                int p = Projectile.NewProjectile(NPC.GetSource_FromAI(), spawn, Vector2.Zero,
                    ModContent.ProjectileType<Projectiles.BigDogRainSword>(), 55, 0f,
                    Main.myPlayer, Projectiles.BigDogRainSword.TelegraphFrames, 0f);
                if (Main.projectile.IndexInRange(p))
                    Main.projectile[p].netUpdate = true;
            }

            // 出手提示音(轻): 用现成的item音效占位
            if (Main.netMode != NetmodeID.Server)
                SoundEngine.PlaySound(SoundID.Item32 with { Volume = 0.6f, Pitch = -0.3f }, NPC.Center);
        }

        // 离场: 一圈白粒子后消失
        private void Leave()
        {
            for (int i = 0; i < 14; i++)
            {
                Dust d = Dust.NewDustDirect(NPC.position, NPC.width, NPC.height, DustID.Silver);
                d.noGravity = true;
                d.velocity = Main.rand.NextVector2Circular(4f, 4f);
            }
            NPC.active = false;
            NPC.netUpdate = true;
        }
    }
}
