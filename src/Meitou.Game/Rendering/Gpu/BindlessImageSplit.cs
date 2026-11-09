using System.Runtime.InteropServices;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The rewrite behind <see cref="SpirvPatch.BindlessFetchImages"/>: one module's bindless combined arrays split into plain image arrays
/// with a sampler array beside each. A scan collects the module's types, variables and chains; the plan picks the arrays to split and
/// gives them fresh ids; the rebuild copies the module instruction by instruction, rewriting what touches the split arrays.
/// </summary>
internal sealed class BindlessImageSplit
{
    #region Scan

    /// <summary>
    /// The module's copy (the scan and the early return read it; the rebuild makes new words).
    /// </summary>
    readonly byte[] _copy;

    /// <summary>
    /// The bindless table's descriptor set.
    /// </summary>
    readonly uint _set;

    /// <summary>
    /// Where the sampler arrays' bindings begin above the texture arrays' (the backend's table total).
    /// </summary>
    readonly uint _samplerOffset;

    /// <summary>
    /// Decorations: variable id -> descriptor set.
    /// </summary>
    readonly Dictionary<uint, uint> _sets = new();

    /// <summary>
    /// Decorations: variable id -> binding.
    /// </summary>
    readonly Dictionary<uint, uint> _bindings = new();

    /// <summary>
    /// OpTypeInt ids.
    /// </summary>
    readonly HashSet<uint> _intScalars = new();

    /// <summary>
    /// OpTypeImage: sampled type, dim, depth.
    /// </summary>
    readonly Dictionary<uint, (uint Scalar, uint Dim, uint Depth)> _images = new();

    /// <summary>
    /// Sampled image id -> image.
    /// </summary>
    readonly Dictionary<uint, uint> _sampledImages = new();

    /// <summary>
    /// Array id -> element, length (a constant id, copied verbatim).
    /// </summary>
    readonly Dictionary<uint, (uint Elem, uint Len)> _arrayElem = new();

    /// <summary>
    /// Pointer id -> pointee.
    /// </summary>
    readonly Dictionary<uint, uint> _pointerAt = new();

    /// <summary>
    /// Variable id -> pointer.
    /// </summary>
    readonly Dictionary<uint, uint> _variables = new();

    /// <summary>
    /// Access chain result -> base variable.
    /// </summary>
    readonly Dictionary<uint, uint> _chains = new();

    /// <summary>
    /// Access chain result -> its index operand.
    /// </summary>
    readonly Dictionary<uint, uint> _chainIndex = new();

    /// <summary>
    /// Copies <paramref name="spirv"/> for the rewrite of the bindless table at <paramref name="set"/>.
    /// </summary>
    public BindlessImageSplit(byte[] spirv, uint set, uint samplerOffset)
    {
        _copy = (byte[])spirv.Clone();
        _set = set;
        _samplerOffset = samplerOffset;
    }

    /// <summary>
    /// The rewritten module, or the plain copy when the set has no combined array to split.
    /// </summary>
    public byte[] Run()
    {
        var w = MemoryMarshal.Cast<byte, uint>(_copy.AsSpan());
        Scan(w);
        PlanSplits(w);

        if (_ours.Count == 0) return _copy;
        IndexSplits();
        return Rebuild(w);
    }

    /// <summary>
    /// Pass 1: the decorations, the types, the variables and the access chains of the whole module.
    /// </summary>
    void Scan(Span<uint> w)
    {
        for (int i = 5; i < w.Length;)
        {
            uint op = w[i] & 0xFFFF, count = w[i] >> 16;
            if (count == 0) break;
            var a = w.Slice(i + 1, (int)count - 1);
            if (op == 71 && count == 4)
            {
                if (a[1] == 34) _sets[a[0]] = a[2];
                else if (a[1] == 33) _bindings[a[0]] = a[2];
            }
            else if (op == 21) _intScalars.Add(a[0]);
            else if (op == 25 && count >= 8) _images[a[0]] = (a[1], a[2], a[3]);
            else if (op == 27 && count == 3) _sampledImages[a[0]] = a[1];
            else if (op == 28 && count == 4) _arrayElem[a[0]] = (a[1], a[2]);
            else if (op == 32 && count == 4) _pointerAt[a[0]] = a[2];
            else if (op == 59) _variables[a[1]] = a[0];
            else if (op == 65 && count >= 5)
            {
                _chains[a[1]] = a[2];
                _chainIndex[a[1]] = a[3];
            }

            i += (int)count;
        }
    }

