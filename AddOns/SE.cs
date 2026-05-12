// AddOns/SE.cs
// SightEngine — NT8 BookMap data layer
// Adapted from itchy5/NT8-OrderFlowKit for NT8 8.1.6.3 / SharpDX 2.6.3
// All original logic preserved; thread-safety and naming kept intact.

using System;
using System.Collections.Generic;
using NinjaTrader.Data;

namespace SightEngine
{
    // ────────────────────────────────────────────────────────────────────────────
    #region Math2 — static utility helpers

    public static class Math2
    {
        /// <summary>Returns what percentage <paramref name="part"/> is of <paramref name="total"/>.</summary>
        public static double Percent(double total, double part)
        {
            if (total == 0d) return 0d;
            return (part / total) * 100.0d;
        }

        public static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }

        public static float Clampf(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }

        public static long ClampLong(long value, long min, long max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }

    #endregion

    // ────────────────────────────────────────────────────────────────────────────
    #region VolumeNode — bid / ask / delta aggregation per price level

    public class VolumeNode
    {
        public long Bid;
        public long Ask;
        public long Total;
        public long Delta;  // Ask - Bid (positive = buy pressure)

        public void Reset()
        {
            Bid   = 0;
            Ask   = 0;
            Total = 0;
            Delta = 0;
        }

        /// <summary>Record a sell-side (bid) hit.</summary>
        public void AddBid(long qty)
        {
            if (qty <= 0) return;
            Bid   += qty;
            Total += qty;
            Delta -= qty;
        }

        /// <summary>Record a buy-side (ask) hit.</summary>
        public void AddAsk(long qty)
        {
            if (qty <= 0) return;
            Ask   += qty;
            Total += qty;
            Delta += qty;
        }
    }

    #endregion

    // ────────────────────────────────────────────────────────────────────────────
    #region WyckoffBar — per-bar OHLC + volume summary

    public class WyckoffBar
    {
        public double   Open;
        public double   High;
        public double   Low;
        public double   Close;
        public DateTime Time;
        public long     BidVolume;
        public long     AskVolume;
        public long     TotalVolume;
        public long     Delta;     // AskVolume - BidVolume
        public int      BarIndex;
    }

    #endregion

    // ────────────────────────────────────────────────────────────────────────────
    #region WyckoffBars — collection of per-bar summaries

    public class WyckoffBars
    {
        private readonly object       _syncRoot = new object();
        private readonly List<WyckoffBar> _bars = new List<WyckoffBar>();

        public List<WyckoffBar> GetSnapshot()
        {
            lock (_syncRoot)
                return new List<WyckoffBar>(_bars);
        }

        public void AddOrUpdate(WyckoffBar bar)
        {
            if (bar == null) return;
            lock (_syncRoot)
            {
                for (int i = _bars.Count - 1; i >= 0; i--)
                {
                    if (_bars[i].BarIndex == bar.BarIndex)
                    {
                        _bars[i] = bar;
                        return;
                    }
                }
                _bars.Add(bar);
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
                _bars.Clear();
        }
    }

    #endregion

    // ────────────────────────────────────────────────────────────────────────────
    #region BookMap — live DOM (Depth of Market) snapshot

    /// <summary>
    /// Tracks the current state of the Level 2 order book (pending resting orders).
    /// Bid levels = buyers waiting; Ask levels = sellers waiting.
    /// </summary>
    public class BookMap
    {
        private readonly object _syncRoot = new object();

        // Price → resting size
        private readonly Dictionary<double, long> _bidLevels = new Dictionary<double, long>();
        private readonly Dictionary<double, long> _askLevels = new Dictionary<double, long>();

        public long MaxBidSize { get; private set; }
        public long MaxAskSize { get; private set; }
        public long MaxSize    { get { return Math.Max(MaxBidSize, MaxAskSize); } }

        /// <summary>Returns a snapshot of bid levels (thread-safe).</summary>
        public Dictionary<double, long> GetBidLevels()
        {
            lock (_syncRoot)
                return new Dictionary<double, long>(_bidLevels);
        }

