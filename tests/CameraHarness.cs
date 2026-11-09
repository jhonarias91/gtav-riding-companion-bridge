using System;
using System.Collections.Generic;
using CompanionBridge.V1;

public sealed class CameraGame : IGtbikeV1CameraGame
{
    public string Mode = "near";
    public float Heading, Pitch;
    public int Pulses, Writes;
    public GtbikeV1CameraReading Read() { return new GtbikeV1CameraReading { Mode = Mode, Heading = Heading, Pitch = Pitch }; }
    public void SetMode(string mode) { Mode = mode; Writes++; }
    public void Next() { Pulses++; Mode = Mode == "near" ? "medium" : "far"; }
    public void Look(float heading, float pitch) { Heading = heading; Pitch = pitch; Writes++; }
}
public static class CameraHarness
{
    private static void Equal(object actual, object expected, string name) { if (!object.Equals(actual, expected)) throw new Exception(name + ": " + actual + " != " + expected); }
    public static int Main()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            bool behind = false;
            CameraGame game = new CameraGame();
            GtbikeV1Camera camera = new GtbikeV1Camera(game);
            camera.Context(true, "inControl", () => behind, now);
            Equal(camera.Command("camera.next", "next", new Dictionary<string, object>(), now)["status"], "dispatched", "next dispatched");
            camera.Frame(now); camera.Frame(now.AddMilliseconds(16)); camera.Frame(now.AddMilliseconds(32));
            Equal(game.Pulses, 1, "NextCamKey control emitted exactly once");
            Equal(((Dictionary<string, object>)camera.Capture(now.AddMilliseconds(32))["command"])["status"], "done", "next read back");
            game.Heading = 170;
            camera.Command("camera.look", "look", new Dictionary<string, object> { { "headingDeg", -170 }, { "pitchDeg", 20 } }, now);
            camera.Frame(now.AddMilliseconds(225));
            if (Math.Abs(game.Heading) < 175 || game.Pitch <= 0 || game.Pitch >= 20) throw new Exception("rotation did not interpolate along shortest arc");
            camera.Frame(now.AddMilliseconds(450)); camera.Frame(now.AddMilliseconds(466));
            Equal(game.Heading, -170f, "final heading observed");
            Equal(((Dictionary<string, object>)camera.Capture(now.AddMilliseconds(466))["command"])["status"], "done", "rotation read back");
            behind = true;
            Equal(camera.Command("camera.look", "blocked", new Dictionary<string, object> { { "headingDeg", 0 }, { "pitchDeg", 0 } }, now)["reasonCode"], "CAMERA_CONTROL_IN_USE", "mod look-behind takes priority");
            behind = false;
            camera.Command("camera.look", "interrupt", new Dictionary<string, object> { { "headingDeg", 0 }, { "pitchDeg", 0 } }, now);
            behind = true;
            int writes = game.Writes;
            camera.Frame(now.AddMilliseconds(16));
            Equal(game.Writes, writes, "an orbit stops when the physical controller takes over");
            Equal(camera.Command("camera.setMode", "stale", new Dictionary<string, object> { { "mode", "far" } }, now.AddSeconds(2))["reasonCode"], "ADAPTER_STALE", "expired context never moves camera");
            camera.Context(true, "inControl", () => false, now);
            camera.Command("camera.look", "abort", new Dictionary<string, object> { { "headingDeg", 0 }, { "pitchDeg", 0 } }, now);
            camera.Abort(); camera.Frame(now.AddMilliseconds(20));
            Equal(game.Writes, writes, "script abort never leaves an active camera job");
            Console.WriteLine("PASS: NextCamKey pulse once; observed mode; smooth shortest-arc yaw/pitch; mod controller priority; stale/abort cancellation. Mock camera only, not in-game validation.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
