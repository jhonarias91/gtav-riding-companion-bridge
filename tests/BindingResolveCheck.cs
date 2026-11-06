// Resolves the v1 bindings against the REAL GTBikeV.dll by reflection only (no static constructors, no game).
// Proves member names and CLR types for profile gtbikev-0804; it does not prove runtime behaviour.
//   Run through bridge/Test-BindingResolve.ps1
using System;
using System.IO;
using System.Reflection;
using CompanionBridge.V1;

public static class BindingResolveCheck
{
    public static int Main(string[] args)
    {
        string modDll = args[0];
        string[] probe = new string[args.Length - 1];
        Array.Copy(args, 1, probe, 0, probe.Length);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in probe)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
        Assembly mod = Assembly.LoadFrom(modDll);
        string version = mod.GetName().Version.ToString();
        Func<string, Type> typeOf = delegate(string n) { return mod.GetType(n, false); };
        GtbikeV1Binding binding = new GtbikeV1Binding(typeOf, version);
        GtbikeV1Extensions ext = new GtbikeV1Extensions(typeOf, version == GtbikeV1Binding.SupportedVersion);
        GtbikeV1More more = new GtbikeV1More(typeOf, version == GtbikeV1Binding.SupportedVersion);
        Console.WriteLine("GTBikeV " + version + " -> P2 binding: " + binding.BindingStatus);
        Console.WriteLine("P3/P4 commands: " + string.Join(", ", new System.Collections.Generic.List<string>(ext.Commands()).ToArray()));
        if (ext.Missing.Count > 0) Console.WriteLine("Missing P3/P4 members: " + string.Join(", ", new System.Collections.Generic.List<string>(ext.Missing).ToArray()));
        Console.WriteLine("P5-P7 commands: " + string.Join(", ", new System.Collections.Generic.List<string>(more.Commands()).ToArray()));
        if (more.Missing.Count > 0) Console.WriteLine("Missing P5-P7 members: " + string.Join(", ", new System.Collections.Generic.List<string>(more.Missing).ToArray()));
        bool ok = binding.BindingStatus == "compatible" && ext.Missing.Count == 0 && more.Missing.Count == 0;
        ok &= CheckAssign(mod, more, "FEC", 0xC0FFEE000001UL, "BLE:FEC:C0FFEE000001");  // example trainer C0:FF:EE:00:00:01 (made-up address)
        ok &= CheckAssign(mod, more, "HR", 0xC0FFEE000002UL, "BLE:HR:C0FFEE000002");    // example chest strap C0:FF:EE:00:00:02
        ok &= CheckAssign(mod, more, "BTS", 0xC0FFEE000003UL, "BLE:BTS:C0FFEE000003");  // example STERZO C0:FF:EE:00:00:03
        ok &= CheckName(more, "KICKR 1984", "Wahoo Fitness", "Wahoo Fitness KICKR 1984");
        ok &= CheckName(more, "Wahoo KICKR", "Wahoo", "Wahoo KICKR");
        Console.WriteLine(ok ? "RESOLVE PASS" : "RESOLVE FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>P12: the name the adapter reports is the mod's own BLEDeviceInfo.Name (manufacturer + advertised name).</summary>
    private static bool CheckName(GtbikeV1More more, string deviceName, string manufacturer, string expected)
    {
        const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object device = more.BleDeviceForCheck("FEC", 0xC0FFEE000001UL);
        if (device == null) { Console.WriteLine("P12 name: no device built"); return false; }
        FieldInfo di = device.GetType().GetField("_di", I);
        if (di == null) { Console.WriteLine("P12 name: BLEDeviceInfo._di missing"); return false; }
        object record = di.GetValue(device);
        record.GetType().GetField("DeviceName").SetValue(record, deviceName);
        record.GetType().GetField("ManufacturerName").SetValue(record, manufacturer);
        di.SetValue(device, record);
        object name = device.GetType().GetProperty("Name", I).GetValue(device, null);
        bool ok = object.Equals(name, expected);
        Console.WriteLine("P12 name -> " + name + (ok ? " OK" : " MISMATCH (expected " + expected + ")"));
        return ok;
    }

    /// <summary>
    /// P11: the mod's own BLEDeviceInfo built by devices.assign must report the phone's MAC as DeviceId, and the
    /// rule the mod would write (DeviceConfigSerialized.ToString, as in ChangeDeviceConfig) must name that address.
    /// </summary>
    private static bool CheckAssign(Assembly mod, GtbikeV1More more, string type, ulong address, string expectedRule)
    {
        const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object device = more.BleDeviceForCheck(type, address);
        if (device == null) { Console.WriteLine("P11 " + type + ": no device built"); return false; }
        Type t = device.GetType();
        object id = t.GetProperty("DeviceId", I).GetValue(device, null);
        object sensor = t.GetProperty("SensorType", I).GetValue(device, null);
        object source = t.GetProperty("DataSourceType", I).GetValue(device, null);
        Type serialized = mod.GetType("GTBikeV.Devices.DeviceConfigSerialized", true);
        object entry = Activator.CreateInstance(serialized, true);
        serialized.GetProperty("DeviceId", I).SetValue(entry, id, null);
        serialized.GetProperty("SensorType", I).SetValue(entry, sensor, null);
        serialized.GetProperty("DataSourceType", I).SetValue(entry, source, null);
        serialized.GetProperty("Exclude", I).SetValue(entry, false, null);
        string rule = entry.ToString();
        bool ok = object.Equals(id, address) && rule == expectedRule;
        Console.WriteLine("P11 " + type + " -> DeviceId " + string.Format("{0:X12}", id) + ", rule " + rule + (ok ? " OK" : " MISMATCH (expected " + expectedRule + ")"));
        return ok;
    }
}
