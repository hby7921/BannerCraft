using System.Collections.Generic;
using TaleWorlds.Library;

namespace Mycraft.Demo;

public static class BannerCraftConsoleCommands
{
    [CommandLineFunctionality.CommandLineArgumentFunction("summon","mycraft")]
    public static string Summon(List<string> arguments)
    {
        if(arguments.Count!=1)return "用法：mycraft.summon wither";
        var bridge=DemoBridgeMissionBehavior.Current;
        return bridge==null?"请先进入 BannerCraft 自定义野战并连接 MC。":bridge.RequestMobSummon(arguments[0]);
    }
}
