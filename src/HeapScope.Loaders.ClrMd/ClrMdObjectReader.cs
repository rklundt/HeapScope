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

using System.Globalization;
using HeapScope.Core;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// Reads field values, strings and reference paths from a full dump (sprint 1.5, <see cref="IObjectReader"/>), as typed
/// values (sprint 1.6; the worker formats them). The dump is opened with ClrMD on first use, not when the snapshot
/// opens, so snapshots nobody inspects cost nothing; reads are serialized (ClrMD's caches are not meant for concurrent
/// readers) and the dump is released on dispose.
/// </summary>
public sealed class ClrMdObjectReader : IObjectReader, IDisposable
{
    /// <summary>Chunk of strings read per lock acquisition by <see cref="ReadStrings"/>.</summary>
    private const int StringChunk = 4096;

    /// <summary>Characters per piece of <see cref="ReadStringPieces"/> (128 KB of UTF-16).</summary>
    private const int PieceChars = 64 * 1024;

    /// <summary>
    /// Characters streamed per lock acquisition by <see cref="ReadStringPieces"/>: a batch of huge strings releases the
    /// lock often enough for other reads (pages, the object view) to get in (review of 1.6).
    /// </summary>
    private const long CharBudget = 16L * 1024 * 1024;

    private readonly string _dumpPath;
    private readonly ClrMdLoaderOptions _options;
    private readonly Action? _beforeOpen;
    private readonly Lock _gate = new();
    private DataTarget? _target;
    private ClrRuntime? _runtime;
    private bool _disposed;

