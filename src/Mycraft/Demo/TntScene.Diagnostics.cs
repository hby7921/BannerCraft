using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

namespace Mycraft.Demo;

public sealed partial class TntScene
{
    private GameEntity[]? _referenceSprites;
    internal void ShowReferenceSprites(float x,float y,float z,bool visible)
    {
        if(!Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--mycraft-tnt-scene-smoke"))return;
        if(!visible){if(_referenceSprites!=null)foreach(var entity in _referenceSprites)entity.SetVisibilityExcludeParents(false);return;}
        EnsureAssets();
        if(_referenceSprites==null)
        {
            _referenceSprites=new GameEntity[2];
            for(int i=0;i<2;i++)
            {
                var mesh=ParticleMesh(false,8,i==0?15:5);
                _referenceSprites[i]=Entity(mesh);
                DemoLog.Diagnostic("TNT_ALPHA_REFERENCE: index="+i+", fixedFrame=8, original opaque-pixel silhouette, shade="+(i==0?15:5));
            }
        }
        var camera=ScreenManager.TopScreen is MissionScreen screen?screen.CombatCamera.Frame:MatrixFrame.Identity;
        var rotation=Mat3.Identity;rotation.s=camera.rotation.s*1.4f;rotation.f=-camera.rotation.u*1.4f;rotation.u=camera.rotation.f*1.4f;
        for(int i=0;i<2;i++)
        {
            _referenceSprites[i].SetGlobalFrame(new MatrixFrame(rotation,World(x,y,z)+camera.rotation.s*(i==0?-1.2f:1.2f)));
            _referenceSprites[i].SetVisibilityExcludeParents(true);
        }
    }
    private void DisposeReferenceSprites()
    {
        if(_referenceSprites==null)return;
        foreach(var entity in _referenceSprites)entity.Remove(0);
        _referenceSprites=null;
    }
}
