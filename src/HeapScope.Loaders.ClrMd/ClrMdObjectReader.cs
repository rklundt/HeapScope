using System.Globalization;
using HeapScope.Core;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// Reads field values, strings and reference paths from a full dump (sprint 1.5, <see cref="IObjectReader"/>). The dump
/// is opened with ClrMD on first use, not when the snapshot opens, so snapshots nobody inspects cost nothing; reads are
/// serialized (ClrMD's caches are not meant for concurrent readers) and the dump is released on dispose.
/// </summary>
public sealed class ClrMdObjectReader : IObjectReader, IDisposable
{
    private const int MaxStringField = 1000;

    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.Ordinal)
    {
        ["System.Boolean"] = "bool",
        ["System.Char"] = "char",
        ["System.SByte"] = "sbyte",
        ["System.Byte"] = "byte",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "uint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong",
        ["System.Single"] = "float",
        ["System.Double"] = "double",
        ["System.String"] = "string",
        ["System.Object"] = "object",
        ["System.IntPtr"] = "nint",
        ["System.UIntPtr"] = "nuint",
    };

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

    public IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var obj = Object(address);
            if (obj.Type is not { } type)
            {
                return [];
            }

            return obj.IsArray
                ? Elements(obj.AsArray(), maxElements, cancellationToken)
                : Fields(type, obj.Address, interior: false, maxElements, depth: 0, cancellationToken);
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

        var obj = _runtime.Heap.GetObject(address);
        return obj.IsValid ? obj : throw new ArgumentException($"There is no object at 0x{address:x} in the dump.", nameof(address));
    }

    private static List<ObjectField> Fields(ClrType type, ulong address, bool interior, int maxElements, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fields = new List<ObjectField>();
        foreach (var field in type.Fields)
        {
            fields.Add(Field(field, address, interior, maxElements, depth, cancellationToken));
        }

        return fields;
    }

    private static ObjectField Field(ClrInstanceField field, ulong address, bool interior, int maxElements, int depth, CancellationToken cancellationToken)
    {
        var name = DisplayName(field.Name ?? "?");
        var typeName = field.Type?.Name ?? field.ElementType.ToString();
        if (field.IsObjectReference)
        {
            var target = field.ReadObject(address, interior);
            return Reference(name, typeName, target, maxElements, depth, cancellationToken);
        }

        if (field.IsValueType && field.ElementType == ClrElementType.Struct)
        {
            var value = field.ReadStruct(address, interior);
            if (value.Type is not { } structType || depth >= 8)
            {
                return new ObjectField(name, typeName, ObjectFieldKind.Struct, "?", 0, []);
            }

            return new ObjectField(name, structType.Name ?? typeName, ObjectFieldKind.Struct, Alias(structType.Name), 0, Fields(structType, value.Address, interior: true, maxElements, depth + 1, cancellationToken));
        }

        return new ObjectField(name, typeName, ObjectFieldKind.Primitive, Primitive(field.ElementType, field.Type, new FieldSource(field, address, interior)), 0, []);
    }

    private static ObjectField Reference(string name, string declaredType, ClrObject target, int maxElements, int depth, CancellationToken cancellationToken)
    {
        if (target.IsNull)
        {
            return new ObjectField(name, declaredType, ObjectFieldKind.Null, "null", 0, []);
        }

        if (target.Type is not { } type)
        {
            // A reference to something that is not an object here (a truncated or damaged dump): say so, never "null".
            return new ObjectField(name, declaredType, ObjectFieldKind.Primitive, string.Create(CultureInfo.InvariantCulture, $"0x{target.Address:x} (not a valid object in this dump)"), 0, []);
        }

        if (type.IsString)
        {
            return new ObjectField(name, declaredType, ObjectFieldKind.String, Quote(target.AsString(MaxStringField)), target.Address, []);
        }

        if (target.IsArray)
        {
            var array = target.AsArray();
            var elements = depth < 1 ? Elements(array, maxElements, cancellationToken) : [];
            return new ObjectField(name, type.Name ?? declaredType, ObjectFieldKind.Array, ArrayText(type, array), target.Address, elements);
        }

        return new ObjectField(name, declaredType, ObjectFieldKind.Reference, type.Name ?? "?", target.Address, []);
    }

    private static List<ObjectField> Elements(ClrArray array, int maxElements, CancellationToken cancellationToken)
    {
        var component = array.Type.ComponentType;
        var elements = new List<ObjectField>();
        var count = Math.Min(array.Length, maxElements);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = new ArraySlot(array, i);
            var name = slot.Name;
            if (component is null)
            {
                elements.Add(new ObjectField(name, "?", ObjectFieldKind.Primitive, "?", 0, []));
            }
            else if (component.IsObjectReference)
            {
                elements.Add(Reference(name, component.Name ?? "?", slot.Object(), maxElements, depth: 1, cancellationToken));
            }
            else if (component.IsValueType && component.ElementType == ClrElementType.Struct)
            {
                var value = slot.Struct();
                elements.Add(new ObjectField(name, component.Name ?? "?", ObjectFieldKind.Struct, Alias(component.Name), 0,
                    value.Type is { } structType ? Fields(structType, value.Address, interior: true, maxElements, depth: 1, cancellationToken) : []));
            }
            else
            {
                elements.Add(new ObjectField(name, component.Name ?? "?", ObjectFieldKind.Primitive, Primitive(component.ElementType, component, slot), 0, []));
            }
        }

        if (array.Length > count)
        {
            elements.Add(new ObjectField(string.Empty, string.Empty, ObjectFieldKind.More, string.Create(CultureInfo.InvariantCulture, $"{array.Length - count:N0} more"), 0, []));
        }

        return elements;
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

    private static string Primitive(ClrElementType elementType, ClrType? type, IValueSource source)
    {
        var text = elementType switch
        {
            ClrElementType.Boolean => source.Read<bool>() ? "true" : "false",
            ClrElementType.Char => "'" + source.Read<char>() + "'",
            ClrElementType.Int8 => source.Read<sbyte>().ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt8 => source.Read<byte>().ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int16 => source.Read<short>().ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt16 => source.Read<ushort>().ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int32 => source.Read<int>().ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt32 => source.Read<uint>().ToString(CultureInfo.InvariantCulture),
            ClrElementType.Int64 => source.Read<long>().ToString(CultureInfo.InvariantCulture),
            ClrElementType.UInt64 => source.Read<ulong>().ToString(CultureInfo.InvariantCulture),
            ClrElementType.Float => source.Read<float>().ToString("R", CultureInfo.InvariantCulture),
            ClrElementType.Double => source.Read<double>().ToString("R", CultureInfo.InvariantCulture),
            ClrElementType.NativeInt or ClrElementType.NativeUInt or ClrElementType.Pointer or ClrElementType.FunctionPointer =>
                string.Create(CultureInfo.InvariantCulture, $"0x{source.Read<ulong>():x}"),
            _ => "?",
        };

        return type?.IsEnum == true ? EnumText(type, text) : text;
    }

    private static string EnumText(ClrType type, string number)
    {
        if (!decimal.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return number;
        }

        foreach (var (name, constant) in type.AsEnum().EnumerateValues())
        {
            if (constant is not null && Convert.ToDecimal(constant, CultureInfo.InvariantCulture) == value)
            {
                return $"{name} ({number})";
            }
        }

        return number;
    }

    /// <summary>An array as element type and length, <c>int[5]</c>, or its dimensions, <c>int[2,3]</c>.</summary>
    private static string ArrayText(ClrType arrayType, ClrArray array) =>
        string.Create(CultureInfo.InvariantCulture, $"{Alias(arrayType.ComponentType?.Name)}[{(array.Rank == 1 ? array.Length.ToString(CultureInfo.InvariantCulture) : string.Join(",", Enumerable.Range(0, array.Rank).Select(array.GetLength)))}]");

    private static string Alias(string? typeName) =>
        typeName is null ? "?" : s_aliases.TryGetValue(typeName, out var alias) ? alias : typeName;

    private static string Quote(string? text) => text is null ? "null" : "\"" + text + "\"";

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
