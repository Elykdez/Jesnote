using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jesnote.Core;

/// <summary>
/// A loaded JSON document held as a compact, virtualization-friendly tree.
/// Memory layout (parallel arrays, indexed by node id 0..Count-1):
///     Types[i]          byte (1) - <see cref="JsonNodeType"/>
///     Keys[i]           ref  (8) - key string (or "[i]" for array elements; null for root). Object keys are interned during parse.
///     Values[i]         long (8) - discriminated value slot, meaning depends on Types[i]
///     Parents[i         int  (4) - parent id, -1 for root
///     FirstChild[i]     int  (4) - id of first child or -1
///     NextSibling[i]    int  (4) - id of next sibling or -1
/// Total: 29 bytes/node + however much storage the active <see cref="StringStorage"/>
/// chooses to use for the actual String values. The encoded ref returned by
/// <see cref="StringStorage.Append"/> rides in the existing 64-bit Values slot per String node,
/// so swapping storage strategies does not change the per-node memory cost.
/// Object key strings are interned, which collapses many duplicate property names
/// to a single instance - typical JSON has < 200 distinct keys regardless of document size.
///
/// Object children are stored alphabetically by key.
/// Loading uses one of three shapes:
/// - In-memory two-pass: the file (or supplied buffer) is fully loaded into a byte[]
///     and parsed twice over the same span - once to count nodes,
///     once to build the pre-sized arrays. Used for non-JSONL up to <see cref="InMemoryLoadLimit"/>.
/// - Streaming two-pass: chunked read with two passes over the file stream.
///     <see cref="FileOptions.SequentialScan"/> hints aggressive prefetch;
///     a 1 MB stream buffer keeps the syscall rate down. Used for non-JSONL
///     files larger than <see cref="InMemoryLoadLimit"/>.
/// - Streaming single-pass with progressive publish: read once, append nodes
///     into grow-on-write arrays, publish batches to the UI every ~33 ms via
///     <see cref="DocumentGrew"/>. Used for all JSONL regardless of size.
/// </summary>
public sealed class JsonTreeDocument
{
    #region Constants

    public const int RootId = 0;
    public const int NotFound = -1;

    const int InitialBufferSize = 256 * 1024;
    const long FileStreamBufferSize = 1 << 20;
    const int ProgressTick = 10_000;
    const int SmallIndexKeyCacheSize = 4096;
    const int MaxParserDepth = 256;

    // Upper bound for the chunked-stream parser's growing buffer. The buffer
    // must hold a single Utf8JsonReader token in full (string/number literal);
    // for typical JSON 1 MB is plenty, but a pathological huge string token
    // can force doubling. Cap matches InMemoryLoadLimit so that anything that
    // wouldn't fit there gets a clear error rather than a raw OOM at ~2 GB.
    const int MaxStreamChunkBuffer = 1 << 30; // 1 GiB

    // Files up to this many bytes are fully loaded into RAM before parsing.
    // Avoids reading the file from disk twice. Above this, we fall back to a
    // streaming chunked parse - the OS page cache keeps the second pass cheap
    // for files smaller than physical RAM.
    const long InMemoryLoadLimit = 1L << 30; // 1 GiB

    // Streaming JSONL with progressive publish.
    // Single pass: read chunks, parse complete lines, append nodes to the
    // grow-on-write arrays, link top-level values into the synthetic root via
    // a persistent tail pointer. Every ~33 ms (and at the very end) Publish()
    // bumps the visible Count and fires DocumentGrew so the UI grows live.
    // No count pass; no second IO pass.
    const long StreamPublishIntervalTicks = 33;

    // Capacity threshold above which a pooled frame's child list is replaced
    // instead of cleared. Parsing a wide array/object can grow Children to
    // millions of entries; List.Clear() keeps the underlying T[] alive, so
    // without this the pool would retain hundreds of MB across loads.
    const int FrameChildrenShrinkAbove = 1024;

    #endregion

    #region Fields and Events

    public byte[] Types = [];
    public string?[] Keys = [];
    public long[] Values = [];
    public int[] Parents = [];
    public int[] FirstChild = [];
    public int[] NextSibling = [];

    /// <summary>
    /// Which storage strategy to use for String values. Read at load start
    /// (via <see cref="Reset"/>); changing it mid-document has no effect on
    /// already-stored values. The default matches the post-#3 layout.
    /// </summary>
    public StringStorageMode StringStorageMode { get; set; } = StringStorageMode.Compact;

    StringStorage _strings = StringStorage.Create(StringStorageMode.Compact);

    int _nodeCapacity;

    // Last value of _count at which we emitted a step-3 progress report.
    // JSONL parses tokens in line-sized batches, so _count can skip multiples
    // of ProgressTick entirely (e.g. lines that always have 17 tokens never
    // land _count on a multiple of 10000); the bucket-diff comparison below
    // fires reliably regardless of per-line token count.
    int _lastProgressCount;

    // The parser-internal node counter. Grows as nodes are appended.
    // External consumers see <see cref="Count"/>, which is _publishedCount.
    int _count;

    // Publication marker: ids in [0.._publishedCount) are guaranteed fully
    // initialised and safe for concurrent read by the UI thread. Written under
    // a release fence so reads of node data after a Volatile.Read of this
    // field are valid. Streaming loads bump this every ~33 ms; non-streaming
    // loads bump it once at end of load.
    int _publishedCount;

    /// <summary>Number of nodes safe to read from any thread.</summary>
    public int Count => Volatile.Read(ref _publishedCount);

    /// <summary>Fires after a streaming load publishes additional nodes. Raised on a worker thread.</summary>
    public event Action? DocumentGrew;

    /// <summary>
    /// Set by <see cref="LoadAsync(string, IProgress{ProgressInfo}?, CancellationToken, bool)"/>
    /// (and the byte[] overload) to reflect the source format. Drives how
    /// <see cref="SaveAsync"/> writes - JSONL round-trips back to newline-
    /// delimited form, JSON writes a single top-level value.
    /// </summary>
    public bool IsJsonl { get; private set; }

    bool _isModified;

    /// <summary>True if the document has been mutated (graft or edit) since the last load or save.</summary>
    public bool IsModified => _isModified;

    /// <summary>Fires after any mutation that changes <see cref="IsModified"/> or the modified-id set. Raised on the calling thread.</summary>
    public event Action? DocumentModified;

    // Per-edit undo/redo stacks. Each op records the inverse so undo is O(1).
    // Empty when no edits exist; no per-node fields added.
    readonly Stack<EditOp> _undo = new();
    readonly Stack<EditOp> _redo = new();

    // Ids touched by any edit since the last load or save. Consulted in paint only.
    readonly HashSet<int> _modifiedIds = new();

    /// <summary>Ids touched by any edit since the last load or save.</summary>
    public IReadOnlyCollection<int> ModifiedIds => _modifiedIds;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // Cache of "[0]".."[N-1]" - array-heavy docs (telemetry, logs) otherwise
    // allocate one short string per element. 100M × 8 byte ref = unavoidable in
    // Keys[], but the heap-side strings dedupe for small array indices.
    static readonly string[] s_smallIndexKeys = BuildSmallIndexKeys(SmallIndexKeyCacheSize);
    static readonly JsonReaderOptions s_readerOptions = new() { MaxDepth = MaxParserDepth };

    readonly Stack<Frame> _framePool = new();

    #endregion

    #region Lifecycle

    static string[] BuildSmallIndexKeys(int n)
    {
        var arr = new string[n];
        for (int i = 0; i < n; i++)
            arr[i] = "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
        return arr;
    }

    static string ArrayIndexKey(int i) =>
        (uint)i < (uint)s_smallIndexKeys.Length
            ? s_smallIndexKeys[i]
            : "[" + i.ToString(CultureInfo.InvariantCulture) + "]";

    public void Reset()
    {
        Types = [];
        Keys = [];
        Values = [];
        Parents = [];
        FirstChild = [];
        NextSibling = [];
        // Rebind to whichever storage matches the current mode. Reset on the
        // existing instance only clears its data; switching modes between
        // loads is the use-case for swapping the instance itself.
        _strings = StringStorage.Create(StringStorageMode);
        _nodeCapacity = 0;
        _count = 0;
        _lastProgressCount = 0;
        Volatile.Write(ref _publishedCount, 0);
        _framePool.Clear();
        IsJsonl = false;
        _isModified = false;
        _undo.Clear();
        _redo.Clear();
        _modifiedIds.Clear();
    }