    #endregion

    #region Plan

    /// <summary>
    /// Fresh ids above the module's bound (word 3 = max id + 1).
    /// </summary>
    uint _next;

    /// <summary>
    /// The OpTypeSampler id the rewrite declares (0 while no split array takes a sampler).
    /// </summary>
    uint _samplerType;

    /// <summary>
    /// Old array pointer -> the new ids.
    /// </summary>
    readonly Dictionary<uint, Made> _ours = new();

    /// <summary>
    /// The old sampled image type -> the new ids.
    /// </summary>
    readonly Dictionary<uint, Made> _oursByElem = new();

    /// <summary>
    /// Ours' variable id -> old array pointer.
    /// </summary>
    readonly Dictionary<uint, uint> _oursVars = new();

    /// <summary>
    /// Pointer to one of ours' sampled images -> the array.
    /// </summary>
    readonly Dictionary<uint, Made> _oursPointers = new();

    /// <summary>
    /// The arrays to split: the set's array variables whose element is a sampled image. The integer kinds' images get no sampler.
    /// </summary>
    void PlanSplits(Span<uint> w)
    {
        _next = w[3];
        foreach (var (id, ptr) in _variables)
        {
            if (_sets.GetValueOrDefault(id) != _set) continue;
            if (!_pointerAt.TryGetValue(ptr, out uint arr) || !_arrayElem.TryGetValue(arr, out var shape)) continue;
            if (!_sampledImages.TryGetValue(shape.Elem, out uint image) || !_images.TryGetValue(image, out var format))
                continue;
            bool integer = _intScalars.Contains(format.Scalar);
            if (!integer && _samplerType == 0) _samplerType = _next++;
            var made = new Made
            {
                OldPtr = ptr,
                ArrayPtr = _next++,
                ElemPtr = _next++,
                Array = _next++,
                Image = image,
                Sampled = shape.Elem,
                Length = shape.Len,
                Integer = integer,
                Binding = _bindings.GetValueOrDefault(id) + _samplerOffset,
            };
            if (!integer)
            {
                made.SamplerArray = _next++;
                made.SamplerArrayPtr = _next++;
                made.SamplerElemPtr = _next++;
                made.SamplerVar = _next++;
            }

            _ours[ptr] = made;
            _oursByElem[shape.Elem] = made;
        }
    }

    /// <summary>
    /// The split arrays' variables and the pointer types into their elements.
    /// </summary>
    void IndexSplits()
    {
        foreach (var (id, ptr) in _variables)
            if (_ours.ContainsKey(ptr)) _oursVars[id] = ptr;
        foreach (var (ptr, pointee) in _pointerAt)
            if (_oursByElem.TryGetValue(pointee, out var pointed)) _oursPointers[ptr] = pointed;
    }

    #endregion

    #region Rebuild

    /// <summary>
    /// The rebuilt module's words.
    /// </summary>
    List<uint> _list = null!;

    /// <summary>
    /// A function parameter of pointer type -> its sampler parameter.
    /// </summary>
    readonly Dictionary<uint, uint> _pointerParams = new();

    /// <summary>
    /// The same -> the array it points into.
    /// </summary>
    readonly Dictionary<uint, Made> _pointerMade = new();

    /// <summary>
    /// Access chain result -> the array it indexes.
    /// </summary>
    readonly Dictionary<uint, Made> _chainMade = new();

