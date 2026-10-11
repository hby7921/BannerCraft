using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Mycraft.Patches;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace Mycraft.Demo;

// Only the active bridge player's input is filtered; ordinary soldiers keep native controls.
[HarmonyPatch(typeof(MissionMainAgentController),"ControlTick")]
internal static class MinecraftInventoryInputPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var key=generator.DeclareLocal(typeof(int));
        var filter=AccessTools.Method(typeof(MinecraftInventoryInputPatch),nameof(FilterNativeKey));
        var count=0;
        foreach(var instruction in instructions)
        {
            if(instruction.operand is MethodInfo method &&
                (method.Name=="IsGameKeyPressed" || method.Name=="IsGameKeyDown") &&
                method.ReturnType==typeof(bool) && method.GetParameters().Length==1 && method.GetParameters()[0].ParameterType==typeof(int))
            {
                // Preserve the exact original call, filtering its result inside ControlTick.
                // This also covers a native WieldNextWeapon wrapper that was JIT-inlined.
                var duplicate=new CodeInstruction(OpCodes.Dup);
                var filteredCall=new CodeInstruction(OpCodes.Call,filter);
                duplicate.labels.AddRange(instruction.labels);instruction.labels.Clear();
                foreach(var block in instruction.blocks)
                {
                    if(block.blockType==ExceptionBlockType.EndExceptionBlock)filteredCall.blocks.Add(block);
                    else duplicate.blocks.Add(block);
                }
                instruction.blocks.Clear();
                yield return duplicate;
                yield return new CodeInstruction(OpCodes.Stloc,key);
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Ldloc,key);
                yield return filteredCall;
                count++;
            }
            else yield return instruction;
        }
        if(count==0)throw new System.InvalidOperationException("Native ControlTick key call sites missing");
        MinecraftHeroState.Log("MC input filter installed at native ControlTick: "+count+" key reads");
    }

    private static bool FilterNativeKey(bool pressed,int key)
    {
        var bridge=DemoBridgeMissionBehavior.Current;
        if(bridge?.IsEngaged!=true)return pressed;
        if(key==9 && bridge.SuppressNativeAttack)return false;
        return key==10 || key==11 || key==12 || key==17 || key>=18 && key<=21 || key==23 ? false : pressed;
    }
    static bool Prefix()
    {
        var bridge=DemoBridgeMissionBehavior.Current;
        bridge?.HandleFeatureKeys();
        bridge?.ReadMinecraftInput();
        if(bridge?.IsEngaged!=true || !bridge.InventoryOpen)return true;
        var player=bridge.Player;
        if(player!=null)
        {
            player.EventControlFlags=0;
            player.MovementFlags=0;
            player.MovementInputVector=Vec2.Zero;
        }
        return false;
    }

    static void Postfix()
    {
        var bridge=DemoBridgeMissionBehavior.Current;
        if(bridge?.IsEngaged!=true || bridge.Player==null)return;
        bridge.Player.EventControlFlags &= ~(Agent.EventControlFlag.Wield0 | Agent.EventControlFlag.Wield1 |
            Agent.EventControlFlag.Wield2 | Agent.EventControlFlag.Wield3 | Agent.EventControlFlag.Sheath0 |
            Agent.EventControlFlag.Sheath1 | Agent.EventControlFlag.ToggleAlternativeWeapon);
        bridge.Player.MovementFlags &= ~(Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock);
        if(!bridge.SuppressNativeAttack)return;
        // Walking/jumping remain native; the targeted block owns the left button.
        bridge.Player.MovementFlags &= ~(Agent.MovementControlFlag.AttackMask |
            Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock);
    }
}
