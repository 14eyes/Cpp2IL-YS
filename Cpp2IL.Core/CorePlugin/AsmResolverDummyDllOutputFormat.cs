using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.PE.DotNet.Builder;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.CorePlugin;

public class AsmResolverDummyDllOutputFormat : Cpp2IlOutputFormat
{
    public override string OutputFormatId => "dummydll";
    
    public override string OutputFormatName => "Stub (\"Dummy\") DLL Files";

    private AssemblyDefinition? MostRecentCorLib { get; set; }

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot)
    {
        //Build the stub assemblies
        Logger.VerboseNewline("Building stub assemblies...", "DummyDllOutput");
        var ret = BuildStubAssemblies(context);

        TypeDefinitionsAsmResolver.CacheNeededTypeDefinitions();

        Logger.VerboseNewline("Configuring hierarchy...", "DummyDllOutput");
        
        Parallel.ForEach(context.Assemblies, AsmResolverAssemblyPopulator.ConfigureHierarchy);

        //Populate them
        Logger.VerboseNewline("Populating assemblies...", "DummyDllOutput");

        MiscUtils.ExecuteParallel(context.Assemblies, AsmResolverAssemblyPopulator.CopyDataFromIl2CppToManaged);
        
        //Populate custom attributes
        Logger.VerboseNewline("Populating custom attributes...", "DummyDllOutput");
        MiscUtils.ExecuteParallel(context.Assemblies, AsmResolverAssemblyPopulator.PopulateCustomAttributes);

        TypeDefinitionsAsmResolver.Reset();
        
        Logger.VerboseNewline("Generating assemblies...", "DummyDllOutput");
        
        if (!Directory.Exists(outputRoot))
            Directory.CreateDirectory(outputRoot);

        //Convert assembly definitions to PE files
        var peImagesToWrite = ret.AsParallel().Select(a => (image: a.ManifestModule!.ToPEImage(new ManagedPEImageBuilder()), name: a.ManifestModule.Name!)).ToList();
        
        Logger.VerboseNewline("Writing generated assemblies to disk...", "DummyDllOutput");
        
        //Save them
        var fileBuilder = new ManagedPEFileBuilder();
        foreach (var (image, name) in peImagesToWrite)
        {
            var dllPath = Path.Combine(outputRoot, name);
            fileBuilder.CreateFile(image).Write(dllPath);
        }
    }

    private List<AssemblyDefinition> BuildStubAssemblies(ApplicationAnalysisContext context)
    {
        var assemblyResolver = new Il2CppAssemblyResolver();
        var metadataResolver = new DefaultMetadataResolver(assemblyResolver);

        var corlib = context.Assemblies.First(a => a.Definition.AssemblyName.Name == "mscorlib");
        MostRecentCorLib = BuildStubAssembly(corlib, null, metadataResolver);
        assemblyResolver.DummyAssemblies.Add(MostRecentCorLib.Name!, MostRecentCorLib);

        var ret = context.Assemblies
            // .AsParallel()
            .Where(a => a.Definition.AssemblyName.Name != "mscorlib")
            .Select(a => BuildStubAssembly(a, MostRecentCorLib, metadataResolver))
            .ToList();

        ret.ForEach(a => assemblyResolver.DummyAssemblies.Add(a.Name!, a));

        ret.Add(MostRecentCorLib);
        return ret;
    }

    private static AssemblyDefinition BuildStubAssembly(AssemblyAnalysisContext assemblyContext, AssemblyDefinition? corLib, IMetadataResolver metadataResolver)
    {
        var assemblyDefinition = assemblyContext.Definition;

        var imageDefinition = assemblyDefinition.Image;

        //Get the name of the assembly (= the name of the DLL without the file extension)
        var assemblyNameString = assemblyDefinition.AssemblyName.Name;

        //Build an AsmResolver assembly from this definition
        Version version;
        if (assemblyDefinition.AssemblyName.build >= 0)
            version = new(assemblyDefinition.AssemblyName.major, assemblyDefinition.AssemblyName.minor, assemblyDefinition.AssemblyName.build, assemblyDefinition.AssemblyName.revision);
        else
            //handle __Generated assembly on v29, which has a version of 0.0.-1.-1
            version = new(0, 0, 0, 0);

        var ourAssembly = new AssemblyDefinition(assemblyNameString, version)
        {
            HashAlgorithm = (AssemblyHashAlgorithm) assemblyDefinition.AssemblyName.hash_alg,
            Attributes = (AssemblyAttributes) assemblyDefinition.AssemblyName.flags,
            Culture = assemblyDefinition.AssemblyName.Culture,
            //TODO find a way to set hash? or not needed
        };

        //Setting the corlib module allows element types in references to that assembly to be set correctly without us having to manually set them.
        var managedModule = new ModuleDefinition(imageDefinition.Name, new(corLib ?? ourAssembly)) //Use either ourself as corlib, if we are corlib, otherwise the provided one
        {
            MetadataResolver = metadataResolver
        }; 
        ourAssembly.Modules.Add(managedModule);

        foreach (var il2CppTypeDefinition in assemblyContext.TopLevelTypes)
        {
            if(il2CppTypeDefinition.Name != "<Module>")
                //We skip module because I've never come across an il2cpp assembly with any top-level functions, and it's simpler to skip it as AsmResolver adds one by default.
                managedModule.TopLevelTypes.Add(BuildStubType(il2CppTypeDefinition));
        }

        //Store the managed assembly in the context so we can use it later.
        assemblyContext.PutExtraData("AsmResolverAssembly", ourAssembly);

        return ourAssembly;
    }

    private static TypeDefinition BuildStubType(TypeAnalysisContext typeContext)
    {
        var typeDef = typeContext.Definition;
        
        const int defaultAttributes = (int) (TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
        
        //Initialize an empty type definition
        var ret = new TypeDefinition(typeContext.Namespace, typeContext.Name, (TypeAttributes) (typeDef?.Flags ?? defaultAttributes));

        //Set up its layout
        if(typeDef != null && typeDef.BaseType?.ToString() != "System.Enum")
            ConfigureTypeSize(typeDef, ret);

        //Create nested types
        foreach (var cppNestedType in typeContext.NestedTypes) 
            ret.NestedTypes.Add(BuildStubType(cppNestedType));

        //Associate this asm resolve td with the type context
        typeContext.PutExtraData("AsmResolverType", ret);
        
        //Add to the lookup-by-id table used by the resolver
        if(typeDef != null)
            AsmResolverUtils.TypeDefsByIndex[typeDef.TypeIndex] = ret;
        
        return ret;
    }

    private static void ConfigureTypeSize(Il2CppTypeDefinition il2CppDefinition, TypeDefinition asmResolverDefinition)
    {
        ushort packingSize = 0;
        var classSize = 0U;
        if (!il2CppDefinition.PackingSizeIsDefault)
            packingSize = (ushort) il2CppDefinition.PackingSize;

        if (!il2CppDefinition.ClassSizeIsDefault && !il2CppDefinition.IsEnumType)
        {
            if (il2CppDefinition.Size > 1 << 30)
                throw new Exception($"Got invalid size for type {il2CppDefinition}: {il2CppDefinition.RawSizes}");

            if (il2CppDefinition.Size != -1)
                classSize = (uint) il2CppDefinition.Size;
            else
                classSize = 0; //Not sure what this value actually implies but it seems to work
        }

        if (packingSize != 0 || classSize != 0)
            asmResolverDefinition.ClassLayout = new(packingSize, classSize);
    }
}