        /// <summary>Returns a snapshot of ask levels (thread-safe).</summary>
        public Dictionary<double, long> GetAskLevels()
        {
            lock (_syncRoot)
                return new Dictionary<double, long>(_askLevels);
        }

        /// <summary>Called from OnMarketDepth — updates the resting-order book.</summary>
        public void onMarketDepth(MarketDepthEventArgs e)
        {
            if (e == null) return;

            lock (_syncRoot)
            {
                bool isBid = e.MarketDataType == MarketDataType.Bid;
                bool isAsk = e.MarketDataType == MarketDataType.Ask;
                if (!isBid && !isAsk) return;

                var levels = isBid ? _bidLevels : _askLevels;

                long RecomputeMaxSize()
                {
                    long maxSize = 0;
                    foreach (var level in levels)
                        if (level.Value > maxSize)
                            maxSize = level.Value;

                    return maxSize;
                }

                long previousVolume = 0;
                bool hadPreviousLevel = levels.TryGetValue(e.Price, out previousVolume);
                bool touchedCurrentMax =
                    hadPreviousLevel &&
                    ((isBid && previousVolume >= MaxBidSize) || (isAsk && previousVolume >= MaxAskSize));

                if (e.Operation == Operation.Remove || e.Volume <= 0)
                {
                    levels.Remove(e.Price);

                    if (touchedCurrentMax)
                    {
                        if (isBid) MaxBidSize = RecomputeMaxSize();
                        if (isAsk) MaxAskSize = RecomputeMaxSize();
                    }
                }
                else
                {
                    levels[e.Price] = e.Volume;

                    if (isBid)
                    {
                        if (e.Volume > MaxBidSize)
                            MaxBidSize = e.Volume;
                        else if (touchedCurrentMax && e.Volume < previousVolume)
                            MaxBidSize = RecomputeMaxSize();
                    }

                    if (isAsk)
                    {
                        if (e.Volume > MaxAskSize)
                            MaxAskSize = e.Volume;
                        else if (touchedCurrentMax && e.Volume < previousVolume)
                            MaxAskSize = RecomputeMaxSize();
                    }
                }
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _bidLevels.Clear();
                _askLevels.Clear();
                MaxBidSize = 0;
                MaxAskSize = 0;
            }
        }
    }

    #endregion

    // ────────────────────────────────────────────────────────────────────────────
    #region OrderBookLadder — DOM sidebar (current bid/ask per level)

    /// <summary>
    /// Tracks the full depth of market for sidebar rendering.
    /// Unlike BookMap which tracks pending orders as the heatmap fills in over time,
    /// OrderBookLadder always reflects the current real-time DOM snapshot.
    /// </summary>
    public class OrderBookLadder
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<double, long> _bidLevels = new Dictionary<double, long>();
        private readonly Dictionary<double, long> _askLevels = new Dictionary<double, long>();

        public long MaxBidSize { get; private set; }
        public long MaxAskSize { get; private set; }

        public Dictionary<double, long> GetBidLevels()
        {
            lock (_syncRoot)
                return new Dictionary<double, long>(_bidLevels);
        }

        public Dictionary<double, long> GetAskLevels()
        {
            lock (_syncRoot)
                return new Dictionary<double, long>(_askLevels);
        }

        /// <summary>Called from OnMarketDepth — same as BookMap but kept separate for sidebar rendering.</summary>
        public void AddOrder(double lastPrice, MarketDepthEventArgs e)
        {
            if (e == null) return;

            lock (_syncRoot)
            {
                bool isBid = e.MarketDataType == MarketDataType.Bid;
                bool isAsk = e.MarketDataType == MarketDataType.Ask;
                if (!isBid && !isAsk) return;

                var levels = isBid ? _bidLevels : _askLevels;

                if (e.Operation == Operation.Remove || e.Volume <= 0)
                {
                    levels.Remove(e.Price);
                }
                else
                {
                    levels[e.Price] = e.Volume;
                    if (isBid && e.Volume > MaxBidSize) MaxBidSize = e.Volume;
                    if (isAsk && e.Volume > MaxAskSize) MaxAskSize = e.Volume;
                }
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _bidLevels.Clear();
                _askLevels.Clear();
                MaxBidSize = 0;
                MaxAskSize = 0;
            }
        }
    }

