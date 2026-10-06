using System.Text;
using SeekClaw.Runtime.Tools;
using Xunit;

namespace SeekClaw.Tests;

public sealed class HeadTailBufferTests
{
    [Fact]
    public void UnderCapacity_PreservesAllContentWithoutOmission()
    {
        var buffer = new HeadTailBuffer(maxBytes: 1024);
        var input = Encoding.UTF8.GetBytes("Hello, world! This is a test.");
        buffer.PushChunk(input, 0, input.Length);

        var output = buffer.GetFormattedText(Encoding.UTF8);
        Assert.Equal("Hello, world! This is a test.", output);
        Assert.Equal(0, buffer.OmittedBytes);
        Assert.Equal(input.Length, buffer.TotalBytesObserved);
    }

    [Fact]
    public void OverCapacity_RetainsHeadAndTailAndOmitsMiddle()
    {
        // 100 bytes budget: 50 bytes head, 50 bytes tail
        var buffer = new HeadTailBuffer(maxBytes: 100);

        var headContent = new string('A', 50);
        var middleContent = new string('B', 200);
        var tailContent = new string('C', 50);

        var fullInput = Encoding.UTF8.GetBytes(headContent + middleContent + tailContent);
        buffer.PushChunk(fullInput, 0, fullInput.Length);

        var output = buffer.GetFormattedText(Encoding.UTF8);

        Assert.StartsWith(headContent, output);
        Assert.EndsWith(tailContent, output);
        Assert.Contains("[... 200 bytes omitted ...]", output);
        Assert.Equal(200, buffer.OmittedBytes);
        Assert.Equal(300, buffer.TotalBytesObserved);
    }

    [Fact]
    public void MultipleChunkStreams_CorrectlyMaintainsSlidingTail()
    {
        var buffer = new HeadTailBuffer(maxBytes: 60); // 30 head, 30 tail

        var chunk1 = Encoding.UTF8.GetBytes("HEAD_PREFIX_012345678901234567"); // 30 bytes
        var chunk2 = Encoding.UTF8.GetBytes("MIDDLE_DATA_1_"); // 14 bytes
        var chunk3 = Encoding.UTF8.GetBytes("MIDDLE_DATA_2_"); // 14 bytes
        var chunk4 = Encoding.UTF8.GetBytes("TAIL_SUFFIX_012345678901234567"); // 30 bytes

        buffer.PushChunk(chunk1, 0, chunk1.Length);
        buffer.PushChunk(chunk2, 0, chunk2.Length);
        buffer.PushChunk(chunk3, 0, chunk3.Length);
        buffer.PushChunk(chunk4, 0, chunk4.Length);

        var output = buffer.GetFormattedText(Encoding.UTF8);

        Assert.StartsWith("HEAD_PREFIX_012345678901234567", output);
        Assert.EndsWith("TAIL_SUFFIX_012345678901234567", output);
        Assert.Contains("omitted", output);
        Assert.Equal(28, buffer.OmittedBytes);
    }
}
