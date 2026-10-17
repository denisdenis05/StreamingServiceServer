using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class CoverRendererBatchTests
{
    [Fact]
    public async Task ThreeVariantsStartOneDecoderProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "cover-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "source.png");
            var generate = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=800x600", "-frames:v", "1", "-threads", "1", input })
                generate.ArgumentList.Add(argument);
            using (var process = Process.Start(generate)!)
            { var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await error); }
            var executable = Path.Combine(directory, "renderer");
            var counter = Path.Combine(directory, "starts");
            await File.WriteAllTextAsync(executable, $"#!/bin/sh\necho started >> '{counter}'\nexec ffmpeg \"$@\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var renderer = new FfmpegCoverImageRenderer(Options.Create(new CoverAssetOptions { RendererExecutable = executable }));
            var outputs = new[] { new CoverRenderOutput(Path.Combine(directory,"full.jpg"),1400),
                new CoverRenderOutput(Path.Combine(directory,"small.jpg"),500),new CoverRenderOutput(Path.Combine(directory,"thumbnail.jpg"),250) };
            await renderer.RenderVariantsAsync(input, outputs, CancellationToken.None);
            Assert.Single(await File.ReadAllLinesAsync(counter));
            Assert.All(outputs, output => Assert.True(new FileInfo(output.Path).Length > 0));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RendererDeadlineKillsAndReapsTheChildProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "cover-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var header = new byte[24];
            new byte[] {137,80,78,71,13,10,26,10}.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 1);
            var input = Path.Combine(directory,"header.png");
            await File.WriteAllBytesAsync(input, header);
            var executable = Path.Combine(directory,"renderer");
            await File.WriteAllTextAsync(executable,"#!/bin/sh\nsleep 60\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var renderer = new FfmpegCoverImageRenderer(Options.Create(new CoverAssetOptions
                { RendererExecutable = executable, RenderTimeout = TimeSpan.FromMilliseconds(100) }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(input, input + ".jpg", 250,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { Directory.Delete(directory, true); }
    }
}
