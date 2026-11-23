// PulseOps implementation for millisecond durations, nearest-rank quantiles with observed-maximum clamping
// and strictly validated binary encoding stored in SQLite.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PulseOps.Server;

/// <summary>
/// Mergeable log-scale latency histogram. Bucket i &gt; 0 covers (Min·γ^(i-1), Min·γ^i] milliseconds and reports its
/// midpoint Min·γ^i·2/(1+γ), so every duration above 1 µs is reported within (γ-1)/(γ+1) ≈ 1.96% relative error.
/// Durations at or below 1 µs share bucket 0. Merging adds counts, so rollups combine without re-reading raw telemetry.
/// Intake caps durations at 86,400,000 ms (index 643), so histograms built from stored telemetry use at most 644 buckets.
/// </summary>
internal sealed class LatencyHistogram
{
    public const double MinimumMs = 0.001;
    public const double Gamma = 1.04;
    public const int MaxIndex = 4096;
    public const int MaxBuckets = 1024;
    private const byte FormatVersion = 1;
    private const int PairBytes = 6;
    private static readonly double LogGamma = Math.Log(Gamma);
    private readonly Dictionary<int, long> _counts;

    public LatencyHistogram() : this(0) { }
    private LatencyHistogram(int capacity) => _counts = new(capacity);

    public long Count { get; private set; }

    public static int Index(double ms) =>
        !(ms > MinimumMs) ? 0 : (int)Math.Min(MaxIndex, Math.Ceiling(Math.Log(ms / MinimumMs) / LogGamma));

    public static double Value(int index) =>
        index <= 0 ? MinimumMs : MinimumMs * Math.Pow(Gamma, index) * 2 / (1 + Gamma);

    /// <summary>Adds one duration. Non-finite values are ignored and negative values count as zero.</summary>
    public void Add(double ms)
    {
        if (double.IsFinite(ms))
            Increment(Index(Math.Max(0, ms)), 1);
    }

    public void Merge(LatencyHistogram other)
    {
        foreach (var (index, count) in other._counts)
            Increment(index, count);
    }

    private void Increment(int index, long count)
    {
        CollectionsMarshal.GetValueRefOrAddDefault(_counts, index, out _) += count;
        Count += count;
    }

    /// <summary>
    /// The p-quantile (0 ≤ p ≤ 1) as a bucket midpoint, clamped to <paramref name="observedMax"/> when given, or null when
    /// empty. PulseOps uses nearest rank, the first bucket whose cumulative
    /// count reaches max(1, ceil(p·n)), so long-range values align with the exact nearest-rank percentiles that the raw-data
    /// queries compute with (n·P+99)/100.
    /// </summary>
    public double? Quantile(double p, double? observedMax)
    {
        if (!(p >= 0 && p <= 1))
            throw new ArgumentOutOfRangeException(nameof(p));
        if (Count == 0)
            return null;
        long target = Rank(p, Count);
        long seen = 0;
        foreach (var index in SortedIndices())
        {
            seen += _counts[index];
            if (seen < target)
                continue;
            double value = Value(index);
            return observedMax is double maximum && maximum < value ? maximum : value;
        }
        throw new UnreachableException();
    }

    /// <summary>Nearest rank max(1, ceil(p·n)). The decimal conversion keeps 15 significant digits, so p = 0.95 ranks
    /// exactly like the integer form (n·95+99)/100 instead of inheriting the binary approximation of 0.95.</summary>
    internal static long Rank(double p, long count) => Math.Max(1, (long)Math.Ceiling((decimal)p * count));

    /// <summary>Byte 1 (format version), then ascending (uint16 LE index, uint32 LE count) pairs. A bucket count above
    /// uint.MaxValue saturates; that needs roughly 50,000 events per second into one rollup key for a whole day.</summary>
    public byte[] Serialize()
    {
        if (_counts.Count > MaxBuckets)
            throw new InvalidOperationException("The latency histogram exceeds the serializable bucket limit.");
        var data = new byte[1 + _counts.Count * PairBytes];
        data[0] = FormatVersion;
        var span = data.AsSpan(1);
        foreach (var index in SortedIndices())
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)index);
            BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)Math.Min(_counts[index], uint.MaxValue));
            span = span[PairBytes..];
        }
        return data;
    }

    /// <summary>Parses <see cref="Serialize"/> output. Any other version, length, ordering, empty bucket, index or bucket
    /// count throws <see cref="FormatException"/>; messages never echo the bytes.</summary>
    public static LatencyHistogram Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data[0] != FormatVersion || (data.Length - 1) % PairBytes != 0 ||
            (data.Length - 1) / PairBytes > MaxBuckets)
            throw new FormatException("Unsupported latency histogram encoding.");
        var histogram = new LatencyHistogram((data.Length - 1) / PairBytes);
        int previous = -1;
        for (var span = data[1..]; !span.IsEmpty; span = span[PairBytes..])
        {
            int index = BinaryPrimitives.ReadUInt16LittleEndian(span);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[2..]);
            if (index <= previous || index > MaxIndex || count == 0)
                throw new FormatException("Invalid latency histogram bucket.");
            histogram._counts.Add(index, count);
            // At most 1,024 uint32 counts, so the total always fits; checked documents the invariant.
            histogram.Count = checked(histogram.Count + count);
            previous = index;
        }
        return histogram;
    }

    private int[] SortedIndices()
    {
        var indices = _counts.Keys.ToArray();
        Array.Sort(indices);
        return indices;
    }
}
