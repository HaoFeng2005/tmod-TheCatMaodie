using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TheCatMaodie.NPCs;
using ReLogic.Content;

namespace TheCatMaodie.Projectiles
{
    // 敌对版小弧弹幕:boss和小怪"喷吐"用的弹幕,复用大狗武器0号弧(最小那道)的贴图。
    // 注意它不是武器的三连爪击——每次喷吐只发一个这样的小弧,连发节奏由boss的AI控制。
    // 和 BigDogShot 的区别:敌对(打玩家)、无出场延迟、单发直飞;遮挡判定和拖尾沿用同一套手感
    public class BigDogSpit : ModProjectile
    {
        private const float BlockedKillRatio = 0.6f;   // 被实心方块遮挡超过这个比例才消失(和武器版一致)

        // 贴图:默认路径就是 Projectiles/BigDogSpit.png(现在放的是"声波环"那张美术),
        // 所以不用再写 Texture 覆盖。想换图直接替换那个文件即可
        private Asset<Texture2D> arc;   // 贴图缓存(第一次用到时才加载)

        private Asset<Texture2D> GetArc()
        {
            if (arc == null)
                arc = ModContent.Request<Texture2D>(Texture, AssetRequestMode.ImmediateLoad);
            return arc;
        }

        public override void SetDefaults()
        {
            Projectile.friendly = false;   // 敌对弹幕:打玩家,不打NPC(所以boss和小怪不会被自己的弹幕咬到)
            Projectile.hostile = true;
            Projectile.DamageType = DamageClass.Default;   // 原版敌对弹幕的通用伤害类

            Projectile.penetrate = 1;      // 打中1个玩家就消失
            Projectile.timeLeft = 180;     // 最多3秒(足够飞过半个屏幕)
            Projectile.light = 0.3f;
            Projectile.tileCollide = false; // 不用"碰一格方块就死",改用AI里的遮挡比例检测
        }

        // 每帧调用。便签分工(弹幕 ai 只有3格: ai[0] ai[1] ai[2]):
        //   ai[0] = 追踪强度(每帧朝玩家偏转的插值系数;0 = 不追踪)
        //   ai[1] = 追踪目标的玩家序号(由boss锁定玩家的编号传进来)
        //   ai[2] = 最大放大倍率(由发射方传入;0 = 不变大——小怪的弹幕就是0)
        //   localAI[1] = 存活帧数(用来算长大进度)
        private const float GrowFrames = 45f;      // 从小到大用多少帧(约0.75秒)
        private const float StartScale = 0.55f;    // 刚出口时的大小(相对贴图)

        public override void AI()
        {
            Texture2D tex = GetArc().Value;

            // 变大:发射方用 ai[2] 传最大倍率。判定箱每帧跟着贴图一起调,
            // 不然会出现"看着变大了却打不中"(放大只改画面不改判定是最容易犯的错)
            float growMax = Projectile.ai[2];
            if (growMax > 0f)
            {
                Projectile.localAI[1]++;
                float t = MathHelper.Clamp(Projectile.localAI[1] / GrowFrames, 0f, 1f);
                Projectile.scale = MathHelper.Lerp(StartScale, growMax, t);
            }

            int hitW = Math.Max(1, (int)(tex.Width * Projectile.scale));
            int hitH = Math.Max(1, (int)(tex.Height * Projectile.scale));
            if (Projectile.width != hitW || Projectile.height != hitH)
                Projectile.Resize(hitW, hitH);

            // 微弱追踪(仿骷髅王二阶段的骷髅头):每帧把速度方向朝目标玩家偏一点点。
            // 关键:插值后必须重新归一化再乘回原速——否则 Vector2.Lerp 会把速度越拖越小
            // (当目标方向与当前方向相反时,两个向量插值的结果接近零向量,弹幕会自己停住)
            float homing = Projectile.ai[0];
            int targetIndex = (int)Projectile.ai[1];
            if (homing > 0f && targetIndex >= 0 && targetIndex < Main.maxPlayers)
            {
                Player target = Main.player[targetIndex];
                if (target.active && !target.dead)
                {
                    Vector2 toTarget = target.Center - Projectile.Center;
                    if (toTarget.LengthSquared() > 0.01f)   // 防止目标跟自己重合时归一化出 NaN
                    {
                        float speed = Projectile.velocity.Length();
                        Vector2 desired = Vector2.Normalize(toTarget) * speed;
                        Vector2 curved = Vector2.Lerp(Projectile.velocity, desired, homing);
                        if (curved.LengthSquared() > 0.01f)
                            Projectile.velocity = Vector2.Normalize(curved) * speed;
                    }
                }
            }

            // 遮挡检测:统计判定箱压住的实心方块比例,大半被方块吞掉才消失
            // (平台、树、背景墙都不算数,只有真正的实心方块才算)
            Rectangle box = Projectile.Hitbox;
            int solidCount = 0, totalCount = 0;
            for (int x = box.Left / 16; x <= box.Right / 16; x++)
            {
                for (int y = box.Top / 16; y <= box.Bottom / 16; y++)
                {
                    if (!WorldGen.InWorld(x, y, 10)) continue;
                    totalCount++;
                    Tile t = Main.tile[x, y];
                    if (t.HasTile && Main.tileSolid[t.TileType]) solidCount++;
                }
            }
            if (totalCount > 0 && solidCount >= totalCount * BlockedKillRatio)
            {
                Projectile.Kill();
                return;
            }

            // 朝飞行方向(贴图里的弧线本来就是斜着的,看起来像挥爪,和武器版同款姿态)
            Projectile.rotation = Projectile.velocity.ToRotation();

            // 绿色拖尾,和武器弧线同款
            if (Main.rand.NextBool(3))
            {
                Dust trail = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.GreenFairy);
                trail.noGravity = true;
                trail.velocity *= 0.2f;
                trail.scale = 0.9f;
            }
        }

