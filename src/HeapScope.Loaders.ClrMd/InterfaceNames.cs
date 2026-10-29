// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// Names the generic interfaces a type implements (sprint 3.2 review): ClrMD names a type's own non-generic interfaces
/// but leaves out instantiations (<c>IEquatable&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>). Their InterfaceImpl rows are read from
/// the module's metadata and decoded with the concrete type's generic arguments, in ClrMD's naming
/// (<c>System.IEquatable&lt;App.Order&gt;</c>). The metadata comes from the dump when it holds the module's image, else
/// from the module's file on this machine (local only, ADR-017) when its image size matches; otherwise the module's
/// generic interfaces are left out (counted by the caller's log).
/// </summary>
public sealed class InterfaceNames(DataTarget target)
{
    private readonly Dictionary<ulong, MetadataReaderProvider?> _modules = [];
    private Dictionary<ulong, ModuleInfo>? _images;

    /// <summary>The generic interfaces <paramref name="type"/>'s own metadata lists.</summary>
    public IEnumerable<string> GenericInterfaces(ClrType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Module is not { } module || Reader(module) is not { } reader || (type.MetadataToken >> 24) != 0x02)
        {
            yield break;
        }

        // Metadata that does not match the dump's module (a rebuilt file) fails the type's read: the caller counts the
        // type as unread rather than show made-up rows (the 3.2 re-review).
        var names = new List<string>();
        try
        {
            var handle = MetadataTokens.TypeDefinitionHandle(type.MetadataToken & 0xFFFFFF);
            var definition = reader.GetTypeDefinition(handle);
            var provider = new NameProvider(GenericArguments(type.Name));
            foreach (var implementation in definition.GetInterfaceImplementations())
            {
                var target = reader.GetInterfaceImplementation(implementation).Interface;
                if (target.Kind == HandleKind.TypeSpecification)
                {
                    var name = reader.GetTypeSpecification((TypeSpecificationHandle)target).DecodeSignature(provider, reader);
                    if (name.Contains(NameProvider.Unknown, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"A generic argument of {type.Name}'s interfaces is not known.");
                    }

                    names.Add(name);
                }
            }
        }
        catch (BadImageFormatException e)
        {
            throw new InvalidOperationException($"The metadata of {type.Module.Name} does not match the dump.", e);
        }

        foreach (var name in names)
        {
            yield return name;
        }
    }

    public void Clear()
    {
        foreach (var provider in _modules.Values)
        {
            provider?.Dispose();
        }

        _modules.Clear();
    }

    private MetadataReader? Reader(ClrModule module)
    {
        if (!_modules.TryGetValue(module.Address, out var provider))
        {
            provider = FromDump(module) ?? FromFile(module);
            if (provider is null)
            {
                MissingModules++;
            }

            _modules[module.Address] = provider;
        }

        try
        {
            return provider?.GetMetadataReader();
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Modules whose metadata could not be read (neither in the dump nor a matching file).</summary>
    public int MissingModules { get; private set; }

    private MetadataReaderProvider? FromDump(ClrModule module)
    {
        if (module.MetadataAddress == 0 || module.MetadataLength is <= 0 or > 256 * 1024 * 1024)
        {
            return null;
        }

        var bytes = new byte[module.MetadataLength];
        return target.DataReader.Read(module.MetadataAddress, bytes) == bytes.Length && bytes.AsSpan(0, 4).SequenceEqual("BSJB"u8)
            ? MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(bytes))
            : null;
    }

    private MetadataReaderProvider? FromFile(ClrModule module)
    {
        if (string.IsNullOrEmpty(module.Name) || !File.Exists(module.Name))
        {
            return null;
        }

        // The file must be the image the process loaded: its time stamp and size as the dump records them (the index a
        // symbol server would use; the 3.2 re-review).
        _images ??= target.EnumerateModules().GroupBy(m => m.ImageBase).ToDictionary(g => g.Key, g => g.First());
        if (!_images.TryGetValue(module.ImageBase, out var image) || image.IndexTimeStamp == 0)
        {
            return null;
        }

        try
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(module.Name));
            var headers = pe.PEHeaders;
            if (!pe.HasMetadata || headers.CoffHeader.TimeDateStamp != image.IndexTimeStamp || headers.PEHeader!.SizeOfImage != image.IndexFileSize)
            {
                return null;
            }

            return MetadataReaderProvider.FromMetadataImage(pe.GetMetadata().GetContent());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The generic arguments of a ClrMD type name, of every generic segment in order, since a nested type has its outer
    /// types' parameters first (<c>A&lt;X, Y&lt;Z&gt;&gt;+B&lt;W&gt;</c> gives X, Y&lt;Z&gt; and W; the 3.2 re-review).
    /// </summary>
    public static IReadOnlyList<string> GenericArguments(string? name)
    {
        var arguments = new List<string>();
        if (name is null)
        {
            return arguments;
        }

        var depth = 0;
        var start = -1;
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '<':
                    if (depth++ == 0)
                    {
                        start = i + 1;
                    }

                    break;
                case '>':
                    if (--depth == 0 && start >= 0)
                    {
                        arguments.Add(name[start..i].Trim());
                        start = -1;
                    }

                    break;
                case '[' when depth > 0:
                    depth++;
                    break;
                case ']' when depth > 1:
                    depth--;
                    break;
                case ',' when depth == 1:
                    arguments.Add(name[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        return arguments;
    }

    /// <summary>Type names as ClrMD writes them, from signatures.</summary>
    private sealed class NameProvider(IReadOnlyList<string> arguments) : ISignatureTypeProvider<string, object?>
    {
        /// <summary>Stands for a generic argument the type's name does not give; a name holding it is not used.</summary>
        public const string Unknown = "\u0001";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "(fnptr)";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            var tick = genericType.LastIndexOf('`');
            return (tick < 0 ? genericType : genericType[..tick]) + "<" + string.Join(", ", typeArguments) + ">";
        }

        public string GetGenericMethodParameter(object? genericContext, int index) => Unknown;

        public string GetGenericTypeParameter(object? genericContext, int index) => index < arguments.Count ? arguments[index] : Unknown;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode switch
        {
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => typeCode.ToString(),
        };

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);
            var declaring = definition.GetDeclaringType();
            return declaring.IsNil
                ? Qualified(reader.GetString(definition.Namespace), name)
                : GetTypeFromDefinition(reader, declaring, rawTypeKind) + "+" + name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            var name = reader.GetString(reference.Name);
            return reference.ResolutionScope.Kind == HandleKind.TypeReference
                ? GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind) + "+" + name
                : Qualified(reader.GetString(reference.Namespace), name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static string Qualified(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    }
}
