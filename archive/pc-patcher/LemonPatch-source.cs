using System;
using System.IO;
using System.Linq;
using MelonLoader.Installer.Core;
using AssetRipper.Primitives;

// usage: LemonPatch <folder with the game's .apk files> <output folder> <melon_data.zip> [unity version]
if (args.Length < 3) { Console.WriteLine("usage: LemonPatch <apk folder> <output folder> <melon_data.zip> [unity version]"); return 2; }
string src = Path.GetFullPath(args[0]), outDir = Path.GetFullPath(args[1]), melon = Path.GetFullPath(args[2]);
string unity = args.Length > 3 ? args[3] : "6000.2.6f2";
const string package = "com.Everguild.WarhammerWarpforge";

string[] apks = Directory.GetFiles(src, "*.apk");
string? lib = apks.FirstOrDefault(a => Path.GetFileName(a).StartsWith("config.arm64", StringComparison.OrdinalIgnoreCase));
string? main = apks.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a).Equals(package, StringComparison.OrdinalIgnoreCase))
            ?? apks.FirstOrDefault(a => Path.GetFileName(a).Equals("base.apk", StringComparison.OrdinalIgnoreCase));
if (main == null || lib == null)
{
    Console.WriteLine("Could not find the game's files in " + src);
    Console.WriteLine("Expected " + package + ".apk and config.arm64_v8a.apk. Found: " + string.Join(", ", apks.Select(Path.GetFileName)));
    return 2;
}
if (!File.Exists(melon)) { Console.WriteLine("Missing " + melon); return 2; }
string[] extra = apks.Where(a => a != main && a != lib).ToArray();
Console.WriteLine("Main file:   " + Path.GetFileName(main));
Console.WriteLine("Code file:   " + Path.GetFileName(lib));
Console.WriteLine("Other files: " + (extra.Length == 0 ? "(none)" : string.Join(", ", extra.Select(Path.GetFileName))));
if (extra.Length == 0) Console.WriteLine("WARNING: no data pack (UnityDataAssetPack.apk) found - the game will not start without it.");

L.LogFile = Path.Combine(Path.GetDirectoryName(outDir)!, "patch-log.txt");
try { File.Delete(L.LogFile); } catch { }
if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
Directory.CreateDirectory(outDir);
string temp = Path.Combine(Path.GetDirectoryName(outDir)!, "work-temp");
if (Directory.Exists(temp)) Directory.Delete(temp, true);

Directory.CreateDirectory(temp);
// one signing key for every run, kept next to the tool (see GenerateCertificate)
Environment.SetEnvironmentVariable("LEMON_SIGNING_KEY", Path.Combine(Path.GetDirectoryName(melon)!, "signing-key.pem"));
string melonCopy = Path.Combine(temp, "melon_data.zip");     // the patcher deletes the copy it is given
File.Copy(melon, melonCopy, true);
// Keep the game's own libunity.so. The patcher would otherwise swap in a generic build of the engine
// library; for this game that build is 6000.2.6f1 while the game's data is 6000.2.6f2, and the engine
// then refuses to load the data. Supplying a "local" package with no libraries in it prevents the swap.
string unityZip = Path.Combine(temp, "unity.zip");
using (var z = System.IO.Compression.ZipFile.Open(unityZip, System.IO.Compression.ZipArchiveMode.Create))
{
    var e = z.CreateEntry("arm64-v8a/keep-the-games-own-libunity.txt");
    using var w = new StreamWriter(e.Open());
    w.Write("intentionally empty");
}
Console.WriteLine("Keeping the game's own engine library (libunity.so)");

var a = new PatchArguments(main, lib, extra, outDir, temp, melonCopy, Path.Combine(temp, "unity.zip"), UnityVersion.Parse(unity), package, true);
bool ok = new Patcher(a, new L()).Run();
try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
Console.WriteLine(ok ? "PATCH OK" : "PATCH FAILED");
return ok ? 0 : 1;

class L : IPatchLogger
{
    public static string LogFile = "";
    public void Log(string m)
    {
        Console.WriteLine(m);
        try { if (LogFile.Length > 0) System.IO.File.AppendAllText(LogFile, m + Environment.NewLine); } catch { }
    }
}