    /// <summary>
    /// Publish the current parser-internal count to <see cref="Count"/> readers
    /// and fire <see cref="DocumentGrew"/>. All node data for ids in
    /// [0.._count) MUST be fully written before this call (the Volatile.Write
    /// establishes the release fence). Safe to call on any thread; readers see
    /// updated state on their next <see cref="Volatile.Read"/>.
    /// </summary>
    void Publish()
    {
        Volatile.Write(ref _publishedCount, _count);
        DocumentGrew?.Invoke();
    }

    #endregion

    #region Public Accessors

    public JsonNodeType TypeOf(int id) => (JsonNodeType)Types[id];

    public string KeyOf(int id) => Keys[id] ?? string.Empty;

    public double GetNumber(int id) => BitConverter.Int64BitsToDouble(Values[id]);

    public string GetString(int id) => _strings.Get(Values[id]);

    public bool GetBool(int id) => Values[id] != 0;

    /// <summary>Boxed value access - kept for compatibility. Hot paths should use the typed getters above.</summary>
    public object? ValueOf(int id) =>
        (JsonNodeType)Types[id] switch
        {
            JsonNodeType.String => GetString(id),
            JsonNodeType.Number => BitConverter.Int64BitsToDouble(Values[id]),
            JsonNodeType.Boolean => Values[id] != 0,
            _ => null,
        };

    public int ParentOf(int id) => Parents[id];

    public bool IsBranch(int id) => id >= 0 && id < Count && ((JsonNodeType)Types[id]).IsBranch();

    public int ChildCount(int id)
    {
        int published = Count;
        if (id < 0 || id >= published)
            return 0;
        int c = FirstChild[id];
        int n = 0;
        while (c != -1 && c < published)
        {
            n++;
            c = NextSibling[c];
        }
        return n;
    }

    public IEnumerable<int> ChildIds(int id)
    {
        int published = Count;
        if (id < 0 || id >= published)
            yield break;
        int c = FirstChild[id];
        while (c != -1 && c < published)
        {
            yield return c;
            c = NextSibling[c];
        }
    }

    /// <summary>Returns the path from root (exclusive) to id (exclusive).</summary>
    public List<int> Path(int id)
    {
        var path = new List<int>();
        if (id <= 0 || id >= Count)
            return path;
        int cur = Parents[id];
        while (cur > 0)
        {
            path.Add(cur);
            cur = Parents[cur];
        }
        path.Reverse();
        return path;
    }

    #endregion

    #region Loading

    public Task LoadAsync(
        string path,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct,
        bool jsonl = false
    ) => LoadFileInternalAsync(path, progress, ct, jsonl);

    public Task LoadAsync(
        byte[] data,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct,
        bool jsonl = false
    ) => Task.Run(() => LoadFromBytes(data, progress, ct, jsonl), ct);

