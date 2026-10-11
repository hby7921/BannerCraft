using System;
using Mycraft.Demo;

namespace Mycraft.Patches;

internal static class MinecraftHeroState
{
    public static void Log(string message) => DemoLog.Info(message);
    public static void LogFailure(string where, Exception e) => DemoLog.Info(where + " failed: " + e);
}
