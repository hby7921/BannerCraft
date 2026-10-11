using System;
using System.Collections.Generic;
using System.IO;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Visuals;

public sealed class BoatVisual : IDisposable
{
    private static readonly Dictionary<string,EntityVisualAsset> Assets=new Dictionary<string,EntityVisualAsset>(StringComparer.Ordinal);
    private readonly EntityVisualAsset _model;
    private readonly NativeEntityVisual _visual;
    private bool _disposed;
    public static bool AssetsAvailable=>Assets.Count==22;
    public static bool SupportsKind(string kind)=>Assets.ContainsKey(Normalize(kind));
    private static string Normalize(string kind)=>kind.StartsWith("minecraft:",StringComparison.Ordinal)?kind.Substring(10):kind;
    public static void Cache(string moduleFolder)
    {
        Assets.Clear();var models=new Dictionary<string,OriginalEntityModel>(StringComparer.Ordinal);string directory=Path.Combine(moduleFolder,"Visuals","entities");
        foreach(string name in new[]{"boat","chest_boat","raft","chest_raft"})using(var stream=File.OpenRead(Path.Combine(directory,name+".model")))models.Add(name,OriginalEntityModel.Read(stream));
        foreach(string wood in new[]{"oak","spruce","birch","jungle","acacia","dark_oak","mangrove","cherry","pale_oak","poplar","bamboo"})
        foreach(bool chest in new[]{false,true})
        {
            string model=wood=="bamboo"?(chest?"chest_raft":"raft"):(chest?"chest_boat":"boat");
            string kind=wood+"_"+model;Assets.Add(kind,new EntityVisualAsset(models[model],new OriginalEntityTexture(Path.Combine(directory,kind+".texture"))));
        }
    }
    public BoatVisual(Mission mission,string kind)
    {
        if(!Assets.TryGetValue(Normalize(kind),out var model))throw new ArgumentException("Unsupported original boat kind: "+kind);
        _model=model;_visual=new NativeEntityVisual(mission,model);
    }
    public void Update(Vec3 hostPosition,float hostScale,float yawDegrees,float pitchDegrees,float leftPaddle,float rightPaddle,float hurtTime,float damage,float bubbleAngle,int nativeTick,int hurtDir=1,bool underWater=false)
    {
        if(_disposed)return;
        // Vanilla AbstractBoatRenderer intentionally uses yaw, not entity pitch.
        var pose=OriginalEntityPose.Boat(_model.Model,leftPaddle,rightPaddle);
        _visual.Update(_model.Model.Frames(pose,OriginalEntityPose.BoatRoot(yawDegrees,hurtTime,damage,bubbleAngle,hurtDir,underWater)),hostPosition,hostScale);
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_visual.Dispose();}
}