        // 自己画贴图:中心对齐、随 rotation 旋转(和武器版同一个画法)
        public override bool PreDraw(ref Color lightColor)
        {
            Texture2D tex = GetArc().Value;
            Vector2 drawPos = Projectile.Center - Main.screenPosition;
            Main.spriteBatch.Draw(
                tex,
                drawPos,
                null,
                lightColor,
                Projectile.rotation,
                tex.Size() / 2f,   // 以贴图中心为旋转轴
                Projectile.scale,
                SpriteEffects.None,
                0f
            );
            return false;   // 拦掉默认绘制
        }

        // 命中玩家:
        //   第一次 → 上"大狗的诅咒"(攻击力-10%,20秒)
        //   已经有诅咒时再被打中 → 不刷新时间(否则会滚雪球),改成招一只小狗
        // 只有boss的音波有效果:发射方用 ai[2] 的正负号标记(boss传正数;小怪传0)。
        // 这样同一个弹幕类不需要额外占便签位就能区分两种来源,而且 ai 是同步的,联机不会错
        public override void OnHitPlayer(Player target, Player.HurtInfo info)
        {
            if (Projectile.ai[2] <= 0f) return;   // 小怪的音波:没有任何附加效果

            int curse = ModContent.BuffType<Buffs.BigDogCurse>();

            if (target.HasBuff(curse))
            {
                // 已经不是第一次了 → 招狗,但绝对不延长减益时间。
                // 位置和boss的召唤波一样:从玩家屏幕两侧的边缘外走进来
                BigDogBoss.PlayDogSound(target.Center);   // 各客户端都播,所以放在联机判断外面
                if (Main.netMode != NetmodeID.MultiplayerClient)
                {
                    BigDogBoss.SpawnMinionAt(
                        Projectile.GetSource_OnHit(target),
                        new Vector2(BigDogBoss.ScreenEdgeX(target, Main.rand.NextBool()), target.Center.Y),
                        ModContent.NPCType<NPCs.BigDogMinion>());
                }
                BigDogBoss.SayLineFromAnyBoss("SpitHitCursed");   // "它们来了"
                return;
            }

            target.AddBuff(curse, 20 * 60);   // 20秒
            BigDogBoss.SayLineFromAnyBoss("SpitHitPlain");   // "犬吠..."
        }

        // 消失(打中玩家 / 扎进方块 / 超时)时溅一圈绿色粒子
        public override void OnKill(int timeLeft)
        {
            for (int i = 0; i < 8; i++)
            {
                Dust burst = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.GreenFairy);
                burst.noGravity = true;
                burst.velocity = Main.rand.NextVector2Circular(4f, 4f);
                burst.scale = 1.2f;
            }
        }
    }
}
