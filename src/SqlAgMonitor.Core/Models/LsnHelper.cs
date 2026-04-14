namespace SqlAgMonitor.Core.Models;

/// <summary>
/// Utility for parsing and comparing SQL Server Log Sequence Numbers stored as numeric(25,0).
/// 
/// The numeric(25,0) representation packs three components as decimal digits:
///   VLF_seq (10 digits) × 10^15  +  Block_offset (10 digits) × 10^5  +  Slot (5 digits)
///
/// For last_hardened_lsn, the slot is always 00000 because hardening operates
/// at the log-block level. We strip it for display and comparison.
/// </summary>
public static class LsnHelper
{
    private const decimal SlotDivisor = 100_000m;
    private const long BlockDivisor = 10_000_000_000L;

    /// <summary>
    /// Extracts the VLF sequence number and block offset from a numeric(25,0) LSN.
    /// </summary>
    public static (long Vlf, long Block) ParseComponents(decimal numericLsn)
    {
        long stripped = (long)decimal.Truncate(numericLsn / SlotDivisor);
        long vlf = stripped / BlockDivisor;
        long block = stripped % BlockDivisor;
        return (vlf, block);
    }

    /// <summary>
    /// Formats a numeric(25,0) LSN as "VVVVVVVV:BBBBBBBB" hex notation (slot stripped).
    /// Matches SQL Server's standard LSN display format minus the slot.
    /// </summary>
    public static string FormatAsVlfBlock(decimal numericLsn)
    {
        if (numericLsn == 0) return "00000000:00000000";
        var (vlf, block) = ParseComponents(numericLsn);
        return $"{vlf:X8}:{block:X8}";
    }

    /// <summary>
    /// Computes the block-offset difference between two LSNs within their
    /// respective VLFs. When both LSNs share the same VLF, the result is
    /// the byte-offset difference in the transaction log. When the VLFs
    /// differ, only the block-offset difference within the primary's VLF
    /// is meaningful; the VLF gap is tracked separately via
    /// <see cref="ComputeVlfDiff"/>.
    /// </summary>
    public static long ComputeLogBlockDiff(decimal primaryLsn, decimal secondaryLsn)
    {
        var (primaryVlf, primaryBlock) = ParseComponents(primaryLsn);
        var (secondaryVlf, secondaryBlock) = ParseComponents(secondaryLsn);

        if (primaryVlf == secondaryVlf)
            return Math.Abs(primaryBlock - secondaryBlock);

        // Different VLFs — block offsets are not directly comparable because
        // VLF sizes vary. Return the secondary's block offset distance from
        // the end of its VLF (unknown) plus the primary's offset — but since
        // we don't know VLF sizes, just return the primary's block offset as
        // a lower-bound estimate of the intra-VLF lag.
        return Math.Max(primaryBlock, secondaryBlock);
    }

    /// <summary>
    /// Returns the absolute VLF sequence number difference between two LSNs.
    /// Zero means both LSNs are in the same VLF.
    /// </summary>
    public static long ComputeVlfDiff(decimal primaryLsn, decimal secondaryLsn)
    {
        var (primaryVlf, _) = ParseComponents(primaryLsn);
        var (secondaryVlf, _) = ParseComponents(secondaryLsn);
        return Math.Abs(primaryVlf - secondaryVlf);
    }

    /// <summary>
    /// Produces a human-readable description of the lag between two LSNs.
    /// </summary>
    public static string FormatLag(decimal primaryLsn, decimal secondaryLsn)
    {
        if (primaryLsn == secondaryLsn) return "in sync";

        var vlfDiff = ComputeVlfDiff(primaryLsn, secondaryLsn);
        var blockDiff = ComputeLogBlockDiff(primaryLsn, secondaryLsn);

        if (vlfDiff == 0)
            return $"{FormatBytes(blockDiff)} behind";

        var vlfLabel = vlfDiff == 1 ? "1 VLF" : $"{vlfDiff:N0} VLFs";
        if (blockDiff > 0)
            return $"{vlfLabel} + {FormatBytes(blockDiff)} behind";
        return $"{vlfLabel} behind";
    }

    /// <summary>
    /// Formats a byte count as a human-readable string (B, KB, MB, GB).
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        const long KB = 1_024;
        const long MB = 1_024 * KB;
        const long GB = 1_024 * MB;

        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F1} GB",
            >= MB => $"{bytes / (double)MB:F1} MB",
            >= KB => $"{bytes / (double)KB:F1} KB",
            _ => $"{bytes:N0} bytes"
        };
    }
}
