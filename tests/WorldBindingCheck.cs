using System;
using System.IO;
using System.Reflection;
using CompanionBridge.V1;

public static class WorldBindingCheck
{
    public static int Main(string[] args)
    {
        string mod = args[0];
        string gta = args[1];
        string[] roots = { Path.GetDirectoryName(mod), gta, Path.Combine(gta, "scripts") };
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string root in roots)
            {
                string file = Path.Combine(root, name);
                if (File.Exists(file)) return Assembly.LoadFrom(file);
            }
            return null;
        };
        Assembly shvdn = Assembly.LoadFrom(Path.Combine(gta, "ScriptHookVDotNet3.dll"));
        Assembly assembly = Assembly.LoadFrom(mod);
        Type vector = shvdn.GetType("GTA.Math.Vector3", true);
        GtbikeV1World world = new GtbikeV1World(name => assembly.GetType(name, false), vector);
        Console.WriteLine("GTBikeV {0} | SHVDN {1} | P8 waypoints: {2} | P8 riders: {3}",
            assembly.GetName().Version, shvdn.GetName().Version, world.CanReadWaypoints, world.CanReadRiders);
        return assembly.GetName().Version.ToString() == "0.8.0.4" && world.CanReadWaypoints && world.CanReadRiders ? 0 : 1;
    }
}
