// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using EmitContext = Microsoft.CodeAnalysis.Emit.EmitContext;

namespace Microsoft.Cci
{
    internal sealed class PeWritingException : Exception
    {
        public PeWritingException(Exception inner)
            : base(inner.Message, inner)
        { }
    }

    internal static class PeWriter
    {
        public static bool WritePeToStream(
            EmitContext context,
            CommonMessageProvider messageProvider,
            Func<Stream> getPeStream,
            Func<Stream> getPortablePdbStreamOpt,
            PdbWriter nativePdbWriterOpt,
            string pdbPathOpt,
            bool allowMissingMethodBodies,
            bool isDeterministic,
            CancellationToken cancellationToken)
        {
            // If PDB writer is given, we have to have PDB path.
            Debug.Assert(nativePdbWriterOpt == null || pdbPathOpt != null);

            var mdWriter = FullMetadataWriter.Create(context, messageProvider, allowMissingMethodBodies, isDeterministic, getPortablePdbStreamOpt != null, cancellationToken);

            var properties = context.Module.Properties;

            nativePdbWriterOpt?.SetMetadataEmitter(mdWriter);

            // Since we are producing a full assembly, we should not have a module version ID
            // imposed ahead-of time. Instead we will compute a deterministic module version ID
            // based on the contents of the generated stream.
            Debug.Assert(properties.PersistentIdentifier == default(Guid));

            var ilBuilder = new BlobBuilder(32 * 1024);
            var mappedFieldDataBuilder = new BlobBuilder();
            var managedResourceBuilder = new BlobBuilder(1024);

            Blob mvidFixup;
            mdWriter.BuildMetadataAndIL(
                nativePdbWriterOpt,
                ilBuilder,
                mappedFieldDataBuilder,
                managedResourceBuilder,
                out mvidFixup);

            MethodDefinitionHandle entryPointHandle;
            MethodDefinitionHandle debugEntryPointHandle;
            mdWriter.GetEntryPoints(out entryPointHandle, out debugEntryPointHandle);
            
            if (!debugEntryPointHandle.IsNil)
            {
                nativePdbWriterOpt?.SetEntryPoint((uint)MetadataTokens.GetToken(debugEntryPointHandle));
            }

            if (nativePdbWriterOpt != null)
            {
                var assembly = mdWriter.Module.AsAssembly;
                if (assembly != null && assembly.Kind == OutputKind.WindowsRuntimeMetadata)
                {
                    // Dev12: If compiling to winmdobj, we need to add to PDB source spans of
                    //        all types and members for better error reporting by WinMDExp.
                    nativePdbWriterOpt.WriteDefinitionLocations(mdWriter.Module.GetSymbolToLocationMap());
                }
                else
                {
#if DEBUG
                    // validate that all definitions are writable
                    // if same scenario would happen in an winmdobj project
                    nativePdbWriterOpt.AssertAllDefinitionsHaveTokens(mdWriter.Module.GetSymbolToLocationMap());
#endif
                }
            }

            Stream peStream = getPeStream();
            if (peStream == null)
            {
                return false;
            }

            BlobContentId pdbContentId = nativePdbWriterOpt?.GetContentId() ?? default(BlobContentId);

            // the writer shall not be used after this point for writing:
            nativePdbWriterOpt = null;

            ushort portablePdbVersion = 0;
            var metadataSerializer = mdWriter.GetTypeSystemMetadataSerializer();

            var peHeaderBuilder = new PEHeaderBuilder(
                machine: properties.Machine,
                sectionAlignment: properties.SectionAlignment,
                fileAlignment: properties.FileAlignment,
                imageBase: properties.BaseAddress,
                majorLinkerVersion: properties.LinkerMajorVersion,
                minorLinkerVersion: properties.LinkerMinorVersion,
                majorOperatingSystemVersion: 4,
                minorOperatingSystemVersion: 0,
                majorImageVersion: 0,
                minorImageVersion: 0,
                majorSubsystemVersion: properties.MajorSubsystemVersion,
                minorSubsystemVersion: properties.MinorSubsystemVersion,
                subsystem: properties.Subsystem,
                dllCharacteristics: properties.DllCharacteristics,
                imageCharacteristics: properties.ImageCharacteristics,
                sizeOfStackReserve: properties.SizeOfStackReserve,
                sizeOfStackCommit: properties.SizeOfStackCommit,
                sizeOfHeapReserve: properties.SizeOfHeapReserve,
                sizeOfHeapCommit: properties.SizeOfHeapCommit);

            var deterministicIdProvider = isDeterministic ?
                new Func<IEnumerable<Blob>, BlobContentId>(content => BlobContentId.FromHash(CryptographicHashProvider.ComputeSha1(content))) :
                null;

            if (mdWriter.EmitStandaloneDebugMetadata)
            {
                Debug.Assert(getPortablePdbStreamOpt != null);

                var debugMetadataBuilder = new BlobBuilder();
                var debugMetadataSerializer = mdWriter.GetStandaloneDebugMetadataSerializer(metadataSerializer.MetadataSizes, debugEntryPointHandle, deterministicIdProvider);
                debugMetadataSerializer.SerializeMetadata(debugMetadataBuilder, out pdbContentId);
                portablePdbVersion = debugMetadataSerializer.FormatVersion;

                // write to Portable PDB stream:
                Stream portablePdbStream = getPortablePdbStreamOpt();
                if (portablePdbStream != null)
                {
                    debugMetadataBuilder.WriteContentTo(portablePdbStream);
                }
            }

            DebugDirectoryBuilder debugDirectoryBuilder;
            if (pdbPathOpt != null || isDeterministic)
            {
                debugDirectoryBuilder = new DebugDirectoryBuilder();
                if (pdbPathOpt != null)
                {
                    string paddedPath = isDeterministic ? pdbPathOpt : PadPdbPath(pdbPathOpt);
                    debugDirectoryBuilder.AddCodeViewEntry(paddedPath, pdbContentId, portablePdbVersion);
                }

                if (isDeterministic)
                {
                    debugDirectoryBuilder.AddReproducibleEntry();
                }
            }
            else
            {
                debugDirectoryBuilder = null;
            }

            var peBuilder = new ManagedPEBuilder(
                peHeaderBuilder,
                metadataSerializer,
                ilBuilder,
                mappedFieldDataBuilder,
                managedResourceBuilder,
                CreateNativeResourceSectionSerializer(context.Module),
                debugDirectoryBuilder,
                CalculateStrongNameSignatureSize(context.Module),
                entryPointHandle,
                properties.CorFlags,
                deterministicIdProvider);

            var peBlob = new BlobBuilder();
            BlobContentId peContentId;
            peBuilder.Serialize(peBlob, out peContentId);

            // Patch MVID
            if (!mvidFixup.IsDefault)
            {
                var writer = new BlobWriter(mvidFixup);
                writer.WriteGuid(peContentId.Guid);
                Debug.Assert(writer.RemainingBytes == 0);
            }

            try
            {
                peBlob.WriteContentTo(peStream);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                throw new PeWritingException(e);
            }

            return true;
        }

