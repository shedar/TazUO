using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassicUO.Frontend;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Frontend;

public sealed class FrontendInstrumentationTests
{
    [Fact]
    public void WritesCompletedGeneratedJsonReportWithTransportAndHotspotMetrics()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tazuo-frontend-instrumentation-{Guid.NewGuid():N}"
        );
        string path = Path.Combine(directory, "report.json");

        try
        {
            FrontendOptions options = new()
            {
                Mode = FrontendMode.WebSocket,
                FramesPerSecond = 12,
                FrameFormat = FrontendFrameFormat.Png,
                InstrumentationPath = path
            };

            using (FrontendInstrumentation instrumentation = FrontendInstrumentation.Create(options))
            {
                instrumentation.BeginProfilerFrame();
                long updateStarted = instrumentation.BeginSample();

                using (FrontendInstrumentation.Measure("ui.gump", "LoginGump"))
                {
                }

                instrumentation.RecordUpdate(updateStarted, attached: true);

                long drawStarted = instrumentation.BeginSample();
                FrontendPresentResult present = new(
                    EncodedBytes: 4096,
                    CaptureMilliseconds: 2.5,
                    Enqueued: true,
                    Dropped: true
                );
                instrumentation.RecordDraw(
                    drawStarted,
                    present,
                    sprites: 120,
                    flushes: 4,
                    textureSwitches: 17,
                    renderedObjects: 32,
                    attached: true
                );
            }

            string json = File.ReadAllText(path);
            FrontendInstrumentationReport report = JsonSerializer.Deserialize(
                json,
                FrontendJsonContext.Default.FrontendInstrumentationReport
            );

            report.Should().NotBeNull();
            report.ReportVersion.Should().Be(1);
            report.Mode.Should().Be(nameof(FrontendMode.WebSocket));
            report.FrameFormat.Should().Be(nameof(FrontendFrameFormat.Png));
            report.TargetFramesPerSecond.Should().Be(12);
            report.Completed.Should().BeTrue();
            report.Attached.Should().BeTrue();
            report.Totals.Updates.Should().Be(1);
            report.Totals.Draws.Should().Be(1);
            report.Totals.EncodedFrames.Should().Be(1);
            report.Totals.EnqueuedFrames.Should().Be(1);
            report.Totals.DroppedFrames.Should().Be(1);
            report.Totals.EncodedBytes.Should().Be(4096);
            report.Totals.AverageCaptureMilliseconds.Should().Be(2.5);
            report.Totals.PeakSpritesPerDraw.Should().Be(120);
            report.Totals.PeakFlushesPerDraw.Should().Be(4);
            report.Totals.PeakTextureSwitchesPerDraw.Should().Be(17);
            report.Totals.PeakRenderedObjects.Should().Be(32);
            report.Hotspots.Single().Category.Should().Be("ui.gump");
            report.Hotspots.Single().Name.Should().Be("LoginGump");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
