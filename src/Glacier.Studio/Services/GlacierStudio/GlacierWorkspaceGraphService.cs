using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Glacier.Inference.Hardware;
using Glacier.Rag.Chunking;
using Glacier.Rag.Embeddings;
using Glacier.Studio.Services;
using Glacier.Vector.Index;
using Glacier.Vector.Storage;

namespace Glacier.Showcase.Services.GlacierStudio;

public sealed class WorkspaceRagOptions
{
    public bool EnableNpuEmbeddings { get; init; } = true;
    public string? EmbeddingEndpoint { get; init; }
    public string? EmbeddingModelId { get; init; }
    public int? EmbeddingDimensions { get; init; }
    public int FallbackHashDimensions { get; init; } = 384;
    public string? EmbeddingApiKey { get; init; }
    public HttpClient? HttpClient { get; init; }
    public IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? NpuCandidates { get; init; }
    public IEmbeddingModel? EmbeddingModel { get; init; }
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan EmbeddingTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan? ProbeCacheTtl { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxChunkLength { get; init; } = 512;
    public int ChunkOverlap { get; init; } = 64;
    public int MaxWorkspaceFilesToIndex { get; init; } = 250;
    public int MaxChunksPerFile { get; init; } = 32;
}

public sealed record SemanticWorkspaceHit(
    string ChunkId,
    string RelativePath,
    int StartLine,
    int EndLine,
    float SimilarityScore,
    float HybridScore,
    string Preview);

/// <summary>
/// Stage 4 Super Agent Central Knowledge Graph &amp; Semantic Workspace RAG Service.
/// Maintains an in-process zero-allocation Glacier.Graph.Storage.GraphStore of workspace projects,
/// dependency topology, files, C# AST types, interfaces, and methods, paired with isolated
/// NPU-backed OpenAiEmbeddingModel and FastHashEmbeddingModel vector indices.
/// </summary>
public sealed class GlacierWorkspaceGraphService : IDisposable
{
    private sealed record WorkspaceChunkRecord(
        string ChunkId,
        string RelativePath,
        int StartLine,
        int EndLine,
        string Content,
        string ContentHash);

    private static readonly string[] s_allowedWorkspaceExtensions =
    [
        ".cs", ".razor", ".csproj", ".slnx", ".md", ".json", ".yml", ".yaml"
    ];

    private GraphStore _graph;
    private string _workspaceRoot;
    private readonly WorkspaceRagOptions _options;
    private readonly ConcurrentDictionary<string, DateTime> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _indexedFileHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WorkspaceChunkRecord> _chunksById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _fileChunkIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deepIndexedProjects = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _BootstrapLock = new();
    private readonly object _indexLock = new();
    private readonly SemaphoreSlim _semanticGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private readonly FastHashEmbeddingModel _hashModel;
    private InMemoryVectorStorage _hashStorage;
    private VectorIndex _hashIndex;
    private readonly HashSet<string> _hashIndexedKeys = new(StringComparer.Ordinal);

    private OpenAiEmbeddingModel? _activeOpenAiModel;
    private bool _ownsOpenAiModel;
    private InMemoryVectorStorage? _openAiStorage;
    private VectorIndex? _openAiIndex;
    private string? _openAiSpaceKey;
    private readonly HashSet<string> _openAiIndexedKeys = new(StringComparer.Ordinal);

    private IEmbeddingModel? _customModel;
    private InMemoryVectorStorage? _customStorage;
    private VectorIndex? _customIndex;
    private readonly HashSet<string> _customIndexedKeys = new(StringComparer.Ordinal);

    private bool _isBootstrapped;
    private bool _hasAttemptedAutoInit;
    private bool _npuDemoted;
    private long _demotedAtTimestamp;
    private long _workspaceGeneration;
    private int _disposed;
    private int _activeOps = 1; // initial ref owned by instance until Dispose()

    private static readonly Regex ProjectRefRegex = new(
        @"<ProjectReference\s+Include=""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GraphStore Store => _graph;
    public int NodeCount => _graph.NodeCount;
    public int EdgeCount => _graph.EdgeCount;
    public string WorkspaceRoot => _workspaceRoot;

    public bool IsUsingNpuEmbeddings => Volatile.Read(ref _activeOpenAiModel) != null && !Volatile.Read(ref _npuDemoted);
    public string? ActiveEmbeddingEndpoint => IsUsingNpuEmbeddings ? Volatile.Read(ref _activeOpenAiModel)?.BaseUrl : null;
    public string? ActiveEmbeddingModelId => IsUsingNpuEmbeddings ? Volatile.Read(ref _activeOpenAiModel)?.ModelId : null;
    public int ActiveEmbeddingDimensions
    {
        get
        {
            var oai = Volatile.Read(ref _activeOpenAiModel);
            if (oai != null && !Volatile.Read(ref _npuDemoted))
                return oai.Dimensions;
            if (_customModel != null)
                return _customModel.Dimensions;
            return _hashModel.Dimensions;
        }
    }
    public int SemanticChunkCount => _chunksById.Count;
    public string EmbeddingModeLabel
    {
        get
        {
            var oai = Volatile.Read(ref _activeOpenAiModel);
            if (oai != null && !Volatile.Read(ref _npuDemoted))
                return $"NPU · {oai.ModelId} ({oai.Dimensions}d)";
            if (_customModel != null)
                return $"Custom ({_customModel.Dimensions}d)";
            return $"Hash Fallback ({_hashModel.Dimensions}d)";
        }
    }

    public GlacierWorkspaceGraphService(string? workspaceRoot = null)
        : this(workspaceRoot, new WorkspaceRagOptions())
    {
    }

    public GlacierWorkspaceGraphService(string? workspaceRoot, WorkspaceRagOptions ragOptions)
    {
        ArgumentNullException.ThrowIfNull(ragOptions);
        if (ragOptions.FallbackHashDimensions <= 0 || ragOptions.FallbackHashDimensions % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(ragOptions), "FallbackHashDimensions must be a positive multiple of 8.");
        if (ragOptions.EmbeddingDimensions is <= 0)
            throw new ArgumentOutOfRangeException(nameof(ragOptions), "EmbeddingDimensions must be positive when specified.");
        if (ragOptions.ProbeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ragOptions), "ProbeTimeout must be positive.");
        if (ragOptions.EmbeddingTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ragOptions), "EmbeddingTimeout must be positive.");
        if (ragOptions.MaxChunkLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(ragOptions), "MaxChunkLength must be positive.");
        if (ragOptions.ChunkOverlap < 0 || ragOptions.ChunkOverlap >= ragOptions.MaxChunkLength)
            throw new ArgumentOutOfRangeException(nameof(ragOptions), "ChunkOverlap must be >= 0 and < MaxChunkLength.");

        _options = ragOptions;
        _workspaceRoot = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? Path.GetFullPath(workspaceRoot)
            : GlacierWorkspaceResolver.ResolveWorkspaceRoot();

        _graph = new GraphStore(initialNodeCapacity: 32_768, initialEdgeCapacity: 131_072);
        _hashModel = new FastHashEmbeddingModel(_options.FallbackHashDimensions);
        _hashStorage = new InMemoryVectorStorage(_hashModel.Dimensions);
        _hashIndex = new VectorIndex(_hashStorage);