    /// <summary>
    /// Image value (load, parameter) -> its array, its sampler value.
    /// </summary>
    readonly Dictionary<uint, (Made Array, uint Sampler)> _imagesDone = new();

    /// <summary>
    /// Image load result -> the index operand it used.
    /// </summary>
    readonly Dictionary<uint, uint> _loadIndex = new();

    /// <summary>
    /// Whether the sampler decorations and type went in already.
    /// </summary>
    bool _decorated;

    /// <summary>
    /// Pass 2: the rewrite and the rebuild — the new types go in right after each old array pointer, before every use.
    /// </summary>
    byte[] Rebuild(Span<uint> w)
    {
        _list = new List<uint>(w.Length + 128) { w[0], w[1], w[2], w[3], w[4] };
        for (int i = 5; i < w.Length;)
        {
            uint op = w[i] & 0xFFFF, count = w[i] >> 16;
            if (count == 0) break;
            var a = w.Slice(i + 1, (int)count - 1);

            // The new sampler type and the sampler variables' decorations go into the annotations section, before the first type.
            if (!_decorated && op is >= 19 and <= 39) AddSamplerDeclarations();
            RewriteInstruction(w, i, op, count, a);
            i += (int)count;
        }

        uint[] outWords = [.. _list];
        outWords[3] = _next;                                    // the id bound grew with what the rewrite made
        return MemoryMarshal.AsBytes(outWords.AsSpan()).ToArray();
    }

    /// <summary>
    /// The sampler variables' set and binding decorations and the sampler type, once.
    /// </summary>
    void AddSamplerDeclarations()
    {
        _decorated = true;
        foreach (var made2 in _ours.Values)
        {
            if (made2.Integer) continue;
            _list.AddRange([71u | (4u << 16), made2.SamplerVar, 34u, _set]);          // DescriptorSet
            _list.AddRange([71u | (4u << 16), made2.SamplerVar, 33u, made2.Binding]);  // Binding
        }

        if (_samplerType != 0) _list.AddRange([26u | (2u << 16), _samplerType]);          // OpTypeSampler
    }

    /// <summary>
    /// One instruction into the rebuilt module, rewritten where it touches a split array.
    /// </summary>
    void RewriteInstruction(Span<uint> w, int i, uint op, uint count, Span<uint> a)
    {
        if (op == 15) RewriteEntryPoint(w, i, count);
        else if (op == 32 && _ours.TryGetValue(a[0], out var declared)) DeclareSplit(w, i, count, declared);
        else if (op == 33) RewriteFunctionType(a);
        else if (op == 55 && _oursByElem.TryGetValue(a[0], out var parameter)) RewriteImageParameter(a, parameter);
        else if (op == 55 && _oursPointers.TryGetValue(a[0], out var pointed)) RewritePointerParameter(a, pointed);
        else if (op == 57) RewriteCall(a);
        else if (op == 59 && _oursVars.TryGetValue(a[1], out uint varPtr))
        {
            var words = w.Slice(i, (int)count).ToArray();
            words[1] = _ours[varPtr].ArrayPtr;                  // the variable's declared type
            _list.AddRange(words);
        }
        else RewriteUse(w, i, op, count, a);
    }

    /// <summary>
    /// OpEntryPoint lists every global variable the entry point uses: the new sampler variables are such.
    /// </summary>
    void RewriteEntryPoint(Span<uint> w, int i, uint count)
    {
        var entry = w.Slice(i, (int)count).ToArray().ToList();
        foreach (var extra in _ours.Values)
            if (!extra.Integer) entry.Add(extra.SamplerVar);
        entry[0] = 15u | ((uint)entry.Count << 16);
        _list.AddRange(entry);
    }

