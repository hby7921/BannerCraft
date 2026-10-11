using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Mycraft.Demo;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Building;

// Keep managed native-resource wrappers until their Mission becomes collectible.
// NativeObject owns reference-counted release; PhysicsShape.Clear is a destructive
// mutation, not a release API. No mission-tick eviction invalidates engine jobs.
internal sealed class NativeSceneResources
{
    private static readonly ConditionalWeakTable<Mission,NativeSceneResources> Pools=new ConditionalWeakTable<Mission,NativeSceneResources>();
    private const long Limit=128L*1024*1024;
    private readonly List<object> _references=new List<object>();
    private long _bytes;
    public static NativeSceneResources For(Mission mission)=>Pools.GetValue(mission,_=>new NativeSceneResources());
    public void Check(long bytes=0)
    {
        if(bytes<0||_bytes+bytes>Limit||_references.Count>=16384)
            throw new InvalidOperationException("Mission native-resource retention budget reached");
    }
    public void Hold(object value,long bytes=0){Check(bytes);_references.Add(value);_bytes+=bytes;}
}

internal static class NativeBlockCollider
{
    public static GameEntity Create(Mission mission,BlockBox local,Vec3 cellOrigin)
    {
        var native=BlockGeometry.NativeBox(local);
        var size=new Vec3(native.MaxX-native.MinX,native.MaxY-native.MinY,native.MaxZ-native.MinZ);
        if(size.x<=0||size.y<=0||size.z<=0)throw new ArgumentException("Invalid collider AABB");
        var pool=NativeSceneResources.For(mission);pool.Check(2048);
        if(!GameEntity.PrefabExists("editor_cube"))throw new InvalidOperationException("Native editor_cube prefab unavailable");
        var frame=MatrixFrame.Identity;frame.origin=cellOrigin;
        DemoLog.Diagnostic("BLOCK native: instantiate editor_cube at "+cellOrigin);
        // This overload creates the prefab's actual bo_editor_cube body. Never
        // copy/transform/clear its shared PhysicsShape or replace it via AddPhysics.
        var entity=GameEntity.Instantiate(mission.Scene,"editor_cube",frame,false);
        if(entity==null)throw new InvalidOperationException("Native cube instantiation failed");
        pool.Hold(entity,2048);
        try
        {
            bool stateBeforeStrip=entity.GetPhysicsState();
            var shape=entity.GetBodyShape();
            if(shape==null)throw new InvalidOperationException("Native cube has no prefab physics shape");
            shape.GetBoundingBox(out var bounds);
            var dimensions=bounds.max-bounds.min;
            if(!Finite(dimensions)||dimensions.x<.01f||dimensions.y<.01f||dimensions.z<.01f)
                throw new InvalidOperationException("Invalid native cube resource bounds");
            // Native editor_contents.xml defines <physics shape="bo_editor_cube">
            // independently of its <meta_mesh_component>. Remove only this new
            // instance's render component. Hiding the GameEntity itself makes
            // Scene raycasts skip it (confirmed by the native smoke A/B run).
            int renderParts=entity.MultiMeshComponentCount;
            for(int i=renderParts-1;i>=0;i--)
            {
                var render=entity.GetMetaMesh(i);pool.Hold(render,1024);
                if(!entity.RemoveComponent(render))throw new InvalidOperationException("Could not detach native cube renderer");
            }
            if(entity.MultiMeshComponentCount!=0)throw new InvalidOperationException("Native cube render components remain attached");
            var remainingShape=entity.GetBodyShape();
            if(remainingShape==null||!remainingShape.IsValid||(!entity.HasPhysicsBody()&&!entity.HasStaticPhysicsBody()))
                throw new InvalidOperationException("Removing the prefab renderer removed its independent physics body");
            remainingShape.GetBoundingBox(out var remainingBounds);
            if((remainingBounds.min-bounds.min).LengthSquared>.000001f||(remainingBounds.max-bounds.max).LengthSquared>.000001f)
                throw new InvalidOperationException("Prefab physics bounds changed when its renderer was detached");
            entity.SetVisibilityExcludeParents(true);
            DemoLog.Diagnostic("BLOCK native: detached own render components="+renderParts+", remaining="+entity.MultiMeshComponentCount+
                ", shapeBefore="+shape.Pointer+", shapeAfter="+remainingShape.Pointer+", hasStatic="+entity.HasStaticPhysicsBody()+", visible="+entity.GetVisibilityExcludeParents());
            var transform=BlockGeometry.PrefabFrame(local,new BlockBox(bounds.min.x,bounds.min.y,bounds.min.z,bounds.max.x,bounds.max.y,bounds.max.z));
            frame.rotation.s=new Vec3(transform[0],0,0);
            frame.rotation.f=new Vec3(0,transform[1],0);
            frame.rotation.u=new Vec3(0,0,transform[2]);
            frame.origin=cellOrigin+new Vec3(transform[3],transform[4],transform[5]);
            DemoLog.Diagnostic("BLOCK native: position prefab body, scale="+size+", resource="+dimensions);
            entity.SetGlobalFrame(frame);
            // Terrain sampling explicitly excludes BodyOwnerEntity. Retain the
            // owner flag so placed blocks never become part of the host ground.
            entity.AddBodyFlags(BodyFlags.NotDestructible|BodyFlags.BodyOwnerEntity);
            bool stateBeforeEnable=entity.GetPhysicsState();
            // Native MissionObject/DestructableComponent explicitly enable
            // physics after visibility/frame changes. A body wrapper alone is
            // not proof that it is participating in collision queries.
            entity.SetPhysicsState(true,true);
            entity.SetFrameChanged();
            if(!entity.GetPhysicsState()||(!entity.HasPhysicsBody()&&!entity.HasStaticPhysicsBody()))throw new InvalidOperationException("Prefab cube collision is not enabled");
            var globalBounds=entity.ComputeGlobalPhysicsBoundingBoxMinMax();
            DemoLog.Diagnostic("BLOCK native: prefab collision ready pointer="+entity.Pointer+", stateBeforeStrip="+stateBeforeStrip+
                ", stateBeforeEnable="+stateBeforeEnable+", stateAfterEnable="+entity.GetPhysicsState()+", visible="+entity.GetVisibilityExcludeParents()+
                ", flags="+entity.BodyFlag+", frame="+entity.GetGlobalFrame().origin+", bounds="+globalBounds.Item1+".."+globalBounds.Item2);
            return entity;
        }
        catch {entity.Remove(0);throw;}
    }
    private static bool Finite(Vec3 v)=>BlockGeometry.Finite(v.x)&&BlockGeometry.Finite(v.y)&&BlockGeometry.Finite(v.z);
}
