using System;
using System.Collections.Generic;
using System.Diagnostics;
using Mycraft.Bridge;
using Mycraft.Patches;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;
using Mycraft.UI;
using TaleWorlds.InputSystem;
using Mycraft.Building;

namespace Mycraft.Demo;

public sealed partial class DemoBridgeMissionBehavior : MissionBehavior, IPlayerInputEffector
{
    private enum DemoState { Inactive, Connecting, Ready, Active, Ending }

    public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    public static DemoBridgeMissionBehavior? Current { get; private set; }
    private DemoState _state;
    private DemoConfig _config = new DemoConfig();
    private DemoTransform? _transform;
    private BridgeConnection? _conn;
    private readonly BridgeSessionValidator _validator = new BridgeSessionValidator();
    private readonly CombatEventLedger _attacks = new CombatEventLedger();
    private readonly EventRetryTracker _hurts = new EventRetryTracker();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Agent? _player;
    private Agent? _enemy;
    private readonly Dictionary<Agent,int> _actorIds=new Dictionary<Agent,int>();
    private readonly Dictionary<int,Agent> _knownActors=new Dictionary<int,Agent>();
    private readonly HashSet<int> _activeActors=new HashSet<int>();
    private int _nextActorId;
    private double _actorAccumulator;
    private bool _mcReady;
    private bool _mcDeathHandled;
    private bool _endRequested;
    private bool _applyingMcHit;
    private bool _started;
    private string _sessionId = "";
    private double _poseAccumulator;
    private long _readySince;
    private int _dismountAttempts;
    private long _nextDismountAt;
    private Agent.ActionStage _prevStage = Agent.ActionStage.None;
    private readonly HotbarWheel _hotbarWheel=new HotbarWheel();
    private int _pendingSelectedSlot=-1;
    private long _pendingSelectionTime;
    private readonly MinecraftInventoryOverlay _inventoryUI=new MinecraftInventoryOverlay();
    private McInventoryState? _inventory;
    private BattleBlockScene? _blocks;
    private TntScene? _tntScene;
    private readonly ExplosionLedger _explosions=new ExplosionLedger();
    private readonly Dictionary<string,Vec3> _primedTnt=new Dictionary<string,Vec3>();
    private bool _hasStagingOrigin;
    private float _anchorBLz;
    private double _inventoryPoll;
    private MissionWeapon[]? _savedCarrierExtras;
    private bool _useInputDown;
    private long _lastUseRefresh;
    private long _usePressedAt;
    private long _lastPlayerStateAt;
    private long _shieldImpactAt=-1000;
    private int _shieldImpactSound=-1;
    private bool _mineInputDown;
    private long _lastMineRefresh;
    private string _mineTarget="";
    private long _lastBuildNotice;
    private string _lastSceneStatus="";
    private string _lastTntStatus="";
    private bool _containerOpen;
    public bool McUsingShield{get;private set;}
    public bool McBlocking{get;private set;}
    public bool McUsingItem{get;private set;}
    public float ShieldImpactOffset=>Math.Max(0,1-(_clock.ElapsedMilliseconds-_shieldImpactAt)/160f)*.06f;
    public bool InventoryOpen=>_inventoryUI.IsOpen;
    public bool SuppressNativeAttack=>InventoryOpen || _mineInputDown || McUsingItem || UsesNativeWeaponInput;
    public string BuildStatus{get;private set;}="";

    public Agent? Enemy => _enemy;
    public Agent? Player => _player;
    public bool ApplyingMcHit => _applyingMcHit;
    public bool IsEngaged => _state == DemoState.Active;
    public bool OwnsCombat => IsEngaged || _endRequested;
    public bool IsConnected => _conn?.State == BridgeConnection.BridgeState.Connected;
    public bool OwnsPlayer(Agent? agent) => agent!=null && IsEngaged && IsConnected && ReferenceEquals(_player,agent);
    public string McVersion { get; private set; } = "";
    public float McHp { get; private set; }
    public float McMaxHp { get; private set; } = 20f;
    public bool McAlive { get; private set; }
    public string McItem { get; private set; } = "";
    public McHotbarState? McHotbar { get; private set; }
    public bool OwnsHotbarWheel=>IsEngaged && IsConnected && McAlive && _player?.IsActive()==true &&
        Mission.Mode==MissionMode.Battle && !InventoryOpen && !MBCommon.IsPaused && !Mission.IsOrderMenuOpen &&
        ScreenManager.TopScreen is MissionScreen screen && !screen.IsFocusLost &&
        !screen.IsPhotoModeEnabled && !screen.IsRadialMenuActive;

    public override void AfterStart()
    {
        base.AfterStart();
        _started = true;
        TryActivate();
    }

    private void TryActivate()
    {
        if (_state != DemoState.Inactive || !SubModule.PatchesReady || GameNetwork.IsMultiplayer ||
            !(Game.Current?.GameType is TaleWorlds.MountAndBlade.CustomBattle.CustomGame) || !Mission.IsFieldBattle)
            return;
        _config = DemoConfig.Load(DemoConfig.ModuleFolder);
        if (!_config.Enabled)
        {
            McArmyMissionBehavior.Current?.FailSetup("MC 军队需要启用 BannerCraft 桥接，请检查 demo.json 的 enabled 设置。");
            return;
        }
        _transform = _config.Transform;
        _conn = new BridgeConnection();
        _validator.ResetConnection();
        _mcReady=false;
        _dismountAttempts=0;_nextDismountAt=0;
        McHotbar=null;
        if (!_conn.Start(_config.Port, out var error,"v1.4.8"))
        {
            MinecraftHeroState.Log("demo listener failed: " + error);
            _conn.Dispose();
            _conn = null;
            McArmyMissionBehavior.Current?.FailSetup("MC 军队监听失败：" + error);
            return;
        }
        _state = DemoState.Connecting;
        Current = this;
        MinecraftHeroState.Log("demo listening on 127.0.0.1:" + _config.Port);
    }