    async Task LoadFileInternalAsync(
        string path,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct,
        bool jsonl
    )
    {
        IsJsonl = jsonl;

        // JSONL: always stream + publish progressively so the UI can render
        // the first rows almost immediately even for multi-GB files. Skips
        // the count pass entirely - arrays grow on append.
        if (jsonl)
        {
            await StreamJsonlAsync(path, progress, ct).ConfigureAwait(false);
            return;
        }

        progress?.Report(new ProgressInfo(1, 3, 0, 0));
        long size = new FileInfo(path).Length;

        if (size <= InMemoryLoadLimit)
        {
            byte[] data = await ReadAllBytesAsync(path, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => LoadFromBytes(data, progress, ct, jsonl: false), ct)
                .ConfigureAwait(false);
            return;
        }

        // Streaming path for single-document JSON too large to fit in memory.
        // Still does the two-pass count+build because we cannot render a
        // partial subtree of a single root usefully.
        progress?.Report(new ProgressInfo(2, 3, 0, 0));
        ParseStats stats;
        using (var s1 = OpenSequentialRead(path))
        {
            stats = await CountFromStreamAsync(s1, progress, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(new ProgressInfo(3, 3, stats.TotalCount, 0));

        AllocateArrays(stats.TotalCount, stats.StringCount);
        using (var s2 = OpenSequentialRead(path))
        {
            await BuildFromStreamAsync(s2, stats.TotalCount, progress, ct).ConfigureAwait(false);
        }
        ValidateRoot();
        Publish();
        progress?.Report(new ProgressInfo(3, 3, stats.TotalCount, 1.0));
    }

    void LoadFromBytes(
        byte[] data,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct,
        bool jsonl
    )
    {
        IsJsonl = jsonl;
        progress?.Report(new ProgressInfo(2, 3, 0, 0));

        var stats = jsonl ? CountJsonl(data, progress, ct) : CountTokens(data, progress, ct);
        ct.ThrowIfCancellationRequested();

        progress?.Report(new ProgressInfo(3, 3, stats.TotalCount, 0));
        AllocateArrays(stats.TotalCount, stats.StringCount);
        if (jsonl)
            BuildJsonl(data, stats.TotalCount, progress, ct);
        else
            BuildFromSpan(data, stats.TotalCount, progress, ct);
        ValidateRoot();
        Publish();
        progress?.Report(new ProgressInfo(3, 3, stats.TotalCount, 1.0));
    }

    #endregion

    #region In-Memory Parsing

    // Non-JSONL: count tokens then build into pre-sized arrays.
    static ParseStats CountTokens(
        ReadOnlySpan<byte> data,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        var state = new JsonReaderState(s_readerOptions);
        var reader = new Utf8JsonReader(data, isFinalBlock: true, state);
        int count = 0;
        int strings = 0;
        int sinceCheck = 0;
        while (reader.Read())
        {
            AccumulateTokenCount(reader.TokenType, ref count, ref strings);
            if (++sinceCheck >= 65536)
            {
                ct.ThrowIfCancellationRequested();
                ReportByteProgress(progress, 2, reader.BytesConsumed, data.Length);
                sinceCheck = 0;
            }
        }
        ReportByteProgress(progress, 2, data.Length, data.Length);
        return new ParseStats { TotalCount = count, StringCount = strings };
    }

    void BuildFromSpan(
        ReadOnlySpan<byte> data,
        int expectedSize,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        var state = new JsonReaderState(s_readerOptions);
        var reader = new Utf8JsonReader(data, isFinalBlock: true, state);
        var stack = new Stack<Frame>();
        var ctx = new BuildContext { Stack = stack };

        try
        {
            ProcessAllTokens(ref reader, ctx, expectedSize, progress, ct);
        }
        finally
        {
            ReturnAllFrames(stack);
        }
    }

    void ProcessAllTokens(
        ref Utf8JsonReader reader,
        BuildContext ctx,
        int expectedSize,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        while (reader.Read())
        {
            HandleToken(ref reader, ctx);

            if (expectedSize > 0 && _count % ProgressTick == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(
                    new ProgressInfo(3, 3, expectedSize, (double)_count / expectedSize)
                );
            }
        }
    }

    // JSONL byte path (used when a JSONL document is fed via the byte[] overload).
    // The file-streaming JSONL path lives in StreamJsonlAsync; this two-pass
    // variant is kept for the in-memory entry point.
    // .NET 8's Utf8JsonReader cannot read multiple top-level values from a single span
    // (the AllowMultipleValues option only exists in .NET 9+), so we split on '\n' and
    // parse each non-empty line independently. Each line's top-level value is attached
    // to a synthetic Array root as [0], [1]... so the existing tree code renders it
    // like a regular JSON array.
    static ParseStats CountJsonl(
        ReadOnlySpan<byte> data,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        int count = 1; // synthetic Array root
        int strings = 0;
        int sinceCheck = 0;
        int pos = 0;
        while (TryReadLine(data, ref pos, isFinal: true, out var line))
        {
            if (!line.IsEmpty)
                CountSingleValueLine(line, ref count, ref strings);
            if (++sinceCheck >= 1024)
            {
                ct.ThrowIfCancellationRequested();
                ReportByteProgress(progress, 2, pos, data.Length);
                sinceCheck = 0;
            }
        }
        ReportByteProgress(progress, 2, data.Length, data.Length);
        return new ParseStats { TotalCount = count, StringCount = strings };
    }

    static void CountSingleValueLine(ReadOnlySpan<byte> line, ref int count, ref int strings)
    {
        var reader = new Utf8JsonReader(line, isFinalBlock: true, default);
        while (reader.Read())
            AccumulateTokenCount(reader.TokenType, ref count, ref strings);
    }

    void BuildJsonl(
        ReadOnlySpan<byte> data,
        int expectedSize,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        var stack = new Stack<Frame>();
        var ctx = new BuildContext { Stack = stack };
        PushSyntheticJsonlRoot(stack);
        try
        {
            int pos = 0;
            while (TryReadLine(data, ref pos, isFinal: true, out var line))
            {
                if (!line.IsEmpty)
                {
                    var reader = new Utf8JsonReader(line, isFinalBlock: true, default);
                    while (reader.Read())
                        HandleToken(ref reader, ctx);
                }
                ct.ThrowIfCancellationRequested();
                if (expectedSize > 0 && _count - _lastProgressCount >= ProgressTick)
                {
                    _lastProgressCount = _count;
                    progress?.Report(
                        new ProgressInfo(3, 3, expectedSize, (double)_count / expectedSize)
                    );
                }
            }
            PopSyntheticJsonlRoot(stack);
        }
        finally
        {
            ReturnAllFrames(stack);
        }
    }

    // Create the synthetic Array root that holds top-level JSONL values as children.
    // Must run before any HandleToken so the first top-level value attaches
    // with key "[0]" via the normal array-index path.
    void PushSyntheticJsonlRoot(Stack<Frame> stack)
    {
        int rootId = AddArray(string.Empty);
        // Parents[] is filled with -1 in AllocateArrays; explicit assignment here
        // documents intent and survives any future change to that fill.
        Parents[rootId] = -1;
        stack.Push(RentFrame(rootId, isObject: false));
    }

    void PopSyntheticJsonlRoot(Stack<Frame> stack)
    {
        if (stack.Count == 0)
            return;
        var f = stack.Pop();
        LinkChildren(f.Id, f.Children);
        ReturnFrame(f);
    }

    #endregion

    #region Streaming JSONL

    // Single-Pass, Live Publish
    // JSONL splits on \n or use AllowMultipleValues (.NET 9+)
    // Multi-value + grow because progressive publish only makes sense for multi-value.
    async Task StreamJsonlAsync(
        string path,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        long totalBytes = new FileInfo(path).Length;

        // Synthetic Array root + persistent build context spanning the whole load.
        // Each line resets only StashedKey on the context.
        var stack = new Stack<Frame>();
        var ctx = new BuildContext { Stack = stack };
        int rootId = AddArray(string.Empty);
        Parents[rootId] = -1;
        var rootFrame = RentFrame(rootId, isObject: false);
        rootFrame.IsStreamingRoot = true;
        stack.Push(rootFrame);
        Publish();

        long lastPublishTicks = Environment.TickCount64;

        using var stream = OpenSequentialRead(path);
        try
        {
            await PumpStreamAsync(
                    stream,
                    consume: (data, isFinal) => StreamJsonlChunk(data, isFinal, this, ctx),
                    // Publish + progress every ~33 ms. Doing it after each chunk
                    // (rather than after each line) avoids cache-line ping-pong
                    // on _publishedCount and keeps the UI thread's event posts
                    // bounded at ~30 Hz.
                    afterChunk: pos =>
                    {
                        long now = Environment.TickCount64;
                        if (now - lastPublishTicks >= StreamPublishIntervalTicks)
                        {
                            Publish();
                            if (totalBytes > 0)
                                progress?.Report(
                                    new ProgressInfo(3, 3, _count, (double)pos / totalBytes)
                                );
                            lastPublishTicks = now;
                        }
                    },
                    tooLongErrorKey: "Error.JsonlLineTooLong",
                    ct: ct
                )
                .ConfigureAwait(false);
        }
        finally
        {
            ReturnAllFrames(stack);
            // Final publish so cancellation and successful completion both
            // leave the visible Count equal to whatever we managed to parse.
            Publish();
        }

        ValidateRoot();
        progress?.Report(new ProgressInfo(3, 3, _count, 1.0));
    }

    static int StreamJsonlChunk(
        ReadOnlySpan<byte> data,
        bool isFinal,
        JsonTreeDocument doc,
        BuildContext ctx
    )
    {
        int pos = 0;
        while (TryReadLine(data, ref pos, isFinal, out var line))
        {
            if (!line.IsEmpty)
                ParseStreamingLine(doc, ctx, line);
        }
        return pos;
    }

    static void ParseStreamingLine(JsonTreeDocument doc, BuildContext ctx, ReadOnlySpan<byte> line)
    {
        ctx.StashedKey = null;
        var reader = new Utf8JsonReader(line, isFinalBlock: true, default);
        while (reader.Read())
            doc.HandleToken(ref reader, ctx);
    }

    #endregion

    #region Streaming JSON

    // Non-JSONL, Two-Pass
    // JSON feeds chunks straight through, publish only at the end
    // Large JSON picked single-root + pre-size to bound memory
    // TODO: Maybe extend progressive publish to single-root JSON so user sees a partial tree while it streams in.
    static async Task<ParseStats> CountFromStreamAsync(
        Stream stream,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        var state = new JsonReaderState(s_readerOptions);
        int count = 0;
        int stringCount = 0;
        long totalBytes = stream.CanSeek ? stream.Length : 0;

        await PumpStreamAsync(
                stream,
                consume: (data, isFinal) =>
                    CountChunk(data, isFinal, ref state, ref count, ref stringCount),
                afterChunk: pos => ReportByteProgress(progress, 2, pos, totalBytes),
                tooLongErrorKey: "Error.JsonHugeToken",
                ct: ct
            )
            .ConfigureAwait(false);

        ReportByteProgress(progress, 2, totalBytes, totalBytes);
        return new ParseStats { TotalCount = count, StringCount = stringCount };
    }

    static int CountChunk(
        ReadOnlySpan<byte> data,
        bool isFinalBlock,
        ref JsonReaderState state,
        ref int count,
        ref int stringCount
    )
    {
        var reader = new Utf8JsonReader(data, isFinalBlock, state);
        while (reader.Read())
            AccumulateTokenCount(reader.TokenType, ref count, ref stringCount);
        state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    async Task BuildFromStreamAsync(
        Stream stream,
        int expectedSize,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        var state = new JsonReaderState(s_readerOptions);
        var stack = new Stack<Frame>();
        var ctx = new BuildContext { Stack = stack };

        try
        {
            await PumpStreamAsync(
                    stream,
                    consume: (data, isFinal) =>
                        BuildChunk(data, isFinal, ref state, this, ctx, expectedSize, progress),
                    afterChunk: null,
                    tooLongErrorKey: "Error.JsonHugeToken",
                    ct: ct
                )
                .ConfigureAwait(false);
        }
        finally
        {
            ReturnAllFrames(stack);
        }
    }

    static int BuildChunk(
        ReadOnlySpan<byte> data,
        bool isFinalBlock,
        ref JsonReaderState state,
        JsonTreeDocument doc,
        BuildContext ctx,
        int expectedSize,
        IProgress<ProgressInfo>? progress
    )
    {
        var reader = new Utf8JsonReader(data, isFinalBlock, state);
        while (reader.Read())
        {
            doc.HandleToken(ref reader, ctx);

            if (expectedSize > 0 && doc._count % ProgressTick == 0)
            {
                progress?.Report(
                    new ProgressInfo(3, 3, expectedSize, (double)doc._count / expectedSize)
                );
            }
        }
        state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    #endregion

    #region Token Handling

    void HandleToken(ref Utf8JsonReader reader, BuildContext ctx)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                string key = CurrentKey(ctx);
                int id = AddObject(key);
                AttachToParent(ctx, id);
                ctx.Stack.Push(RentFrame(id, isObject: true));
                ctx.StashedKey = null;
                break;
            }
            case JsonTokenType.StartArray:
            {
                string key = CurrentKey(ctx);
                int id = AddArray(key);
                AttachToParent(ctx, id);
                ctx.Stack.Push(RentFrame(id, isObject: false));
                ctx.StashedKey = null;
                break;
            }
            case JsonTokenType.EndObject:
            {
                var f = ctx.Stack.Pop();
                if (f.Children.Count > 1)
                {
                    // Sort object children alphabetically.
                    // Struct comparer + Span<int>.Sort<TComparer> avoids the closure allocation
                    // a List.Sort lambda would incur per parent.
                    var span = CollectionsMarshal.AsSpan(f.Children);
                    span.Sort(new KeyComparer(Keys));
                }
                LinkChildren(f.Id, f.Children);
                ReturnFrame(f);
                break;
            }
            case JsonTokenType.EndArray:
            {
                var f = ctx.Stack.Pop();
                LinkChildren(f.Id, f.Children);
                ReturnFrame(f);
                break;
            }
            case JsonTokenType.PropertyName:
                ctx.StashedKey = InternKey(ctx, reader.GetString() ?? string.Empty);
                break;
            case JsonTokenType.String:
            {
                string key = CurrentKey(ctx);
                long encoded = _strings.Append(ref reader);
                int id = AddStringRef(key, encoded);
                AttachToParent(ctx, id);
                ctx.StashedKey = null;
                break;
            }
            case JsonTokenType.Number:
            {
                string key = CurrentKey(ctx);
                double val = reader.GetDouble();
                int id = AddNumber(key, val);
                AttachToParent(ctx, id);
                ctx.StashedKey = null;
                break;
            }
            case JsonTokenType.True:
            {
                string key = CurrentKey(ctx);
                int id = AddBool(key, true);
                AttachToParent(ctx, id);
                ctx.StashedKey = null;
                break;
            }
            case JsonTokenType.False:
            {
                string key = CurrentKey(ctx);
                int id = AddBool(key, false);
                AttachToParent(ctx, id);
                ctx.StashedKey = null;
                break;
            }
            case JsonTokenType.Null:
            {
                string key = CurrentKey(ctx);
                int id = AddNull(key);
                AttachToParent(ctx, id);
                ctx.StashedKey = null;
                break;
            }
        }
    }

    string CurrentKey(BuildContext ctx)
    {
        if (ctx.Stack.Count == 0)
            return string.Empty;
        var top = ctx.Stack.Peek();
        if (top.IsObject)
            return ctx.StashedKey ?? string.Empty;
        int idx = top.ArrayIndex++;
        return ArrayIndexKey(idx);
    }

    static string InternKey(BuildContext ctx, string raw)
    {
        if (ctx.KeyInterner.TryGetValue(raw, out var canonical))
            return canonical;
        ctx.KeyInterner[raw] = raw;
        return raw;
    }

    void AttachToParent(BuildContext ctx, int childId)
    {
        if (ctx.Stack.Count == 0)
        {
            // root: parent stays -1
            Parents[childId] = -1;
            return;
        }
        var top = ctx.Stack.Peek();
        Parents[childId] = top.Id;
        if (top.IsStreamingRoot)
        {
            // Eager link into the root's child chain via tail pointer. Avoids
            // accumulating millions of ids in top.Children. The link write to
            // NextSibling[oldTail] happens here; readers must filter chain
            // walks by Count to skip nodes added after the last Publish().
            if (top.Tail == -1)
                FirstChild[top.Id] = childId;
            else
                NextSibling[top.Tail] = childId;
            top.Tail = childId;
            return;
        }
        top.Children.Add(childId);
    }

    void LinkChildren(int parentId, List<int> children)
    {
        if (children.Count == 0)
        {
            FirstChild[parentId] = -1;
            return;
        }
        FirstChild[parentId] = children[0];
        for (int i = 0; i < children.Count - 1; i++)
        {
            NextSibling[children[i]] = children[i + 1];
        }
        NextSibling[children[^1]] = -1;
    }

    #endregion

    #region Storage Management

    void AllocateArrays(int size, int stringCount)
    {
        _ = stringCount; // string storage is grow-on-write; only used as a hint
        if (size < 1)
            size = 1;
        Types = new byte[size];
        Keys = new string?[size];
        Values = new long[size];
        Parents = new int[size];
        FirstChild = new int[size];
        NextSibling = new int[size];
        _strings = StringStorage.Create(StringStorageMode);
        _nodeCapacity = size;
        Array.Fill(Parents, -1);
        Array.Fill(FirstChild, -1);
        Array.Fill(NextSibling, -1);
        _count = 0;
        _lastProgressCount = 0;
        Volatile.Write(ref _publishedCount, 0);
    }

    /// <summary>
    /// Grow the parallel node arrays to hold at least <paramref name="needed"/>
    /// entries, doubling current capacity each time. New link-array slots are
    /// filled with -1 so unwritten nodes look empty to chain walkers.
    /// </summary>
    void EnsureNodeCapacity(int needed)
    {
        if (needed <= _nodeCapacity)
            return;
        int newCap = _nodeCapacity == 0 ? 16 * 1024 : _nodeCapacity;
        while (newCap < needed)
            newCap *= 2;
        int oldCap = _nodeCapacity;
        Array.Resize(ref Types, newCap);
        Array.Resize(ref Keys, newCap);
        Array.Resize(ref Values, newCap);
        Array.Resize(ref Parents, newCap);
        Array.Resize(ref FirstChild, newCap);
        Array.Resize(ref NextSibling, newCap);
        int added = newCap - oldCap;
        Array.Fill(Parents, -1, oldCap, added);
        Array.Fill(FirstChild, -1, oldCap, added);
        Array.Fill(NextSibling, -1, oldCap, added);
        _nodeCapacity = newCap;
    }

    void ValidateRoot()
    {
        if (_count == 0)
            throw new InvalidDataException(Localization.T("Error.DocumentEmpty"));
        var rootType = (JsonNodeType)Types[0];
        if (rootType != JsonNodeType.Object && rootType != JsonNodeType.Array)
            throw new InvalidDataException(Localization.T("Error.RootMustBeObjectOrArray"));
    }

    #endregion

    #region Frame Pool

    Frame RentFrame(int id, bool isObject)
    {
        Frame f;
        if (_framePool.Count > 0)
        {
            f = _framePool.Pop();
            f.Children.Clear();
        }
        else
        {
            f = new Frame();
        }
        f.Id = id;
        f.IsObject = isObject;
        f.ArrayIndex = 0;
        f.IsStreamingRoot = false;
        f.Tail = -1;
        return f;
    }

    void ReturnFrame(Frame f)
    {
        if (f.Children.Capacity > FrameChildrenShrinkAbove)
            f.Children = new List<int>(4);
        else
            f.Children.Clear();
        if (_framePool.Count < 1024)
            _framePool.Push(f);
    }

    void ReturnAllFrames(Stack<Frame> stack)
    {
        while (stack.Count > 0)
            ReturnFrame(stack.Pop());
    }

    #endregion

    #region Search

    /// <summary>
    /// Finds the next node matching the pattern (wildcard) under the given search type.
    /// Mirrors the Go implementation: depth-first, ignores the starting node itself,
    /// returns -1 when nothing found.
    /// </summary>
    public int Search(int startId, string pattern, SearchType type, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pattern))
            return NotFound;
        if (Count == 0)
            return NotFound;
        if (startId < 0 || startId >= Count)
            startId = RootId;