    /// <summary>
    /// The old array pointer as-is, then the array of the image (the length's constant id copied), its pointer, the element's pointer, and
    /// (every kind but the integer ones) its sampler array, pointer and variable.
    /// </summary>
    void DeclareSplit(Span<uint> w, int i, uint count, Made declared)
    {
        _list.AddRange(w.Slice(i, (int)count).ToArray());
        _list.AddRange([28u | (4u << 16), declared.Array, declared.Image, declared.Length]);
        _list.AddRange([32u | (4u << 16), declared.ArrayPtr, 0u, declared.Array]);
        _list.AddRange([32u | (4u << 16), declared.ElemPtr, 0u, declared.Image]);
        if (!declared.Integer)
        {
            _list.AddRange([28u | (4u << 16), declared.SamplerArray, _samplerType, declared.Length]);
            _list.AddRange([32u | (4u << 16), declared.SamplerArrayPtr, 0u, declared.SamplerArray]);
            _list.AddRange([32u | (4u << 16), declared.SamplerElemPtr, 0u, _samplerType]);
            _list.AddRange([59u | (4u << 16), declared.SamplerArrayPtr, declared.SamplerVar, 0u]);   // OpVariable, UniformConstant
        }
    }

    /// <summary>
    /// OpTypeFunction: a parameter of a split sampled image type takes the image and its sampler.
    /// </summary>
    void RewriteFunctionType(Span<uint> a)
    {
        var types = new List<uint> { 33u, a[0], a[1] };
        for (int k = 2; k < a.Length; k++)
        {
            // A sampled image parameter becomes the image (and its sampler); a pointer to one becomes a pointer to the image.
            if (_oursByElem.TryGetValue(a[k], out var byValue))
            {
                types.Add(byValue.Image);
                types.Add(_samplerType);
            }
            else if (_oursPointers.TryGetValue(a[k], out var byPointer))
            {
                types.Add(byPointer.ElemPtr);
                types.Add(_samplerType);
            }
            else types.Add(a[k]);
        }

        types[0] = 33u | ((uint)types.Count << 16);
        _list.AddRange(types);
    }

    /// <summary>
    /// A sampled image parameter: the image, then its sampler.
    /// </summary>
    void RewriteImageParameter(Span<uint> a, Made parameter)
    {
        uint sampler = _next++;
        _imagesDone[a[1]] = (parameter, sampler);
        _list.AddRange([55u | (3u << 16), parameter.Image, a[1]]);                     // OpFunctionParameter: the image
        _list.AddRange([55u | (3u << 16), _samplerType, sampler]);                       // and its sampler
    }

    /// <summary>
    /// A sampler the GLSL passes to a function comes as a pointer to the sampled image; the callee loads it. It takes the image's pointer
    /// and the sampler itself.
    /// </summary>
    void RewritePointerParameter(Span<uint> a, Made pointed)
    {
        uint sampler = _next++;
        _pointerParams[a[1]] = sampler;
        _pointerMade[a[1]] = pointed;
        _list.AddRange([55u | (3u << 16), pointed.ElemPtr, a[1]]);
        _list.AddRange([55u | (3u << 16), _samplerType, sampler]);
    }

    /// <summary>
    /// OpFunctionCall: every image argument brings its sampler right after it (a load's from the parallel array here, a parameter's own
    /// otherwise).
    /// </summary>
    void RewriteCall(Span<uint> a)
    {
        var args = new List<uint> { 57u, a[0], a[1], a[2] };
        var before = new List<uint>();
        var samplers = new uint[a.Length];
        for (int k = 3; k < a.Length; k++)
        {
            Made value;
            uint index, beside = 0;
            if (_imagesDone.TryGetValue(a[k], out var loaded))
            {
                value = loaded.Array;
                index = _loadIndex.GetValueOrDefault(a[k]);
                beside = loaded.Sampler;
            }
            else if (_chainMade.TryGetValue(a[k], out var chainArray))
            {
                value = chainArray;
                index = _chainIndex.GetValueOrDefault(a[k]);
            }
            else continue;
            if (beside != 0)
            {
                samplers[k] = beside;
                continue;
            }

            uint chain = _next++, load = _next++;
            before.AddRange([65u | (5u << 16), value.SamplerElemPtr, chain, value.SamplerVar, index]);
            before.AddRange([61u | (4u << 16), _samplerType, load, chain]);
            samplers[k] = load;
        }

        _list.AddRange(before);
        for (int k = 3; k < a.Length; k++)
        {
            args.Add(a[k]);
            if (samplers[k] != 0) args.Add(samplers[k]);
        }

        args[0] = 57u | ((uint)args.Count << 16);
        _list.AddRange(args);
    }

