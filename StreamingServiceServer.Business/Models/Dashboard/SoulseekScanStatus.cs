namespace StreamingServiceServer.Business.Models.Dashboard;

public static class SoulseekScanStatus
{
    private static volatile bool _isRunning;
    private static volatile string _state = "idle";
    private static volatile string? _error;
    private static int _importedCount;
    private static DateTime? _startedAt;
    private static DateTime? _completedAt;

    public static bool IsRunning => _isRunning;

    public static object Current => new
    {
        state = _state,
        isRunning = _isRunning,
        importedCount = _importedCount,
        error = _error,
        startedAt = _startedAt,
        completedAt = _completedAt
    };

    public static void Start()
    {
        _isRunning = true;
        _state = "running";
        _error = null;
        _importedCount = 0;
        _startedAt = DateTime.UtcNow;
        _completedAt = null;
    }

    public static void Complete(int imported)
    {
        _importedCount = imported;
        _state = "complete";
        _completedAt = DateTime.UtcNow;
        _isRunning = false;
    }

    public static void Fail(string error)
    {
        _error = error;
        _state = "error";
        _completedAt = DateTime.UtcNow;
        _isRunning = false;
    }
}
