using StreamingServiceServer.Business.Services.Images.Assets;
using Xunit;
namespace StreamingServiceServer.CoverTests;
public sealed class CoverStorageAdmissionTests
{
    [Fact]
    public async Task TransientInitialScanFailureDoesNotPreventRecovery()
    {
        using var fixture = new AssetFixture(); var reads = 0;
        var admission = new CoverStorageAdmission(fixture.Options, () => {
            if (Interlocked.Increment(ref reads) == 1) throw new IOException("fixture storage unavailable");
            return 0;
        }, () => long.MaxValue);
        await Assert.ThrowsAsync<IOException>(() => admission.ReserveAsync(CancellationToken.None));
        using var recovered = await admission.ReserveAsync(CancellationToken.None); Assert.Equal(2, reads);
    }
    [Fact]
    public async Task MutationAccountingRequiresAnInitializedSnapshot()
    {
        using var fixture = new AssetFixture(); var admission = new CoverStorageAdmission(fixture.Options, () => 100, () => long.MaxValue);
        Assert.Throws<InvalidOperationException>(() => admission.RecordMutation(-100));
        await admission.InitializeAsync(CancellationToken.None); admission.RecordMutation(-100);
        using var reservation = await admission.ReserveAsync(CancellationToken.None);
    }
}
