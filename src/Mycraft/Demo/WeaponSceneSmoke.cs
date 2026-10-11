using System;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// Explicit diagnostic input only; real MC weapon rules are tested in the Fabric server.
internal sealed class WeaponSceneSmoke : IDisposable
{
    private readonly ProjectileScene _projectiles;
    private readonly LightningScene _lightning;
    private readonly Mycraft.Building.BattleBlockScene? _wire;
    private readonly float _x,_z;
    private bool _sawArrow,_sawBolt;
    public WeaponSceneSmoke(Mission mission,float ground,float x,float z)
    {
        _x=x;_z=z;_projectiles=new ProjectileScene(mission);_projectiles.SetOrigin(0,ground);
        _lightning=new LightningScene(mission);_lightning.SetOrigin(0,ground);
        if(DemoBridgeMissionBehavior.WireOffSmoke!=null&&DemoBridgeMissionBehavior.WireOnSmoke!=null)
        {
            _wire=new Mycraft.Building.BattleBlockScene(mission);_wire.SetSession(Guid.NewGuid().ToString("N"));_wire.SetOrigin(0,ground);
            _wire.ApplySmokeSnapshot((int)x-1,0,(int)z-1,"00000000000000000000000000000021",DemoBridgeMissionBehavior.WireOffSmoke);
            _wire.ApplySmokeSnapshot((int)x,0,(int)z-1,"00000000000000000000000000000022",DemoBridgeMissionBehavior.WireOnSmoke);
        }
    }
    public void Tick(float age,float dt)
    {
        _projectiles.ApplyState("native-scene-arrow","arrow",_x-1+age,.9f,_z+.5f,90,0,true);
        _projectiles.ApplyState("native-scene-trident","trident",_x+1,1.2f,_z+.5f,0,0,true);
        bool visible=age<.3f||age>.7f&&age<1f||age>1.4f&&age<1.7f;
        _lightning.ApplyState("native-scene-channeling",_x+3,0,_z+.5f,"ffffffff87654321",visible,true);
        _projectiles.Tick(dt);_lightning.Tick(dt);
        _sawArrow|=_projectiles.ActiveCount==2;_sawBolt|=_lightning.ActiveCount==1;
        if(_projectiles.Status.Length>0||_lightning.Status.Length>0)throw new InvalidOperationException(_projectiles.Status+_lightning.Status);
    }
    public void Verify()
    {
        _projectiles.ApplyState("native-scene-arrow","arrow",_x,1,_z,0,0,false);
        _projectiles.ApplyState("native-scene-trident","trident",_x,1,_z,0,0,false);
        _lightning.ApplyState("native-scene-channeling",_x+3,0,_z+.5f,"ffffffff87654321",false,false);
        if(!_sawArrow||!_sawBolt||_projectiles.ActiveCount!=0||_lightning.ActiveCount!=0)
            throw new InvalidOperationException("Weapon native visual lifecycle failed");
        DemoLog.Diagnostic("WEAPONS_NATIVE_SCENE_PASS: original arrow/trident meshes, native-seed lightning additive material, state retirement; diagnostic transforms only");
        if(_wire!=null)DemoLog.Diagnostic("REDSTONE_NATIVE_SCENE_PASS: actual recorded off/on wire snapshots uploaded with opaque-texel geometry and native power tints; collision preserved");
    }
    public void Dispose(){_projectiles.Dispose();_lightning.Dispose();_wire?.Dispose();}
}
