namespace StreamingServiceServer.Business.Services.Images.Assets;

internal sealed class CoverStorageAdmission(CoverAssetOptions configuration, Func<long>? readStoredBytes = null, Func<long>? readAvailableBytes = null)
{
    private const long MetadataReservationBytes = 1048576;
    private readonly object synchronization = new();
    private Task<long>? initialSize;
    private long committedBytes;
    private long reservedBytes;
    public async Task<long> InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(configuration.Path);
        Task<long> initialization;
        lock (synchronization) initialization = initialSize ??= Task.Run(() => readStoredBytes?.Invoke() ?? Directory.EnumerateFiles(configuration.Path)
            .Where(path => !path.EndsWith(".partial", StringComparison.Ordinal))
            .Sum(path => { try { return new FileInfo(path).Length; } catch (FileNotFoundException) { return 0; } }));
        try { return await initialization.WaitAsync(cancellationToken); }
        catch
        {
            lock (synchronization) if (initialization.IsFaulted && ReferenceEquals(initialSize, initialization)) initialSize = null;
            throw;
        }
    }
    public async Task<IDisposable> ReserveAsync(CancellationToken cancellationToken)
    {
        var baseline = await InitializeAsync(cancellationToken);
        var requestedBytes = checked(configuration.MaximumInputBytes * 3 + configuration.MaximumOutputBytes * 3 + MetadataReservationBytes);
        var availableBytes = readAvailableBytes?.Invoke() ?? new DriveInfo(configuration.Path).AvailableFreeSpace;
        lock (synchronization)
        {
            if (baseline + committedBytes + reservedBytes + requestedBytes > configuration.MaximumStoredBytes
                || availableBytes - reservedBytes - requestedBytes < configuration.MinimumFreeBytes)
                throw new IOException("Cover storage has insufficient reserved capacity.");
            reservedBytes += requestedBytes;
        }
        return new Reservation(this, requestedBytes);
    }
    public void RecordMutation(long differenceBytes)
    {
        lock (synchronization)
        {
            if (initialSize?.IsCompletedSuccessfully != true) throw new InvalidOperationException("Initialize cover storage before recording mutations.");
            committedBytes += differenceBytes;
        }
    }
    private sealed class Reservation(CoverStorageAdmission owner, long reservedBytes) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.synchronization) owner.reservedBytes -= reservedBytes;
        }
    }
}
