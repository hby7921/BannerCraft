using System;
using System.IO;

using Mycraft.Bridge;

namespace Mycraft.Demo;

/// <summary>
/// Demo battle config: Config/demo.json next to SubModule.xml. Enables the demo
/// gate explicitly (nothing is hijacked without this file) and holds tunables.
/// </summary>
public sealed class DemoConfig
{
    public bool Enabled;
    public bool ModelTestLights;
    public int Port = 38765;
    public double PlayerHurtFactor = 1.0;
    public double McHitFactor = 1.0;
    public double McCreatureDamageFactor = 5.0;
    public double GroundY = 128.0;
    public double OffsetX;
    public double OffsetZ;
    public double Scale = 1.0;
    public double YawOffsetDeg;

    public DemoTransform Transform => new DemoTransform(Scale, OffsetX, OffsetZ, GroundY, YawOffsetDeg);

    public static string ModuleFolder => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(DemoConfig).Assembly.Location) ?? ".", "..", ".."));

    public static DemoConfig Load(string modulePath)
    {
        try
        {
            var path = Path.Combine(modulePath, "Config", "demo.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = new DemoConfig();
                Apply(config, json);
                if (config.Port < 1 || config.Port > 65535 ||
                    !CombatEventLedger.IsFinite(config.Scale) || config.Scale <= 0 || config.Scale > 10 ||
                    !CombatEventLedger.IsFinite(config.PlayerHurtFactor) || config.PlayerHurtFactor <= 0 || config.PlayerHurtFactor > 10 ||
                    !CombatEventLedger.IsFinite(config.McHitFactor) || config.McHitFactor <= 0 || config.McHitFactor > 10 ||
                    !CombatEventLedger.IsFinite(config.McCreatureDamageFactor) || config.McCreatureDamageFactor <= 0 || config.McCreatureDamageFactor > 20 ||
                    !CombatEventLedger.IsFinite(config.GroundY) || !CombatEventLedger.IsFinite(config.OffsetX) ||
                    !CombatEventLedger.IsFinite(config.OffsetZ) || !CombatEventLedger.IsFinite(config.YawOffsetDeg))
                    throw new FormatException("Invalid demo numeric settings");
                return config;
            }
        }
        catch (Exception e)
        {
            MycraftBridgeLog.Failure("demo config load", e);
        }
        return new DemoConfig();
    }

    private static void Apply(DemoConfig config, string json)
    {
        if (BridgeJson.TryParse(json, out var obj))
        {
            config.Enabled = BridgeJson.B(obj, "enabled", false);
            config.ModelTestLights = BridgeJson.B(obj, "modelTestLights", false);
            config.Port = (int)BridgeJson.L(obj, "port", DemoProtocol.DefaultPort);
            config.PlayerHurtFactor = BridgeJson.D(obj, "playerHurtFactor", 1.0);
            config.McHitFactor = BridgeJson.D(obj, "mcHitFactor", 1.0);
            config.McCreatureDamageFactor = BridgeJson.D(obj, "mcCreatureDamageFactor", 5.0);
            config.GroundY = BridgeJson.D(obj, "groundY", 128.0);
            config.OffsetX = BridgeJson.D(obj, "offsetX", 0.0);
            config.OffsetZ = BridgeJson.D(obj, "offsetZ", 0.0);
            config.Scale = BridgeJson.D(obj, "scale", 1.0);
            config.YawOffsetDeg = BridgeJson.D(obj, "yawOffsetDeg", 0.0);
        }
        else throw new FormatException("Malformed demo.json");
    }

    public static void WriteDefault(string modulePath)
    {
        try
        {
            var dir = Path.Combine(modulePath, "Config");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "demo.json");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "{\n  \"enabled\": false,\n  \"port\": 38765,\n  \"playerHurtFactor\": 1.0,\n  \"mcHitFactor\": 1.0,\n  \"groundY\": 128.0,\n  \"offsetX\": 0.0,\n  \"offsetZ\": 0.0,\n  \"scale\": 1.0,\n  \"yawOffsetDeg\": 0.0\n}\n");
            }
        }
        catch (Exception e)
        {
            MycraftBridgeLog.Failure("demo config write", e);
        }
    }
}
