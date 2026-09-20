using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TheCatMaodie.Buffs
{
    // 被boss的音波命中后获得的减益(小怪的音波不会给):
    //   效果:攻击力下降10%,持续20秒
    //   不叠加:再次被音波命中不会刷新/延长持续时间,而是改为"招来一只小狗"
    //   (如果让 AddBuff 直接叠加,20秒的减益会被连续命中无限续期,变成滚雪球)
    public class BigDogCurse : ModBuff
    {
        // 图标借用原版"恐惧"(肉山给玩家的那个,BuffID.Horrified = 37)
        public override string Texture => "Terraria/Images/Buff_" + BuffID.Horrified;

        public override void SetStaticDefaults()
        {
            Main.debuff[Type] = true;   // 减益:红色边框、死亡时清除、护士可解
        }

        // 持续期间:玩家所有伤害 ×0.9。
        // 用乘法修正而不是"减0.1个百分点",这样不管玩家身上有多少加成,
        // 最终伤害都正好是原来的90%(加成多的时候百分点的效果会失真)
        public override void Update(Player player, ref int buffIndex)
        {
            player.GetDamage(DamageClass.Generic) *= 0.9f;
        }
    }
}