    public override void OnMissionTick(float dt)
    {
        base.OnMissionTick(dt);
        if (!_started || _state == DemoState.Inactive)
            return;
        if (_endRequested)
        {
            Teardown("error");
            Mission.EndMission();
            return;
        }
        try
        {
            _inventoryUI.Tick();
            if(IsEngaged && !OwnsHotbarWheel){SetItemUse(false,true);SetMining(false,null);}
            if (Mission.CurrentState != Mission.State.Continuing)
                return;
            if (!IsConnected)
            {
                if (_state == DemoState.Active || _state == DemoState.Ready)
                    Abort("bridge disconnected: " + _conn?.LastError);
                else if (_conn?.State == BridgeConnection.BridgeState.Closed)
                {
                    Teardown("disconnect");
                    TryActivate();
                }
                return;
            }
            DrainInbound();
            if (_endRequested)
                return;
            if (_state == DemoState.Ready)
            {
                TryEngage();
                if (!_mcReady && _clock.ElapsedMilliseconds - _readySince > 15000)
                    Abort("MC player/session not ready within 15 seconds");
            }
            if (_state == DemoState.Active)
                TickActive(dt);
        }
        catch (Exception e)
        {
            MinecraftHeroState.LogFailure("demo tick", e);
            Abort("demo processing failed");
        }
    }

    private bool _pausedIntakeLogged;
    public static void PumpPausedBridge()
    {
        var bridge=Current;
        if(bridge==null)return;
        if(!MBCommon.IsPaused){bridge._pausedIntakeLogged=false;return;}
        if(!bridge._started || bridge._state==DemoState.Inactive || bridge._state==DemoState.Ending || bridge._endRequested ||
            !bridge.IsConnected || !ReferenceEquals(bridge.Mission,TaleWorlds.MountAndBlade.Mission.Current) ||
            bridge.Mission.CurrentState!=Mission.State.Continuing || bridge.Mission.IsMissionEnding || bridge.Mission.MissionEnded)return;
        try
        {
            // ApplicationTick continues on the same game thread while native combat is paused.
            // Consume the real MC results and ACK after applying them; do not tick AI or collect input.
            if(!bridge._pausedIntakeLogged){bridge._pausedIntakeLogged=true;DemoLog.Info("MC paused intake active on game thread");}
            bridge.DrainInbound();
        }
        catch(Exception e){MinecraftHeroState.LogFailure("paused bridge intake",e);bridge.Abort("paused bridge processing failed");}
    }

