using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using ReLogic.Content;

namespace TheCatMaodie.Projectiles
{
    // 一条弧线弹幕:同一个类通过 ai[0] (0/1/2) 决定画哪一张弧线贴图,
    // bigdog 一次开火生成三条,每条都是独立判定的弹幕
    public class BigDogShot : ModProjectile
    {
        // 弧线被实心方块遮挡超过这个比例才消失(0.6 = 大半被吞才死,仿泰拉刃剑气的穿行手感)
        private const float BlockedKillRatio = 0.6f;

        private Asset<Texture2D>[] arcs;

        // 第一次用到时才加载贴图(不依赖 Load 钩子的调用时序,避免缓存为空导致空引用)
        private Asset<Texture2D> GetArc(int i)
        {
            if (arcs == null)
            {
                arcs = new Asset<Texture2D>[3];
                for (int k = 0; k < 3; k++)
                    arcs[k] = ModContent.Request<Texture2D>($"TheCatMaodie/Projectiles/BigDogShot_{k}", AssetRequestMode.ImmediateLoad);
            }
            return arcs[i];
        }

        // 默认贴图路径必须指向一个真实存在的文件,否则加载报 MissingResourceException
        public override string Texture => "TheCatMaodie/Projectiles/BigDogShot_0";

        public override void SetDefaults()
        {
            Projectile.friendly = true;    // 对敌人造成伤害
            Projectile.hostile = false;
            Projectile.DamageType = DamageClass.Ranged; // 吃远程加成

            Projectile.penetrate = 1;      // 每条弧线打中1个敌人就消失
            Projectile.timeLeft = 120;     // 最多存在2秒
            Projectile.light = 0.3f;
            Projectile.tileCollide = false; // 关掉"碰一格方块就死",改用下面 AI 里的遮挡比例检测

            // 三弧齐射的关键:用"弹幕自己的"无敌计时,而不是全玩家共享的那套。
            // 否则同一轮里第2、3条弧会被前一条打出的无敌帧挡掉(表现为穿过敌人却不造成伤害)
            Projectile.usesLocalNPCImmunity = true;
            Projectile.localNPCHitCooldown = 1;
        }

        // 每帧调用。便签分工(这个版本弹幕 ai 只有3格: ai[0] ai[1] ai[2],没有第4格):
        // ai[0]=画哪张弧线(0/1/2)  ai[1]=出场倒计时(>0时原地待命)
        // localAI[0]=判定箱已调整的标记
        public override void AI()
        {
            // 出场延迟:三弧依次射出,视觉上是"爪、爪、爪"三连挥
            if (Projectile.ai[1] > 0f)
            {
                Projectile.ai[1]--;
                if (Projectile.ai[1] > 0f)
                {
                    Projectile.friendly = false;   // 待命期间不伤人,免得蹲在枪口阴到贴脸的怪
                    // 冻结技巧:AI先跑、引擎后把velocity加进position,这里先减掉一步位移,
                    // 引擎再加回去,净位移为零——速度全程没动过,起飞时自然带着原初速
                    Projectile.position -= Projectile.velocity;
                }
                else
                {
                    Projectile.friendly = true;    // 起飞:必须恢复伤害判定,否则弹幕会无视敌人穿过去
                }
            }

            if (Projectile.localAI[0] == 0f)
            {
                Projectile.localAI[0] = 1f;
                Texture2D tex = GetArc((int)Projectile.ai[0]).Value;
                Projectile.Resize(tex.Width, tex.Height); // 判定箱匹配弧线大小
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

            // 让弧线朝飞行方向(贴图里的弧线本来就是斜着的,看起来像挥爪)
            Projectile.rotation = Projectile.velocity.ToRotation();

            // 轻微绿色拖尾,呼应腐蚀主题(等待出场期间不撒)
            if (Projectile.ai[1] <= 0f && Main.rand.NextBool(3))
            {
                Dust trail = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.GreenFairy);
                trail.noGravity = true;
                trail.velocity *= 0.2f;
                trail.scale = 0.9f;
            }
        }

        // 自己画贴图:按 ai[0] 选三张弧线之一,中心对齐、随 rotation 旋转
        public override bool PreDraw(ref Color lightColor)
        {
            if (Projectile.ai[1] > 0f) return false;   // 还在等待出场,不画(隐身待命)

            Texture2D tex = GetArc((int)Projectile.ai[0]).Value;
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
            return false; // 拦掉默认绘制(默认画的是 _0,我们需要按 ai[0] 换图)
        }

        // 消失时炸开一圈绿色粒子
        public override void OnKill(int timeLeft)
        {
            for (int i = 0; i < 10; i++)
            {
                Dust burst = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.GreenFairy);
                burst.noGravity = true;
                burst.velocity = Main.rand.NextVector2Circular(4f, 4f);
                burst.scale = 1.2f;
            }
        }
    }
}
