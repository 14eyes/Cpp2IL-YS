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
    public override string Description => "Provides support for loading Genshin Impact and decrypting the global-metadata file.";
    public override void OnLoad()
    {
        //No-op
    }
    public override bool HandleGamePath(string gamePath, ref Cpp2IlRuntimeArgs args)
    {
        var start = DateTime.Now;
        var exeName = Path.GetFileNameWithoutExtension(Directory.GetFiles(gamePath)
            .FirstOrDefault(f => f.EndsWith(".exe") && !MiscUtils.BlacklistedExecutableFilenames.Any(f.EndsWith)));

        args.PathToAssembly = Path.Combine(gamePath, $"{exeName}_Data", "Native", "UserAssembly.dll");
        if (!File.Exists(args.PathToAssembly))
        {
            Logger.WarnNewline($"Could not find GI UserAssembly at {args.PathToAssembly}");
            return false;
        }

        args.PathToMetadata = Path.Combine(gamePath, $"{exeName}_Data", "Managed", "Metadata", "global-metadata.dat");
        if (!File.Exists(args.PathToMetadata))
        {
            Logger.WarnNewline($"Could not find GI global-metadata at {args.PathToMetadata}");
            return false;
        }

        try
        {
            Logger.Verbose($"Found GI global-metadata. Attempting to Decrypt...");

            var metadataRawBytes = File.ReadAllBytes(args.PathToMetadata);
            var decryptedRaw = Decrypter.decrypt_global_metadata(metadataRawBytes, metadataRawBytes.Length);

            Logger.VerboseNewline($"OK ({(DateTime.Now - start).TotalMilliseconds:F0} ms)");

            Logger.Verbose($"Decrypted metadata. Saving to temporary file...");

            var tmpPath = GetTemporaryFilePath();
            File.WriteAllBytes(tmpPath, decryptedRaw);
            args.PathToMetadata = tmpPath;

            Logger.VerboseNewline($"OK ({(DateTime.Now - start).TotalMilliseconds:F0} ms)");
        }
        catch (Exception ex)
        {
            Logger.ErrorNewline($"Exception while decrypting global-metadata {ex}");
            return false;
        }

        Logger.InfoNewline($"global-metadata Decrypted Successfully");

        var gameDataPath = Path.Combine(gamePath, $"{exeName}_Data");
        var uv = UnityVersion.Parse("2019.4.21"); // Forces IL2Cpp Version to 24.5
        args.UnityVersion = uv;

        Logger.InfoNewline($"Set unity version to {args.UnityVersion}");
        Logger.InfoNewline($"GIPlugin completed in {(DateTime.Now - start).TotalMilliseconds:F0}ms");
        return args.Valid = true;
    }
}