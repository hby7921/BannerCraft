using System;

using Mycraft.Patches;
using TaleWorlds.GauntletUI.BaseTypes;

using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace Mycraft.UI;

/// <summary>
/// Shows the real MC hotbar only while the bridge owns the player. Renders only; never
/// takes focus, so all input keeps reaching the native mission screen.
/// </summary>
public sealed class MinecraftHotbarMissionBehavior : MissionBehavior
{
    public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

    private const string LayerName = "Mycraft.Hotbar";
    private const string MovieName = "MCHotbar";

    private MinecraftHotbarVM? _vm;
    private GauntletLayer? _layer;
    private GauntletMovieIdentifier? _movie;
    private ScreenBase? _ownerScreen;
    private bool _movieLoaded;
    private bool _layerAdded;
    private bool _setupFailed;
    private Widget? _nativeHealth;
    private bool _nativeHealthVisible;
    private bool _missingNativeStatusLogged;
    internal bool IsHotbarVisible => _vm?.IsVisible==true;
    internal bool HasNativeBossBar(float expectedWidth)=>_vm?.Bosses.Count==1 &&
        _layer?.UIContext?.Root.FindChild("McBossClip",true) is Widget clip &&
        clip.IsVisible && Math.Abs(clip.SuggestedWidth-expectedWidth)<.01f;

    public override void OnMissionTick(float dt)
    {
        var demo = Mycraft.Demo.DemoBridgeMissionBehavior.Current;
        var agent = Mission.MainAgent;
        if (agent == null || !agent.IsActive() || demo?.OwnsPlayer(agent)!=true)
        {
            _vm?.Hide();
            RestoreNativeHealth();
            return;
        }
        EnsureLayer();
        _vm?.Refresh(agent);
    }

    public override void OnPreDisplayMissionTick(float dt)
    {
        var bridge=Mycraft.Demo.DemoBridgeMissionBehavior.Current;
        var hide=bridge?.IsEngaged==true && bridge.IsConnected && _layerAdded && _vm?.IsVisible==true;
        if(!hide) { RestoreNativeHealth(); return; }
        if(_nativeHealth==null && _ownerScreen!=null)
        {
            foreach(var screenLayer in _ownerScreen.Layers)
            {
                if(!(screenLayer is GauntletLayer native) || ReferenceEquals(native,_layer) || native.UIContext==null)continue;
                var band=native.UIContext.Root.GetFirstInChildrenAndThisRecursive(
                    widget=>widget.VisualDefinition?.Name=="AgentStatus");
                if(band==null)
                {
                    var health=native.UIContext.Root.FindChild("HeroHealthWidget",true);
                    band=health?.ParentWidget ?? health;
                }
                if(band==null)continue;
                _nativeHealth=band;
                _nativeHealthVisible=band.IsVisible;
                MinecraftHeroState.Log("native health/equipment status band hidden while MC owns life");
                break;
            }
        }
        if(_nativeHealth!=null)_nativeHealth.IsVisible=false;
        else if(!_missingNativeStatusLogged)
        {
            _missingNativeStatusLogged=true;
            MinecraftHeroState.Log("native AgentStatus node not yet found; retrying each display tick");
        }
    }

    private void RestoreNativeHealth()
    {
        if(_nativeHealth==null)return;
        _nativeHealth.IsVisible=_nativeHealthVisible;
        _nativeHealth=null;
    }
    public override void OnRemoveBehavior()
    {
        Teardown();
        base.OnRemoveBehavior();
    }

    private void EnsureLayer()
    {
        var screen = ScreenManager.TopScreen;
        if (_layer != null || screen == null || _setupFailed)
            return;
        try
        {
            MinecraftHudSprites.EnsureLoaded();
            _vm = new MinecraftHotbarVM();
            // A HUD overlays the mission; clearing this view erases the scene below it.
            _layer = new GauntletLayer(LayerName, 100, shouldClear: false);
            _movie = _layer.LoadMovie(MovieName, _vm);
            _movieLoaded = true;
            _ownerScreen = screen;
            screen.AddLayer(_layer);
            _layerAdded = true;
            MinecraftHeroState.Log("hotbar layer attached");
        }
        catch (Exception e)
        {
            MinecraftHeroState.LogFailure("hotbar layer setup", e);
            _setupFailed = true;
            Teardown();
        }
    }

    private void Teardown()
    {
        RestoreNativeHealth();
        if (_layer == null)
            return;
        var layer = _layer;
        var vm = _vm;
        var owner = _ownerScreen;
        _layer = null;
        _vm = null;
        try
        {
            if (_movieLoaded && _movie != null) layer.ReleaseMovie(_movie);
        }
        catch (Exception e)
        {
            MinecraftHeroState.LogFailure("hotbar movie release", e);
        }
        try
        {
            if (owner != null && (_layerAdded || owner.HasLayer(layer)))
                owner.RemoveLayer(layer);
        }
        catch (Exception e)
        {
            MinecraftHeroState.LogFailure("hotbar layer removal", e);
        }
        _movieLoaded = false;
        _movie = null;
        _layerAdded = false;
        _ownerScreen = null;
        vm?.OnFinalize();
    }
}