        if (_options.EmbeddingModel is OpenAiEmbeddingModel injectedOpenAi)
        {
            _activeOpenAiModel = injectedOpenAi;
            _ownsOpenAiModel = false;
            _openAiSpaceKey = BuildSpaceKey(injectedOpenAi);
            _openAiStorage = new InMemoryVectorStorage(injectedOpenAi.Dimensions);
            _openAiIndex = new VectorIndex(_openAiStorage);
        }
        else if (_options.EmbeddingModel != null)
        {
            _customModel = _options.EmbeddingModel;
            _customStorage = new InMemoryVectorStorage(_customModel.Dimensions);
            _customIndex = new VectorIndex(_customStorage);
        }

        EnsureBootstrapped();
    }

    public void SetWorkspaceRoot(string newWorkspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(newWorkspaceRoot) || !Directory.Exists(newWorkspaceRoot))
            return;

        string normalized = Path.GetFullPath(newWorkspaceRoot);
        if (string.Equals(_workspaceRoot, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_BootstrapLock)
        {
            _workspaceRoot = normalized;
            _indexedFiles.Clear();
            _indexedFileHashes.Clear();
            _deepIndexedProjects.Clear();
            _chunksById.Clear();
            _fileChunkIds.Clear();
            _graph = new GraphStore(initialNodeCapacity: 32_768, initialEdgeCapacity: 131_072);
            _isBootstrapped = false;

            lock (_indexLock)
            {
                Interlocked.Increment(ref _workspaceGeneration);
                _hashIndexedKeys.Clear();
                _hashStorage = new InMemoryVectorStorage(_hashModel.Dimensions);
                _hashIndex = new VectorIndex(_hashStorage);

                _openAiIndexedKeys.Clear();
                if (_activeOpenAiModel != null)
                {
                    _openAiStorage = new InMemoryVectorStorage(_activeOpenAiModel.Dimensions);
                    _openAiIndex = new VectorIndex(_openAiStorage);
                }

                _customIndexedKeys.Clear();
                if (_customModel != null)
                {
                    _customStorage = new InMemoryVectorStorage(_customModel.Dimensions);
                    _customIndex = new VectorIndex(_customStorage);
                }
            }
        }
        EnsureBootstrapped();
    }

    public void EnsureBootstrapped()
    {
        if (_isBootstrapped) return;
        lock (_BootstrapLock)
        {
            if (_isBootstrapped) return;
            try
            {
                BootstrapWorkspaceTopology();
            }
            catch
            {
                // Ignore non-fatal I/O errors during initial workspace topology scan
            }
            _isBootstrapped = true;
        }
    }