        // Padding: We pad the path to this minimal size to
        // allow some tools to patch the path without the need to rewrite the entire image.
        // This is a workaround put in place until these tools are retired.
        private static string PadPdbPath(string path)
        {
            const int minLength = 260;
            return path + new string('\0', Math.Max(0, minLength - Encoding.UTF8.GetByteCount(path) - 1));
        }

        private static ResourceSectionBuilder CreateNativeResourceSectionSerializer(IModule module)
        {
            // Win32 resources are supplied to the compiler in one of two forms, .RES (the output of the resource compiler),
            // or .OBJ (the output of running cvtres.exe on a .RES file). A .RES file is parsed and processed into
            // a set of objects implementing IWin32Resources. These are then ordered and the final image form is constructed
            // and written to the resource section. Resources in .OBJ form are already very close to their final output
            // form. Rather than reading them and parsing them into a set of objects similar to those produced by 
            // processing a .RES file, we process them like the native linker would, copy the relevant sections from 
            // the .OBJ into our output and apply some fixups.

            var nativeResourceSectionOpt = module.Win32ResourceSection;
            if (nativeResourceSectionOpt != null)
            {
                return new ResourceSectionBuilderFromObj(nativeResourceSectionOpt);
            }

            var nativeResourcesOpt = module.Win32Resources;
            if (nativeResourcesOpt?.Any() == true)
            {
                return new ResourceSectionBuilderFromResources(nativeResourcesOpt);
            }

            return null;
        }

        private class ResourceSectionBuilderFromObj : ResourceSectionBuilder
        {
            private readonly ResourceSection _resourceSection;

            public ResourceSectionBuilderFromObj(ResourceSection resourceSection)
            {
                Debug.Assert(resourceSection != null);
                _resourceSection = resourceSection;
            }

            protected override void Serialize(BlobBuilder builder, SectionLocation location)
            {
                NativeResourceWriter.SerializeWin32Resources(builder, _resourceSection, location.RelativeVirtualAddress);
            }
        }

        private class ResourceSectionBuilderFromResources : ResourceSectionBuilder
        {
            private readonly IEnumerable<IWin32Resource> _resources;

            public ResourceSectionBuilderFromResources(IEnumerable<IWin32Resource> resources)
            {
                Debug.Assert(resources.Any());
                _resources = resources;
            }

            protected override void Serialize(BlobBuilder builder, SectionLocation location)
            {
                NativeResourceWriter.SerializeWin32Resources(builder, _resources, location.RelativeVirtualAddress);
            }
        }

        private static int CalculateStrongNameSignatureSize(IModule module)
        {
            IAssembly assembly = module.AsAssembly;
            if (assembly == null)
            {
                return 0;
            }

            // EDMAURER the count of characters divided by two because the each pair of characters will turn in to one byte.
            int keySize = (assembly.SignatureKey == null) ? 0 : assembly.SignatureKey.Length / 2;

            if (keySize == 0)
            {
                keySize = assembly.PublicKey.Length;
            }

            if (keySize == 0)
            {
                return 0;
            }

            return (keySize < 128 + 32) ? 128 : keySize - 32;
        }
    }
}