    private void DrainInbound()
    {
        while (!_endRequested && _conn != null && _conn.TryTakeInbound(out var msg))
        {
            var verdict = _validator.Validate(msg);
            if (verdict == BridgeSessionValidator.Verdict.StaleSession)
                continue;
            if (verdict != BridgeSessionValidator.Verdict.Ok)
            {
                Abort("invalid message envelope: " + verdict);
                return;
            }
            switch (DemoProtocol.S(msg, "type"))
            {
                case "hello_ack":
                    if (_state != DemoState.Connecting || _conn?.GreetingSent!=true || DemoProtocol.S(msg, "mcVersion") != "26.3")
                    {
                        Abort("unexpected handshake or incompatible Minecraft version");
                        return;
                    }
                    McVersion = DemoProtocol.S(msg, "mcVersion");
                    _sessionId = Guid.NewGuid().ToString("N");
                    _validator.BeginSession(_sessionId);
                    _attacks.Clear();
                    _hurts.Clear();
                    _readySince = _clock.ElapsedMilliseconds;
                    _state = DemoState.Ready;
                    var begin=DemoProtocol.BeginSession(_sessionId, 0, _config.GroundY, _config.OffsetX, _config.OffsetZ, _config.Scale);
                    begin["actorLimit"]=ActorLimit;
                    var anchor=Mission.MainAgent?.Position??Vec3.Zero;
                    begin["anchorX"]=(double)anchor.x;begin["anchorZ"]=(double)anchor.y;
                    Send(begin);
                    MinecraftHeroState.Log("demo session begun: " + _sessionId);
                    break;
                case "staging_origin":
                    if(_state!=DemoState.Ready || _blocks!=null){Abort("unexpected staging origin");break;}
                    _config.OffsetX=DemoProtocol.L(msg,"offsetX");_config.OffsetZ=DemoProtocol.L(msg,"offsetZ");
                    _transform=_config.Transform;_hasStagingOrigin=true;
                    MinecraftHeroState.Log("MC empty staging selected: offset="+_config.OffsetX+","+_config.OffsetZ);
                    break;
                case "player_state":
                    _lastPlayerStateAt=_clock.ElapsedMilliseconds;
                    var previouslyUsing=McUsingItem;
                    var previouslyBlocking=McBlocking;
                    McUsingItem=BridgeJson.B(msg,"usingItem");
                    // Vanilla starts the raise animation immediately; damage blocking
                    // becomes active only after the item's own warm-up time.
                    McUsingShield=McUsingItem && BridgeJson.B(msg,"usingShield",BridgeJson.B(msg,"blocking"));
                    McBlocking=BridgeJson.B(msg,"blocking");
                    if(previouslyUsing!=McUsingItem || previouslyBlocking!=McBlocking)
                        MinecraftHeroState.Log("MC use confirmed: using="+McUsingItem+", shield="+McUsingShield+", blocking="+McBlocking+
                            ", elapsedFromPressMs="+(_usePressedAt>0?_clock.ElapsedMilliseconds-_usePressedAt:-1));
                    var hp = DemoProtocol.D(msg, "hp", double.NaN);
                    var maxHp = DemoProtocol.D(msg, "maxHp", double.NaN);
                    if (!CombatEventLedger.IsFinite(hp) || !CombatEventLedger.IsFinite(maxHp) ||
                        maxHp <= 0 || maxHp > 1024 || hp < 0 || hp > maxHp ||
                        !msg.TryGetValue("alive", out var aliveValue) || !(aliveValue is bool))
                    {
                        Abort("invalid MC player state");
                        return;
                    }
                    McHp = (float)hp;
                    McMaxHp = (float)maxHp;
                    McAlive = BridgeJson.B(msg, "alive") && hp > 0;
                    McItem = DemoProtocol.S(msg, "item");
                    McHotbar = McHotbarState.Read(msg);
                    ReadWeaponState(msg);
                    var containerOpen=BridgeJson.B(msg,"containerOpen");
                    if(containerOpen && !_containerOpen && IsEngaged && !InventoryOpen)OpenInventory();
                    _containerOpen=containerOpen;
                    if(McHotbar.SelectedSlot==_pendingSelectedSlot)_pendingSelectedSlot=-1;
                    _mcReady = McAlive && _hasStagingOrigin;
                    if (!McAlive)
                    {
                        if (IsEngaged && !_mcDeathHandled)
                            ApplyMcDeathToPlayer();
                        else if (_state == DemoState.Ready)
                            Abort("MC player must be alive before beginning the duel");
                    }
                    break;
                case "inventory_state":
                    _inventory=McInventoryState.Read(msg);
                    _inventoryUI.Apply(_inventory);
                    break;
                case "inventory_result":
                    _inventoryUI.Status(DemoProtocol.S(msg,"message"));
                    break;
                case "block_state":
                    _blocks?.Apply((int)DemoProtocol.L(msg,"x"),(int)DemoProtocol.L(msg,"y"),(int)DemoProtocol.L(msg,"z"),
                        DemoProtocol.S(msg,"item"),(float)_config.GroundY,_anchorBLz,DemoProtocol.S(msg,"modelKey"));
                    break;
                case "tnt_state":
                    var entityId=DemoProtocol.S(msg,"entityId");
                    var alive=BridgeJson.B(msg,"alive");
                    float tx=(float)DemoProtocol.D(msg,"x"),ty=(float)DemoProtocol.D(msg,"y"),tz=(float)DemoProtocol.D(msg,"z");
                    if(alive)_primedTnt[entityId]=ToHostPoint(tx,ty,tz);else _primedTnt.Remove(entityId);
                    _tntScene?.ApplyState(entityId,tx,ty,tz,(int)DemoProtocol.L(msg,"fuse"),alive);
                    break;
                case "explosion":case "explosion_hit":ReceiveExplosion(msg);break;
                case "weapon_hit":ReceiveWeaponHit(msg);break;
                case "player_motion":ReceiveNativeMotion(msg);break;
                case "lightning_state":
                    _lightningScene?.ApplyState(DemoProtocol.S(msg,"entityId"),(float)DemoProtocol.D(msg,"x"),
                        (float)DemoProtocol.D(msg,"y"),(float)DemoProtocol.D(msg,"z"),DemoProtocol.S(msg,"seed"),BridgeJson.B(msg,"visible"),BridgeJson.B(msg,"alive"));
                    break;
                case "projectile_state":
                    var projectileId=DemoProtocol.S(msg,"entityId");
                    if(BridgeJson.B(msg,"alive"))_projectilePositions[projectileId]=ToHostPoint((float)DemoProtocol.D(msg,"x"),(float)DemoProtocol.D(msg,"y"),(float)DemoProtocol.D(msg,"z"));
                    else {_projectilePositions.Remove(projectileId);_projectileVelocities.Remove(projectileId);}
                    if(BridgeJson.B(msg,"alive"))_projectileVelocities[projectileId]=new Vec3((float)DemoProtocol.D(msg,"vx")*20,
                        (float)DemoProtocol.D(msg,"vz")*20,(float)DemoProtocol.D(msg,"vy")*20);
                    _projectileScene?.ApplyState(DemoProtocol.S(msg,"entityId"),DemoProtocol.S(msg,"kind"),
                        (float)DemoProtocol.D(msg,"x"),(float)DemoProtocol.D(msg,"y"),(float)DemoProtocol.D(msg,"z"),
                        (float)DemoProtocol.D(msg,"yaw"),(float)DemoProtocol.D(msg,"pitch"),BridgeJson.B(msg,"alive"),(int)DemoProtocol.L(msg,"color",-1),_sessionId,DemoProtocol.S(msg,"visualKey"));
                    break;
                case "interaction_result":
                    BuildStatus=DemoProtocol.S(msg,"message");
                    MinecraftHeroState.Log("MC interaction result: request="+DemoProtocol.S(msg,"requestId")+
                        ", ok="+BridgeJson.B(msg,"ok")+", message="+BuildStatus);
                    if(!BridgeJson.B(msg,"ok"))BuildNotice(BuildStatus);
                    break;
                case "build_result":
                    BuildStatus=DemoProtocol.S(msg,"message");
                    if(!BridgeJson.B(msg,"ok"))InformationManager.DisplayMessage(new InformationMessage("BannerCraft: "+BuildStatus));
                    break;
                case "hit_actor":
                    ReceiveHit(msg);
                    break;
                case "player_hurt_result":
                    MinecraftHeroState.Log("MC defense result: event="+DemoProtocol.S(msg,"eventId")+
                        ", hp="+DemoProtocol.D(msg,"hpBefore")+" -> "+DemoProtocol.D(msg,"hpAfter")+
                        ", expectedBlocked="+DemoProtocol.D(msg,"expectedBlocked")+", sourceKnown="+BridgeJson.B(msg,"sourceKnown")+
                        ", actualBlocked="+DemoProtocol.D(msg,"actualBlocked")+", nativeBlockChecked="+BridgeJson.B(msg,"nativeBlockChecked")+
                        ", headYaw="+DemoProtocol.D(msg,"headYaw")+", hostPredicted="+BridgeJson.B(msg,"hostBlockPredicted"));
                    break;
                case "ack":
                    _hurts.Acknowledge(DemoProtocol.S(msg, "ackEventId"));
                    _mobHurts.Acknowledge(DemoProtocol.S(msg,"ackEventId"));
                    break;
                case "mob_state": case "boat_state": ReceiveEntityState(msg);break;
                case "mc_army_spawn_result":
                    McArmyMissionBehavior.Current?.HandleSpawnResult(msg);
                    Send(DemoProtocol.Ack(_sessionId,0,DemoProtocol.S(msg,"eventId")));
                    break;
                case "mob_hit": ReceiveMobHit(msg);break;
                case "mob_remove": case "boat_remove": ReceiveEntityRemoval(msg);break;
                case "heartbeat":
                    break;
                case "error":
                    if (DemoProtocol.S(msg, "code") != "stale_session")
                        Abort("MC error: " + DemoProtocol.S(msg, "message"));
                    break;
                default:
                    Abort("unexpected MC message type");
                    break;
            }
        }
    }

