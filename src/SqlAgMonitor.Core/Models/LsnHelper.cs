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
    /// Computes a meaningful difference between two LSNs by stripping the slot
    /// component first. The result represents the combined VLF + block offset
    /// distance. Within the same VLF, this is the byte-offset difference in the
    /// transaction log. Across VLF boundaries, the VLF difference dominates
    /// (each VLF boundary adds ~10^10 to the result).
    /// </summary>
    public static decimal ComputeLogBlockDiff(decimal primaryLsn, decimal secondaryLsn)
    {
        decimal primaryStripped = decimal.Truncate(primaryLsn / SlotDivisor);
        decimal secondaryStripped = decimal.Truncate(secondaryLsn / SlotDivisor);
        return Math.Abs(primaryStripped - secondaryStripped);
    }
}
