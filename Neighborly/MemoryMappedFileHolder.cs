using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;

namespace Neighborly;

internal class MemoryMappedFileHolder : IDisposable
{
    private static readonly Guid s_tempFilePrefix = Guid.NewGuid();
    private static int s_tempFileCounter = 0;
    private readonly long _capacity;
    private MemoryMappedFile _file;
    private MemoryMappedViewStream _stream;
    private bool _disposedValue;
    private string _fileName;
    public string FileName => _fileName;
    public long Capacity => _capacity;

    public MemoryMappedFileHolder(long capacity)
    {
        _capacity = capacity;

        Reset();
    }

    public MemoryMappedViewStream Stream => _stream;

    [MemberNotNull(nameof(_fileName), nameof(_file), nameof(_stream))]
    public void Reset()
    {
        int fileNumber = Interlocked.Increment(ref s_tempFileCounter);
        _fileName = Path.Combine(Path.GetTempPath(), $"Neighborly_{s_tempFilePrefix}_{fileNumber}.tmp");
        MemoryMappedFileServices.WinFileAlloc(_fileName);
        double capacityTiB = _capacity / (1024.0 * 1024.0 * 1024.0 * 1024.0);
        Logging.Logger.Information("Creating temporary file: {FileName}, size {Capacity} TiB", _fileName, capacityTiB);
        try
        {
            _file = MemoryMappedFile.CreateFromFile(_fileName, FileMode.OpenOrCreate, null, _capacity);
            _stream = _file.CreateViewStream();
        }
        catch (IOException ex)
        {
            if (File.Exists(_fileName))
            {
                File.Delete(_fileName);
                Logging.Logger.Error(ex, "Error occurred while trying to create file ({FileName}). File was successfully deleted. Error: {ErrorMessage}", _fileName, ex.Message);
            }
            else
            {
                Logging.Logger.Error(ex, "Error occurred while trying to create file ({FileName}). Error: {ErrorMessage}", _fileName, ex.Message);
            }
            throw;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                DisposeStreams();
                try
                {
                    if (File.Exists(_fileName))
                    {
                        File.Delete(_fileName);
                        Logging.Logger.Information("Deleted temporary file: {FileName}", _fileName);
                    }
                    else
                    {
                        Logging.Logger.Warning("Temporary file not found: {FileName}", _fileName);
                    }
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error(ex, "Failed to delete temporary file: {FileName}", _fileName);
                }
            }

            _disposedValue = true;
        }
    }

    ~MemoryMappedFileHolder()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public void DisposeStreams()
    {
        _stream?.Dispose();
        _file?.Dispose();
    }
}