    private void TryEngage()
    {
        if (!_mcReady || Mission.Mode != MissionMode.Battle || MBCommon.IsPaused)
            return;
        _player = Mission.MainAgent;
        if (_player == null || !_player.IsActive())
            return;
        var mount=_player.MountAgent;
        if(mount!=null)
        {
            if(_clock.ElapsedMilliseconds<_nextDismountAt)return;
            if(_dismountAttempts>=20)
            {
                BuildNotice("自动下马未完成，请停稳或下马后重新执行 /mycraft connect。");
                Teardown("dismount_blocked");
                TryActivate();
                return;
            }
            if(_dismountAttempts==0)MinecraftHeroState.Log("MC ready: requesting native dismount before takeover");
            _dismountAttempts++;
            _nextDismountAt=_clock.ElapsedMilliseconds+500;
            return;
        }
        if(Math.Abs(_config.Scale-1)>0.0001)
        {Abort("building demo requires scale=1");return;}
        _player.TryToWieldWeaponInSlot(EquipmentIndex.Weapon0,Agent.WeaponWieldActionType.Instant,false);
        _savedCarrierMain=_player.Equipment[EquipmentIndex.Weapon0];
        // Extra native weapons are animation leftovers, not MC inventory. In particular,
        // a native shield must not intercept a hit before Minecraft can apply its rules.
        _savedCarrierExtras=new MissionWeapon[4];
        for(var i=1;i<5;i++)
        {
            var slot=(EquipmentIndex)i;
            _savedCarrierExtras[i-1]=_player.Equipment[slot];
            if(!_player.Equipment[slot].IsEmpty)_player.RemoveEquippedWeapon(slot);
        }
        _anchorBLz=_player.Position.z;
        _blocks=new BattleBlockScene(Mission);
        _blocks.SetSession(_sessionId);
        _blocks.SetOrigin((float)_config.GroundY,_anchorBLz,(int)_config.OffsetX,(int)_config.OffsetZ);
        _tntScene=new TntScene(Mission);
        _tntScene.SetOrigin((float)_config.GroundY,_anchorBLz,(int)_config.OffsetX,(int)_config.OffsetZ);
        _projectileScene=new ProjectileScene(Mission);
        _projectileScene.SetOrigin((float)_config.GroundY,_anchorBLz,(int)_config.OffsetX,(int)_config.OffsetZ);
        _lightningScene=new LightningScene(Mission);
        _entities=new NativeCreatureScene(Mission);
        _lightningScene.SetOrigin((float)_config.GroundY,_anchorBLz,(int)_config.OffsetX,(int)_config.OffsetZ);
        _prevStage = _player.GetCurrentActionStage(1);
        SendPose();
        SyncActors();
        _state = DemoState.Active;
        SyncWeaponCarrier();
        RequestInventory();
        MinecraftHeroState.Log("demo ACTIVE: nearby enemy proxies=" + _activeActors.Count + ", army-size gate removed");
    }

    public Agent.EventControlFlag OnCollectPlayerEventControlFlags()
    {
        if(_state!=DemoState.Ready || !_mcReady || !IsConnected || _dismountAttempts==0 ||
            _player==null || !ReferenceEquals(_player,Mission.MainAgent) || !_player.IsActive() ||
            Mission.Mode!=MissionMode.Battle || !Mission.IsFieldBattle || MBCommon.IsPaused)
            return Agent.EventControlFlag.None;
        var mount=_player.MountAgent;
        if(mount==null || mount.GetCurrentActionType(0)==Agent.ActionCodeType.Rear)
            return Agent.EventControlFlag.None;
        // ControlTick clears earlier MissionTick flags before collecting this
        // official interface. Keep the native velocity/rearing conditions.
        return _player.GetCurrentVelocity().y<Agent.DismountVelocityLimit
            ? Agent.EventControlFlag.Dismount : Agent.EventControlFlag.DoubleTapToDirectionDown;
    }

    private void TickActive(float dt)
    {
        _blocks?.Tick();
        _tntScene?.Tick(dt);
        _projectileScene?.Tick(dt);
        _lightningScene?.Tick(dt);
        TickEntities();
        TickNativeMotion(dt);
        if(_tntScene!=null && _tntScene.Status.Length>0 && _tntScene.Status!=_lastTntStatus)
        {_lastTntStatus=_tntScene.Status;BuildNotice(_lastTntStatus);MinecraftHeroState.Log("TNT scene status: "+_lastTntStatus);}
        if(_blocks!=null && _blocks.Status.Length>0 && _blocks.Status!=_lastSceneStatus)
        {
            _lastSceneStatus=_blocks.Status;
            if(!_lastSceneStatus.StartsWith("Waiting for Minecraft model:",StringComparison.Ordinal) && _lastSceneStatus!="Minecraft block ready")
                BuildNotice(_lastSceneStatus);
        }
        var retries = _hurts.TakeDue(_clock.ElapsedMilliseconds, out var exhausted);
        if (exhausted)
        {
            Abort("MC damage acknowledgement timed out");
            return;
        }
        foreach (var retry in retries) Send(retry);
        if (_player == null || !_player.IsActive())
        {
            Teardown("death");
            return;
        }
        if(InventoryOpen)
        {
            _inventoryPoll+=dt;
            if(_inventoryPoll>=.5){_inventoryPoll=0;RequestInventory();}
            _player.MovementInputVector=Vec2.Zero;
        }
        if(OwnsHotbarWheel) ReadHotbarWheel();
        else _hotbarWheel.Clear();
        if (_player.MountAgent != null)
        {
            Abort("mounted combat is outside this demo");
            return;
        }
        if (Mission.Mode != MissionMode.Battle || MBCommon.IsPaused ||
            !(ScreenManager.TopScreen is MissionScreen screen) || screen.IsFocusLost)
        {
            _prevStage = _player.GetCurrentActionStage(1);
            return;
        }
        _poseAccumulator += dt;
        if (_poseAccumulator >= 1.0 / DemoProtocol.PoseHz)
        {
            _poseAccumulator = 0;
            SendPose();
        }
        _actorAccumulator+=dt;
        if(_actorAccumulator>=.1)
        {
            _actorAccumulator=0;
            SyncActors();
        }
        if(InventoryOpen)
        {
            _prevStage=_player.GetCurrentActionStage(1);
            return;
        }
        if(SuppressNativeAttack)_prevStage=_player.GetCurrentActionStage(1);
        else DetectPlayerAttack();
    }

