using Terraria;
using Terraria.ModLoader;

namespace TheCatMaodie.SceneEffects
{
    // 大狗的战斗音乐。
    // 用 SceneEffect 而不是 NPC 的 Music 属性:Music 属性在boss一出现就生效,
    // 没法等"入场音频放完"。这里改成条件激活——大狗在场、并且入场音频已经放完才切音乐。
    //
    // 音乐文件放在 Sounds/Music/ 下面就够了:tModLoader 的约定是"名为 Music 的文件夹
    // (或其子文件夹)里的音频会被自动注册成音乐",所以不需要在 Load 里手动 AddMusic
    public class BigDogMusic : ModSceneEffect
    {
        // 一阶段战斗曲 = BigDogboss1.mp3。二阶段曲 BigDogboss2.mp3 也已经放在同一个 Music
        // 文件夹里备用,但现在故意不接——等二阶段做出来再改成"按血量在两首之间切换"。
        // 本模组的 boss 战曲目由 Ucchii0-うっちーゼロ 的 R.I.P / Reversal / The identity 剪辑整合,
        // 并按战斗阶段拆成两段(一阶段 BigDogboss1、二阶段 BigDogboss2)。
        // 作者站点:https://ucchii0artist.wixsite.com/ucchii0
        // 使用依据:按照作者站点规定使用;本模组免费发布、非商业盈利,仅供游玩与学习交流。
        // 完整来源见 description.txt。(音频文件名是 ASCII,作者信息只留在这段注释和
        // description.txt 里,别删)
        public override int Music => MusicLoader.GetMusicSlot("TheCatMaodie/Sounds/Music/BigDogboss1");

        // 压过原版的血月/事件音乐,免得打boss时被别的曲子顶掉
        public override SceneEffectPriority Priority => SceneEffectPriority.BossHigh;

        public override bool IsSceneEffectActive(Player player)
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc.active && npc.ModNPC is NPCs.BigDogBoss boss && boss.MusicReady)
                    return true;
            }
            return false;
        }
    }
}
