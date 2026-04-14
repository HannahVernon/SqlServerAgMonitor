using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Tests.Models;

public class LsnHelperTests
{
    [Fact]
    public void ParseComponents_ExtractsVlfAndBlock()
    {
        // LSN: VLF=42, Block=12345, Slot=0
        // numeric(25,0) = 42 * 10^15 + 12345 * 10^5 + 0
        decimal lsn = 42_000_000_000_000_000m + 1_234_500_000m;
        var (vlf, block) = LsnHelper.ParseComponents(lsn);

        Assert.Equal(42, vlf);
        Assert.Equal(12345, block);
    }

    [Fact]
    public void ComputeLogBlockDiff_SameVlf_ReturnsBlockDifference()
    {
        // Both in VLF 10, primary block=5000, secondary block=3000
        decimal primary = 10_000_000_000_000_000m + 500_000_000m;
        decimal secondary = 10_000_000_000_000_000m + 300_000_000m;

        long diff = LsnHelper.ComputeLogBlockDiff(primary, secondary);

        Assert.Equal(2000, diff);
    }

    [Fact]
    public void ComputeLogBlockDiff_DifferentVlf_ReturnsMaxBlock()
    {
        // Primary in VLF 12 block=5000, secondary in VLF 10 block=3000
        decimal primary = 12_000_000_000_000_000m + 500_000_000m;
        decimal secondary = 10_000_000_000_000_000m + 300_000_000m;

        long diff = LsnHelper.ComputeLogBlockDiff(primary, secondary);

        Assert.Equal(5000, diff);
    }

    [Fact]
    public void ComputeVlfDiff_SameVlf_ReturnsZero()
    {
        decimal primary = 10_000_000_000_000_000m + 500_000_000m;
        decimal secondary = 10_000_000_000_000_000m + 100_000_000m;

        Assert.Equal(0, LsnHelper.ComputeVlfDiff(primary, secondary));
    }

    [Fact]
    public void ComputeVlfDiff_DifferentVlf_ReturnsAbsoluteDifference()
    {
        decimal primary = 15_000_000_000_000_000m;
        decimal secondary = 10_000_000_000_000_000m;

        Assert.Equal(5, LsnHelper.ComputeVlfDiff(primary, secondary));
    }

    [Fact]
    public void FormatLag_IdenticalLsns_ReturnsInSync()
    {
        decimal lsn = 10_000_000_000_000_000m + 500_000_000m;
        Assert.Equal("in sync", LsnHelper.FormatLag(lsn, lsn));
    }

    [Fact]
    public void FormatLag_SameVlf_ShowsBytesOnly()
    {
        decimal primary = 10_000_000_000_000_000m + 200_000_000m;
        decimal secondary = 10_000_000_000_000_000m + 100_000_000m;

        string result = LsnHelper.FormatLag(primary, secondary);

        Assert.Contains("behind", result);
        Assert.DoesNotContain("VLF", result);
    }

    [Fact]
    public void FormatLag_DifferentVlf_ShowsVlfCount()
    {
        decimal primary = 12_000_000_000_000_000m + 500_000_000m;
        decimal secondary = 10_000_000_000_000_000m + 300_000_000m;

        string result = LsnHelper.FormatLag(primary, secondary);

        Assert.Contains("2 VLFs", result);
        Assert.Contains("behind", result);
    }

    [Fact]
    public void FormatLag_SingleVlfDiff_UsesSingular()
    {
        decimal primary = 11_000_000_000_000_000m + 500_000_000m;
        decimal secondary = 10_000_000_000_000_000m + 300_000_000m;

        string result = LsnHelper.FormatLag(primary, secondary);

        Assert.Contains("1 VLF", result);
        Assert.DoesNotContain("VLFs", result);
    }

    [Fact]
    public void FormatBytes_FormatsSmallValues()
    {
        Assert.Equal("500 bytes", LsnHelper.FormatBytes(500));
    }

    [Fact]
    public void FormatBytes_FormatsKilobytes()
    {
        Assert.Equal("1.0 KB", LsnHelper.FormatBytes(1024));
    }

    [Fact]
    public void FormatBytes_FormatsMegabytes()
    {
        Assert.Equal("1.0 MB", LsnHelper.FormatBytes(1_048_576));
    }

    [Fact]
    public void FormatBytes_FormatsGigabytes()
    {
        Assert.Equal("1.0 GB", LsnHelper.FormatBytes(1_073_741_824));
    }

    [Fact]
    public void FormatAsVlfBlock_Zero_ReturnsAllZeros()
    {
        Assert.Equal("00000000:00000000", LsnHelper.FormatAsVlfBlock(0));
    }

    [Theory]
    [InlineData(0, 0, false, HealthLevel.InSync)]
    [InlineData(500_000, 0, false, HealthLevel.InSync)]
    [InlineData(1_000_001, 0, false, HealthLevel.SlightlyBehind)]
    [InlineData(50_000_000, 0, false, HealthLevel.SlightlyBehind)]
    [InlineData(100_000_001, 0, false, HealthLevel.ModeratelyBehind)]
    [InlineData(0, 1, false, HealthLevel.DangerZone)]
    [InlineData(0, 0, true, HealthLevel.DangerZone)]
    public void FromLogBlockDifference_ReturnsExpectedLevel(long blockDiff, long vlfDiff, bool disconnected, HealthLevel expected)
    {
        Assert.Equal(expected, HealthLevelExtensions.FromLogBlockDifference(blockDiff, vlfDiff, disconnected));
    }

    [Fact]
    public void OldBug_CrossVlfDiff_NoLongerProducesHugeNumber()
    {
        // This is the exact scenario from the user's bug report:
        // Primary VLF=1, Block=257032, Secondary VLF=0, Block=0
        // Old code: diff = (1*10^10 + 257032) - 0 = 10,000,257,032 (nonsensical)
        // New code: block diff = max(257032, 0) = 257032, vlf diff = 1
        decimal primary = 1_000_000_000_000_000m + 25_703_200_000m;
        decimal secondary = 0m;

        long blockDiff = LsnHelper.ComputeLogBlockDiff(primary, secondary);
        long vlfDiff = LsnHelper.ComputeVlfDiff(primary, secondary);

        Assert.Equal(257032, blockDiff);
        Assert.Equal(1, vlfDiff);
        Assert.True(blockDiff < 10_000_000, "Block diff should be a reasonable byte offset, not billions");
    }
}