    private void RequestInventory()=>Send(DemoProtocol.Msg("inventory_request",_sessionId,0));
    public void HandleFeatureKeys()
    {
        if(!OwnsHotbarWheel || _inventoryUI.ClosedThisTick)return;
        if(Mission.InputManager.IsKeyPressed(InputKey.E))
            OpenInventory();
    }
    private void OpenInventory()
    {
        SetItemUse(false,true);
        SetMining(false,null);
        _inventoryUI.Open(_sessionId);
        RequestInventory();
    }
    public void ReadMinecraftInput()
    {
        var canRead=OwnsHotbarWheel && !_inventoryUI.ClosedThisTick;
        var right=canRead && Mission.InputManager.IsKeyDown(InputKey.RightMouseButton);
        SetItemUse(right,!canRead);
        BlockInteractionTarget? target=null;
        var left=canRead && !right && !McUsingItem && Mission.InputManager.IsKeyDown(InputKey.LeftMouseButton);
        if(left && McWeaponKind!="spear" && _player!=null && _blocks?.TryInteractionTarget(_player,out var hit)==true && !hit.Terrain)target=hit;
        SetMining(target!=null,target);
        if(UsesNativeWeaponInput && left && !_weaponLeftDown && target==null)SendWeaponAttack();
        _weaponLeftDown=left;
    }
    private void SetItemUse(bool pressed,bool cancel=false)
    {
        if(!IsEngaged || !IsConnected){_useInputDown=false;return;}
        var now=_clock.ElapsedMilliseconds;
        if(pressed==_useInputDown && (!pressed || now-_lastUseRefresh<200))return;
        if(pressed!=_useInputDown)
        {
            if(pressed)_usePressedAt=now;
            MinecraftHeroState.Log("MC right mouse "+(pressed?"pressed":"released")+", heldMs="+(pressed?0:now-_usePressedAt));
        }
        _useInputDown=pressed;_lastUseRefresh=now;
        var message=DemoProtocol.Msg("interact",_sessionId,0);
        message["requestId"]=Guid.NewGuid().ToString("N");message["pressed"]=pressed;message["target"]=false;
        message["cancel"]=cancel;
        message["inputAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if(!cancel && _player?.IsActive()==true)
        {
            SendPose();
            if(UsesNativeWeaponInput)SyncActors();
        }
        if(pressed)
        {
            if(_player!=null && _blocks?.TryInteractionTarget(_player,out var target)==true)
            {
                AddTarget(message,target);
                message["terrain"]=target.Terrain;
                message["hitX"]=(double)target.HitX;message["hitY"]=(double)target.HitY;message["hitZ"]=(double)target.HitZ;
                bool tntItem=McItem=="minecraft:tnt"||McHotbar?.OffhandItem=="minecraft:tnt";
                message["surfaceH"]=(double)(target.Terrain?target.SurfaceHeight:1f);
                message["canPlace"]=tntItem&&target.Terrain
                    ?_blocks.CanPlaceTnt(target.PlaceX,target.TntPlacementY,target.PlaceZ)
                    :_blocks.CanPlace(target.PlaceX,target.PlaceY,target.PlaceZ);
                if(tntItem || _blocks.ItemAt(target.X,target.Y,target.Z)=="minecraft:tnt")
                {
                    var ground=_blocks.CaptureTntGroundPatch(target.X,target.Z,target.HitY);
                    message["groundCount"]=ground.Count;
                    for(var i=0;i<ground.Count;i++)
                    {message["ground"+i+"X"]=ground[i].X;message["ground"+i+"Y"]=ground[i].Y;message["ground"+i+"Z"]=ground[i].Z;message["ground"+i+"H"]=(double)ground[i].H;}
                }
            }
        }
        Send(message);
    }
    private void SetMining(bool pressed,BlockInteractionTarget? target)
    {
        if(!IsEngaged || !IsConnected){_mineInputDown=false;return;}
        var now=_clock.ElapsedMilliseconds;
        var key=target==null?"":target.X+":"+target.Y+":"+target.Z;
        if(pressed==_mineInputDown && key==_mineTarget && (!pressed || now-_lastMineRefresh<200))return;
        _mineInputDown=pressed;_mineTarget=key;_lastMineRefresh=now;
        var message=DemoProtocol.Msg("mine",_sessionId,0);
        message["requestId"]=Guid.NewGuid().ToString("N");message["pressed"]=pressed;message["target"]=false;
        if(pressed && target!=null){SendPose();AddTarget(message,target);}
        Send(message);
    }
    private static void AddTarget(Dictionary<string,object> message,BlockInteractionTarget target)
    {
        message["target"]=true;message["x"]=target.X;message["y"]=target.Y;message["z"]=target.Z;message["face"]=target.Face;
    }
    private void BuildNotice(string text)
    {
        BuildStatus=text;
        var now=_clock.ElapsedMilliseconds;
        if(now-_lastBuildNotice<1500)return;
        _lastBuildNotice=now;
        InformationManager.DisplayMessage(new InformationMessage("BannerCraft: "+text));
    }

    private void ReadHotbarWheel()
    {
        if(McHotbar==null)return;
        if(_pendingSelectedSlot>=0 && _clock.ElapsedMilliseconds-_pendingSelectionTime>2000)
        {
            Abort("MC hotbar selection was not confirmed");
            return;
        }
        var current=_pendingSelectedSlot>=0 ? _pendingSelectedSlot : McHotbar.SelectedSlot;
        var next=_hotbarWheel.Apply(current,Mission.InputManager.GetDeltaMouseScroll());
        if(next==null || next.Value==current)return;
        _pendingSelectedSlot=next.Value;
        _pendingSelectionTime=_clock.ElapsedMilliseconds;
        Send(DemoProtocol.SelectSlot(_sessionId,0,next.Value));
        MinecraftHeroState.Log("MC hotbar selection requested: " + (next.Value+1));
    }

    private void SendPose()
    {
        var pos = _player!.Position;
        var dir = _useInputDown || UsesNativeWeaponInput || McOffhandWeaponKind=="bow" || McOffhandWeaponKind=="crossbow" || McOffhandWeaponKind=="trident"
            ?ResolveRangedAim():_player.LookDirection;
        var mc = _transform!.BlToMc(pos.x, pos.y, pos.z, Math.Atan2(dir.x, dir.y) * 180 / Math.PI);
        var message=DemoProtocol.PlayerPose(_sessionId, 0, mc.X, mc.Y+(pos.z-_anchorBLz)*_config.Scale, mc.Z, mc.Yaw);
        message["pitch"]=-Math.Asin(Math.Max(-1,Math.Min(1,dir.z)))*180/Math.PI;
        AddMotion(message,_player);
        Send(message);
    }

    private void SendActorUpsert(int eid,Agent actor)
    {
        var pos = actor.Position;
        var dir = actor.LookDirection;
        var mc = _transform!.BlToMc(pos.x, pos.y, pos.z, Math.Atan2(dir.x, dir.y) * 180 / Math.PI);
        var message=DemoProtocol.ActorUpsert(_sessionId, 0, eid, mc.X, mc.Y+(pos.z-_anchorBLz)*_config.Scale, mc.Z, mc.Yaw, true, actor.Character.Name.ToString());
        message["hostile"]=actor.IsEnemyOf(_player);
        message["hp"]=(double)actor.Health/_config.McHitFactor;
        message["maxHp"]=(double)actor.HealthLimit/_config.McHitFactor;
        AddMotion(message,actor);
        Send(message);
    }

    private int ActorId(Agent actor)
    {
        if(_actorIds.TryGetValue(actor,out var id))return id;
        id=++_nextActorId;
        _actorIds.Add(actor,id); _knownActors.Add(id,actor);
        return id;
    }

    private void SyncActors()
    {
        if(_player==null)return;
        var agents=new Dictionary<int,Agent>();
        var candidates=new List<(int Id,int Priority,double Score)>();
        var origin=_player.Position;
        var aim=ResolveRangedAim();var eye=origin+new Vec3(0,0,McEyeHeight/(float)_config.Scale);
        var aimEnd=eye+aim*(float)RangedTargeting.MaximumDistance;
        foreach(var actor in Mission.Agents)
        {
            if(!actor.IsHuman || !actor.IsActive() || ReferenceEquals(actor,_player) || NativeCreatureScene.OwnsAgent(actor))continue;
            var delta=actor.Position-origin;
            var distance=(double)delta.x*delta.x+(double)delta.y*delta.y+(double)delta.z*delta.z;
            double blastDistance=double.MaxValue;
            foreach(var blastCenter in _primedTnt.Values){var d=actor.Position-blastCenter;blastDistance=Math.Min(blastDistance,d.LengthSquared);}
            int priority=3;double score=distance;
            double mobDistance=CreatureInterest(actor.Position);
            if(mobDistance<=32*32){priority=2;score=mobDistance;}
            if(blastDistance<=100){priority=0;score=blastDistance;}
            if(_actorIds.TryGetValue(actor,out var effectActor) && _mobEffectActors.ContainsKey(effectActor))
            {priority=0;score=distance;}
            var center=actor.Position+new Vec3(0,0,.9f);
            foreach(var projectile in _projectilePositions)
            {
                _projectileVelocities.TryGetValue(projectile.Key,out var velocity);
                var start=projectile.Value;var finish=start+velocity*(float)RangedTargeting.LookaheadSeconds;
                finish.z-=(float)(.5*20*RangedTargeting.LookaheadSeconds*RangedTargeting.LookaheadSeconds);
                double corridor=RangedTargeting.SegmentDistanceSquared(center.x,center.y,center.z,start.x,start.y,start.z,
                    finish.x,finish.y,finish.z,out var along);
                if(corridor<=36&&priority>1){priority=1;score=along+corridor/100;}
            }
            if(distance<=RangedTargeting.MaximumDistance*RangedTargeting.MaximumDistance && (UsesNativeWeaponInput||_useInputDown))
            {
                double corridor=RangedTargeting.SegmentDistanceSquared(center.x,center.y,center.z,eye.x,eye.y,eye.z,
                    aimEnd.x,aimEnd.y,aimEnd.z,out var along);
                if((actor.Index==_aimedActorIndex||corridor<=16)&&priority>2)
                {priority=2;score=(actor.Index==_aimedActorIndex?0:1)+along+corridor/100;}
            }
            if(priority==3&&distance>ActorInterestBudget.Radius*ActorInterestBudget.Radius && ActorLimit==64)continue;
            agents[actor.Index]=actor;
            candidates.Add((actor.Index,priority,score));
        }
        var selected=ActorInterestBudget.SelectPrioritized(candidates,ActorLimit);
        var next=new HashSet<int>();
        foreach(var index in selected)next.Add(ActorId(agents[index]));
        foreach(var old in _activeActors)
            if(!next.Contains(old))Send(DemoProtocol.ActorRemove(_sessionId,0,old));
        _activeActors.Clear();
        foreach(var id in next)
        {
            _activeActors.Add(id);
            SendActorUpsert(id,_knownActors[id]);
        }
    }

    private void DetectPlayerAttack()
    {
        var stage = _player!.GetCurrentActionStage(1);
        var release = stage == Agent.ActionStage.AttackRelease && _prevStage != stage;
        _prevStage = stage;
        var weapon = _player.WieldedWeapon;
        if (!release || (!weapon.IsEmpty && weapon.CurrentUsageItem?.IsRangedWeapon == true))
            return;
        var attackId = Guid.NewGuid().ToString("N");
        if (!_attacks.TryIssue(attackId, _clock.ElapsedMilliseconds))
        {
            Abort("attack ledger full");
            return;
        }
        // Send the target before the attack, so both MC positions belong to this swing.
        // Refresh actor poses before the swing so MC tests the current target locations.
        foreach(var id in _activeActors)
        {
            var actor=_knownActors[id];
            if(actor.IsActive())SendActorUpsert(id,actor);
        }
        var pos = _player.Position;
        var dir = _player.LookDirection;
        var mc = _transform!.BlToMc(pos.x, pos.y, pos.z, Math.Atan2(dir.x, dir.y) * 180 / Math.PI);
        var message=DemoProtocol.AttackInput(_sessionId, 0, attackId, mc.X, mc.Y+(pos.z-_anchorBLz)*_config.Scale, mc.Z, mc.Yaw);
        message["pitch"]=-Math.Asin(Math.Max(-1,Math.Min(1,dir.z)))*180/Math.PI;
        Send(message);
    }

    private void ReceiveHit(Dictionary<string, object> msg)
    {
        var eventId = DemoProtocol.S(msg, "eventId");
        var result = _attacks.Resolve(DemoProtocol.S(msg, "attackId"), eventId,
            DemoProtocol.D(msg, "damage", double.NaN), _clock.ElapsedMilliseconds);
        if (result == CombatEventLedger.Resolution.Duplicate)
        {
            Send(DemoProtocol.Ack(_sessionId, 0, eventId));
            return;
        }
        if (!IsEngaged || result != CombatEventLedger.Resolution.Accepted)
        {
            Abort("invalid MC hit: " + result);
            return;
        }
        var eid=(int)DemoProtocol.L(msg,"eid");
        if(_activeActors.Contains(eid) && _knownActors.TryGetValue(eid,out var target) && target.IsActive() &&
            target.Health>0 && _player?.IsActive()==true && target.IsEnemyOf(_player))
            ApplyMcHit(target,(float)DemoProtocol.D(msg,"damage"));
        else MinecraftHeroState.Log("MC hit target no longer active: eid=" + eid);
        Send(DemoProtocol.Ack(_sessionId, 0, eventId));
    }

    private void ApplyMcHit(Agent target,float mcDamage)
        =>ApplyMcDamage(target,mcDamage,DamageTypes.Cut,_player!.LookDirection);

    private void ApplyMcDamage(Agent target,float mcDamage,DamageTypes damageType,Vec3 direction,Agent? attributedSource=null,bool environmental=false)
    {
        var damage = CombatEventLedger.ToBannerlordDamage(mcDamage, _config.McHitFactor);
        var source=attributedSource??_player!;
        var blow = new Blow(environmental?-1:source.Index);
        if(environmental && damage>=target.Health)NativeCreatureScene.MarkMissingSourceVictim(target);
        blow.DamageType = damageType;
        blow.BoneIndex = target.Monster.HeadLookDirectionBoneIndex;
        blow.GlobalPosition = target.Position;
        blow.GlobalPosition.z += target.GetEyeGlobalHeight();
        blow.BaseMagnitude = damage;
        blow.WeaponRecord.FillAsMeleeBlow(null, null, -1, -1);
        blow.InflictedDamage = damage;
        blow.SwingDirection = direction;
        blow.Direction = blow.SwingDirection;
        blow.DamageCalculated = true;
        int targetId=_actorIds.TryGetValue(target,out var knownId)?knownId:0;
        var collision = AttackCollisionData.GetAttackCollisionDataForDebugPurpose(false, false, false, true,
            false, false, false, false, false, false, false, false, (CombatCollisionResult)1, -1, 0, 2,
            blow.BoneIndex, (BoneBodyPartType)0, source.Monster.MainHandItemBoneIndex, (Agent.UsageDirection)2,
            -1, (CombatHitResultFlags)0, 0.5f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, Vec3.Up, blow.Direction,
            blow.GlobalPosition, Vec3.Zero, Vec3.Zero, target.Velocity, Vec3.Up);
        _applyingMcHit = true;
        try
        {
            float before=target.Health;
            target.RegisterBlow(blow, in collision);
            MinecraftHeroState.Log("demo MC hit applied once: eid=" + targetId + ", damage=" + damage+
                ", hpBefore="+before+", hpAfter="+target.Health);
        }
        finally
        {
            _applyingMcHit=false;
            if(environmental && target.IsActive() && target.Health>0)NativeCreatureScene.ClearMissingSourceVictim(target);
        }
    }

    private Vec3 ToHostPoint(float x,float y,float z)=>new Vec3(x-(float)_config.OffsetX,z-(float)_config.OffsetZ,
        _anchorBLz+y-(float)_config.GroundY);

    private void ReceiveExplosion(Dictionary<string,object> message)
    {
        string id=DemoProtocol.S(message,"eventId"),blast=DemoProtocol.S(message,"blastId");
        bool visual=DemoProtocol.S(message,"type")=="explosion";
        var result=_explosions.Register(id,blast,visual,_clock.ElapsedMilliseconds);
        if(result==ExplosionLedger.Result.Duplicate){Send(DemoProtocol.Ack(_sessionId,0,id));return;}
        if(!IsEngaged || result!=ExplosionLedger.Result.Accepted){Abort("invalid explosion event: "+result);return;}
        if(visual)
        {
            _tntScene?.Explode(blast,(float)DemoProtocol.D(message,"x"),(float)DemoProtocol.D(message,"y"),
                (float)DemoProtocol.D(message,"z"),(float)DemoProtocol.D(message,"radius"));
            MinecraftHeroState.Log("MC TNT explosion displayed: "+blast);
        }
        else
        {
            int eid=(int)DemoProtocol.L(message,"eid");
            if(_knownActors.TryGetValue(eid,out var target) && target.IsActive() && target.Health>0 && !ReferenceEquals(target,_player))
            {
                var direction=new Vec3((float)DemoProtocol.D(message,"knockbackX"),(float)DemoProtocol.D(message,"knockbackZ"),(float)DemoProtocol.D(message,"knockbackY"));
                if(direction.Normalize()<.001f)direction=Vec3.Up;
                ApplyMcDamage(target,(float)DemoProtocol.D(message,"damage"),DamageTypes.Blunt,direction);
                MinecraftHeroState.Log("MC TNT area damage applied once: blast="+blast+", eid="+eid+", damage="+DemoProtocol.D(message,"damage"));
            }
            else MinecraftHeroState.Log("MC TNT damage target unavailable: blast="+blast+", eid="+eid);
        }
        Send(DemoProtocol.Ack(_sessionId,0,id));
    }

    public void ForwardPlayerHurt(int blDamage,int sourceAgentIndex)
    {
        if (_endRequested || blDamage <= 0)
            return;
        var eventId = Guid.NewGuid().ToString("N");
        var damage = blDamage * _config.PlayerHurtFactor;
        var source=Mission.FindAgentWithIndex(sourceAgentIndex);
        if(source?.IsHuman==true)_enemy=source;
        var sourceEid=source?.IsHuman==true ? ActorId(source) : 0;
        if(source?.IsHuman==true && _activeActors.Contains(sourceEid))SendActorUpsert(sourceEid,source);
        SendPose();
        var msg = DemoProtocol.PlayerHurt(_sessionId, 0, eventId, damage, sourceEid);
        msg["hostBlockPredicted"]=CanPredictShieldBlock(source);
        // Capture victim pose and damage origin in the damage event itself. Separate
        // pose packets can be interleaved with the background MC client's rotation.
        var position=_player!.Position;var look=_player.LookDirection;
        var victim=_transform!.BlToMc(position.x,position.y,position.z,Math.Atan2(look.x,look.y)*180/Math.PI);
        msg["x"]=victim.X;msg["y"]=victim.Y+(position.z-_anchorBLz)*_config.Scale;msg["z"]=victim.Z;msg["yaw"]=victim.Yaw;
        msg["pitch"]=-Math.Asin(Math.Max(-1,Math.Min(1,look.z)))*180/Math.PI;
        msg["hasSourcePosition"]=source!=null;
        if(source!=null)
        {
            var p=source.Position;var origin=_transform.BlToMc(p.x,p.y,p.z,0);
            msg["sourceX"]=origin.X;msg["sourceY"]=origin.Y+(p.z-_anchorBLz)*_config.Scale;msg["sourceZ"]=origin.Z;
        }
        if (!Send(msg) || !_hurts.Track(eventId, msg, _clock.ElapsedMilliseconds))
            Abort("unable to deliver player damage");
        MinecraftHeroState.Log("demo BL hit forwarded: " + damage);
    }

    public bool CanPredictShieldBlock(Agent? attacker)
    {
        if(_player==null || attacker==null || !attacker.IsHuman || !attacker.IsActive() || !attacker.IsEnemyOf(_player) ||
           !OwnsHotbarWheel || _pendingSelectedSlot>=0)return false;
        var look=_player.LookDirection;var direction=attacker.Position-_player.Position;
        return ShieldBlockPrediction.CanBounce(IsEngaged&&IsConnected&&McUsingShield,_useInputDown,McBlocking,
            _clock.ElapsedMilliseconds-_lastPlayerStateAt,look.x,look.y,direction.x,direction.y);
    }

    public void NotifyShieldImpact(Agent attacker,Vec3 point)
    {
        var now=_clock.ElapsedMilliseconds;
        if(now-_shieldImpactAt<60)return;
        _shieldImpactAt=now;
        if(_shieldImpactSound<0)_shieldImpactSound=TaleWorlds.Engine.SoundEvent.GetEventIdFromString("event:/mission/combat/impact/metal_weapon/wood_shield");
        if(_shieldImpactSound>=0)Mission.MakeSound(_shieldImpactSound,point,false,true,attacker.Index,_player!.Index);
        MinecraftHeroState.Log("MC shield melee impact predicted for current collision: attacker="+attacker.Index+", stateAgeMs="+(now-_lastPlayerStateAt));
    }

    private void ApplyMcDeathToPlayer()
    {
        _mcDeathHandled = true;
        if (_player?.IsActive() == true)
        {
            // player_state does not identify the fatal source. A previous melee
            // attacker may be unrelated to this TNT/fire death.
            NativeMcDeath.Apply(Mission,_player,null);
            MinecraftHeroState.Log("demo MC death applied once");
        }
        Teardown("death");
    }

    public void Abort(string reason)
    {
        if (_endRequested)
            return;
        MinecraftHeroState.Log("demo abort: " + reason);
        _endRequested = true;
        InformationManager.DisplayMessage(new InformationMessage("BannerCraft: " + reason));
    }

    private bool Send(Dictionary<string, object> msg)
    {
        if (_conn == null)
            return false;
        msg["seq"] = _conn.NextSeq();
        var sent = _conn.Send(msg);
        if (!sent)
            Abort("bridge send failed: type="+DemoProtocol.S(msg,"type")+", "+_conn.LastError+
                ", coalescedOutbound="+_conn.CoalescedOutbound);
        return sent;
    }

    protected override void OnEndMission()
    {
        Teardown("mission_end");
        base.OnEndMission();
    }

    public override void OnRemoveBehavior()
    {
        Teardown("mission_end");
        base.OnRemoveBehavior();
    }

    internal void EndCommandOnlySmokeSession()
    {
        if(!Array.Exists(Environment.GetCommandLineArgs(),arg=>arg=="--mycraft-command-only-smoke"))
            throw new InvalidOperationException("Command-only diagnostics are disabled");
        Teardown("command_only_smoke");
    }

    private void Teardown(string reason)
    {
        if (_state == DemoState.Inactive)
            return;
        SetItemUse(false,true);
        SetMining(false,null);
        _state = DemoState.Ending;
        McArmyMissionBehavior.Current?.NotifySessionEnded(reason);
        ClearEntities();
        Mission.GetMissionBehavior<Mycraft.Visuals.MinecraftPlayerModelBehavior>()?.RestoreNativePlayer();
        if(_savedCarrierExtras!=null && _player?.IsActive()==true)
        {
            try
            {
                if(!_player.Equipment[EquipmentIndex.Weapon0].IsEmpty)_player.RemoveEquippedWeapon(EquipmentIndex.Weapon0);
                if(!_savedCarrierMain.IsEmpty)_player.EquipWeaponWithNewEntity(EquipmentIndex.Weapon0,ref _savedCarrierMain);
                for(var i=1;i<5;i++)
                {
                    var weapon=_savedCarrierExtras[i-1];
                    if(!weapon.IsEmpty)_player.EquipWeaponWithNewEntity((EquipmentIndex)i,ref weapon);
                }
            }
            catch(Exception e){MinecraftHeroState.LogFailure("native equipment restore",e);}
        }
        _savedCarrierExtras=null;
        _dismountAttempts=0;_nextDismountAt=0;
        ClearWeapons();
        McUsingShield=false;McBlocking=false;McUsingItem=false;_useInputDown=false;_mineInputDown=false;_mineTarget="";
        try{_inventoryUI.Dispose();}catch(Exception e){MinecraftHeroState.LogFailure("inventory teardown",e);}
        _inventory=null;
        try{_blocks?.Dispose();}catch(Exception e){MinecraftHeroState.LogFailure("block teardown",e);}
        _blocks=null;
        try{_tntScene?.Dispose();}catch(Exception e){MinecraftHeroState.LogFailure("TNT teardown",e);}
        _tntScene=null;_primedTnt.Clear();_explosions.Clear();_hasStagingOrigin=false;
        if (IsConnected && _sessionId.Length > 0)
            Send(DemoProtocol.EndSession(_sessionId, 0, reason));
        _conn?.Dispose();
        _conn = null;
        _validator.EndSession();
        _attacks.Clear();
        _hurts.Clear();
        _hotbarWheel.Clear();
        _pendingSelectedSlot=-1;
        _actorIds.Clear(); _knownActors.Clear(); _activeActors.Clear(); _nextActorId=0;
        _inventoryPoll=0;_actorAccumulator=0;_poseAccumulator=0;
        _lastSceneStatus="";
        _lastTntStatus="";
        _containerOpen=false;
        _lastPlayerStateAt=0;_shieldImpactAt=-1000;
        _sessionId = "";
        _state = DemoState.Inactive;
        _endRequested = false;
        if (Current == this)
            Current = null;
        MinecraftHeroState.Log("demo ended: " + reason);
    }
}
