using System;
using System.Diagnostics;
using Mycraft.Building;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// Root installs this only for --mycraft-tnt-scene-smoke and omits the network
// bridge. --mycraft-model-smoke may open the test battle. These are synthetic
// authority inputs: this validates native visuals/physics, not Minecraft logic.
public sealed class TntSceneSmokeBehavior : MissionBehavior
{
    private static Mission? _pendingEnd;
    private static WeakReference<Mission>? _monitorMission;
    private static int _endDelayTicks;
    private static bool _monitorActive,_endObserved,_cleanupFailed;
    private static long _endRequestedAt,_observeStartedAt;
    private static bool _deathObserving;
    private static int _deathApplicationTicks;
    private static readonly bool DeathSmoke=Array.Exists(Environment.GetCommandLineArgs(),arg=>arg=="--mycraft-tnt-death-smoke");
    private BattleBlockScene? _blocks;
    private TntScene? _tnt;
    private WeaponSceneSmoke? _weapons;
    private float _age,_phaseAge,_lastState;
    private int _phase,_round,_x,_z;
    private float _anchor;
    private bool _started,_finished,_sawParticles;
    private bool _capturedSmoke,_capturedFlash,_capturedBlast;
    private bool _capturedReference;
    private bool _deathRequested,_deathObserved,_deathDeleted;
    private Agent? _deathAgent;
    private long _deathStartedAt;
    private int _deathMissionTicks;
    public override MissionBehaviorType BehaviorType=>MissionBehaviorType.Other;
    public override void OnMissionTick(float dt)
    {
        if(!BlockGeometry.Finite(dt)||dt<=0)return;
        try
        {
            var player=Mission.MainAgent;
            // This behavior exists only under the explicit diagnostic flag.
            // Keep ordinary battle death out of the controlled teardown test.
            if(!_deathRequested&&player!=null&&player.IsActive())player.Health=player.HealthLimit;
            if(_finished)return;
            _age+=dt;
            if(!_started)
            {
                if(_age<2||player==null||!player.IsActive()||Mission.Mode!=MissionMode.Battle)return;
                var forward=player.LookDirection;forward.z=0;if(forward.Normalize()<.01f)forward=new Vec3(0,1,0);
                _x=BlockGeometry.Cell(player.Position.x+forward.x*4);_z=BlockGeometry.Cell(player.Position.y+forward.y*4);_anchor=player.Position.z+1;
                _blocks=new BattleBlockScene(Mission);_blocks.SetSession(Guid.NewGuid().ToString("N"));_blocks.SetOrigin(0,_anchor);
                _tnt=new TntScene(Mission);_tnt.SetOrigin(0,_anchor);_started=true;
                if(Array.Exists(Environment.GetCommandLineArgs(),arg=>arg=="--mycraft-weapon-scene-smoke"))_weapons=new WeaponSceneSmoke(Mission,_anchor,_x,_z);
                DemoLog.Diagnostic("TNT_NATIVE_SMOKE_BEGIN: native prefab boxes x3; synthetic fuse/VFX; actual MC receipt through production damage receiver; no network/audio");
            }
            _phaseAge+=dt;_tnt!.Tick(dt);
            if(_phase==4)_weapons?.Tick(_phaseAge,dt);
            if(_tnt.ActiveParticleCount>0)_sawParticles=true;
            if(_tnt.Status.Length>0)throw new InvalidOperationException(_tnt.Status);
            if(_phase==0)
            {
                _blocks!.ApplySmokeSnapshot(_x,0,_z,"00000000000000000000000000000001",TntScene.SmokeSnapshot(false));
                _blocks.ApplySmokeSnapshot(_x+2,0,_z,"00000000000000000000000000000002",TntScene.SmokeSnapshot(true));
                _phase=1;_phaseAge=0;
            }
            else if(_phase==1&&_phaseAge>.75f)
            {
                bool full=_blocks!.ProbeSmokeCollision(_x,0,_z),compound=_blocks.ProbeSmokeCollision(_x+2,0,_z);
                if(!full||!compound)
                {
                    DemoLog.Diagnostic("TNT_SMOKE_BASELINE_FAILED full="+full+", compound="+compound+"; starting isolated owner/visibility controls");
                    _blocks.SetSmokeControls(1);_phase=5;_phaseAge=0;return;
                }
                DemoLog.Diagnostic("TNT_NATIVE_SMOKE_BODY_PASS: cycle="+(_round+1)+", full cube + 2 independent AABBs ray-hit");
                _blocks.Apply(_x,0,_z,"minecraft:air",0,_anchor);_blocks.Apply(_x+2,0,_z,"minecraft:air",0,_anchor);
                _phase=2;_phaseAge=0;
            }
            else if(_phase==2&&_phaseAge>.75f)
            {
                bool full=_blocks!.ProbeSmokeEmpty(_x,0,_z),compound=_blocks.ProbeSmokeEmpty(_x+2,0,_z);
                if(!full||!compound)throw new InvalidOperationException("Removed smoke collider still blocks its original ray: full="+full+", compound="+compound);
                DemoLog.Diagnostic("TNT_NATIVE_SMOKE_REMOVE_PASS: cycle="+(_round+1));
                _round++;_phaseAge=0;
                if(_round<3)_phase=0;
                else{_phase=10;}
            }
            else if(_phase==10)
            {
                _tnt.ShowReferenceSprites(_x+.5f,.5f,_z+.5f,true);
                if(!_capturedReference&&_phaseAge>=.75f){_capturedReference=true;Capture("tnt-alpha-reference");}
                if(_phaseAge>=3){_tnt.ShowReferenceSprites(0,0,0,false);_phase=3;_phaseAge=0;_lastState=-1;}
            }
            else if(_phase==3)
            {
                if(!_capturedSmoke&&_phaseAge>=.8f){_capturedSmoke=true;Capture("tnt-smoke");}
                if(!_capturedFlash&&_phaseAge>=1.5f){_capturedFlash=true;Capture("tnt-flash");}
                if(_phaseAge-_lastState>=.05f)
                {
                    _lastState=_phaseAge;int fuse=Math.Max(0,80-(int)(_phaseAge*20));
                    // Motion test input only; production coordinates come from PrimedTnt.
                    float bob=(float)(Math.Abs(Math.Sin(_phaseAge*8))*Math.Exp(-_phaseAge*2)*.3);
                    _tnt.ApplyState("smoke-primed-tnt",_x+.5f, bob,_z+.5f,fuse,true);
                }
                if(_phaseAge>=4)
                {
                    if(_tnt.ActiveCount!=1)throw new InvalidOperationException("Primed TNT was not rendered through its fuse");
                    _tnt.ApplyState("smoke-primed-tnt",_x+.5f,0,_z+.5f,0,false);
                    _tnt.Explode("smoke-blast",_x+.5f,.5f,_z+.5f,4);_phase=4;_phaseAge=0;
                }
            }
            else if(_phase==4&&_phaseAge>2.5f)
            {
                if(!_sawParticles||_tnt.ActiveCount!=0||_tnt.ActiveParticleCount!=0||_tnt.AnimationFramesVisited<4||
                    _tnt.SmokeParticlesEmitted==0||_tnt.SmokeFramesVisited<2)
                    throw new InvalidOperationException("TNT visual animation/cleanup verification failed");
                int frames=_tnt.AnimationFramesVisited;
                _weapons?.Verify();
                DemoBridgeMissionBehavior.VerifyTntDamageSmoke(Mission);
                DemoLog.Diagnostic("TNT_NATIVE_SMOKE_PASS: 3 create/raycast/remove cycles, fuse=80 ticks, explosion material frames="+frames+
                    ", smokeEmitted="+_tnt.SmokeParticlesEmitted+", smokeFrames="+_tnt.SmokeFramesVisited+
                    ", production MC receipt damage verified, no remaining TNT or particles");
                if(DeathSmoke)
                {
                    if(player==null||!player.IsActive())throw new InvalidOperationException("Death smoke needs a living native player");
                    _deathAgent=player;_deathRequested=true;_deathObserving=true;_deathApplicationTicks=0;_deathMissionTicks=0;
                    _deathStartedAt=Stopwatch.GetTimestamp();_phase=9;_phaseAge=0;
                    DemoLog.Diagnostic("TNT_NATIVE_DEATH_REQUEST: production MC death helper without attacker; no health refill after this point");
                    NativeMcDeath.Apply(Mission,player,null);
                }
                else{_finished=true;QueueNormalEnd();}
            }
            else if(_phase==4&&!_capturedBlast&&_phaseAge>=.2f)
            {
                _capturedBlast=true;Capture("tnt-blast");
                if(_weapons!=null)Capture("weapons-arrow-trident-lightning");
            }
            else if(_phase==5&&_phaseAge>.25f)
            {
                bool owner=_blocks!.ProbeSmokeControls("OWNER_ONLY");
                _blocks.SetSmokeControls(owner?0:2);_phase=owner?8:6;_phaseAge=0;
            }
            else if(_phase==6&&_phaseAge>.25f)
            {
                _blocks!.ProbeSmokeControls("OWNER_AND_VISIBLE");_blocks.SetSmokeControls(3);_phase=7;_phaseAge=0;
            }
            else if(_phase==7&&_phaseAge>.25f)
            {
                _blocks!.ProbeSmokeControls("VISIBLE_ONLY");_blocks.SetSmokeControls(0);_phase=8;_phaseAge=0;
            }
            else if(_phase==8&&_phaseAge>.25f)
            {
                _blocks!.ProbeSmokeControls("RESTORED");
                throw new InvalidOperationException("Production collision baseline failed; owner/visibility A/B diagnostics completed and original state restored");
            }
            else if(_phase==9)
            {
                _deathMissionTicks++;
                double elapsed=(Stopwatch.GetTimestamp()-_deathStartedAt)/(double)Stopwatch.Frequency;
                if(!_deathObserved&&elapsed>2)throw new InvalidOperationException("Agent.Die did not produce a genuine killed/unconscious removal callback");
                if(_deathObserved&&player!=null&&ReferenceEquals(player,_deathAgent)&&player.IsActive())
                    throw new InvalidOperationException("Death smoke player unexpectedly became active again");
                if(elapsed>=45)
                {
                    if(!_deathObserved||_deathMissionTicks<20||_deathApplicationTicks<20)
                        throw new InvalidOperationException("Death observation did not cover active application and mission ticks");
                    DemoLog.Diagnostic("TNT_NATIVE_DEATH_PASS: inactive for "+elapsed.ToString("F1")+"s, missionTicks="+_deathMissionTicks+
                        ", appTicks="+_deathApplicationTicks+", deletedCallback="+_deathDeleted);
                    _deathObserving=false;_finished=true;QueueNormalEnd();
                }
            }
        }
        catch(Exception e){_finished=true;DemoLog.Diagnostic("TNT_NATIVE_SMOKE_FAIL: "+e);Cleanup();}
    }
    private static void Capture(string name)
    {
        try
        {
            var path=System.IO.Path.Combine(DemoConfig.ModuleFolder,"Logs",name+".png");
            DemoLog.Diagnostic("TNT_NATIVE_SCREENSHOT_BEGIN: "+path);
            var capture=typeof(TaleWorlds.Engine.Utilities).GetMethod("TakeScreenshotAsPng",new[]{typeof(string)});
            if(capture==null)throw new NotSupportedException("This game engine has no PNG capture API");
            capture.Invoke(null,new object[]{path});
            DemoLog.Diagnostic("TNT_NATIVE_SCREENSHOT_REQUEST: "+path);
        }
        catch(Exception e){DemoLog.Diagnostic("TNT_NATIVE_SCREENSHOT_FAILED: "+e.Message);}
    }
    public static void ApplicationTick(float dt)
    {
        if(_deathObserving)_deathApplicationTicks++;
        if(!_monitorActive)return;
        try
        {
            if(_pendingEnd!=null)
            {
                if(_endDelayTicks-->0)return;
                var endingMission=_pendingEnd;_pendingEnd=null;_endRequestedAt=Stopwatch.GetTimestamp();
                DemoLog.Diagnostic("TNT_NATIVE_TEARDOWN_REQUEST: Mission.EndMission");
                // Upstream EndMission schedules EndingNextFrame. Calling from
                // app tick also avoids mutating the behavior iteration itself.
                endingMission.EndMission();return;
            }
            long now=Stopwatch.GetTimestamp();
            if(_cleanupFailed)throw new InvalidOperationException("Smoke cleanup threw during normal OnEndMission");
            bool finalized=_endObserved&&(_monitorMission==null||!_monitorMission.TryGetTarget(out var observedMission)||observedMission.CurrentState==Mission.State.Over);
            if(!finalized)
            {
                if(_endRequestedAt!=0&&(now-_endRequestedAt)/(double)Stopwatch.Frequency>30)
                    throw new InvalidOperationException("Normal mission teardown did not reach OnEndMission and State.Over within 30 seconds");
                return;
            }
            if(_observeStartedAt==0)
            {
                _observeStartedAt=now;
                DemoLog.Diagnostic("TNT_NATIVE_TEARDOWN_OBSERVE: OnEndMission completed and native mission reached State.Over");
                return;
            }
            if((now-_observeStartedAt)/(double)Stopwatch.Frequency<10)return;
            DemoLog.Diagnostic("TNT_NATIVE_TEARDOWN_PASS: normal EndMission, resource cleanup, State.Over, and 10 seconds of application ticks survived");
            _monitorActive=false;_monitorMission=null;
        }
        catch(Exception e)
        {
            _monitorActive=false;_pendingEnd=null;_monitorMission=null;
            DemoLog.Diagnostic("TNT_NATIVE_TEARDOWN_FAIL: "+e);
        }
    }
    private void QueueNormalEnd()
    {
        _pendingEnd=Mission;_monitorMission=new WeakReference<Mission>(Mission);_monitorActive=true;
        _endDelayTicks=1;_endObserved=_cleanupFailed=false;_endRequestedAt=_observeStartedAt=0;
        DemoLog.Diagnostic("TNT_NATIVE_TEARDOWN_QUEUED: normal Mission.EndMission on a later application tick");
    }
    public override void OnAgentRemoved(Agent affectedAgent,Agent affectorAgent,AgentState agentState,KillingBlow blow)
    {
        if(_deathRequested&&ReferenceEquals(affectedAgent,_deathAgent))
        {
            _deathObserved=agentState==AgentState.Killed||agentState==AgentState.Unconscious;
            DemoLog.Diagnostic("TNT_NATIVE_DEATH_REMOVED: state="+agentState+", genuineInactive="+_deathObserved);
        }
        base.OnAgentRemoved(affectedAgent,affectorAgent,agentState,blow);
    }
    public override void OnAgentDeleted(Agent affectedAgent)
    {
        if(_deathRequested&&ReferenceEquals(affectedAgent,_deathAgent))
        {_deathDeleted=true;_deathAgent=null;DemoLog.Diagnostic("TNT_NATIVE_DEATH_DELETED");}
        base.OnAgentDeleted(affectedAgent);
    }
    private bool Cleanup()
    {
        bool clean=true;
        try{_weapons?.Dispose();}catch(Exception e){clean=false;DemoLog.Diagnostic("Weapon scene cleanup failed: "+e);}_weapons=null;
        try{_tnt?.Dispose();}catch(Exception e){clean=false;DemoLog.Diagnostic("TNT smoke visual cleanup failed: "+e);}
        try{_blocks?.Dispose();}catch(Exception e){clean=false;DemoLog.Diagnostic("TNT smoke collider cleanup failed: "+e);}
        _tnt=null;_blocks=null;
        return clean;
    }
    protected override void OnEndMission()
    {
        if(_deathRequested&&!_finished)DemoLog.Diagnostic("TNT_NATIVE_DEATH_FAIL: mission ended before the 45-second death observation completed");
        _deathObserving=false;
        DemoLog.Diagnostic("TNT_NATIVE_TEARDOWN_ON_END_ENTER");
        bool clean=Cleanup();
        if(_monitorActive&&_monitorMission!=null&&_monitorMission.TryGetTarget(out var observed)&&ReferenceEquals(observed,Mission))
        {_cleanupFailed|=!clean;_endObserved=true;}
        _finished=true;
        DemoLog.Diagnostic("TNT_NATIVE_TEARDOWN_ON_END_COMPLETE: cleanup="+clean);
        base.OnEndMission();
    }
    public override void OnRemoveBehavior(){Cleanup();base.OnRemoveBehavior();}
}
