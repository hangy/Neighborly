﻿using Microsoft.Extensions.Logging;
using Neighborly.ETL;
using Neighborly.Search;
using Neighborly.Distance;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using static System.Net.Mime.MediaTypeNames;

namespace Neighborly;

/// <summary>
/// Represents a database for storing and searching vectors.
/// </summary>
public partial class VectorDatabase : IDisposable
{
    /// <summary>
    /// The version of the database file format that this class writes.
    /// </summary>
    private const int s_currentFileVersion = 1;

    private readonly ILogger<VectorDatabase> _logger = Logging.LoggerFactory.CreateLogger<VectorDatabase>();
    private readonly Instrumentation _instrumentation;
    /// <summary>
    /// The unique identifier of the database for telemetry purposes.
    /// </summary>
    private readonly Guid _id = Guid.NewGuid();
    private readonly IEnumerable<KeyValuePair<string, object?>>? _defaultTags;
    private readonly VectorList _vectors = new();
    private readonly System.Diagnostics.Metrics.Counter<long> _indexRebuildCounter;
    public VectorList Vectors => _vectors;

    /// <summary>
    /// Thread-safe method to add a vector to the database.
    /// </summary>
    /// <param name="vector">The vector to add</param>
    public void AddVector(Vector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        
        _rwLock.EnterWriteLock();
        try
        {
            _vectors.Add(vector);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Thread-safe method to update a vector in the database.
    /// </summary>
    /// <param name="id">The ID of the vector to update</param>
    /// <param name="vector">The new vector data</param>
    /// <returns>True if the vector was updated, false if not found</returns>
    public bool UpdateVector(Guid id, Vector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        
        _rwLock.EnterWriteLock();
        try
        {
            return _vectors.Update(id, vector);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Thread-safe method to remove a vector from the database.
    /// </summary>
    /// <param name="vector">The vector to remove</param>
    /// <returns>True if the vector was removed, false if not found</returns>
    public bool RemoveVector(Vector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        
        _rwLock.EnterWriteLock();
        try
        {
            return _vectors.Remove(vector);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Thread-safe method to get a vector by ID.
    /// </summary>
    /// <param name="id">The ID of the vector to retrieve</param>
    /// <returns>The vector if found, null otherwise</returns>
    public Vector? GetVector(Guid id)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _vectors.GetById(id);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }
    private Search.SearchService _searchService = null!;
    private ReaderWriterLockSlim _rwLock = new();
    private Thread? indexService;
    private CancellationTokenSource? _indexServiceCancellationTokenSource;


    /// <summary>
    /// Last time the database was modified. This is updated when a vector is added or removed.
    /// </summary>
    private DateTime _lastModification = DateTime.UtcNow;

    /// <summary>
    /// The time threshold in seconds for rebuilding the search indexes and VectorTags after a database change was detected.
    /// </summary>
    private const int timeThresholdSeconds = 5;

    /// <summary>
    /// Gets the number of vectors in the database.
    /// </summary>
    public int Count => _vectors.Count;

    /// <summary>
    /// Gets a value indicating whether the database is read-only.
    /// </summary>
    public bool IsReadOnly => false;

    /// <summary>
    /// Indicates whether the database has been modified since the last save.
    /// </summary>
    private bool _hasUnsavedChanges = false;

    /// <summary>
    /// Indicates whether the database has changed since the last indexing, and it needs to be rebuilt.
    /// </summary>
    private bool _hasOutdatedIndex = false;

    private bool _disposedValue;
    private volatile bool _isDisposing;
    private readonly CancellationTokenSource _disposalCancellationTokenSource = new();

    /// <summary>
    /// Gets a value indicating whether the database has been modified since the last save.
    /// </summary>
    public bool HasUnsavedChanges { get { return _hasUnsavedChanges; } }

    private void VectorList_Modified(object? sender, EventArgs e)
    {
        _lastModification = DateTime.UtcNow;
        _hasUnsavedChanges = true;
        _hasOutdatedIndex = true;
    }

    /// <summary>
    /// Passes in details about how to generate embeddings.
    /// </summary>
    /// <seealso cref="EmbeddingGenerationInfo"/>
    public void SetEmbeddingGenerationInfo(EmbeddingGenerationInfo embeddingGeneratorInfo)
    {
        _searchService.EmbeddingGenerator = new EmbeddingGenerator(embeddingGeneratorInfo);
    }

    /// <summary>
    /// Generates a Vector class from text.
    /// </summary>
    /// <param name="originalText"></param>
    /// <returns></returns>
    public Vector GenerateVector(string originalText)
    {
        float[] embedding = _searchService.EmbeddingGenerator.GenerateEmbedding(originalText);
        return new Vector(embedding, originalText);
    }

    /// <summary>
    /// Searches for a specified text in the database and returns the k nearest neighbors.
    /// This text is first converted into an embedding using the EmbeddingGenerator.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="k">Proximity</param>
    /// <param name="searchMethod">Search algorithm</param>
    /// <param name="similarityThreshold"></param>
    /// <returns>Vectors that match the search results</returns>
    /// <seealso cref="EmbeddingGenerator"/>
    public IList<Vector> Search(string text, int k, SearchAlgorithm searchMethod = SearchAlgorithm.KDTree, float similarityThreshold = 0.5f)
    {
        using var activity = StartActivity(tags: [new("search.searchMethod", searchMethod), new("search.k", k)]);
        try
        {
            var result = _searchService.Search(text, k, searchMethod, similarityThreshold);
            activity?.AddTag("search.result.count", result.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            // CouldNotFindVectorInDb(text, k, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new List<Vector>();
        }
    }

    /// <summary>
    /// Searches for a specified vector in the database and returns the k nearest neighbors.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="k"></param>
    /// <param name="searchMethod">Search algorithm</param>
    /// <param name="similarityThreshold"></param>
    /// <returns></returns>
    public IList<Vector> Search(Vector query, int k, SearchAlgorithm searchMethod = SearchAlgorithm.KDTree, float similarityThreshold = 0.5f)
    {
        using var activity = StartActivity(tags: [new("search.searchMethod", searchMethod), new("search.k", k)]);
        try
        {
            var result = _searchService.Search(query:query, k, searchMethod, similarityThreshold: similarityThreshold);
            activity?.AddTag("search.result.count", result.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            CouldNotFindVectorInDb(query, k, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new List<Vector>();
        }
    }

    /// <summary>
    /// Searches for all vectors within a specified radius of the given text in the database.
    /// This text is first converted into an embedding using the EmbeddingGenerator.
    /// </summary>
    /// <param name="text">The text to search for</param>
    /// <param name="radius">The maximum distance from the query</param>
    /// <param name="searchMethod">Search algorithm to use</param>
    /// <param name="distanceCalculator">The distance calculator to use (defaults to Euclidean)</param>
    /// <returns>Vectors that are within the specified radius</returns>
    /// <seealso cref="EmbeddingGenerator"/>
    public IList<Vector> RangeSearch(string text, float radius, SearchAlgorithm searchMethod = SearchAlgorithm.Linear, IDistanceCalculator? distanceCalculator = null)
    {
        using var activity = StartActivity(tags: [new("search.searchMethod", searchMethod), new("search.radius", radius)]);
        try
        {
            var result = _searchService.RangeSearch(text, radius, searchMethod, distanceCalculator);
            activity?.AddTag("search.result.count", result.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (ArgumentException)
        {
            // Re-throw validation exceptions so tests can catch them
            throw;
        }
        catch (NotSupportedException)
        {
            // Re-throw unsupported operation exceptions so tests can catch them
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new List<Vector>();
        }
    }

    /// <summary>
    /// Searches for all vectors within a specified radius of the given query vector in the database.
    /// </summary>
    /// <param name="query">The query vector</param>
    /// <param name="radius">The maximum distance from the query</param>
    /// <param name="searchMethod">Search algorithm to use</param>
    /// <param name="distanceCalculator">The distance calculator to use (defaults to Euclidean)</param>
    /// <returns>Vectors that are within the specified radius</returns>
    public IList<Vector> RangeSearch(Vector query, float radius, SearchAlgorithm searchMethod = SearchAlgorithm.Linear, IDistanceCalculator? distanceCalculator = null)
    {
        using var activity = StartActivity(tags: [new("search.searchMethod", searchMethod), new("search.radius", radius)]);
        try
        {
            var result = _searchService.RangeSearch(query, radius, searchMethod, distanceCalculator);
            activity?.AddTag("search.result.count", result.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (ArgumentException)
        {
            // Re-throw validation exceptions so tests can catch them
            throw;
        }
        catch (NotSupportedException)
        {
            // Re-throw unsupported operation exceptions so tests can catch them
            throw;
        }
        catch (Exception ex)
        {
            CouldNotPerformRangeSearchInDb(query, radius, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new List<Vector>();
        }
    }

    [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Error,
    Message = "Could not perform range search for vector `{Query}` with radius {Radius} in the database.")]
    private partial void CouldNotPerformRangeSearchInDb(Vector query, float radius, Exception ex);

    [LoggerMessage(
    EventId = 0,
    Level = LogLevel.Error,
    Message = "Could not find vector `{Query}` in the database searching the {k} nearest neighbor(s).")]
    private partial void CouldNotFindVectorInDb(Vector query, int k, Exception ex);

    #region Constructors
    /// <summary>
    /// Initializes a new instance of the <see cref="VectorDatabase"/> class.
    /// </summary>
    public VectorDatabase()
        : this(Logging.LoggerFactory.CreateLogger<VectorDatabase>(), null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VectorDatabase"/> class.
    /// </summary>
    /// <param name="logger">The logger to be used for logging.</param>
    /// <param name="instrumentation">The instrumentation to be used for metrics and tracing.</param>
    /// <exception cref="ArgumentNullException">Thrown when the logger is null.</exception>
    public VectorDatabase(ILogger<VectorDatabase> logger, Instrumentation? instrumentation)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _instrumentation = instrumentation ?? Instrumentation.Instance;

        _instrumentation.Meter.CreateObservableGauge(
            name: "neighborly.db.vectors.count",
            unit: "{vectors}",
            description: "The number of vectors in the database.",
            observeValue: () => Count,
            tags: [new("db.namespace", _id)]
        );
        _indexRebuildCounter = _instrumentation.Meter.CreateCounter<long>(
            name: "neighborly.db.index.rebuild",
            unit: "{rebuilds}",
            description: "The number of times the search index was rebuilt.",
            tags: [new("db.namespace", _id)]
        );
        _defaultTags = [new("db.system", "neighborly"), new("db.namespace", _id)];

        // Wire up the event handler for the VectorList.Modified event
        _vectors.Modified += VectorList_Modified;
        _searchService = new Search.SearchService(_vectors);
        StartIndexService();
    }

    #endregion

    #region Load/Save
    /// <summary>
    /// Loads vectors from a specified file path.
    /// </summary>
    /// <param name="path">The file path to load the vectors from.</param>
    /// <param name="createOnNew">Indicates whether to create a new file if it doesn't exist.</param>
    public async Task LoadAsync(string path, bool createOnNew = true, CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity(name: "LoadVectors");
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposalCancellationTokenSource.Token);
        var combinedToken = combinedCts.Token;
        string filePath = Path.Combine(path, "vectors.bin");
        bool fileExists = File.Exists(filePath);
        if (!createOnNew && !fileExists)
        {
            _logger.LogError("The file {FilePath} does not exist.", filePath);
            var error = $"The file {filePath} does not exist.";
            activity?.SetStatus(ActivityStatusCode.Error, error);
            throw new FileNotFoundException(error);
        }
        else if (createOnNew && !fileExists)
        {
            // Do nothing here. We'll create the file when SaveAsync() is called
            activity?.SetStatus(ActivityStatusCode.Ok, "File does not exist. Will create it when saving.");
            return;
        }
        else
        {
            bool indexesAreDirty;
            
            // Load data without holding lock for the entire operation
            _rwLock.EnterWriteLock();
            try
            {
                using (var inputStream = new FileStream(filePath, FileMode.Open))
                using (var decompressionStream = new GZipStream(inputStream, CompressionMode.Decompress))
                using (var reader = new BinaryReader(decompressionStream))
                {
                    _vectors.Clear();
                    (int vectorCount, indexesAreDirty) = await ReadFromAsync(reader, true, combinedToken).ConfigureAwait(false);
                    _logger.LogInformation("Loaded {VectorCount} vectors from {FilePath}.", vectorCount, inputStream.Name);
                }
            }
            finally
            {
                if (_rwLock.IsWriteLockHeld)
                {
                    _rwLock.ExitWriteLock();
                }
            }

            // Rebuild indexes outside the write lock to reduce lock contention
            if (indexesAreDirty && !_isDisposing)
            {
                // Check for disposal/cancellation before rebuilding indexes
                combinedToken.ThrowIfCancellationRequested();
                await RebuildSearchIndexesAsync(combinedToken).ConfigureAwait(false);     // Rebuild both k-d tree and Ball Tree search index  
            }

            // Update state flags with minimal lock time
            _rwLock.EnterWriteLock();
            try
            {
                _vectors.Tags.BuildMap();   // Rebuild the tag map
                _hasUnsavedChanges = false; // Set the flag to indicate the database hasn't been modified
                _hasOutdatedIndex = false;  // Set the flag to indicate the index is up-to-date
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            finally
            {
                if (_rwLock.IsWriteLockHeld)
                {
                    _rwLock.ExitWriteLock();
                }
            }
        }
    }

    internal async Task<(int vectorCount, bool indexesAreDirty)> ReadFromAsync(BinaryReader reader, bool includeIndexes, CancellationToken cancellationToken)
    {
        var fileVersion = reader.ReadInt32();   // File version

        Func<BinaryReader, bool, CancellationToken, Task<(int vectorCount, bool indexesAreDirty)>> importFunc = fileVersion switch
        {
            1 => LoadV1Async,
            _ => LoadV0Async
        };

        return await importFunc(reader, includeIndexes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports vectors from a specified file path with the V1 layout.
    /// </summary>
    /// <remarks>
    /// The V1 layout has a leading integer that indicates the total number of vectors in the database
    /// followed by the binary representation of each vector. Up to this, the V0 layout is the same.
    /// However, it is followed by the binary representation the indexes.
    /// </remarks>
    private async Task<(int vectorCount, bool indexesAreDirty)> LoadV1Async(BinaryReader reader, bool includeIndexes, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading vectors from the V1 layout.");
        var (vectorCount, _) = await LoadV0Async(reader, includeIndexes, cancellationToken).ConfigureAwait(false);

        if (includeIndexes)
        {
            await _searchService.LoadAsync(reader, cancellationToken).ConfigureAwait(false);
            return (vectorCount, false);
        }

        return (vectorCount, true);
    }

    /// <summary>
    /// Imports vectors from a specified file path with the original layout.
    /// </summary>
    /// <remarks>
    /// The original layout has a leading integer that indicates the total number of vectors in the database
    /// followed by the binary representation of each vector.
    /// </remarks>
    private Task<(int vectorCount, bool indexesAreDirty)> LoadV0Async(BinaryReader reader, bool includeIndexes, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading vectors from the original (V0) layout.");
        var vectorCount = reader.ReadInt32();   // Total number of Vectors in the database

        for (int i = 0; i < vectorCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextVector = reader.ReadInt32();    // File offset of the next Vector
            var vector = new Vector(reader.ReadBytes(nextVector));
            _vectors.Add(vector);
        }

        return Task.FromResult((vectorCount, true));
    }

    private void StartIndexService()
    {
        // The index service is not supported on mobile platforms
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            return;

        _indexServiceCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _indexServiceCancellationTokenSource.Token;

        indexService = new Thread(async () =>
        {
            _logger.LogInformation("Indexing thread started.");

            while (!_vectors.IsReadOnly && !cancellationToken.IsCancellationRequested && !_isDisposing)
            {
                // If the database has been modified and the last modification was more than 5 seconds ago, rebuild the indexes
                if (_hasOutdatedIndex &&
                    _vectors.Count > 0 &&
                    DateTime.UtcNow.Subtract(_lastModification).TotalSeconds > timeThresholdSeconds)
                {
                    await RebuildTagsAsync();
                    await RebuildSearchIndexesAsync(cancellationToken);
                    _indexRebuildCounter.Add(1);
                }
                try
                {
                    await Task.Delay(5000, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    _logger.LogInformation("Indexing thread was canceled.");
                    break;
                }
            }

            _logger.LogInformation("Indexing thread stopping.");
        });
        indexService.Priority = ThreadPriority.Lowest;
        indexService.Start();
    }

    private void StopIndexService()
    {
        try
        {
            _logger.LogInformation($"StopIndexService called. IndexService exists: {indexService != null}, IsAlive: {indexService?.IsAlive}, CancellationSource exists: {_indexServiceCancellationTokenSource != null}");
            
            if (indexService != null && indexService.IsAlive)
            {
                _indexServiceCancellationTokenSource?.Cancel();
                _logger.LogInformation("Indexing stop requested.");
                
                // Wait for the indexing thread to actually stop before continuing
                // This prevents lock disposal issues when the thread is still running
                while (indexService.IsAlive)
                {
                    if (!indexService.Join(TimeSpan.FromSeconds(1)))
                    {
                        _logger.LogWarning("Indexing thread is still running...");
                    }
                }
                
                _indexServiceCancellationTokenSource?.Dispose();
                _indexServiceCancellationTokenSource = null;
                indexService = null;
            }
            else if (indexService != null)
            {
                _logger.LogInformation("IndexService exists but is not alive - cleaning up.");
                _indexServiceCancellationTokenSource?.Dispose();
                _indexServiceCancellationTokenSource = null;
                indexService = null;
            }
            else
            {
                _logger.LogInformation("No indexing service to stop.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while stopping index service.");
        }
    }

    /// <summary>
    /// Creates a new kd-tree index for the vectors and a map of tags to vector IDs.
    /// (This searchMethod is eventually calls when the database is modified.)
    /// </summary>
    public async Task RebuildTagsAsync()
    {
        if (!_hasOutdatedIndex || _vectors == null || _vectors.Count == 0)
        {
            return;
        }
        await Task.Run(() =>
        {
            using var activity = StartActivity(name: "RebuildTags");
            _vectors.Tags.BuildMap();
            activity?.SetStatus(ActivityStatusCode.Ok);
        });
        _hasOutdatedIndex = false;
    }

    // This is an async function
    public async Task RebuildSearchIndexesAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            using var activity = StartActivity(name: "BuildAllSearchIndexes");
            cancellationToken.ThrowIfCancellationRequested();
            _searchService.BuildAllIndexes();
            activity?.SetStatus(ActivityStatusCode.Ok);
        }, cancellationToken);
    }
    public async Task RebuildSearchIndexAsync(SearchAlgorithm searchMethod = SearchAlgorithm.KDTree)
    {
        await Task.Run(() =>
        {
            using var activity = StartActivity(name: "BuildSearchIndex");
            _searchService.BuildIndex(searchMethod);
            activity?.SetStatus(ActivityStatusCode.Ok);
        });
    }

    /// <summary>
    /// Saves the vectors to the current directory.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        // Get the current directory
        string currentDirectory = Directory.GetCurrentDirectory();

        // Call the existing Save searchMethod with the current directory
        await SaveAsync(currentDirectory, cancellationToken);
    }

    /// <summary>
    /// Saves the vectors to a specified file path.
    /// (In the API server, this searchMethod will be called when the host OS sends a shutdown signal.)
    /// </summary>
    /// <param name="path">The file path to save the vectors to.</param>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity(name: "SaveVectors");
        
        string filePath = Path.Combine(path, "vectors.bin");
        string oldFilePath = Path.Combine(path, "vectors.old.bin");

        // If the database hasn't been modified, no need to save it
        if (!_hasUnsavedChanges)
        {
            _logger.LogInformation("The database has not been modified since the last save.");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            _logger.LogInformation("The directory {Path} was created.", path);
        }

        if (File.Exists(filePath))
        {
            if (File.Exists(oldFilePath))
            {
                File.Delete(oldFilePath);
                _logger.LogInformation("The file {oldFilePath} exists and is deleted.", oldFilePath);
            }
            File.Move(filePath, oldFilePath, true);
            _logger.LogInformation("The file {Path} exists and is moved to {oldFilePath} temporarily.", path, oldFilePath);
        }

        try
        {
            _rwLock.EnterWriteLock();
            // Save the vectors to a binary file
            using var outputStream = new FileStream(filePath, FileMode.Create);
            // TODO -- Experiment with other compression types. For now, GZip works.
            using (var compressionStream = new GZipStream(outputStream, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(compressionStream))
            {
                await WriteToAsync(writer, true, cancellationToken).ConfigureAwait(false);
            }
            outputStream.Close();
            _hasUnsavedChanges = false; // Clear the flag to indicate the database hasn't been modified
            activity?.SetStatus(ActivityStatusCode.Ok);
            File.Delete(oldFilePath);   // Delete the old file
            _logger.LogInformation("Saved the database to {FilePath} and deleted old backup {oldFilePath}.", filePath, oldFilePath);

        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "An error occurred while saving the database. Access to the path is denied.");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "An error occurred while saving the database.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while saving the database.");
        }
        finally
        {
            if (_rwLock.IsWriteLockHeld)
            {
                _rwLock.ExitWriteLock();
            }
        }
    }

    internal async Task WriteToAsync(BinaryWriter writer, bool includeIndexes, CancellationToken cancellationToken = default)
    {
        // TODO -- This should be async and potentially parallelized
        writer.Write(s_currentFileVersion);
        writer.Write(_vectors.Count);
        foreach (Vector v in _vectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = v.ToBinary();
            writer.Write(bytes.Length);    // File offset of the next Vector
            writer.Write(bytes);           // The Vector itself
        }

        if (includeIndexes)
        {
            await _searchService.SaveAsync(writer, cancellationToken).ConfigureAwait(false);
        }
    }
    #endregion

    #region Import/Export

    [LoggerMessage(
        EventId = 9_000,
        Level = LogLevel.Debug,
        Message = "Importing vectors from {ContentType} source {Path}.")]
    private partial void ImportingData(ContentType contentType, string path);

    [LoggerMessage(
        EventId = 9_001,
        Level = LogLevel.Information,
        Message = "Vectors were imported source {Path}.")]
    private partial void ImportedData(string path);

    public async Task ImportDataAsync(string path, bool isDirectory, ContentType contentType, CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity(tags: [new("import.contentType", contentType), new("import.isDirectory", isDirectory)]);
        _rwLock.EnterWriteLock();
        try
        {
            ImportingData(contentType, path);
            IETL etl = EtlFactory.CreateEtl(contentType);
            etl.IsDirectory = isDirectory;
            await etl.ImportDataAsync(path, Vectors, cancellationToken).ConfigureAwait(false);
            ImportedData(path);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            if (_rwLock.IsWriteLockHeld)
            {
                _rwLock.ExitWriteLock();
            }
        }
    }

    [LoggerMessage(
        EventId = 9_010,
        Level = LogLevel.Debug,
        Message = "Exporting {VectorCount} vectors to {Path} with type {ContentType}.")]
    private partial void ExportingData(int vectorCount, string path, ContentType contentType);

    [LoggerMessage(
        EventId = 9_011,
        Level = LogLevel.Information,
        Message = "Vectors were exported to {Path}.")]
    private partial void ExportedData(string path);

    public async Task ExportDataAsync(string path, ContentType contentType, CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity(tags: [new("export.contentType", contentType)]);
        _rwLock.EnterReadLock();
        try
        {
            ExportingData(Vectors.Count, path, contentType);
            IETL etl = EtlFactory.CreateEtl(contentType);
            await etl.ExportDataAsync(Vectors, path, cancellationToken).ConfigureAwait(false);
            ExportedData(path);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
            {
                _rwLock.ExitReadLock();
            }
        }
    }
    #endregion

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // Signal that disposal is starting to stop the indexing thread quickly
                _isDisposing = true;
                
                // Cancel all ongoing operations immediately
                _disposalCancellationTokenSource.Cancel();
                
                _logger.LogInformation("Shutting down VectorDatabase.");
                StopIndexService();
                
                _rwLock.EnterWriteLock();
                try
                {
                    _vectors.Modified -= VectorList_Modified;
                    _vectors.Dispose();
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
                
                _rwLock.Dispose();
                _disposalCancellationTokenSource.Dispose();
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' searchMethod
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// StartActivity is responsible for starting a new activity for tracing and telemetry purposes. 
    /// This function leverages the ActivitySource class from the System.Diagnostics namespace to create and start an activity, 
    /// which can be used to track the execution of code and collect telemetry data.
    /// </summary>
    /// <param name="kind">Specifies the kind of activity (e.g., internal, server, client). The default is ActivityKind.Internal.</param>
    /// <param name="parentContext">Provides the context of the parent activity, if any. The default is an empty context.</param>
    /// <param name="tags">A collection of key-value pairs representing tags to be associated with the activity. The default is null.</param>
    /// <param name="links">A collection of links to other activities. The default is null.</param>
    /// <param name="startTime">The start time of the activity. The default is the current time.</param>
    /// <param name="name"> The name of the activity. The default is the name of the calling method, provided by the CallerMemberName attribute.</param>
    /// <returns></returns>
    private Activity? StartActivity(ActivityKind kind = ActivityKind.Internal, ActivityContext parentContext = default, IEnumerable<KeyValuePair<string, object?>>? tags = null, IEnumerable<ActivityLink>? links = null, DateTimeOffset startTime = default, [CallerMemberName] string name = "")
    {
        if (tags is null)
        {
            tags = _defaultTags;
        }
        else
        {
            tags = [.. tags, .. _defaultTags];
        }

        return _instrumentation.ActivitySource.StartActivity(kind, parentContext, tags, links, startTime, name);
    }
}
