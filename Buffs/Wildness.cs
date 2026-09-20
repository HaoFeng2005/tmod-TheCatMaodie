using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Buffs
{
    // "野性":被boss的冲刺(含飞扑)撞到才会获得,小怪的冲刺不会给。
    //   效果:最大生命 -10%、防御 -20%,但攻击力 +10%、移动速度 +10% —— 换血型减益
    //   持续20秒,同样不叠加(冲刺是boss的常用招,能刷新的话整场都甩不掉)
    public class Wildness : ModBuff
    {
        // 图标同样借用原版"恐惧"
        public override string Texture => "Terraria/Images/Buff_" + BuffID.Horrified;

        public override void SetStaticDefaults()
        {
            Main.debuff[Type] = true;
        }

        public override void Update(Player player, ref int buffIndex)
        {
            // 最大生命 -10%:改的是"当前生效的生命上限"(statLifeMax2 每帧都会按装备重算,
            // 所以在这里扣掉就能稳定生效,而且脱掉装备时不会算错)
            player.statLifeMax2 = (int)(player.statLifeMax2 * 0.9f);
            // 注意:减上限后当前血量可能超上限,这里顺手压一下,免得血条显示仍然是旧值
            if (player.statLife > player.statLifeMax2)
                player.statLife = player.statLifeMax2;

            // 防御 -20%:用整数运算,免得 statDefense 的整数/浮点类型差异导致编译不过
            player.statDefense -= (int)(player.statDefense * 0.2f);

            // 攻击力 +10%:和诅咒相反方向的同一个乘法修正
            player.GetDamage(DamageClass.Generic) *= 1.1f;

            // 移动速度 +10%:用的就是原版"敏捷"药水那条通道(+0.25 即 +25%)
            player.moveSpeed += 0.1f;
        }
    }
}