    /// <summary>
    /// Any other instruction: copied, with access chains into, loads from and samples of a split array retyped (a sample gets its pair).
    /// </summary>
    void RewriteUse(Span<uint> w, int i, uint op, uint count, Span<uint> a)
    {
        var words = w.Slice(i, (int)count).ToArray();
        if (op == 65 && _pointerAt.TryGetValue(a[0], out uint pointee) && _oursByElem.TryGetValue(pointee, out var chainArray))
        {
            words[1] = chainArray.ElemPtr;                 // the chain's result type: pointer to the plain image
            _chainMade[a[1]] = chainArray;
        }
        else if (op == 61 && a[2] != 0 && _chainMade.TryGetValue(a[2], out var loadArray))
        {
            words[1] = loadArray.Image;                    // the load dereferences the pointer: the image itself
            _imagesDone[a[1]] = (loadArray, 0);
            _loadIndex[a[1]] = _chainIndex.GetValueOrDefault(a[2]);
        }
        else if (op == 61 && a[2] != 0 && _pointerParams.TryGetValue(a[2], out uint samplerParam)
            && _pointerMade.TryGetValue(a[2], out var loadMade))
        {
            words[1] = loadMade.Image;
            _imagesDone[a[1]] = (loadMade, samplerParam);
        }
        else if (op == 100 && a.Length > 2 && _imagesDone.TryGetValue(a[2], out var imageOnly) && imageOnly.Array.Integer)
            words[0] = 83u | (count << 16);                // OpImage -> OpCopyObject: the operand is already the image
        else if (a.Length > 2 && _imagesDone.TryGetValue(a[2], out var sampled) && !sampled.Array.Integer)
            words[3] = PairSample(a, sampled);

        _list.AddRange(words);
    }

    /// <summary>
    /// A sample needs the pair: the image is at hand, the sampler is loaded (from the parallel array with the same index) or is the
    /// function's own parameter. Emits the pair and returns its id.
    /// </summary>
    uint PairSample(Span<uint> a, (Made Array, uint Sampler) sampled)
    {
        uint pair = _next++, sampler;
        if (sampled.Sampler != 0) sampler = sampled.Sampler;
        else
        {
            uint index = _loadIndex.GetValueOrDefault(a[2]), chain = _next++, load = _next++;
            _list.AddRange([65u | (5u << 16), sampled.Array.SamplerElemPtr, chain, sampled.Array.SamplerVar, index]);
            _list.AddRange([61u | (4u << 16), _samplerType, load, chain]);
            sampler = load;
        }

        _list.AddRange([86u | (5u << 16), sampled.Array.Sampled, pair, a[2], sampler]);   // OpSampledImage
        return pair;
    }

    #endregion

    /// <summary>
    /// One combined array's split: the old array pointer, the plain image array's ids, and (not for the integer kinds) its sampler array's.
    /// </summary>
    sealed class Made
    {
        /// <summary>
        /// The old array pointer, the plain image array's pointer, its element's pointer, the array, the image and sampled image types, the
        /// length's constant id and the sampler array's binding.
        /// </summary>
        public uint OldPtr, ArrayPtr, ElemPtr, Array, Image, Sampled, Length, Binding;

        /// <summary>
        /// An integer kind: no sampler array.
        /// </summary>
        public bool Integer;

        /// <summary>
        /// The sampler array's type, its pointer, its element's pointer and its variable.
        /// </summary>
        public uint SamplerArray, SamplerArrayPtr, SamplerElemPtr, SamplerVar;
    }
}
