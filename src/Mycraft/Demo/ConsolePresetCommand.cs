using System.Collections.Generic;
using TaleWorlds.Library;

namespace Mycraft.Demo;

public static class ConsolePresetCommand
{
    [CommandLineFunctionality.CommandLineArgumentFunction("fians_wither", "mycraft")]
    public static string StartFiansWither(List<string> arguments) => arguments.Count == 0
        ? FiansWitherPresetLaunch.Request()
        : arguments.Count==1 && int.TryParse(arguments[0],out var count)
            ? FiansWitherPresetLaunch.Request(count)
            : arguments.Count==2 && int.TryParse(arguments[0],out var withers) && int.TryParse(arguments[1],out var fians)
                ? FiansWitherPresetLaunch.Request(withers,fians)
                : "用法：mycraft.fians_wither [凋零数量 1-16] [巴弓数量 1-600]";
}