    #endregion

    // ────────────────────────────────────────────────────────────────────────────
    #region PriceLadder — cumulative executed-trade volume per price level

    /// <summary>
    /// Accumulates trade-print volume per price level. Used to compute the Point of
    /// Control (price with the most cumulative volume) and the cumulative heat column.
    /// </summary>
    public class PriceLadder
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<double, VolumeNode> _levels = new Dictionary<double, VolumeNode>();

        public long MaxTotal  { get; private set; }
        public long MaxBid    { get; private set; }
        public long MaxAsk    { get; private set; }

        public Dictionary<double, VolumeNode> GetSnapshot()
        {
            lock (_syncRoot)
            {
                var copy = new Dictionary<double, VolumeNode>(_levels.Count);
                foreach (var kv in _levels)
                {
                    var n = new VolumeNode();
                    n.Bid   = kv.Value.Bid;
                    n.Ask   = kv.Value.Ask;
                    n.Total = kv.Value.Total;
                    n.Delta = kv.Value.Delta;
                    copy[kv.Key] = n;
                }
                return copy;
            }
        }

        public void AddTrade(double price, bool isBuy, long volume)
        {
            if (volume <= 0 || price <= 0) return;

            lock (_syncRoot)
            {
                VolumeNode node;
                if (!_levels.TryGetValue(price, out node))
                {
                    node = new VolumeNode();
                    _levels[price] = node;
                }

                if (isBuy)
                    node.AddAsk(volume);
                else
                    node.AddBid(volume);

                if (node.Total > MaxTotal) MaxTotal = node.Total;
                if (node.Ask   > MaxAsk)   MaxAsk   = node.Ask;
                if (node.Bid   > MaxBid)   MaxBid   = node.Bid;
            }
        }

        /// <summary>Finds the price level with the highest cumulative volume (Point of Control).</summary>
        public double GetPOCPrice()
        {
            lock (_syncRoot)
            {
                double pocPrice = 0d;
                long   maxVol  = 0L;

                foreach (var kv in _levels)
                {
                    if (kv.Value.Total > maxVol)
                    {
                        maxVol   = kv.Value.Total;
                        pocPrice = kv.Key;
                    }
                }
                return pocPrice;
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _levels.Clear();
                MaxTotal = 0;
                MaxBid   = 0;
                MaxAsk   = 0;
            }
        }
    }

    #endregion

    // ────────────────────────────────────────────────────────────────────────────
    #region MarketOrderEntry + MarketOrderLadder — aggressive market-order tracking

    public class MarketOrderEntry
    {
        public DateTime Time;
        public double   Price;
        public long     Volume;
        public bool     IsBuy;
        public int      BarIndex;
    }

    /// <summary>
    /// Stores recent aggressive market orders for bubble rendering.
    /// Capped at <see cref="MaxEntries"/> entries to bound memory usage.
    /// </summary>
    public class MarketOrderLadder
    {
        private const int MaxEntries = 20000;

        private readonly object _syncRoot = new object();
        private readonly List<MarketOrderEntry> _orders = new List<MarketOrderEntry>(512);

        public List<MarketOrderEntry> GetSnapshot()
        {
            lock (_syncRoot)
                return new List<MarketOrderEntry>(_orders);
        }

        public void AddOrder(double price, bool isBuy, long volume, DateTime time, int barIndex)
        {
            if (volume <= 0 || price <= 0) return;
            lock (_syncRoot)
            {
                _orders.Add(new MarketOrderEntry
                {
                    Time     = time,
                    Price    = price,
                    Volume   = volume,
                    IsBuy    = isBuy,
                    BarIndex = barIndex,
                });

                if (_orders.Count > MaxEntries)
                    _orders.RemoveRange(0, _orders.Count - MaxEntries);
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
                _orders.Clear();
        }
    }

    #endregion
}
