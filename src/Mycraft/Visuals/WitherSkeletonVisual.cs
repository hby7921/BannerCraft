using System;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using Mycraft.Building;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Visuals;

public sealed class WitherSkeletonVisual : IDisposable
{
    private static EntityVisualAsset? _asset;
    private static readonly Dictionary<string,OriginalEntityTexture> Swords=new Dictionary<string,OriginalEntityTexture>(StringComparer.Ordinal);
    private readonly Mission _mission;
    private readonly EntityVisualAsset _model;
    private readonly NativeEntityVisual _body;
    private NativeEntityVisual? _sword;
    private string _held="";
    private bool _disposed;
    public static bool AssetsAvailable=>_asset!=null;
    public static void Cache(string moduleFolder)
    {
        _asset=null;Swords.Clear();string directory=Path.Combine(moduleFolder,"Visuals","entities");
        EntityVisualAsset asset;
        using(var stream=File.OpenRead(Path.Combine(directory,"wither_skeleton.model")))
            asset=new EntityVisualAsset(OriginalEntityModel.Read(stream),new OriginalEntityTexture(Path.Combine(directory,"wither_skeleton.texture")));
        var swords=new Dictionary<string,OriginalEntityTexture>(StringComparer.Ordinal);
        foreach(string material in new[]{"wooden","stone","copper","iron","golden","diamond","netherite"})
        {string id=material+"_sword";swords.Add("minecraft:"+id,new OriginalEntityTexture(Path.Combine(directory,id+".texture")));}
        foreach(var entry in swords)Swords.Add(entry.Key,entry.Value);_asset=asset;
    }
    public WitherSkeletonVisual(Mission mission)
    {_mission=mission;_model=_asset??throw new InvalidOperationException("Original wither skeleton asset missing");_body=new NativeEntityVisual(mission,_model,true);}
    public void Update(Vec3 hostFeet,float hostScale,float bodyYawDegrees,float headYawDegrees,float pitchDegrees,float walkPosition,float walkSpeed,float attackProgress,bool aggressive,int hurtTime,int deathTime,int nativeTick,string mainItem)
    {
        if(_disposed)return;
        var poses=OriginalEntityPose.WitherSkeleton(_model.Model,OriginalEntityPose.RelativeHeadYaw(headYawDegrees,bodyYawDegrees),pitchDegrees,walkPosition,walkSpeed,attackProgress,aggressive,nativeTick,!string.IsNullOrEmpty(mainItem)&&mainItem!="minecraft:air");
        var frames=_model.Model.Frames(poses,OriginalEntityPose.WitherRoot(bodyYawDegrees,deathTime));
        _body.Update(frames,hostFeet,hostScale,hurtTime>0||deathTime>0);
        if(_held!=mainItem)
        {
            _sword?.Dispose();_sword=null;_held=mainItem;
            if(Swords.TryGetValue(mainItem,out var texture))_sword=CreateSword(texture);
        }
        if(_sword!=null)
        {
            int right=_model.Model.Index("/right_arm");int root=_model.Model.Parts[right].Parent;
            var hand=OriginalEntityPose.SwordHand(frames[root],poses[right]);
            _sword.Update(new[]{hand},hostFeet,hostScale);
        }
    }
    private NativeEntityVisual CreateSword(OriginalEntityTexture texture)
    {
        // One generated item part, original 16x16 pixels and one-pixel thickness.
        return new NativeEntityVisual(_mission,new EntityVisualAsset(OriginalEntityModel.SinglePart(EntityModelCutout.GeneratedSword(texture.Pixels)),texture,true));
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_sword?.Dispose();_sword=null;_body.Dispose();}
}
