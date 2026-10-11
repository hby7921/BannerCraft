param(
    [string]$GameRoot = (& (Join-Path $PSScriptRoot 'Get-LocalSetting.ps1') -Name bannerlordGameDir),
    [string]$ModDll = (Join-Path $PSScriptRoot '..\.build-game\stage\Modules\Mycraft\bin\Win64_Shipping_Client\Mycraft.v1.5.4.dll')
)
$ErrorActionPreference = 'Stop'
# Use the same CLR family as Bannerlord, in an isolated process; never invoke ControlTick.
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -GameRoot $GameRoot -ModDll $ModDll
    exit $LASTEXITCODE
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
public static class NativeInputPatchProbe {
    public static void Run(string gameRoot, string modDll) {
        var paths = new List<string> { Path.GetDirectoryName(modDll), Path.Combine(gameRoot,"bin","Win64_Shipping_Client") };
        foreach (var module in Directory.GetDirectories(Path.Combine(gameRoot,"Modules"))) {
            var bin=Path.Combine(module,"bin","Win64_Shipping_Client");
            if(Directory.Exists(bin)) paths.Add(bin);
        }
        AppDomain.CurrentDomain.AssemblyResolve += (sender,args) => {
            var name=new AssemblyName(args.Name).Name+".dll";
            foreach(var dir in paths) { var file=Path.Combine(dir,name); if(File.Exists(file))return Assembly.LoadFrom(file); }
            return null;
        };
        var harmonyAssembly=Assembly.LoadFrom(Path.Combine(gameRoot,"Modules","Bannerlord.Harmony","bin","Win64_Shipping_Client","0Harmony.dll"));
        var viewAssembly=Assembly.LoadFrom(Path.Combine(gameRoot,"Modules","Native","bin","Win64_Shipping_Client","TaleWorlds.MountAndBlade.View.dll"));
        var modAssembly=Assembly.LoadFrom(modDll);
        var original=viewAssembly.GetType("TaleWorlds.MountAndBlade.View.MissionViews.MissionMainAgentController",true).GetMethod("ControlTick",BindingFlags.Instance|BindingFlags.NonPublic);
        var patchType=modAssembly.GetType("Mycraft.Demo.MinecraftInventoryInputPatch",true);
        var harmonyType=harmonyAssembly.GetType("HarmonyLib.Harmony",true);
        var harmonyMethodType=harmonyAssembly.GetType("HarmonyLib.HarmonyMethod",true);
        var harmony=Activator.CreateInstance(harmonyType,new object[]{"mycraft.native.input.probe"});
        Func<string,object> patchMethod = name => Activator.CreateInstance(harmonyMethodType,new object[]{patchType.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)});
        var patch=harmonyType.GetMethods().Single(m=>m.Name=="Patch" && m.GetParameters().Length==5);
        Console.WriteLine("Target: "+original);
        Console.WriteLine("CLR: "+Environment.Version+"; EH clauses: "+original.GetMethodBody().ExceptionHandlingClauses.Count);
        patch.Invoke(harmony,new object[]{original,patchMethod("Prefix"),patchMethod("Postfix"),patchMethod("Transpiler"),null});
        Console.WriteLine("PASS: real staged Prefix/Postfix/Transpiler installed on actual game ControlTick; method not executed.");
        var missionAssembly=Assembly.LoadFrom(Path.Combine(gameRoot,"bin","Win64_Shipping_Client","TaleWorlds.MountAndBlade.dll"));
        var melee=missionAssembly.GetType("TaleWorlds.MountAndBlade.Mission",true).GetMethod("MeleeHitCallback",BindingFlags.Instance|BindingFlags.NonPublic);
        var shieldPatch=modAssembly.GetType("Mycraft.Demo.MinecraftShieldFeedbackPatch",true);
        var shieldPostfix=Activator.CreateInstance(harmonyMethodType,new object[]{shieldPatch.GetMethod("Postfix",BindingFlags.Static|BindingFlags.NonPublic)});
        patch.Invoke(harmony,new object[]{melee,null,shieldPostfix,null,null});
        Console.WriteLine("PASS: shield current-collision Postfix installed on actual game MeleeHitCallback; method not executed.");
        var handleBlow=missionAssembly.GetType("TaleWorlds.MountAndBlade.Agent",true).GetMethod("HandleBlow",BindingFlags.Instance|BindingFlags.NonPublic);
        var damagePatch=modAssembly.GetType("Mycraft.Demo.HandleBlowDamagePatch",true);
        var damagePrefix=Activator.CreateInstance(harmonyMethodType,new object[]{damagePatch.GetMethod("Prefix",BindingFlags.Static|BindingFlags.NonPublic)});
        patch.Invoke(harmony,new object[]{handleBlow,damagePrefix,null,null,null});
        Console.WriteLine("PASS: damage Prefix with collision context installed on actual game HandleBlow; method not executed.");
    }
}
'@
try {
    [NativeInputPatchProbe]::Run($GameRoot,$ModDll)
} catch {
    Write-Output $_.Exception.ToString()
    exit 1
}