    public async Task<bool> InitializeEmbeddingsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        EnterActiveOp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_options.EmbeddingModel != null)
            {
                return _options.EmbeddingModel is OpenAiEmbeddingModel;
            }

            if (!_options.EnableNpuEmbeddings)
            {
                Volatile.Write(ref _npuDemoted, true);
                return false;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            try
            {
                await _semanticGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0 && !cancellationToken.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(GlacierWorkspaceGraphService));
            }

            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                if (!forceRefresh && _hasAttemptedAutoInit)
                {
                    if (Volatile.Read(ref _activeOpenAiModel) != null && !Volatile.Read(ref _npuDemoted))
                    {
                        return true;
                    }
                }

                _hasAttemptedAutoInit = true;
                long generation = Interlocked.Read(ref _workspaceGeneration);

                try
                {
                    string? targetBaseUrl = null;
                    IReadOnlyList<string> discoveredModels = Array.Empty<string>();

                    if (!string.IsNullOrWhiteSpace(_options.EmbeddingEndpoint) &&
                        !string.Equals(_options.EmbeddingEndpoint.Trim(), "auto-npu", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(_options.EmbeddingEndpoint.Trim(), "npu", StringComparison.OrdinalIgnoreCase))
                    {
                        string explicitUrl = _options.EmbeddingEndpoint.Trim();
                        if (!Uri.TryCreate(explicitUrl, UriKind.Absolute, out var uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            throw new UriFormatException($"Invalid embedding endpoint URL: '{explicitUrl}'.");
                        }

                        targetBaseUrl = explicitUrl;
                        if (string.IsNullOrWhiteSpace(_options.EmbeddingModelId))
                        {
                            using HttpClient? ownedClient = _options.HttpClient == null
                                ? new HttpClient { Timeout = _options.ProbeTimeout }
                                : null;
                            var probed = await NpuSidecarDiscovery.ProbeSingleAsync(
                                _options.HttpClient ?? ownedClient!,
                                explicitUrl,
                                NpuSidecarRuntime.CustomOpenAi,
                                _options.ProbeTimeout,
                                linkedCts.Token).ConfigureAwait(false);
                            if (probed != null)
                            {
                                targetBaseUrl = probed.BaseUrl;
                                discoveredModels = probed.Models;
                            }
                        }
                    }
                    else
                    {
                        bool useFreshProbe = forceRefresh || Volatile.Read(ref _npuDemoted);
                        NpuSidecarEndpoint? sidecar = useFreshProbe
                            ? await NpuSidecarDiscovery.ProbeAsync(
                                _options.HttpClient,
                                _options.NpuCandidates,
                                _options.ProbeTimeout,
                                linkedCts.Token).ConfigureAwait(false)
                            : await NpuSidecarDiscovery.ProbeCachedAsync(
                                _options.ProbeCacheTtl,
                                _options.HttpClient,
                                _options.NpuCandidates,
                                _options.ProbeTimeout,
                                linkedCts.Token).ConfigureAwait(false);

                        if (sidecar == null)
                        {
                            DemoteToHashLocked();
                            return false;
                        }

                        targetBaseUrl = sidecar.BaseUrl;
                        discoveredModels = sidecar.Models;
                    }

                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                    if (string.IsNullOrWhiteSpace(targetBaseUrl))
                    {
                        DemoteToHashLocked();
                        return false;
                    }

                    string modelId = SelectEmbeddingModelId(_options.EmbeddingModelId, discoveredModels);

                    var model = await OpenAiEmbeddingModel.CreateAutoAsync(
                        targetBaseUrl,
                        modelId,
                        _options.EmbeddingApiKey,
                        _options.HttpClient,
                        _options.ProbeTimeout,
                        _options.EmbeddingDimensions,
                        linkedCts.Token).ConfigureAwait(false);

                    string spaceKey = BuildSpaceKey(model);
                    bool spaceChanged;
                    lock (_indexLock)
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            model.Dispose();
                            throw new ObjectDisposedException(nameof(GlacierWorkspaceGraphService));
                        }

                        spaceChanged = !string.Equals(_openAiSpaceKey, spaceKey, StringComparison.Ordinal);
                        if (spaceChanged)
                        {
                            if (_ownsOpenAiModel)
                            {
                                _activeOpenAiModel?.Dispose();
                            }
                            _activeOpenAiModel = model;
                            _ownsOpenAiModel = true;
                            _openAiSpaceKey = spaceKey;
                            _openAiStorage = new InMemoryVectorStorage(model.Dimensions);
                            _openAiIndex = new VectorIndex(_openAiStorage);
                            _openAiIndexedKeys.Clear();
                        }
                        else
                        {
                            if (_ownsOpenAiModel)
                            {
                                _activeOpenAiModel?.Dispose();
                            }
                            _activeOpenAiModel = model;
                            _ownsOpenAiModel = true;
                        }
                        _npuDemoted = false;
                        _demotedAtTimestamp = 0;
                    }

                    if (spaceChanged && _chunksById.Count > 0 && Interlocked.Read(ref _workspaceGeneration) == generation)
                    {
                        await EmbedExistingChunksIntoOpenAiLockedAsync(model, linkedCts.Token).ConfigureAwait(false);
                    }

                    return true;
                }
                catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0 && !cancellationToken.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(GlacierWorkspaceGraphService));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsRecoverableEmbeddingException(ex))
                {
                    DemoteToHashLocked();
                    return false;
                }
            }
            finally
            {
                _semanticGate.Release();
            }
        }
        finally
        {
            ExitActiveOp();
        }
    }

    public async Task<int> IngestFileContentAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        IngestFileContentCore(filePath, content, out string relPath, out string contentHash);
        if (string.IsNullOrWhiteSpace(content))
            return 0;

        var records = UpsertChunksForFile(relPath, content, contentHash);
        if (records.Count == 0)
            return 0;

        IndexChunksIntoHash(records);

        if (_customModel != null)
        {
            IndexChunksIntoCustom(_customModel, records);
        }

        var openAi = Volatile.Read(ref _activeOpenAiModel);
        if (openAi != null && !Volatile.Read(ref _npuDemoted))
        {
            await TryEmbedChunksIntoOpenAiAsync(openAi, records, cancellationToken).ConfigureAwait(false);
        }

        return records.Count;
    }

    public async Task<int> IndexWorkspaceSemanticAsync(string? scopePath = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        EnsureBootstrapped();
        cancellationToken.ThrowIfCancellationRequested();

        if (_options.EnableNpuEmbeddings)
        {
            await InitializeEmbeddingsAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        }

        string rootDir = ResolveScopeDirectory(scopePath);
        if (!Directory.Exists(rootDir))
            return 0;

        int indexedFilesCount = 0;
        var openAi = Volatile.Read(ref _activeOpenAiModel);

        try
        {
            foreach (var file in EnumerateWorkspaceFiles(rootDir).Take(_options.MaxWorkspaceFilesToIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relPath = NormalizeRelativePath(file);
                string content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content)) continue;

                string contentHash = ComputeContentHash(content);
                bool alreadySameContent = _indexedFileHashes.TryGetValue(relPath, out var prevHash)
                    && string.Equals(prevHash, contentHash, StringComparison.Ordinal);

                if (!alreadySameContent)
                {
                    await IngestFileContentAsync(file, content, cancellationToken).ConfigureAwait(false);
                    indexedFilesCount++;
                }
                else if (openAi != null && !Volatile.Read(ref _npuDemoted) &&
                         _fileChunkIds.TryGetValue(relPath, out var chunkIds))
                {
                    var pendingRecords = new List<WorkspaceChunkRecord>();
                    lock (_indexLock)
                    {
                        foreach (var cid in chunkIds)
                        {
                            if (_chunksById.TryGetValue(cid, out var rec) &&
                                !_openAiIndexedKeys.Contains(BuildChunkVersionKey(rec)))
                            {
                                pendingRecords.Add(rec);
                            }
                        }
                    }

                    if (pendingRecords.Count > 0)
                    {
                        await TryEmbedChunksIntoOpenAiAsync(openAi, pendingRecords, cancellationToken).ConfigureAwait(false);
                        indexedFilesCount++;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Ignore non-fatal workspace enumeration I/O issues
        }

        return indexedFilesCount;
    }

    public async Task<IReadOnlyList<SemanticWorkspaceHit>> SearchSemanticAsync(
        string query,
        int topK = 8,
        string? scopePath = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        EnsureBootstrapped();
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return Array.Empty<SemanticWorkspaceHit>();

        cancellationToken.ThrowIfCancellationRequested();

        if (_options.EnableNpuEmbeddings &&
            ((!_hasAttemptedAutoInit && Volatile.Read(ref _activeOpenAiModel) == null) || ShouldRetryAfterDemotion()))
        {
            await InitializeEmbeddingsAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        }

        if (_chunksById.IsEmpty)
        {
            await IndexWorkspaceSemanticAsync(scopePath, cancellationToken).ConfigureAwait(false);
        }

        if (_chunksById.IsEmpty)
            return Array.Empty<SemanticWorkspaceHit>();

        string? normalizedScope = NormalizeScopePrefix(scopePath);
        int candidateLimit = Math.Max(topK * 4, 24);

        var openAi = Volatile.Read(ref _activeOpenAiModel);
        if (openAi != null && !Volatile.Read(ref _npuDemoted))
        {
            try
            {
                await EnsureAllChunksEmbeddedForOpenAiAsync(openAi, cancellationToken).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.EmbeddingTimeout);
                float[] queryVec = await openAi.GenerateEmbeddingAsync(query, timeoutCts.Token).ConfigureAwait(false);

                SearchResult[] rawHits;
                int openAiIndexCount = 0;
                lock (_indexLock)
                {
                    if (_openAiIndex != null && queryVec.Length == _openAiIndex.Dimensions)
                    {
                        openAiIndexCount = _openAiIndex.Count;
                        rawHits = _openAiIndex.Search(queryVec, candidateLimit);
                    }
                    else
                    {
                        rawHits = Array.Empty<SearchResult>();
                    }
                }

                if (rawHits.Length > 0)
                {
                    var ranked = RankSemanticHits(query, rawHits, topK, normalizedScope);
                    if (ranked.Count < topK && openAiIndexCount > candidateLimit)
                    {
                        SearchResult[] expandedHits;
                        lock (_indexLock)
                        {
                            expandedHits = _openAiIndex != null && queryVec.Length == _openAiIndex.Dimensions && _openAiIndex.Count > 0
                                ? _openAiIndex.Search(queryVec, _openAiIndex.Count)
                                : Array.Empty<SearchResult>();
                        }
                        if (expandedHits.Length > rawHits.Length)
                        {
                            ranked = RankSemanticHits(query, expandedHits, topK, normalizedScope);
                        }
                    }
                    return ranked;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableEmbeddingException(ex))
            {
                lock (_indexLock)
                {
                    _npuDemoted = true;
                    _demotedAtTimestamp = Stopwatch.GetTimestamp();
                }
            }
        }

        if (_customModel != null)
        {
            try
            {
                float[] customVec = new float[_customModel.Dimensions];
                _customModel.GenerateEmbedding(query.AsSpan(), customVec);
                SearchResult[] customHits;
                int customIndexCount = 0;
                lock (_indexLock)
                {
                    if (_customIndex != null && customVec.Length == _customIndex.Dimensions)
                    {
                        customIndexCount = _customIndex.Count;
                        customHits = _customIndex.Search(customVec, candidateLimit);
                    }
                    else
                    {
                        customHits = Array.Empty<SearchResult>();
                    }
                }
                if (customHits.Length > 0)
                {
                    var ranked = RankSemanticHits(query, customHits, topK, normalizedScope);
                    if (ranked.Count < topK && customIndexCount > candidateLimit)
                    {
                        SearchResult[] expandedHits;
                        lock (_indexLock)
                        {
                            expandedHits = _customIndex != null && customVec.Length == _customIndex.Dimensions && _customIndex.Count > 0
                                ? _customIndex.Search(customVec, _customIndex.Count)
                                : Array.Empty<SearchResult>();
                        }
                        if (expandedHits.Length > customHits.Length)
                        {
                            ranked = RankSemanticHits(query, expandedHits, topK, normalizedScope);
                        }
                    }
                    return ranked;
                }
            }
            catch (Exception ex) when (IsRecoverableEmbeddingException(ex))
            {
                // Fall back to FastHashEmbeddingModel
            }
        }

        float[] hashVec = new float[_hashModel.Dimensions];
        _hashModel.GenerateEmbedding(query.AsSpan(), hashVec);
        SearchResult[] hashHits;
        int hashIndexCount;
        lock (_indexLock)
        {
            hashIndexCount = _hashIndex.Count;
            hashHits = _hashIndex.Search(hashVec, candidateLimit);
        }

        var hashRanked = RankSemanticHits(query, hashHits, topK, normalizedScope);
        if (hashRanked.Count < topK && hashIndexCount > candidateLimit)
        {
            SearchResult[] expandedHashHits;
            lock (_indexLock)
            {
                expandedHashHits = _hashIndex.Count > 0
                    ? _hashIndex.Search(hashVec, _hashIndex.Count)
                    : Array.Empty<SearchResult>();
            }
            if (expandedHashHits.Length > hashHits.Length)
            {
                hashRanked = RankSemanticHits(query, expandedHashHits, topK, normalizedScope);
            }
        }
        return hashRanked;
    }

    public async Task<string> SearchWorkspaceAsync(
        string query,
        string? scopePath = null,
        int maxMatches = 25,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        EnsureBootstrapped();
        if (string.IsNullOrWhiteSpace(query)) return "Error: 'query' parameter is required.";
        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        string rootDir = ResolveScopeDirectory(scopePath);
        var lexicalResults = RunLexicalWorkspaceScan(query, rootDir, maxMatches, out var matchedFiles);

        // Ingest lexically matched files into semantic indices too
        foreach (var (filePath, content) in matchedFiles)
        {
            await IngestFileContentAsync(filePath, content, cancellationToken).ConfigureAwait(false);
        }

        var semanticHits = await SearchSemanticAsync(
            query,
            topK: Math.Clamp(maxMatches, 4, 8),
            scopePath: scopePath,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        sw.Stop();
        var combined = new List<string>(lexicalResults);
        var seenSemanticPaths = new HashSet<string>(
            matchedFiles.Select(m => NormalizeRelativePath(m.FilePath)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var hit in semanticHits)
        {
            if (combined.Count >= maxMatches + 8) break;
            if (seenSemanticPaths.Add($"{hit.RelativePath}:{hit.StartLine}-{hit.EndLine}"))
            {
                combined.Add($"[SEMANTIC MATCH (sim: {hit.SimilarityScore:F2})] {hit.RelativePath}:L{hit.StartLine}-{hit.EndLine}: {hit.Preview}");
            }
        }

        if (combined.Count == 0)
        {
            return $"No matches found for '{query}' in '{NormalizeRelativePath(rootDir)}' ({sw.Elapsed.TotalMilliseconds:F1}ms, {EmbeddingModeLabel}).\n" +
                   QueryNeighborhoodSummary(query, maxEdges: 10);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {combined.Count} match(es) for '{query}' in {sw.Elapsed.TotalMilliseconds:F1}ms [{EmbeddingModeLabel}]:");
        foreach (var r in combined)
        {
            sb.AppendLine($"- {r}");
        }
        return sb.ToString().Trim();
    }

    public async Task<string> QueryNeighborhoodSummaryAsync(
        string targetQuery,
        int maxEdges = 28,
        CancellationToken cancellationToken = default)
    {
        string summary = QueryNeighborhoodSummary(targetQuery, maxEdges);
        if (string.IsNullOrWhiteSpace(targetQuery)) return summary;

        var hits = await SearchSemanticAsync(targetQuery, topK: 4, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (hits.Count == 0) return summary;

        var sb = new StringBuilder(summary);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"### Semantic Workspace Hits [{EmbeddingModeLabel}]");
        foreach (var hit in hits)
        {
            sb.AppendLine($"- `{hit.RelativePath}:L{hit.StartLine}-{hit.EndLine}` (sim `{hit.SimilarityScore:F2}`, hybrid `{hit.HybridScore:F2}`): {hit.Preview}");
        }
        return sb.ToString().Trim();
    }

    private void BootstrapWorkspaceTopology()
    {
        if (!Directory.Exists(_workspaceRoot)) return;

        _graph.AddNode("Workspace", $"root:{_workspaceRoot}");

        foreach (var dir in Directory.GetDirectories(_workspaceRoot))
        {
            string dirName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(dirName) || dirName.StartsWith(".") ||
                dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("BenchmarkDotNet.Artifacts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _graph.AddNode(dirName, "kind:project_dir");
            _graph.AddEdge("Workspace", dirName, "HAS_PROJECT");

            foreach (var mdFile in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            {
                string relMd = NormalizeRelativePath(mdFile);
                _graph.AddNode(relMd, "kind:documentation");
                _graph.AddEdge(dirName, relMd, "HAS_DOCUMENTATION");
            }

            string srcSubDir = Path.Combine(dir, "src");
            var searchRoot = Directory.Exists(srcSubDir) ? srcSubDir : dir;
            try
            {
                foreach (var csproj in Directory.GetFiles(searchRoot, "*.csproj", SearchOption.AllDirectories))
                {
                    if (IsBuildArtifactPath(csproj))
                        continue;

                    string relCsproj = NormalizeRelativePath(csproj);
                    string projName = Path.GetFileNameWithoutExtension(csproj);
                    _graph.AddNode(projName, $"kind:assembly|manifest:{relCsproj}");
                    _graph.AddEdge(dirName, projName, "DEFINES_ASSEMBLY");
                    _graph.AddEdge(projName, relCsproj, "HAS_MANIFEST");

                    string xml = File.ReadAllText(csproj);
                    foreach (Match m in ProjectRefRegex.Matches(xml))
                    {
                        string refPath = m.Groups[1].Value;
                        string refProjName = Path.GetFileNameWithoutExtension(refPath.Replace('\\', '/'));
                        if (!string.IsNullOrEmpty(refProjName))
                        {
                            _graph.AddNode(refProjName, "kind:assembly");
                            _graph.AddEdge(projName, refProjName, "DEPENDS_ON");
                        }
                    }

                    string? csprojDir = Path.GetDirectoryName(csproj);
                    if (!string.IsNullOrEmpty(csprojDir) && Directory.Exists(csprojDir))
                    {
                        foreach (var csFile in Directory.GetFiles(csprojDir, "*.cs", SearchOption.AllDirectories).Take(80))
                        {
                            if (IsBuildArtifactPath(csFile))
                                continue;

                            string relCs = NormalizeRelativePath(csFile);
                            string fileStem = Path.GetFileNameWithoutExtension(csFile);
                            _graph.AddNode(relCs, $"kind:source_file|stem:{fileStem}");
                            _graph.AddEdge(projName, relCs, "CONTAINS_SOURCE");

                            if (!fileStem.Contains('.'))
                            {
                                _graph.AddNode(fileStem, $"kind:symbol|file:{relCs}");
                                _graph.AddEdge(fileStem, relCs, "DEFINED_IN");
                            }
                        }
                    }
                }
            }
            catch
            {
                // Continue scanning remaining directories
            }
        }
    }

    /// <summary>
    /// Ingests directory listing observations into the Knowledge Graph.
    /// </summary>
    public void IngestDirectory(string dirPath, IEnumerable<string> subdirectories, IEnumerable<string> files)
    {
        EnsureBootstrapped();
        string normDir = string.IsNullOrWhiteSpace(dirPath) ? "Workspace" : NormalizeRelativePath(dirPath);
        _graph.AddNode(normDir, "kind:directory");

        foreach (var sub in subdirectories)
        {
            string childDir = normDir == "Workspace" ? sub : $"{normDir}/{sub}";
            _graph.AddNode(childDir, "kind:directory");
            _graph.AddEdge(normDir, childDir, "CONTAINS_DIR");
        }

        foreach (var file in files)
        {
            string childFile = normDir == "Workspace" ? file : $"{normDir}/{file}";
            _graph.AddNode(childFile, "kind:file");
            _graph.AddEdge(normDir, childFile, "CONTAINS_FILE");
        }
    }

    /// <summary>
    /// Parses a source or markdown file and populates AST symbol nodes &amp; edges in the Knowledge Graph
    /// and synchronously updates the local hash fallback index.
    /// </summary>
    public List<string> IngestFileContent(string filePath, string content)
    {
        var discoveredSymbols = IngestFileContentCore(filePath, content, out string relPath, out string contentHash);
        if (string.IsNullOrWhiteSpace(content))
            return discoveredSymbols;

        var records = UpsertChunksForFile(relPath, content, contentHash);
        if (records.Count > 0)
        {
            IndexChunksIntoHash(records);
            if (_customModel != null)
            {
                IndexChunksIntoCustom(_customModel, records);
            }
        }

        return discoveredSymbols;
    }

    private List<string> IngestFileContentCore(string filePath, string content, out string relPath, out string contentHash)
    {
        EnsureBootstrapped();
        var discoveredSymbols = new List<string>();
        relPath = NormalizeRelativePath(filePath);
        contentHash = string.IsNullOrWhiteSpace(content) ? "" : ComputeContentHash(content);
        if (string.IsNullOrWhiteSpace(content)) return discoveredSymbols;

        _indexedFiles[relPath] = DateTime.UtcNow;
        _indexedFileHashes[relPath] = contentHash;

        int lineCount = 1;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n') lineCount++;
        }

        _graph.AddNode(relPath, $"kind:file|lines:{lineCount}|indexed:true");

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".cs" || ext == ".razor")
        {
            ExtractedCSharpSymbols symbols = CSharpSymbolExtractor.Extract(content.AsSpan());

            foreach (string ns in symbols.Namespaces)
            {
                _graph.AddNode(ns, "kind:namespace");
                _graph.AddEdge(relPath, ns, "IN_NAMESPACE");
            }

            foreach (var typeDecl in symbols.Types)
            {
                string meta = $"kind:{typeDecl.Kind}|file:{relPath}|line:{typeDecl.LineNumber}";
                if (typeDecl.MemberSummaries.Count > 0)
                {
                    meta += $"|members:{string.Join("; ", typeDecl.MemberSummaries.Take(10))}";
                }
                else if (!string.IsNullOrEmpty(typeDecl.RecordParameters))
                {
                    meta += $"|params:{typeDecl.RecordParameters}";
                }

                _graph.AddNode(typeDecl.Name, meta);
                _graph.AddEdge(relPath, typeDecl.Name, $"DECLARES_{typeDecl.Kind.ToUpperInvariant()}");
                _graph.AddEdge(typeDecl.Name, relPath, "DEFINED_IN");
                discoveredSymbols.Add($"{typeDecl.Kind} {typeDecl.Name} (L{typeDecl.LineNumber})");

                foreach (string baseName in typeDecl.BaseTypes)
                {
                    if (!string.IsNullOrEmpty(baseName) && char.IsLetter(baseName[0]))
                    {
                        _graph.AddNode(baseName, "kind:type_or_interface");
                        _graph.AddEdge(typeDecl.Name, baseName, "INHERITS_OR_IMPLEMENTS");
                    }
                }
            }

            int methodCount = 0;
            foreach (var methodDecl in symbols.Methods)
            {
                string qualifiedMethod = $"{Path.GetFileNameWithoutExtension(relPath)}.{methodDecl.Name}";
                _graph.AddNode(qualifiedMethod, $"kind:method|file:{relPath}|line:{methodDecl.LineNumber}");
                _graph.AddEdge(relPath, qualifiedMethod, "DECLARES_METHOD");

                if (methodCount < 12)
                {
                    discoveredSymbols.Add($"method {methodDecl.Name}() (L{methodDecl.LineNumber})");
                }
                methodCount++;
            }
        }

        return discoveredSymbols;
    }

    /// <summary>
    /// On-demand deep AST indexing of all C# source files in a target project directory.
    /// </summary>
    public void EnsureProjectDeepIndexed(string targetQuery)
    {
        if (string.IsNullOrWhiteSpace(targetQuery) || string.Equals(targetQuery, "Workspace", StringComparison.OrdinalIgnoreCase))
            return;

        string clean = targetQuery.Trim().Replace('\\', '/').Split('/')[0];
        if (string.IsNullOrEmpty(clean)) return;

        lock (_BootstrapLock)
        {
            if (!_deepIndexedProjects.Add(clean)) return;
        }

        string projDir = Path.Combine(_workspaceRoot, clean);
        if (!Directory.Exists(projDir)) return;

        string srcDir = Path.Combine(projDir, "src");
        string scanRoot = Directory.Exists(srcDir) ? srcDir : projDir;

        try
        {
            foreach (var csFile in Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories).Take(80))
            {
                if (csFile.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
                    csFile.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                string content = File.ReadAllText(csFile);
                IngestFileContent(csFile, content);
            }
        }
        catch
        {
            // Ignore non-fatal file read errors during deep scan
        }
    }

    /// <summary>
    /// Synchronous, non-blocking neighborhood summary for Blazor UI rendering.
    /// </summary>
    public string QueryNeighborhoodSummary(string targetQuery, int maxEdges = 28)
    {
        EnsureBootstrapped();
        if (string.IsNullOrWhiteSpace(targetQuery)) return "";

        EnsureProjectDeepIndexed(targetQuery);

        var candidateNodes = FindMatchingNodes(targetQuery, maxCandidates: 6);
        if (candidateNodes.Count == 0)
        {
            return $"[Knowledge Graph: No indexed nodes yet matching '{targetQuery}'. Total Graph: {_graph.NodeCount} nodes, {_graph.EdgeCount} edges]";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"### Knowledge Graph Context for `{targetQuery}` ({_graph.NodeCount} nodes, {_graph.EdgeCount} edges)");

        int emittedEdges = 0;
        foreach (var nodeId in candidateNodes)
        {
            string meta = _graph.GetNodeMetadata(nodeId);
            sb.AppendLine(!string.IsNullOrEmpty(meta)
                ? $"- **Node `{nodeId}`** (`{meta}`)"
                : $"- **Node `{nodeId}`**");

            var edgesByRel = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var enumerator = _graph.GetOutwardEdges(nodeId);
            while (enumerator.MoveNext())
            {
                string rel = _graph.GetRelationType(enumerator.CurrentRelationId);
                string target = _graph.GetExternalId(enumerator.CurrentTargetNodeId);
                if (!edgesByRel.TryGetValue(rel, out var list))
                {
                    list = new List<string>();
                    edgesByRel[rel] = list;
                }
                if (list.Count < 14 && !list.Contains(target))
                {
                    list.Add(target);
                }
                enumerator.Advance();
            }

            foreach (var kvp in edgesByRel)
            {
                if (emittedEdges >= maxEdges) break;
                sb.AppendLine($"  - `{kvp.Key}` → {string.Join(", ", kvp.Value.Select(v => $"`{v}`"))}");
                emittedEdges++;
            }
        }

        string projPrefix = targetQuery.Trim().Replace('\\', '/').Split('/')[0] + "/src/";
        var projectTypeFacts = new List<string>();
        int totalNodes = _graph.NodeCount;
        for (int i = 1; i <= totalNodes && projectTypeFacts.Count < 20; i++)
        {
            string extId = _graph.GetExternalId(i);
            string meta = _graph.GetNodeMetadata(extId);
            if (string.IsNullOrEmpty(meta) || !meta.Contains(projPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (meta.StartsWith("kind:enum|", StringComparison.OrdinalIgnoreCase) ||
                meta.StartsWith("kind:interface|", StringComparison.OrdinalIgnoreCase) ||
                meta.StartsWith("kind:struct|", StringComparison.OrdinalIgnoreCase) ||
                meta.StartsWith("kind:class|", StringComparison.OrdinalIgnoreCase) ||
                meta.StartsWith("kind:record|", StringComparison.OrdinalIgnoreCase))
            {
                projectTypeFacts.Add($"  - `{extId}` ({meta})");
            }
        }

        if (projectTypeFacts.Count > 0)
        {
            sb.AppendLine($"- **Verified C# AST Symbols in `{targetQuery}`**:");
            foreach (var fact in projectTypeFacts)
            {
                sb.AppendLine(fact);
            }
        }

        return sb.ToString().Trim();
    }

    public string? TryResolveFilePath(string queryName)
    {
        EnsureBootstrapped();
        if (string.IsNullOrWhiteSpace(queryName)) return null;

        string clean = queryName.Trim().Replace('\\', '/');
        string fullCandidate = Path.IsPathRooted(clean) ? clean : Path.Combine(_workspaceRoot, clean);
        if (File.Exists(fullCandidate)) return fullCandidate;

        var enumerator = _graph.GetOutwardEdges(clean);
        while (enumerator.MoveNext())
        {
            string rel = _graph.GetRelationType(enumerator.CurrentRelationId);
            if (rel == "DEFINED_IN")
            {
                string targetRel = _graph.GetExternalId(enumerator.CurrentTargetNodeId);
                string resolved = Path.Combine(_workspaceRoot, targetRel);
                if (File.Exists(resolved)) return resolved;
            }
            enumerator.Advance();
        }

        string fileNameOnly = Path.GetFileName(clean);
        if (!string.IsNullOrEmpty(fileNameOnly))
        {
            try
            {
                string searchPattern = fileNameOnly.Contains('.') ? fileNameOnly : $"{fileNameOnly}.cs";
                var found = Directory.EnumerateFiles(_workspaceRoot, searchPattern, SearchOption.AllDirectories)
                    .FirstOrDefault(f => !IsBuildArtifactPath(f));
                if (!string.IsNullOrEmpty(found)) return found;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[GlacierWorkspaceGraphService] File lookup fallback failed for '{fileNameOnly}': {ex.Message}");
            }
        }

        return null;
    }

    private static bool IsBuildArtifactPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/bin/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Synchronous symbol, text, and local-hash semantic search across the workspace.
    /// </summary>
    public string SearchWorkspace(string query, string? scopePath = null, int maxMatches = 25)
    {
        EnsureBootstrapped();
        if (string.IsNullOrWhiteSpace(query)) return "Error: 'query' parameter is required.";

        var sw = Stopwatch.StartNew();
        string rootDir = ResolveScopeDirectory(scopePath);
        var results = RunLexicalWorkspaceScan(query, rootDir, maxMatches, out var matchedFiles);

        foreach (var (filePath, content) in matchedFiles)
        {
            IngestFileContent(filePath, content);
        }

        if (!_chunksById.IsEmpty)
        {
            float[] hashVec = new float[_hashModel.Dimensions];
            _hashModel.GenerateEmbedding(query.AsSpan(), hashVec);
            SearchResult[] rawHits;
            lock (_indexLock)
            {
                rawHits = _hashIndex.Search(hashVec, Math.Clamp(maxMatches, 4, 16));
            }
            var semanticHits = RankSemanticHits(query, rawHits, Math.Clamp(maxMatches, 4, 6), NormalizeScopePrefix(scopePath));
            var seen = new HashSet<string>(matchedFiles.Select(m => NormalizeRelativePath(m.FilePath)), StringComparer.OrdinalIgnoreCase);
            foreach (var hit in semanticHits)
            {
                if (results.Count >= maxMatches + 6) break;
                if (seen.Add($"{hit.RelativePath}:{hit.StartLine}-{hit.EndLine}"))
                {
                    results.Add($"[SEMANTIC MATCH (sim: {hit.SimilarityScore:F2})] {hit.RelativePath}:L{hit.StartLine}-{hit.EndLine}: {hit.Preview}");
                }
            }
        }

        sw.Stop();
        if (results.Count == 0)
        {
            return $"No matches found for '{query}' in '{NormalizeRelativePath(rootDir)}' ({sw.Elapsed.TotalMilliseconds:F1}ms).\n" +
                   QueryNeighborhoodSummary(query, maxEdges: 10);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} match(es) for '{query}' in {sw.Elapsed.TotalMilliseconds:F1}ms:");
        foreach (var r in results)
        {
            sb.AppendLine($"- {r}");
        }
        return sb.ToString().Trim();
    }

    private List<string> RunLexicalWorkspaceScan(
        string query,
        string rootDir,
        int maxMatches,
        out List<(string FilePath, string Content)> matchedFiles)
    {
        var results = new List<string>();
        matchedFiles = new List<(string FilePath, string Content)>();

        string? containerHint = null;
        string? memberTerm = null;
        if (query.Contains('.') && !query.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !query.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var parts = query.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                containerHint = parts[^2];
                memberTerm = parts[^1];
            }
        }

        try
        {
            foreach (var file in EnumerateWorkspaceFiles(rootDir))
            {
                if (results.Count >= maxMatches) break;

                string relPath = NormalizeRelativePath(file);
                if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"[FILE MATCH] {relPath}");
                }

                string content = File.ReadAllText(file);
                string effectiveSearchTerm = query;
                int idx = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);

                if (idx < 0 && (query.Contains(' ') || query.Contains(',') || query.Contains(';')))
                {
                    var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "or", "and", "the", "for", "its", "project", "in", "of", "to", "a", "an", "is", "with", "breakdown", "details"
                    };
                    var terms = query.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(t => t.Length >= 3 && !stopWords.Contains(t))
                        .ToArray();

                    foreach (var t in terms)
                    {
                        if (content.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            idx = content.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                            effectiveSearchTerm = t;
                            break;
                        }
                    }
                }

                if (idx < 0 && !string.IsNullOrEmpty(containerHint) && !string.IsNullOrEmpty(memberTerm))
                {
                    if (relPath.Contains(containerHint, StringComparison.OrdinalIgnoreCase) ||
                        content.Contains(containerHint, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = content.IndexOf(memberTerm, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0) effectiveSearchTerm = memberTerm;
                    }
                }

                if (idx >= 0)
                {
                    matchedFiles.Add((file, content));
                    using var reader = new StringReader(content);
                    string? line;
                    int lineNum = 0;
                    int fileHits = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNum++;
                        if (line.Contains(effectiveSearchTerm, StringComparison.OrdinalIgnoreCase))
                        {
                            string snippet = line.Trim();
                            if (snippet.Length > 140) snippet = snippet[..140] + "...";
                            results.Add($"{relPath}:{lineNum}: {snippet}");
                            fileHits++;
                            if (fileHits >= 4 || results.Count >= maxMatches) break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            results.Add($"Search warning: {ex.Message}");
        }

        return results;
    }

    private IEnumerable<string> EnumerateWorkspaceFiles(string rootDir)
    {
        if (!Directory.Exists(rootDir)) yield break;

        var allowedExts = new HashSet<string>(s_allowedWorkspaceExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("\\.glacier\\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("/.glacier/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("/.vs/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("\\BenchmarkDotNet.Artifacts\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (allowedExts.Contains(Path.GetExtension(file)))
            {
                yield return file;
            }
        }
    }

    private string ResolveScopeDirectory(string? scopePath)
    {
        if (!string.IsNullOrWhiteSpace(scopePath))
        {
            string candidateDir = Path.IsPathRooted(scopePath)
                ? scopePath
                : Path.Combine(_workspaceRoot, scopePath);
            if (Directory.Exists(candidateDir))
                return candidateDir;
        }
        return _workspaceRoot;
    }

    private string? NormalizeScopePrefix(string? scopePath)
    {
        if (string.IsNullOrWhiteSpace(scopePath))
            return null;
        string rel = NormalizeRelativePath(scopePath).TrimEnd('/');
        return string.IsNullOrWhiteSpace(rel) || rel == "." ? null : rel;
    }

    private List<WorkspaceChunkRecord> UpsertChunksForFile(string relPath, string content, string contentHash)
    {
        var rawChunks = DocumentChunker.ChunkText(relPath, content, _options.MaxChunkLength, _options.ChunkOverlap);
        var records = new List<WorkspaceChunkRecord>(Math.Min(rawChunks.Count, _options.MaxChunksPerFile));
        var newChunkIds = new List<string>(records.Capacity);

        foreach (var chunk in rawChunks.Take(_options.MaxChunksPerFile))
        {
            int startLine = GetLineNumberAtOffset(content, chunk.StartChar);
            int endLine = GetLineNumberAtOffset(content, Math.Max(chunk.StartChar, chunk.EndChar - 1));
            var record = new WorkspaceChunkRecord(
                chunk.ChunkId,
                relPath,
                startLine,
                endLine,
                chunk.Content,
                contentHash);

            records.Add(record);
            newChunkIds.Add(record.ChunkId);
            _chunksById[record.ChunkId] = record;
        }

        if (_fileChunkIds.TryGetValue(relPath, out var oldChunkIds))
        {
            var currentSet = new HashSet<string>(newChunkIds, StringComparer.Ordinal);
            foreach (var oldId in oldChunkIds)
            {
                if (!currentSet.Contains(oldId))
                {
                    _chunksById.TryRemove(oldId, out _);
                }
            }
        }

        _fileChunkIds[relPath] = newChunkIds;
        return records;
    }

    private void IndexChunksIntoHash(IReadOnlyList<WorkspaceChunkRecord> records)
    {
        float[] buffer = new float[_hashModel.Dimensions];
        lock (_indexLock)
        {
            foreach (var rec in records)
            {
                string key = BuildChunkVersionKey(rec);
                if (!_hashIndexedKeys.Add(key)) continue;
                _hashModel.GenerateEmbedding(rec.Content.AsSpan(), buffer);
                _hashIndex.Add(buffer, key);
            }
        }
    }

    private void IndexChunksIntoCustom(IEmbeddingModel model, IReadOnlyList<WorkspaceChunkRecord> records)
    {
        float[] buffer = new float[model.Dimensions];
        lock (_indexLock)
        {
            if (_customIndex == null || _customIndex.Dimensions != model.Dimensions) return;
            foreach (var rec in records)
            {
                string key = BuildChunkVersionKey(rec);
                if (!_customIndexedKeys.Add(key)) continue;
                model.GenerateEmbedding(rec.Content.AsSpan(), buffer);
                _customIndex.Add(buffer, key);
            }
        }
    }

    private async Task TryEmbedChunksIntoOpenAiAsync(
        OpenAiEmbeddingModel model,
        IReadOnlyList<WorkspaceChunkRecord> records,
        CancellationToken cancellationToken)
    {
        long generation = Interlocked.Read(ref _workspaceGeneration);
        try
        {
            foreach (var rec in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _workspaceGeneration) != generation)
                    return;
                string key = BuildChunkVersionKey(rec);
                lock (_indexLock)
                {
                    if (_openAiIndexedKeys.Contains(key))
                        continue;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.EmbeddingTimeout);
                float[] vec = await model.GenerateEmbeddingAsync(rec.Content, timeoutCts.Token).ConfigureAwait(false);

                if (vec.Length != model.Dimensions || !AreAllFinite(vec))
                {
                    throw new InvalidOperationException($"Invalid embedding vector returned (len={vec.Length}, expected={model.Dimensions}).");
                }

                lock (_indexLock)
                {
                    if (Interlocked.Read(ref _workspaceGeneration) != generation)
                        return;
                    if (_openAiIndex != null &&
                        _openAiIndex.Dimensions == vec.Length &&
                        ReferenceEquals(_activeOpenAiModel, model) &&
                        _openAiIndexedKeys.Add(key))
                    {
                        _openAiIndex.Add(vec, key);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableEmbeddingException(ex))
        {
            lock (_indexLock)
            {
                _npuDemoted = true;
                _demotedAtTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    private async Task EmbedExistingChunksIntoOpenAiLockedAsync(
        OpenAiEmbeddingModel model,
        CancellationToken cancellationToken)
    {
        var snapshot = _chunksById.Values.ToList();
        await TryEmbedChunksIntoOpenAiAsync(model, snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAllChunksEmbeddedForOpenAiAsync(
        OpenAiEmbeddingModel model,
        CancellationToken cancellationToken)
    {
        var pending = new List<WorkspaceChunkRecord>();
        lock (_indexLock)
        {
            foreach (var rec in _chunksById.Values)
            {
                if (!_openAiIndexedKeys.Contains(BuildChunkVersionKey(rec)))
                {
                    pending.Add(rec);
                }
            }
        }

        if (pending.Count > 0)
        {
            await TryEmbedChunksIntoOpenAiAsync(model, pending, cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<SemanticWorkspaceHit> RankSemanticHits(
        string query,
        SearchResult[] rawHits,
        int topK,
        string? normalizedScope)
    {
        if (rawHits.Length == 0)
            return Array.Empty<SemanticWorkspaceHit>();

        var queryTerms = Regex.Matches(query, @"[A-Za-z0-9_]{3,}")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var graphConnectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var graphSearch = new GraphSearch(_graph);
            foreach (var term in queryTerms.Take(4))
            {
                foreach (var match in FindMatchingNodes(term, 3))
                {
                    foreach (var neighbor in graphSearch.FindNeighborhood(match, 1))
                    {
                        graphConnectedFiles.Add(neighbor);
                    }
                }
            }
        }
        catch
        {
            // Non-fatal graph boost failure
        }

        var hitsByChunk = new Dictionary<string, SemanticWorkspaceHit>(StringComparer.Ordinal);
        foreach (var res in rawHits)
        {
            if (!TryParseChunkVersionKey(res.Metadata, out string chunkId, out string hash))
                continue;

            if (!_chunksById.TryGetValue(chunkId, out var rec) || !string.Equals(rec.ContentHash, hash, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrEmpty(normalizedScope) &&
                !rec.RelativePath.StartsWith(normalizedScope, StringComparison.OrdinalIgnoreCase))
                continue;

            float boost = 0f;
            if (graphConnectedFiles.Contains(rec.RelativePath))
            {
                boost += 0.15f;
            }
            else
            {
                string fileName = Path.GetFileNameWithoutExtension(rec.RelativePath);
                if (queryTerms.Any(t => fileName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    boost += 0.10f;
                }
            }

            float hybrid = res.Score + boost;
            string preview = CollapsePreview(rec.Content);
            var hit = new SemanticWorkspaceHit(
                rec.ChunkId,
                rec.RelativePath,
                rec.StartLine,
                rec.EndLine,
                res.Score,
                hybrid,
                preview);

            if (!hitsByChunk.TryGetValue(chunkId, out var existing) || hit.HybridScore > existing.HybridScore)
            {
                hitsByChunk[chunkId] = hit;
            }
        }

        return hitsByChunk.Values
            .OrderByDescending(h => h.HybridScore)
            .ThenByDescending(h => h.SimilarityScore)
            .Take(topK)
            .ToList();
    }

    private void DemoteToHashLocked()
    {
        lock (_indexLock)
        {
            _npuDemoted = true;
            _demotedAtTimestamp = Stopwatch.GetTimestamp();
            if (_ownsOpenAiModel)
            {
                _activeOpenAiModel?.Dispose();
            }
            _activeOpenAiModel = null;
            _ownsOpenAiModel = false;
            _openAiSpaceKey = null;
            _openAiStorage = null;
            _openAiIndex = null;
            _openAiIndexedKeys.Clear();
        }
    }

    private bool ShouldRetryAfterDemotion()
    {
        if (!Volatile.Read(ref _npuDemoted))
            return false;

        TimeSpan cooldown = _options.ProbeCacheTtl is { } ttl && ttl > TimeSpan.Zero
            ? ttl
            : TimeSpan.FromSeconds(5);
        long demotedAt = Interlocked.Read(ref _demotedAtTimestamp);
        if (demotedAt == 0)
            return true;
        return Stopwatch.GetElapsedTime(demotedAt) >= cooldown;
    }

    private static readonly string[] s_embeddingModelTokens =
    [
        "embed", "bge-", "e5-", "gte-", "minilm", "snowflake"
    ];

    private static string SelectEmbeddingModelId(string? explicitModelId, IReadOnlyList<string> discoveredModels)
    {
        if (!string.IsNullOrWhiteSpace(explicitModelId))
            return explicitModelId.Trim();

        foreach (var token in s_embeddingModelTokens)
        {
            foreach (var model in discoveredModels)
            {
                if (!string.IsNullOrWhiteSpace(model) && model.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return model.Trim();
            }
        }

        foreach (var model in discoveredModels)
        {
            if (!string.IsNullOrWhiteSpace(model))
                return model.Trim();
        }

        return "embeddinggemma";
    }

    private static string BuildSpaceKey(OpenAiEmbeddingModel model) =>
        $"{model.BaseUrl}|{model.ModelId}|{model.Dimensions}";

    private static string BuildChunkVersionKey(WorkspaceChunkRecord record) =>
        $"{record.ChunkId}\n{record.ContentHash}";

    private static bool TryParseChunkVersionKey(string? metadata, out string chunkId, out string hash)
    {
        chunkId = "";
        hash = "";
        if (string.IsNullOrEmpty(metadata)) return false;
        int sep = metadata.IndexOf('\n');
        if (sep <= 0 || sep >= metadata.Length - 1) return false;
        chunkId = metadata[..sep];
        hash = metadata[(sep + 1)..];
        return true;
    }

    private static string ComputeContentHash(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static bool AreAllFinite(float[] vector)
    {
        for (int i = 0; i < vector.Length; i++)
        {
            if (!float.IsFinite(vector[i])) return false;
        }
        return true;
    }

    private static bool IsRecoverableEmbeddingException(Exception ex) =>
        ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or JsonException
            or InvalidOperationException
            or UriFormatException
            or NotSupportedException
            or ArgumentException;

    private static string CollapsePreview(string text)
    {
        string flat = Regex.Replace(text.Trim(), @"\s+", " ");
        return flat.Length > 160 ? flat[..160] + "..." : flat;
    }

    private List<string> FindMatchingNodes(string query, int maxCandidates)
    {
        var matches = new List<string>();
        string clean = query.Trim().Replace('\\', '/');

        if (_graph.TryGetInternalId(clean, out _))
        {
            matches.Add(clean);
        }

        string fileNameNoExt = Path.GetFileNameWithoutExtension(clean);
        if (!string.IsNullOrEmpty(fileNameNoExt) && !matches.Contains(fileNameNoExt) && _graph.TryGetInternalId(fileNameNoExt, out _))
        {
            matches.Add(fileNameNoExt);
        }

        int totalNodes = _graph.NodeCount;
        for (int i = 1; i <= totalNodes && matches.Count < maxCandidates; i++)
        {
            string extId = _graph.GetExternalId(i);
            if (!string.IsNullOrEmpty(extId) &&
                !matches.Contains(extId) &&
                extId.Contains(clean, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(extId);
            }
        }

        return matches;
    }

    public string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_workspaceRoot, path));
            if (full.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(_workspaceRoot, full).Replace('\\', '/');
            }
        }
        catch { }
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static int GetLineNumberAtOffset(string text, int offset)
    {
        int line = 1;
        int limit = Math.Min(offset, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private void EnterActiveOp()
    {
        while (true)
        {
            int cur = Volatile.Read(ref _activeOps);
            if (cur <= 0 || Volatile.Read(ref _disposed) != 0)
            {
                ObjectDisposedException.ThrowIf(true, this);
            }
            if (Interlocked.CompareExchange(ref _activeOps, cur + 1, cur) == cur)
            {
                return;
            }
        }
    }

    private void ExitActiveOp()
    {
        if (Interlocked.Decrement(ref _activeOps) == 0)
        {
            _semanticGate.Dispose();
            _disposeCts.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }

        lock (_indexLock)
        {
            if (_ownsOpenAiModel)
            {
                _activeOpenAiModel?.Dispose();
            }
            _activeOpenAiModel = null;
            _ownsOpenAiModel = false;
        }
        ExitActiveOp();
    }
}