        pattern = NormalizeSearchPattern(pattern, type);
        var rx = Wildcard.Compile(pattern, RegexOptions.None);
        int id = startId;

        while (true)
        {
            int found = SearchNode(id, rx, type, ct, ignoreSelf: id == startId);
            if (found != NotFound && found != startId)
                return found;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int parent = Parents[id];
                if (parent < 0)
                    return NotFound;
                int next = NextSibling[id];
                if (next != -1)
                {
                    id = next;
                    break;
                }
                if (parent == RootId)
                    return NotFound;
                id = parent;
            }
        }
    }

    int SearchNode(int id, Regex rx, SearchType type, CancellationToken ct, bool ignoreSelf)
    {
        if (IsBranch(id))
        {
            int found = SearchContainer(id, rx, type, ct);
            if (found != NotFound)
                return found;
        }
        if (ignoreSelf)
            return NotFound;

        switch (type)
        {
            case SearchType.Key:
                if (rx.IsMatch(Keys[id] ?? string.Empty))
                    return id;
                break;
            case SearchType.Keyword:
                switch ((JsonNodeType)Types[id])
                {
                    case JsonNodeType.Boolean:
                        if (rx.IsMatch(Values[id] != 0 ? "true" : "false"))
                            return id;
                        break;
                    case JsonNodeType.Null:
                        if (rx.IsMatch("null"))
                            return id;
                        break;
                }
                break;
            case SearchType.Number:
                if ((JsonNodeType)Types[id] == JsonNodeType.Number)
                {
                    // Must use the SAME formatter as DisplayValue / RawValue -
                    // otherwise a user typing the number they see ("1.5") would
                    // miss because search formatted it differently ("1.5000000000000000").
                    string s = FormatNumber(BitConverter.Int64BitsToDouble(Values[id]));
                    if (rx.IsMatch(s))
                        return id;
                }
                break;
            case SearchType.String:
                if ((JsonNodeType)Types[id] == JsonNodeType.String)
                {
                    if (_strings.Match(Values[id], rx))
                        return id;
                }
                break;
        }
        return NotFound;
    }

    int SearchContainer(int id, Regex rx, SearchType type, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int c = FirstChild[id];
        while (c != -1)
        {
            int found = SearchNode(c, rx, type, ct, ignoreSelf: false);
            if (found != NotFound)
                return found;
            c = NextSibling[c];
        }
        return NotFound;
    }

    static string NormalizeSearchPattern(string pattern, SearchType type)
    {
        if (type != SearchType.String)
            return pattern;

        // String search is expected to behave like a "contains" search for
        // plain text input. Keep explicit wildcard patterns untouched so power
        // users can still control the match shape.
        return pattern.Contains('*') ? pattern : $"*{pattern}*";
    }

    #endregion

    #region Extract

    public byte[] Extract(int id)
    {
        if (id < 0 || id >= Count)
            throw new ArgumentOutOfRangeException(nameof(id));
        var type = (JsonNodeType)Types[id];
        if (type != JsonNodeType.Array && type != JsonNodeType.Object)
            throw new InvalidOperationException(Localization.T("Error.ExtractBranchesOnly"));

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteNode(writer, id, propertyName: null);
        }
        return ms.ToArray();
    }

    void WriteNode(Utf8JsonWriter writer, int id, string? propertyName)
    {
        var type = (JsonNodeType)Types[id];
        switch (type)
        {
            case JsonNodeType.Object:
                if (propertyName != null)
                    writer.WriteStartObject(propertyName);
                else
                    writer.WriteStartObject();

                {
                    int c = FirstChild[id];
                    while (c != -1)
                    {
                        WriteNode(writer, c, Keys[c]);
                        c = NextSibling[c];
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonNodeType.Array:
                if (propertyName != null)
                    writer.WriteStartArray(propertyName);
                else
                    writer.WriteStartArray();

                {
                    int c = FirstChild[id];
                    while (c != -1)
                    {
                        WriteNode(writer, c, null);
                        c = NextSibling[c];
                    }
                }
                writer.WriteEndArray();
                break;
            case JsonNodeType.String:
                _strings.Write(writer, propertyName, Values[id]);
                break;
            case JsonNodeType.Number:
                if (propertyName != null)
                    writer.WriteNumber(propertyName, BitConverter.Int64BitsToDouble(Values[id]));
                else
                    writer.WriteNumberValue(BitConverter.Int64BitsToDouble(Values[id]));
                break;
            case JsonNodeType.Boolean:
                if (propertyName != null)
                    writer.WriteBoolean(propertyName, Values[id] != 0);
                else
                    writer.WriteBooleanValue(Values[id] != 0);
                break;
            case JsonNodeType.Null:
                if (propertyName != null)
                    writer.WriteNull(propertyName);
                else
                    writer.WriteNullValue();
                break;
            default:
                throw new UnreachableException();
        }
    }

    #endregion

    #region Mutation - Graft and Save

    /// <summary>
    /// Selects how a grafted source is spliced into the destination tree.
    /// The mode is normally chosen automatically by <see cref="Graft"/>; the
    /// explicit overload exists for callers that need to override.
    /// </summary>
    public enum GraftMode
    {
        /// <summary>Append the source as a new last child of the anchor branch.</summary>
        AppendChild,

        /// <summary>Insert the source as the next sibling after the anchor. Used for JSONL top-level entries.</summary>
        InsertAfterSibling,
    }

    /// <summary>
    /// Returns true when <paramref name="anchorId"/> is a valid drop target for
    /// <see cref="Graft"/>. Used by the UI to enable/disable the Union menu.
    /// </summary>
    public bool CanGraftInto(int anchorId)
    {
        if (anchorId < 0 || anchorId >= Count)
            return false;
        var mode = ChooseGraftMode(anchorId);
        return mode == GraftMode.InsertAfterSibling || ((JsonNodeType)Types[anchorId]).IsBranch();
    }

    /// <summary>
    /// Copies <paramref name="other"/>'s top-level value(s) into this document
    /// at <paramref name="anchorId"/>. Mode is chosen automatically: JSONL
    /// top-level anchors get InsertAfterSibling, everything else AppendChild.
    /// Returns the id of the first inserted node.
    /// </summary>
    public int Graft(JsonTreeDocument other, int anchorId) =>
        GraftWithMode(other, ChooseGraftMode(anchorId), anchorId);

    GraftMode ChooseGraftMode(int anchorId)
    {
        if (anchorId > 0 && IsJsonl && Parents[anchorId] == RootId)
            return GraftMode.InsertAfterSibling;
        return GraftMode.AppendChild;
    }

    int GraftWithMode(JsonTreeDocument other, GraftMode mode, int anchorId)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Count == 0)
            throw new InvalidOperationException(Localization.T("Error.GraftSourceEmpty"));
        if (anchorId < 0 || anchorId >= Count)
            throw new ArgumentOutOfRangeException(nameof(anchorId));

        // Enumerate the source's top-level values to clone. For JSONL the
        // synthetic root's children are the values; for JSON the root itself
        // is the single value.
        var sources = CollectGraftSources(other);
        if (sources.Count == 0)
            throw new InvalidOperationException(Localization.T("Error.GraftSourceEmpty"));

        int firstInserted = mode switch
        {
            GraftMode.AppendChild => GraftAsChildren(other, sources, anchorId),
            GraftMode.InsertAfterSibling => GraftAsSiblings(other, sources, anchorId),
            _ => throw new UnreachableException(),
        };

        _isModified = true;
        // Graft is a coarse-grained restructure; clearing the per-edit undo
        // stacks keeps Ctrl+Z's invariant simple - undo never reaches across
        // a graft boundary. Reload-from-disk is the way to revert a union.
        _undo.Clear();
        _redo.Clear();
        Publish();
        DocumentModified?.Invoke();
        return firstInserted;
    }

    static List<int> CollectGraftSources(JsonTreeDocument other)
    {
        var sources = new List<int>();
        if (other.IsJsonl)
        {
            int c = other.FirstChild[RootId];
            while (c != -1)
            {
                sources.Add(c);
                c = other.NextSibling[c];
            }
        }
        else
        {
            sources.Add(RootId);
        }
        return sources;
    }

    int GraftAsChildren(JsonTreeDocument other, List<int> sources, int anchorId)
    {
        var anchorType = (JsonNodeType)Types[anchorId];
        if (anchorType == JsonNodeType.Array)
        {
            // Walk to the existing tail once; append all sources via local tail pointer.
            int tail = -1;
            int idx = 0;
            for (int c = FirstChild[anchorId]; c != -1; c = NextSibling[c])
            {
                tail = c;
                idx++;
            }
            int firstInserted = -1;
            foreach (int src in sources)
            {
                int newId = CloneSubtree(other, src, anchorId, ArrayIndexKey(idx++));
                if (tail == -1)
                    FirstChild[anchorId] = newId;
                else
                    NextSibling[tail] = newId;
                NextSibling[newId] = -1;
                tail = newId;
                if (firstInserted == -1)
                    firstInserted = newId;
            }
            return firstInserted;
        }

        if (anchorType == JsonNodeType.Object)
        {
            // Object target: only meaningful if the source is a single Object
            // whose keys can be merged into the target. JSONL sources or
            // primitive-rooted JSON would have no keys to merge with.
            if (sources.Count != 1 || (JsonNodeType)other.Types[sources[0]] != JsonNodeType.Object)
                throw new InvalidOperationException(Localization.T("Error.GraftObjectNeedsObject"));
            int otherRoot = sources[0];
            int firstInserted = -1;
            for (int oc = other.FirstChild[otherRoot]; oc != -1; oc = other.NextSibling[oc])
            {
                string key = other.Keys[oc] ?? string.Empty;
                int newId = CloneSubtree(other, oc, anchorId, key);
                MergeChildIntoObject(anchorId, newId);
                if (firstInserted == -1)
                    firstInserted = newId;
            }
            return firstInserted;
        }

        throw new InvalidOperationException(Localization.T("Error.GraftAppendBranchOnly"));
    }

    int GraftAsSiblings(JsonTreeDocument other, List<int> sources, int anchorId)
    {
        if (Parents[anchorId] != RootId)
            throw new InvalidOperationException(Localization.T("Error.GraftInsertTopLevelOnly"));

        int prev = anchorId;
        int firstInserted = -1;
        foreach (int src in sources)
        {
            int newId = CloneSubtree(other, src, RootId, key: string.Empty);
            int next = NextSibling[prev];
            NextSibling[prev] = newId;
            NextSibling[newId] = next;
            prev = newId;
            if (firstInserted == -1)
                firstInserted = newId;
        }

        // Renumber the synthetic-root array indices so keys stay [0]..[N-1].
        if (IsJsonl)
            ReindexTopLevel();
        return firstInserted;
    }

    int CloneSubtree(JsonTreeDocument other, int otherRoot, int parentId, string key)
    {
        int newRoot = CloneNode(other, otherRoot, key);
        Parents[newRoot] = parentId;

        // Iterative DFS clone: for each cloned branch, walk its source children,
        // clone each into self, wire parent + sibling chain, push branches to
        // continue. Recursion-free to survive deep trees.
        var work = new Stack<(int OtherId, int NewId)>();
        work.Push((otherRoot, newRoot));
        while (work.Count > 0)
        {
            var (otherId, newId) = work.Pop();
            if (!((JsonNodeType)other.Types[otherId]).IsBranch())
                continue;

            int prevNewChild = -1;
            for (int oc = other.FirstChild[otherId]; oc != -1; oc = other.NextSibling[oc])
            {
                string childKey = other.Keys[oc] ?? string.Empty;
                int nc = CloneNode(other, oc, childKey);
                Parents[nc] = newId;
                if (prevNewChild == -1)
                    FirstChild[newId] = nc;
                else
                    NextSibling[prevNewChild] = nc;
                prevNewChild = nc;
                work.Push((oc, nc));
            }
            if (prevNewChild != -1)
                NextSibling[prevNewChild] = -1;
        }
        return newRoot;
    }

    int CloneNode(JsonTreeDocument other, int otherId, string key)
    {
        var type = (JsonNodeType)other.Types[otherId];
        return type switch
        {
            JsonNodeType.Object => AddObject(key),
            JsonNodeType.Array => AddArray(key),
            JsonNodeType.String => AddStringRef(
                key,
                _strings.AppendString(other.GetString(otherId))
            ),
            JsonNodeType.Number => AddNumber(key, other.GetNumber(otherId)),
            JsonNodeType.Boolean => AddBool(key, other.GetBool(otherId)),
            JsonNodeType.Null => AddNull(key),
            _ => throw new UnreachableException(),
        };
    }

    // Splice <paramref name="newChildId"/> into <paramref name="parentId"/>'s
    // alphabetically-ordered child chain. If a child with the same key already
    // exists, the new node replaces it; the old subtree's nodes remain in the
    // arrays but become unreachable (saves write only the reachable tree).
    void MergeChildIntoObject(int parentId, int newChildId)
    {
        string newKey = Keys[newChildId] ?? string.Empty;
        int prev = -1;
        int cur = FirstChild[parentId];
        while (cur != -1)
        {
            int cmp = string.CompareOrdinal(Keys[cur], newKey);
            if (cmp == 0)
            {
                NextSibling[newChildId] = NextSibling[cur];
                if (prev == -1)
                    FirstChild[parentId] = newChildId;
                else
                    NextSibling[prev] = newChildId;
                return;
            }
            if (cmp > 0)
                break;
            prev = cur;
            cur = NextSibling[cur];
        }
        NextSibling[newChildId] = cur;
        if (prev == -1)
            FirstChild[parentId] = newChildId;
        else
            NextSibling[prev] = newChildId;
    }

    void ReindexTopLevel()
    {
        int idx = 0;
        for (int c = FirstChild[RootId]; c != -1; c = NextSibling[c])
            Keys[c] = ArrayIndexKey(idx++);
    }

    /// <summary>
    /// Writes the document to <paramref name="path"/> using the original
    /// format - JSONL when <see cref="IsJsonl"/>, single-value JSON otherwise.
    /// Streams via <see cref="Utf8JsonWriter"/>; never materializes a full byte[].
    /// Clears <see cref="IsModified"/> on success.
    /// </summary>
    public Task SaveAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => SaveSync(path, ct), ct);

    void SaveSync(string path, CancellationToken ct)
    {
        const int FileBufSize = 1 << 16;
        using var fs = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileBufSize
        );
        var writerOptions = new JsonWriterOptions { Indented = false };
        using var writer = new Utf8JsonWriter(fs, writerOptions);

        if (IsJsonl)
        {
            // One JSON value per line. Utf8JsonWriter.Reset() lets us reuse
            // the same writer for multiple top-level values without
            // allocating fresh writers per line.
            bool first = true;
            for (int c = FirstChild[RootId]; c != -1; c = NextSibling[c])
            {
                ct.ThrowIfCancellationRequested();
                if (!first)
                {
                    writer.Flush();
                    fs.WriteByte((byte)'\n');
                    writer.Reset();
                }
                WriteNode(writer, c, propertyName: null);
                first = false;
            }
            writer.Flush();
            if (!first)
                fs.WriteByte((byte)'\n');
        }
        else
        {
            WriteNode(writer, RootId, propertyName: null);
        }

        _isModified = false;
        _undo.Clear();
        _redo.Clear();
        _modifiedIds.Clear();
        DocumentModified?.Invoke();
    }

    /// <summary>
    /// Serializes multiple node subtrees into a single JSON array. Used by
    /// the multi-selection export path; matches the single-selection
    /// <see cref="Extract"/> wrapping when called with one id-but-as-array.
    /// </summary>
    public byte[] ExtractMany(IReadOnlyList<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            throw new InvalidOperationException(Localization.T("Error.NoSelection"));
        for (int i = 0; i < ids.Count; i++)
        {
            int id = ids[i];
            if (id < 0 || id >= Count)
                throw new ArgumentOutOfRangeException(nameof(ids));
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartArray();
            for (int i = 0; i < ids.Count; i++)
                WriteNode(writer, ids[i], propertyName: null);
            writer.WriteEndArray();
        }
        return ms.ToArray();
    }

    #endregion

    #region Mutation - Edits and Undo

    enum EditKind : byte
    {
        SetValue,
        AddNode,
        DeleteNode,
    }

    // Single struct for both ops; the Kind discriminates field usage.
    // SetValue: Id is the edited node; ValueBefore is the old Values[id].
    // AddNode:  Id is the new node; ParentId/PrevSiblingId locate it in the chain so undo can unlink.
    // DeleteNode: Id is the unlinked node; ParentId/PrevSiblingId record the
    //             chain position so undo can relink.
    readonly struct EditOp
    {
        public EditKind Kind { get; init; }
        public int Id { get; init; }
        public int ParentId { get; init; }
        public int PrevSiblingId { get; init; }
        public long ValueBefore { get; init; }
    }

    /// <summary>True when <paramref name="id"/> is a leaf that can have its value edited via <see cref="SetString"/>/<see cref="SetNumber"/>/<see cref="SetBool"/>.</summary>
    public bool CanEditValue(int id)
    {
        if (id < 0 || id >= Count)
            return false;
        var t = (JsonNodeType)Types[id];
        return t == JsonNodeType.String
            || t == JsonNodeType.Number
            || t == JsonNodeType.Boolean
            || t == JsonNodeType.Null;
    }

    /// <summary>True when a child can be appended under <paramref name="parentId"/>.</summary>
    public bool CanAddChild(int parentId)
    {
        if (parentId < 0 || parentId >= Count)
            return false;
        return ((JsonNodeType)Types[parentId]).IsBranch();
    }

    /// <summary>True when <paramref name="id"/> can be deleted (anything except the document root).</summary>
    public bool CanDelete(int id) => id > 0 && id < Count && Parents[id] >= 0;

    public void SetString(int id, string value)
    {
        RequireType(id, JsonNodeType.String);
        long oldRaw = Values[id];
        long newRaw = _strings.AppendString(value);
        Values[id] = newRaw;
        RecordEdit(
            new EditOp
            {
                Kind = EditKind.SetValue,
                Id = id,
                ValueBefore = oldRaw,
            }
        );
    }

    public void SetNumber(int id, double value)
    {
        RequireType(id, JsonNodeType.Number);
        long oldRaw = Values[id];
        Values[id] = BitConverter.DoubleToInt64Bits(value);
        RecordEdit(
            new EditOp
            {
                Kind = EditKind.SetValue,
                Id = id,
                ValueBefore = oldRaw,
            }
        );
    }

    public void SetBool(int id, bool value)
    {
        RequireType(id, JsonNodeType.Boolean);
        long oldRaw = Values[id];
        Values[id] = value ? 1L : 0L;
        RecordEdit(
            new EditOp
            {
                Kind = EditKind.SetValue,
                Id = id,
                ValueBefore = oldRaw,
            }
        );
    }

    /// <summary>
    /// Appends a new child to <paramref name="parentId"/>. For arrays
    /// <paramref name="key"/> is ignored - the next index is assigned. For
    /// primitive types, <paramref name="rawText"/> is parsed according to
    /// <paramref name="type"/>; for Object/Array, it is ignored.
    /// Returns the new node's id.
    /// </summary>
    public int AddChild(int parentId, string? key, JsonNodeType type, string rawText)
    {
        if (!CanAddChild(parentId))
            throw new InvalidOperationException(Localization.T("Error.EditAddBranchOnly"));
        var parentType = (JsonNodeType)Types[parentId];
        string effectiveKey =
            parentType == JsonNodeType.Array
                ? ArrayIndexKey(CountChildren(parentId))
                : (key ?? throw new ArgumentNullException(nameof(key)));

        int newId = type switch
        {
            JsonNodeType.Object => AddObject(effectiveKey),
            JsonNodeType.Array => AddArray(effectiveKey),
            JsonNodeType.String => AddStringRef(effectiveKey, _strings.AppendString(rawText)),
            JsonNodeType.Number => AddNumber(effectiveKey, ParseNumber(rawText)),
            JsonNodeType.Boolean => AddBool(effectiveKey, ParseBool(rawText)),
            JsonNodeType.Null => AddNull(effectiveKey),
            _ => throw new InvalidOperationException(Localization.T("Error.EditAddType")),
        };

        int prev = LinkChildIntoParent(parentId, newId, parentType);
        RecordEdit(
            new EditOp
            {
                Kind = EditKind.AddNode,
                Id = newId,
                ParentId = parentId,
                PrevSiblingId = prev,
            }
        );
        Publish();
        return newId;
    }

    /// <summary>Unlinks <paramref name="id"/> from its parent. The node's data remains in the arrays so undo can relink in O(1).</summary>
    public void DeleteNode(int id)
    {
        if (!CanDelete(id))
            throw new InvalidOperationException(Localization.T("Error.EditDeleteInvalid"));
        int parent = Parents[id];
        int prev = UnlinkFromParent(parent, id);
        var parentType = (JsonNodeType)Types[parent];
        if (parentType == JsonNodeType.Array || (IsJsonl && parent == RootId))
            ReindexArrayChildren(parent);
        RecordEdit(
            new EditOp
            {
                Kind = EditKind.DeleteNode,
                Id = id,
                ParentId = parent,
                PrevSiblingId = prev,
            }
        );
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        var op = _undo.Pop();
        var inverse = ApplyInverse(op);
        _redo.Push(inverse);
        FinishEdit(op.Id, op.ParentId);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;
        var op = _redo.Pop();
        var inverse = ApplyInverse(op);
        _undo.Push(inverse);
        FinishEdit(op.Id, op.ParentId);
    }

    EditOp ApplyInverse(EditOp op)
    {
        switch (op.Kind)
        {
            case EditKind.SetValue:
            {
                long current = Values[op.Id];
                Values[op.Id] = op.ValueBefore;
                return new EditOp
                {
                    Kind = EditKind.SetValue,
                    Id = op.Id,
                    ValueBefore = current,
                };
            }
            case EditKind.AddNode:
            {
                // Inverse of Add is Delete: unlink from the chain.
                int parent = op.ParentId;
                int prev = UnlinkFromParent(parent, op.Id);
                if (
                    (JsonNodeType)Types[parent] == JsonNodeType.Array
                    || (IsJsonl && parent == RootId)
                )
                    ReindexArrayChildren(parent);
                return new EditOp
                {
                    Kind = EditKind.DeleteNode,
                    Id = op.Id,
                    ParentId = parent,
                    PrevSiblingId = prev,
                };
            }
            case EditKind.DeleteNode:
            {
                // Inverse of Delete is Add: relink into the chain at the recorded position.
                int parent = op.ParentId;
                RelinkChild(parent, op.Id, op.PrevSiblingId);
                if (
                    (JsonNodeType)Types[parent] == JsonNodeType.Array
                    || (IsJsonl && parent == RootId)
                )
                    ReindexArrayChildren(parent);
                return new EditOp
                {
                    Kind = EditKind.AddNode,
                    Id = op.Id,
                    ParentId = parent,
                    PrevSiblingId = op.PrevSiblingId,
                };
            }
            default:
                throw new UnreachableException();
        }
    }

    void RecordEdit(EditOp op)
    {
        _undo.Push(op);
        _redo.Clear();
        FinishEdit(op.Id, op.ParentId);
    }

    void FinishEdit(int affectedId, int parentId)
    {
        if (affectedId >= 0)
            _modifiedIds.Add(affectedId);
        // Touching a child changes the parent's "shape" too - mark for the
        // tree row indicator so users can see at a glance.
        if (parentId > 0 && parentId < Count)
            _modifiedIds.Add(parentId);
        _isModified = _undo.Count > 0;
        DocumentModified?.Invoke();
    }

    void RequireType(int id, JsonNodeType expected)
    {
        if (id < 0 || id >= Count)
            throw new ArgumentOutOfRangeException(nameof(id));
        if ((JsonNodeType)Types[id] != expected)
            throw new InvalidOperationException(
                Localization.F("Error.EditWrongType", expected.ToString())
            );
    }

    static double ParseNumber(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    static bool ParseBool(string text)
    {
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        throw new FormatException(Localization.T("Error.EditBoolFormat"));
    }

    int CountChildren(int id)
    {
        int n = 0;
        for (int c = FirstChild[id]; c != -1; c = NextSibling[c])
            n++;
        return n;
    }

    // Appends the new child to parent's chain. For Object parents, the new
    // key is alphabetically spliced (matches the parse-time invariant).
    // Returns the prev-sibling id (or -1 if attached at head).
    int LinkChildIntoParent(int parentId, int newId, JsonNodeType parentType)
    {
        Parents[newId] = parentId;
        NextSibling[newId] = -1;
        if (parentType == JsonNodeType.Object)
        {
            string newKey = Keys[newId] ?? string.Empty;
            int prev = -1;
            int cur = FirstChild[parentId];
            while (cur != -1)
            {
                int cmp = string.CompareOrdinal(Keys[cur], newKey);
                if (cmp > 0)
                    break;
                prev = cur;
                cur = NextSibling[cur];
            }
            NextSibling[newId] = cur;
            if (prev == -1)
                FirstChild[parentId] = newId;
            else
                NextSibling[prev] = newId;
            return prev;
        }
        // Array (or synthetic JSONL root): append to tail.
        int tail = -1;
        for (int c = FirstChild[parentId]; c != -1; c = NextSibling[c])
            tail = c;
        if (tail == -1)
            FirstChild[parentId] = newId;
        else
            NextSibling[tail] = newId;
        return tail;
    }

    // Unlinks <paramref name="id"/> from <paramref name="parentId"/>'s chain.
    // Returns the prev-sibling id (or -1 if was at head) so caller can record
    // an undo op that puts it back in the same spot.
    int UnlinkFromParent(int parentId, int id)
    {
        int prev = -1;
        int cur = FirstChild[parentId];
        while (cur != -1 && cur != id)
        {
            prev = cur;
            cur = NextSibling[cur];
        }
        if (cur != id)
            throw new InvalidOperationException(Localization.T("Error.EditChainCorrupt"));
        if (prev == -1)
            FirstChild[parentId] = NextSibling[id];
        else
            NextSibling[prev] = NextSibling[id];
        return prev;
    }

    // Re-attaches <paramref name="id"/> right after <paramref name="prevSiblingId"/>
    // (or at head if -1). Restores the subtree's internal link state which is
    // still intact in the arrays - only the parent-side link was severed.
    void RelinkChild(int parentId, int id, int prevSiblingId)
    {
        if (prevSiblingId == -1)
        {
            NextSibling[id] = FirstChild[parentId];
            FirstChild[parentId] = id;
        }
        else
        {
            NextSibling[id] = NextSibling[prevSiblingId];
            NextSibling[prevSiblingId] = id;
        }
        Parents[id] = parentId;
    }

    void ReindexArrayChildren(int parentId)
    {
        int idx = 0;
        for (int c = FirstChild[parentId]; c != -1; c = NextSibling[c])
            Keys[c] = ArrayIndexKey(idx++);
    }

    #endregion

    #region Display Formatting

    /// <summary>
    /// Returns a display string for the value of the node (single line). String values are NOT truncated here
    /// callers that render in finite space (tree rows, detail panel) are responsible for capping.
    /// </summary>
    public string DisplayValue(int id)
    {
        var type = (JsonNodeType)Types[id];
        return type switch
        {
            JsonNodeType.Object => FirstChild[id] == -1 ? "{}" : "{...}",
            JsonNodeType.Array => FirstChild[id] == -1 ? "[]" : "[...]",
            JsonNodeType.String => "\"" + GetString(id) + "\"",
            JsonNodeType.Number => FormatNumber(BitConverter.Int64BitsToDouble(Values[id])),
            JsonNodeType.Boolean => Values[id] != 0 ? "true" : "false",
            JsonNodeType.Null => "null",
            _ => string.Empty,
        };
    }

    /// <summary>Display value capped at <paramref name="maxStringChars"/> for safe rendering in a fixed-size row.</summary>
    public string DisplayValueCapped(int id, int maxStringChars)
    {
        if ((JsonNodeType)Types[id] != JsonNodeType.String)
            return DisplayValue(id);
        string s = _strings.GetCapped(Values[id], maxStringChars, out bool truncated);
        return truncated ? "\"" + s + "…\"" : "\"" + s + "\"";
    }

    /// <summary>Raw value text for clipboard copy.</summary>
    public string RawValue(int id)
    {
        var type = (JsonNodeType)Types[id];
        return type switch
        {
            JsonNodeType.String => GetString(id),
            JsonNodeType.Number => FormatNumber(BitConverter.Int64BitsToDouble(Values[id])),
            JsonNodeType.Boolean => Values[id] != 0 ? "true" : "false",
            JsonNodeType.Null => "null",
            _ => string.Empty,
        };
    }

    #endregion

    #region Parser Helpers

    delegate int ChunkConsumer(ReadOnlySpan<byte> data, bool isFinal);
    delegate void AfterChunkHook(long posInFile);

    // Shared by every count path so the token-to-counter mapping cannot drift
    // between in-memory and streaming variants.
    static void AccumulateTokenCount(JsonTokenType tt, ref int count, ref int stringCount)
    {
        switch (tt)
        {
            case JsonTokenType.String:
                count++;
                stringCount++;
                break;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                count++;
                break;
        }
    }

    // Pull one JSONL line out of <paramref name="data"/> starting at <paramref name="pos"/>.
    // Trims a trailing \r and ASCII whitespace on both sides. Returns false when the
    // remaining bytes contain no '\n' and <paramref name="isFinal"/> is false (caller
    // must wait for more data); when isFinal is true the no-newline tail is yielded
    // as the final line. Empty/whitespace lines are yielded too so byte-position
    // accounting stays aligned with the file layout - callers filter via IsEmpty.
    static bool TryReadLine(
        ReadOnlySpan<byte> data,
        ref int pos,
        bool isFinal,
        out ReadOnlySpan<byte> line
    )
    {
        if (pos >= data.Length)
        {
            line = default;
            return false;
        }

        int rel = data[pos..].IndexOf((byte)'\n');
        if (rel < 0)
        {
            if (!isFinal)
            {
                line = default;
                return false;
            }
            line = TrimAsciiWs(data[pos..]);
            pos = data.Length;
            return true;
        }

        int lineEnd = pos + rel;
        int adjEnd = lineEnd;
        if (adjEnd > pos && data[adjEnd - 1] == (byte)'\r')
            adjEnd--;
        line = TrimAsciiWs(data[pos..adjEnd]);
        pos = lineEnd + 1;
        return true;
    }

    // Chunked-stream pump used by every streaming load path. Owns a pooled
    // grow-on-stall buffer, drives the read/consume/shift loop, and invokes
    // <paramref name="afterChunk"/> (if provided) with the file offset of the
    // unconsumed tail after each shift - that lets callers do throttled
    // progress reporting or progressive publishing without re-implementing
    // the buffer plumbing. The buffer is capped at <see cref="MaxStreamChunkBuffer"/>;
    // <paramref name="tooLongErrorKey"/> selects the localized error message
    // raised when a single token/line refuses to fit.
    static async Task PumpStreamAsync(
        Stream stream,
        ChunkConsumer consume,
        AfterChunkHook? afterChunk,
        string tooLongErrorKey,
        CancellationToken ct
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        int bytesInBuffer = 0;
        bool reachedEnd = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!reachedEnd)
                {
                    int read = await stream
                        .ReadAsync(
                            buffer.AsMemory(bytesInBuffer, buffer.Length - bytesInBuffer),
                            ct
                        )
                        .ConfigureAwait(false);
                    bytesInBuffer += read;
                    if (read == 0)
                        reachedEnd = true;
                }

                int consumed = consume(buffer.AsSpan(0, bytesInBuffer), reachedEnd);

                if (reachedEnd && consumed == bytesInBuffer)
                    break;

                if (consumed > 0 && consumed < bytesInBuffer)
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, bytesInBuffer - consumed);
                bytesInBuffer -= consumed;

                afterChunk?.Invoke(stream.Position - bytesInBuffer);

                if (consumed == 0 && !reachedEnd && bytesInBuffer == buffer.Length)
                {
                    if (buffer.Length >= MaxStreamChunkBuffer)
                        throw new InvalidDataException(
                            Localization.F(tooLongErrorKey, MaxStreamChunkBuffer / (1024 * 1024))
                        );
                    int newSize = (int)Math.Min((long)buffer.Length * 2, MaxStreamChunkBuffer);
                    var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
                    Buffer.BlockCopy(buffer, 0, newBuf, 0, bytesInBuffer);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = newBuf;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region Helpers

    // Default double.ToString(InvariantCulture) yields the shortest round-trippable representation
    // Used by both DisplayValue and Search so the user can search for the number they see.
    static string FormatNumber(double v) => v.ToString(CultureInfo.InvariantCulture);

    static FileStream OpenSequentialRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: (int)FileStreamBufferSize,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan
        );

    static async Task<byte[]> ReadAllBytesAsync(
        string path,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct
    )
    {
        using var fs = OpenSequentialRead(path);
        long len = fs.Length;
        if (len > int.MaxValue)
            throw new IOException(Localization.T("Error.FileTooLargeForMemory"));
        var buf = new byte[(int)len];
        int offset = 0;
        while (offset < buf.Length)
        {
            int read = await fs.ReadAsync(buf.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            offset += read;
            ReportByteProgress(progress, 1, offset, buf.Length);
        }
        ReportByteProgress(progress, 1, buf.Length, buf.Length);
        return buf;
    }

    static void ReportByteProgress(
        IProgress<ProgressInfo>? progress,
        int step,
        long processedBytes,
        long totalBytes
    )
    {
        if (progress == null || totalBytes <= 0)
            return;

        double ratio = Math.Clamp((double)processedBytes / totalBytes, 0, 1);
        progress.Report(new ProgressInfo(step, 3, 0, ratio));
    }

    static ReadOnlySpan<byte> TrimAsciiWs(ReadOnlySpan<byte> s)
    {
        int start = 0;
        while (start < s.Length && (s[start] == (byte)' ' || s[start] == (byte)'\t'))
            start++;
        int end = s.Length;
        while (end > start && (s[end - 1] == (byte)' ' || s[end - 1] == (byte)'\t'))
            end--;
        return s.Slice(start, end - start);
    }

    int AddObject(string key)
    {
        EnsureNodeCapacity(_count + 1);
        int id = _count++;
        Types[id] = (byte)JsonNodeType.Object;
        Keys[id] = key;
        return id;
    }

    int AddArray(string key)
    {
        EnsureNodeCapacity(_count + 1);
        int id = _count++;
        Types[id] = (byte)JsonNodeType.Array;
        Keys[id] = key;
        return id;
    }

    int AddStringRef(string key, long encoded)
    {
        EnsureNodeCapacity(_count + 1);
        int id = _count++;
        Types[id] = (byte)JsonNodeType.String;
        Keys[id] = key;
        Values[id] = encoded;
        return id;
    }

    int AddNumber(string key, double value)
    {
        EnsureNodeCapacity(_count + 1);
        int id = _count++;
        Types[id] = (byte)JsonNodeType.Number;
        Keys[id] = key;
        Values[id] = BitConverter.DoubleToInt64Bits(value);
        return id;
    }

    int AddBool(string key, bool value)
    {
        EnsureNodeCapacity(_count + 1);
        int id = _count++;
        Types[id] = (byte)JsonNodeType.Boolean;
        Keys[id] = key;
        Values[id] = value ? 1L : 0L;
        return id;
    }

    int AddNull(string key)
    {
        EnsureNodeCapacity(_count + 1);
        int id = _count++;
        Types[id] = (byte)JsonNodeType.Null;
        Keys[id] = key;
        return id;
    }

    #endregion

    #region Nested Types

    sealed class Frame
    {
        public int Id;
        public bool IsObject;
        public int ArrayIndex;
        public List<int> Children = new(4);

        // Streaming-root only: this frame lives for the whole streaming JSONL
        // load and we link its top-level children eagerly via Tail rather than
        // accumulating them in Children (which would grow to the line count).
        public bool IsStreamingRoot;
        public int Tail = -1;
    }

    sealed class BuildContext
    {
        public Stack<Frame> Stack = null!;
        public string? StashedKey;

        // Object property names dedupe through this dictionary: typical JSON
        // has 10-200 distinct keys regardless of element count, so the same
        // canonical string is reused across millions of nodes. Saves on the
        // permanent string heap, not on Keys[] slot count.
        public Dictionary<string, string> KeyInterner = new(StringComparer.Ordinal);
    }

    struct ParseStats
    {
        public int TotalCount;
        public int StringCount;
    }

    readonly struct KeyComparer(string?[] keys) : IComparer<int>
    {
        readonly string?[] _keys = keys;

        public int Compare(int x, int y) => string.CompareOrdinal(_keys[x], _keys[y]);
    }

    #endregion
}
