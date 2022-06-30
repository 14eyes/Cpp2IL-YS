using AssetRipper.VersionUtilities;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Attributes;
using Cpp2IL.Core.Utils;
using Cpp2IL.Plugin.GI;
using System.Buffers;
using System.IO.MemoryMappedFiles;

[assembly: RegisterCpp2IlPlugin(typeof(GIPlugin))]

namespace Cpp2IL.Plugin.GI;

public class GIPlugin : Cpp2IlPlugin
{
    public override string Name => "Genshin Impact Plugin";
    public override string Description => "Provides support for loading Genshin Impact and decrypting the metadata files.";
    public override void OnLoad()
    {
        //No-op
    }
    public override bool HandleGamePath(string gamePath, ref Cpp2IlRuntimeArgs args)
    {
        var exeName = Path.GetFileNameWithoutExtension(Directory.GetFiles(gamePath)
            .FirstOrDefault(f => f.EndsWith(".exe") && !MiscUtils.BlacklistedExecutableFilenames.Any(f.EndsWith)));

        args.PathToAssembly = Path.Combine(gamePath, $"{exeName}_Data", "Native", "UserAssembly.dll");
        var unityPlayerPath = Path.Combine(gamePath, $"{exeName}.exe");
        args.PathToMetadata = Path.Combine(gamePath, $"{exeName}_Data", "Managed", "Metadata", "global-metadata.dat");

        if (File.Exists(args.PathToMetadata))
        {
            Logger.VerboseNewline($"Found GI global-metadata. Attempting to Decrypt...");
            var metadataRawBytes = File.ReadAllBytes(args.PathToMetadata);
            var decryptedRaw = Decrypter.decrypt_global_metadata(metadataRawBytes, metadataRawBytes.Length);
            var tmpPath = GetTemporaryFilePath();
            File.WriteAllBytes(tmpPath, decryptedRaw);
            args.PathToMetadata = tmpPath;
        }

        var gameDataPath = Path.Combine(gamePath, $"{exeName}_Data");
        var uv = Cpp2IlApi.DetermineUnityVersion(unityPlayerPath, gameDataPath);
        Logger.VerboseNewline($"First-attempt unity version detection gave: {uv}");

        if (uv == default)
        {
            Logger.Warn("Could not determine unity version, probably due to not running on windows and not having any assets files to determine it from. Enter unity version, if known, in the format of (xxxx.x.x), else nothing to fail: ");
            var userInputUv = Console.ReadLine()!.Trim();

            if (!string.IsNullOrEmpty(userInputUv))
                uv = UnityVersion.Parse(userInputUv);
        }

        args.UnityVersion = uv;

        Logger.InfoNewline($"Determined game's unity version to be {args.UnityVersion}");

        return args.Valid = true;
    }
}