    /// <param name="beforeOpen">
    /// Runs before the dump is opened, which can be long after the snapshot was: the owner checks the file is still the
    /// one its graph was built from and throws if not (sprint 1.5 review H5).
    /// </param>
    public ClrMdObjectReader(string dumpPath, ClrMdLoaderOptions? options = null, Action? beforeOpen = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dumpPath);
        _dumpPath = dumpPath;
        _options = options ?? new ClrMdLoaderOptions();
        _beforeOpen = beforeOpen;
    }

    /// <summary>Whether the dump has been opened (tests: nothing is opened until something is read).</summary>
    public bool IsOpen => _runtime is not null;

    public IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, int maxText, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var obj = Object(address);
            if (obj.Type is not { } type)
            {
                return [];
            }

            var context = new ReadContext(maxElements, maxText, cancellationToken);
            return obj.IsArray
                ? Elements(obj.AsArray(), context)
                : Fields(type, obj.Address, interior: false, depth: 0, context);
        }
    }

    public ObjectValue? ReadField(ulong address, string path, int maxText = 1000)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            var obj = Object(address);
            var context = new ReadContext(0, maxText, CancellationToken.None);
            var steps = path.Split('.');
            var type = obj.Type;
            var at = obj.Address;
            var interior = false;
            for (var i = 0; i < steps.Length; i++)
            {
                if (type is null || FindField(type, steps[i]) is not { } field)
                {
                    return null;
                }

                var value = Value(field, at, interior, depth: 0, context, out var structType, out var target);
                if (i == steps.Length - 1)
                {
                    return value;
                }

                if (structType is not null)
                {
                    (type, at, interior) = (structType, value.Address, true);
                }
                else if (target is { IsNull: false, IsValid: true } next)
                {
                    (type, at, interior) = (next.Type, next.Address, false);
                }
                else
                {
                    return null;
                }
            }

            return null;
        }
    }

    public string? ReadString(ulong address, int maxLength)
    {
        lock (_gate)
        {
            var obj = Object(address);
            return obj.Type?.IsString == true ? obj.AsString(maxLength) : null;
        }
    }

    public void ReadStrings(IReadOnlyList<ulong> addresses, int maxLength, Action<int, string?> onEach, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(onEach);
        for (var start = 0; start < addresses.Count; start += StringChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(start + StringChunk, addresses.Count);
            var texts = new string?[end - start];
            lock (_gate)
            {
                EnsureOpen();
                for (var i = start; i < end; i++)
                {
                    var obj = _runtime!.Heap.GetObject(addresses[i]);
                    texts[i - start] = obj.IsValid && obj.Type?.IsString == true ? obj.AsString(maxLength) : null;
                }
            }

            // Outside the lock: the handler may be slow (hashing) and must not block other readers.
            for (var i = start; i < end; i++)
            {
                onEach(i, texts[i - start]);
            }
        }
    }

    public void ReadStringPieces(IReadOnlyList<ulong> addresses, StringPieceHandler onPiece, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(onPiece);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(PieceChars * 2);
        try
        {
            var next = 0;
            while (next < addresses.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(next + StringChunk, addresses.Count);
                lock (_gate)
                {
                    EnsureOpen();
                    long streamed = 0;
                    while (next < end && streamed < CharBudget)
                    {
                        streamed += Stream(next, addresses[next], buffer, onPiece, cancellationToken);
                        next++;
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads one string's UTF-16 characters straight from the dump, after its method table and length; returns its length.</summary>
    private int Stream(int index, ulong address, byte[] buffer, StringPieceHandler onPiece, CancellationToken cancellationToken)
    {
        var obj = _runtime!.Heap.GetObject(address);
        if (!obj.IsValid || obj.Type is not { IsString: true } type || type.GetFieldByName("_stringLength") is not { } lengthField)
        {
            onPiece(index, -1, [], last: true);
            return 0;
        }

        var length = lengthField.Read<int>(obj.Address, interior: false);
        if (length <= 0)
        {
            onPiece(index, 0, [], last: true);
            return 0;
        }

        var first = obj.Address + (ulong)_target!.DataReader.PointerSize + sizeof(int);
        for (var offset = 0; offset < length; offset += PieceChars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(PieceChars, length - offset);
            var bytes = buffer.AsSpan(0, count * 2);
            if (_target.DataReader.Read(first + ((ulong)offset * 2), bytes) != bytes.Length)
            {
                // Part of the text is missing from the dump: the string is not readable as a whole.
                onPiece(index, -1, [], last: true);
                return length;
            }

            onPiece(index, length, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(bytes), offset + count >= length);
        }

        return length;
    }

    public IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var paths = new Dictionary<ulong, PathNames>();
            var obj = Object(address);
            if (obj.Type is not { } type)
            {
                return new Dictionary<ulong, string>();
            }

            if (obj.IsArray)
            {
                var array = obj.AsArray();
                var component = type.ComponentType;
                var count = Math.Min(array.Length, maxElements);
                for (var i = 0; i < count; i++)
                {
                    if ((i & 0xFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var slot = new ArraySlot(array, i);
                    if (component?.IsObjectReference == true)
                    {
                        Add(paths, slot.Object().Address, slot.Name);
                    }
                    else if (component?.IsValueType == true && !component.IsPrimitive)
                    {
                        var element = slot.Struct();
                        if (element.Type is { } elementType)
                        {
                            CollectStructPaths(paths, elementType, element.Address, slot.Name + ".", depth: 1);
                        }
                    }
                }
            }
            else
            {
                CollectStructPaths(paths, type, obj.Address, string.Empty, depth: 0, interior: false);
            }

            return paths.ToDictionary(p => p.Key, p => p.Value.ToString());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _runtime?.Dispose();
            _target?.Dispose();
            _runtime = null;
            _target = null;
        }
    }

    /// <summary>A field's display name: the property name for a compiler backing field (<c>&lt;Name&gt;k__BackingField</c>).</summary>
    public static string DisplayName(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        const string Suffix = ">k__BackingField";
        return fieldName.StartsWith('<') && fieldName.EndsWith(Suffix, StringComparison.Ordinal)
            ? fieldName[1..^Suffix.Length]
            : fieldName;
    }

    private ClrObject Object(ulong address)
    {
        EnsureOpen();
        var obj = _runtime!.Heap.GetObject(address);
        return obj.IsValid ? obj : throw new ArgumentException($"There is no object at 0x{address:x} in the dump.", nameof(address));
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runtime is null)
        {
            _beforeOpen?.Invoke();
            _target = ClrMdDump.OpenDataTarget(_dumpPath, _options);
            try
            {
                _runtime = ClrMdDump.CreateRuntime(ClrMdDump.FirstRuntime(_target, _dumpPath), _dumpPath, _options);
            }
            catch
            {
                _target.Dispose();
                _target = null;
                throw;
            }
        }
    }

    private static List<ObjectField> Fields(ClrType type, ulong address, bool interior, int depth, ReadContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var fields = new List<ObjectField>();
        foreach (var field in type.Fields)
        {
            var value = Value(field, address, interior, depth, context, out var structType, out var target);
            IReadOnlyList<ObjectField> children = structType is not null && depth < 8
                ? Fields(structType, value.Address, interior: true, depth + 1, context)
                : value.Kind == ObjectValueKind.Array && depth < 1
                    ? Elements(target.AsArray(), context)
                    : [];
            fields.Add(new ObjectField(DisplayName(field.Name ?? "?"), field.Type?.Name ?? field.ElementType.ToString(), value, children));
        }

        return fields;
    }

    /// <summary>
    /// A field's value. <paramref name="structType"/> is set for an embedded struct (its fields start at the value's
    /// address); <paramref name="target"/> is the referenced object for references.
    /// </summary>
    private static ObjectValue Value(ClrInstanceField field, ulong address, bool interior, int depth, ReadContext context, out ClrType? structType, out ClrObject target)
    {
        structType = null;
        target = default;
        if (field.IsObjectReference)
        {
            target = field.ReadObject(address, interior);
            return Reference(target, context);
        }

        if (field.IsValueType && field.ElementType == ClrElementType.Struct)
        {
            var value = field.ReadStruct(address, interior);
            structType = value.Type;
            return new ObjectValue(ObjectValueKind.Struct) { TypeName = value.Type?.Name ?? field.Type?.Name, Address = value.Address };
        }

        return Primitive(field.ElementType, field.Type, new FieldSource(field, address, interior));
    }

    private static ObjectValue Reference(ClrObject target, ReadContext context)
    {
        if (target.IsNull)
        {
            return ObjectValue.Null;
        }

        if (target.Type is not { } type)
        {
            // A reference to something that is not an object here (a truncated or damaged dump): never "null".
            return new ObjectValue(ObjectValueKind.Invalid) { Address = target.Address };
        }

        if (type.IsString)
        {
            var length = type.GetFieldByName("_stringLength") is { } lengthField ? lengthField.Read<int>(target.Address, interior: false) : 0;
            var text = target.AsString(context.MaxText);
            return new ObjectValue(ObjectValueKind.String) { TypeName = type.Name, Address = target.Address, Text = text, TextLength = Math.Max(length, text?.Length ?? 0) };
        }

        if (target.IsArray)
        {
            var array = target.AsArray();
            return new ObjectValue(ObjectValueKind.Array)
            {
                TypeName = type.Name,
                ElementTypeName = type.ComponentType?.Name,
                Address = target.Address,
                Dimensions = [.. Enumerable.Range(0, array.Rank).Select(array.GetLength)],
            };
        }

        // A boxed primitive or enum: its value is the box's only field (m_value, or value__ for enums).
        if (type.IsValueType && (type.IsPrimitive || type.IsEnum) && type.Fields.Length == 1 && type.Fields[0] is { } inner)
        {
            return new ObjectValue(ObjectValueKind.Boxed)
            {
                TypeName = type.Name,
                Address = target.Address,
                Boxed = Primitive(inner.ElementType, type.IsEnum ? type : inner.Type, new FieldSource(inner, target.Address, interior: false)),
            };
        }

        return new ObjectValue(ObjectValueKind.Reference) { TypeName = type.Name, Address = target.Address };
    }

    private static List<ObjectField> Elements(ClrArray array, ReadContext context)
    {
        var component = array.Type.ComponentType;
        var elements = new List<ObjectField>();
        var count = Math.Min(array.Length, context.MaxElements);
        for (var i = 0; i < count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var slot = new ArraySlot(array, i);
            var declared = component?.Name ?? "?";
            if (component is null)
            {
                elements.Add(new ObjectField(slot.Name, declared, new ObjectValue(ObjectValueKind.Unknown), []));
            }
            else if (component.IsObjectReference)
            {
                // Elements of elements are not expanded: an array's own children are one level.
                elements.Add(new ObjectField(slot.Name, declared, Reference(slot.Object(), context), []));
            }
            else if (component.IsValueType && component.ElementType == ClrElementType.Struct)
            {
                var value = slot.Struct();
                elements.Add(new ObjectField(slot.Name, declared, new ObjectValue(ObjectValueKind.Struct) { TypeName = value.Type?.Name ?? declared, Address = value.Address },
                    value.Type is { } structType ? Fields(structType, value.Address, interior: true, depth: 1, context) : []));
            }
            else
            {
                elements.Add(new ObjectField(slot.Name, declared, Primitive(component.ElementType, component, slot), []));
            }
        }

        if (array.Length > count)
        {
            elements.Add(new ObjectField(string.Empty, string.Empty, new ObjectValue(ObjectValueKind.More) { Unsigned = (ulong)(array.Length - count) }, []));
        }

        return elements;
    }

    /// <summary>A field of <paramref name="type"/> by its raw name or its display name (backing fields as properties).</summary>
    private static ClrInstanceField? FindField(ClrType type, string name)
    {
        foreach (var field in type.Fields)
        {
            if (field.Name == name || (field.Name is { } raw && DisplayName(raw) == name))
            {
                return field;
            }
        }

        return null;
    }

    private static void CollectStructPaths(Dictionary<ulong, PathNames> paths, ClrType type, ulong address, string prefix, int depth, bool interior = true)
    {
        foreach (var field in type.Fields)
        {
            var name = prefix + DisplayName(field.Name ?? "?");
            if (field.IsObjectReference)
            {
                Add(paths, field.ReadObject(address, interior).Address, name);
            }
            else if (field.IsValueType && field.ElementType == ClrElementType.Struct && depth < 8)
            {
                var value = field.ReadStruct(address, interior);
                if (value.Type is { } structType)
                {
                    CollectStructPaths(paths, structType, value.Address, name + ".", depth + 1);
                }
            }
        }
    }

    private static void Add(Dictionary<ulong, PathNames> paths, ulong target, string path)
    {
        if (target == 0)
        {
            return;
        }

        if (!paths.TryGetValue(target, out var names))
        {
            names = new PathNames();
            paths.Add(target, names);
        }

        names.Add(path);
    }

    /// <summary>
    /// The paths to one target: the first few and a count. An array whose slots share one object (empty strings, a
    /// sentinel) has a million paths to it; joining them all would be quadratic and useless (sprint 1.5 review H1).
    /// </summary>
    private sealed class PathNames
    {
        public const int Shown = 3;

        private readonly List<string> _first = new(1);
        private int _total;

        public void Add(string path)
        {
            if (_first.Count < Shown)
            {
                _first.Add(path);
            }

            _total++;
        }

        public override string ToString() => _total <= Shown
            ? string.Join(", ", _first)
            : string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", _first)} and {_total - Shown:N0} more");
    }

    private static ObjectValue Primitive(ClrElementType elementType, ClrType? type, IValueSource source)
    {
        var name = type?.Name;
        var value = elementType switch
        {
            ClrElementType.Boolean => new ObjectValue(ObjectValueKind.Boolean) { Boolean = source.Read<bool>() },
            ClrElementType.Char => new ObjectValue(ObjectValueKind.Char) { Char = source.Read<char>() },
            ClrElementType.Int8 => new ObjectValue(ObjectValueKind.Integer) { Integer = source.Read<sbyte>() },
            ClrElementType.UInt8 => new ObjectValue(ObjectValueKind.Unsigned) { Unsigned = source.Read<byte>() },
            ClrElementType.Int16 => new ObjectValue(ObjectValueKind.Integer) { Integer = source.Read<short>() },
            ClrElementType.UInt16 => new ObjectValue(ObjectValueKind.Unsigned) { Unsigned = source.Read<ushort>() },
            ClrElementType.Int32 => new ObjectValue(ObjectValueKind.Integer) { Integer = source.Read<int>() },
            ClrElementType.UInt32 => new ObjectValue(ObjectValueKind.Unsigned) { Unsigned = source.Read<uint>() },
            ClrElementType.Int64 => new ObjectValue(ObjectValueKind.Integer) { Integer = source.Read<long>() },
            ClrElementType.UInt64 => new ObjectValue(ObjectValueKind.Unsigned) { Unsigned = source.Read<ulong>() },
            ClrElementType.Float => new ObjectValue(ObjectValueKind.Floating) { Floating = source.Read<float>() },
            ClrElementType.Double => new ObjectValue(ObjectValueKind.Floating) { Floating = source.Read<double>() },
            ClrElementType.NativeInt or ClrElementType.NativeUInt or ClrElementType.Pointer or ClrElementType.FunctionPointer =>
                new ObjectValue(ObjectValueKind.Pointer) { Unsigned = source.Read<ulong>() },
            _ => new ObjectValue(ObjectValueKind.Unknown),
        };

        value = value with { TypeName = name ?? ElementTypeName(elementType) };
        return type?.IsEnum == true ? value with { EnumMembers = EnumMembers(type) } : value;
    }

    private static List<EnumMember> EnumMembers(ClrType type)
    {
        var members = new List<EnumMember>();
        foreach (var (name, constant) in type.AsEnum().EnumerateValues())
        {
            if (constant is not null)
            {
                members.Add(new EnumMember(name, constant switch
                {
                    ulong u => u,
                    long l => unchecked((ulong)l),
                    _ => unchecked((ulong)Convert.ToInt64(constant, CultureInfo.InvariantCulture)),
                }));
            }
        }

        return members;
    }

    private static string ElementTypeName(ClrElementType elementType) => elementType switch
    {
        ClrElementType.Boolean => "System.Boolean",
        ClrElementType.Char => "System.Char",
        ClrElementType.Int8 => "System.SByte",
        ClrElementType.UInt8 => "System.Byte",
        ClrElementType.Int16 => "System.Int16",
        ClrElementType.UInt16 => "System.UInt16",
        ClrElementType.Int32 => "System.Int32",
        ClrElementType.UInt32 => "System.UInt32",
        ClrElementType.Int64 => "System.Int64",
        ClrElementType.UInt64 => "System.UInt64",
        ClrElementType.Float => "System.Single",
        ClrElementType.Double => "System.Double",
        _ => elementType.ToString(),
    };

    /// <summary>What a read needs to know: element and text cuts, and cancellation.</summary>
    private readonly record struct ReadContext(int MaxElements, int MaxText, CancellationToken CancellationToken);

    /// <summary>Reads one unmanaged value from a field or an array element.</summary>
    private interface IValueSource
    {
        T Read<T>()
            where T : unmanaged;
    }

    private readonly struct FieldSource(ClrInstanceField field, ulong address, bool interior) : IValueSource
    {
        public T Read<T>()
            where T : unmanaged => field.Read<T>(address, interior);
    }

    /// <summary>
    /// One element of an array by its position in memory (row-major). Multi-dimensional arrays are read with indices
    /// (ClrMD's single-index reads are for vectors) and named <c>[i,j]</c> with their lower bounds (sprint 1.5 review).
    /// </summary>
    private readonly struct ArraySlot : IValueSource
    {
        private readonly ClrArray _array;
        private readonly int _index;
        private readonly int[]? _indices;

        public ArraySlot(ClrArray array, int index)
        {
            _array = array;
            _index = index;
            if (array.Rank > 1)
            {
                _indices = new int[array.Rank];
                var rest = index;
                for (var dimension = array.Rank - 1; dimension >= 0; dimension--)
                {
                    var length = array.GetLength(dimension);
                    _indices[dimension] = array.GetLowerBound(dimension) + (rest % length);
                    rest /= length;
                }
            }
        }

        public string Name => _indices is null
            ? string.Create(CultureInfo.InvariantCulture, $"[{_index}]")
            : "[" + string.Join(",", _indices.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";

        public ClrObject Object() => _indices is null ? _array.GetObjectValue(_index) : _array.GetObjectValue(_indices);

        public ClrValueType Struct() => _indices is null ? _array.GetStructValue(_index) : _array.GetStructValue(_indices);

        public T Read<T>()
            where T : unmanaged => _indices is null ? _array.GetValue<T>(_index) : _array.GetValue<T>(_indices);
    }
}
