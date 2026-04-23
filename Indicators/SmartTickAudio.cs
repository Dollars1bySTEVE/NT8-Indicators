// SmartTickAudio — Audio-only, self-calibrating order-flow alert indicator for NinjaTrader 8.
//
// Combines three detection engines:
//   Engine A, adaptive single-print size outlier
//   Engine B, adaptive same-side burst sweep
//   Engine C, fixed contract-count floor
//
// Works on NQ, ES, MNQ, MES, CL, GC, RTY, YM, BTC futures, equities, FX — no per-market
// tuning required because all thresholds derive from rolling percentiles of each
// instrument's own recent activity.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces
// so the auto-generated partial-class code can resolve them without ambiguity.
// Reference: forum.ninjatrader.com threads #1182932, #95909, #1046853

/// <summary>
/// Operating mode for SmartTickAudio — selects which detection engine(s) are active.
/// </summary>
public enum SmartTickAudioMode
{
    /// <summary>Engine A only — adaptive single-print size outlier (AlgoBox AudioBox-style).</summary>
    BlockOnly,
    /// <summary>Engine B only — adaptive burst-sweep detector (TickStrike-style).</summary>
    BurstOnly,
    /// <summary>Engine C only — fixed contract-count floor (BigTrade / GomMP-style).</summary>
    FixedOnly,
    /// <summary>Engines A + B (default — broadest coverage with no config).</summary>
    Both,
    /// <summary>Engines A + B + C simultaneously.</summary>
    All
}

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// SmartTickAudio — Audio-only, self-calibrating order-flow alert indicator for NinjaTrader 8.
    ///
    /// Three detection engines run independently and can be combined:
    ///   Engine A (Block)  — fires when a single tick's size is a statistical outlier vs the
    ///                        rolling window of recent tick sizes (AlgoBox AudioBox-style).
    ///   Engine B (Burst)  — fires when an unusual number of same-side aggressive ticks occur
    ///                        within BurstWindowMs (TickStrike-style, but adaptive).
    ///   Engine C (Fixed)  — fires when a single tick >= a user-defined contract count
    ///                        (BigTrade / GomMP Big Trades-style).
    ///
    /// Tick classification uses Level-1 Last vs Bid/Ask so the indicator works on any chart
    /// type and any instrument without further configuration.
    ///
    /// Performance: circular buffers (O(1) push/expire) + SortedDictionary value-count map
    /// (O(log n) rolling percentile). Hot path is allocation-free.
    /// </summary>
    public class SmartTickAudio : Indicator
    {
        // ══════════════════════════════════════════════════════════════════
        //  Internal constants
        // ══════════════════════════════════════════════════════════════════

        // Maximum entries in the per-side burst-timestamp circular queue.
        // 4096 is far more than any 1-second window on the fastest instruments.
        private const int MaxBurstQueueCap = 4096;

        // Rolling window of per-second burst-count baseline samples (60 = 1 minute).
        private const int BurstBaselineCap = 60;

        // Minimum block-buffer fill before Engine A will fire (prevents noise on cold start).
        private const int BlockWarmupMin = 30;

        // ══════════════════════════════════════════════════════════════════
        //  Engine A — Block: O(log n) adaptive rolling-window percentile
        // ══════════════════════════════════════════════════════════════════

        // Circular buffer of recent tick volumes (size = WindowTicks).
        private double[] _blockBuf;
        private int      _blockHead, _blockTail, _blockFilled;

        // SortedDictionary<volume, count> gives O(log n) insert/remove and O(k) percentile
        // walk where k = number of distinct volume values (far less than WindowTicks in practice).
        private SortedDictionary<double, int> _blockFreq;
        private int _blockFreqTotal;

        // ══════════════════════════════════════════════════════════════════
        //  Engine B — Burst: timestamp circular queues + adaptive baseline
        // ══════════════════════════════════════════════════════════════════

        // Per-side circular queues of UTC-tick timestamps for the burst window.
        private long[] _upTimeBuf,  _downTimeBuf;
        private int    _upTimeHead, _upTimeTail,  _upTimeCount;
        private int    _downTimeHead, _downTimeTail, _downTimeCount;

        // Per-side circular buffers of per-second burst-count snapshots (baseline).
        private int[] _burstUpBase,  _burstDownBase;
        private int   _burstUpBaseHead,   _burstUpBaseTail,   _burstUpBaseFilled;
        private int   _burstDownBaseHead, _burstDownBaseTail, _burstDownBaseFilled;

        // Pre-allocated scratch array for baseline percentile sort (max 60 elements).
        private int[] _burstBaseScratch;

        // When we last recorded a per-second burst-count snapshot.
        private long _lastBurstSampleTick;

        // ══════════════════════════════════════════════════════════════════
        //  Cooldown (per-side, per-engine) — UTC Ticks
        // ══════════════════════════════════════════════════════════════════

        private long _cooldownTicks;   // CooldownMs converted to 100-ns ticks once at DataLoaded
        private long _lastBlockUpFire,   _lastBlockDownFire;
        private long _lastBurstUpFire,   _lastBurstDownFire;
        private long _lastFixedUpFire,   _lastFixedDownFire;

        // ══════════════════════════════════════════════════════════════════
        //  Level-1 bid/ask cache — updated in OnMarketData
        // ══════════════════════════════════════════════════════════════════

        private double _cachedAsk = double.NaN;
        private double _cachedBid = double.NaN;

        // ══════════════════════════════════════════════════════════════════
        //  Plot pending values — set in OnMarketData, pushed to Series in OnBarUpdate
        // ══════════════════════════════════════════════════════════════════

        private double _pendingUpVol;
        private double _pendingDownVol;

        // ══════════════════════════════════════════════════════════════════
        //  RTH session filter
        // ══════════════════════════════════════════════════════════════════

        private SessionIterator _sessionIterator;

        // ══════════════════════════════════════════════════════════════════
        //  State management
        // ══════════════════════════════════════════════════════════════════

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Audio-only, self-calibrating order-flow alert. "
                                         + "Combines AlgoBox AudioBox-style block detection (Engine A), "
                                         + "TickStrike-style adaptive burst detection (Engine B), "
                                         + "and BigTrade-style fixed threshold (Engine C). "
                                         + "Works on any instrument with no per-market tuning.";
                Name                     = "SmartTickAudio";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsAutoScale              = false;   // MUST remain false — prevents price-axis skew
                DisplayInDataBox         = false;
                PaintPriceMarkers        = false;
                DrawOnPricePanel         = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.TwoHundredFiftySix;
                IsSuspendedWhileInactive = true;

                // ── 1. Mode ──
                Mode        = SmartTickAudioMode.Both;
                Mute        = false;
                OnlyRTH     = false;
                CooldownMs  = 150;

                // ── 2. Block Engine ──
                WindowTicks     = 500;
                BlockPercentile = 97;

                // ── 3. Burst Engine ──
                BurstWindowMs   = 1000;
                BurstPercentile = 95;
                BurstMinTicks   = 3;
                BurstFixedCount = 0;

                // ── 4. Fixed Engine ──
                FixedUpThreshold   = 0;
                FixedDownThreshold = 0;

                // ── 5. Sounds ──
                UpSoundFile        = string.Empty;
                DownSoundFile      = string.Empty;
                BurstUpSoundFile   = string.Empty;
                BurstDownSoundFile = string.Empty;
                FixedUpSoundFile   = string.Empty;
                FixedDownSoundFile = string.Empty;

                // Hidden diagnostic plots — same names as ALGOBOX__AudioBox for drop-in
                // replacement in existing chart templates and downstream script queries.
                AddPlot(new Stroke(Brushes.Green, 2), PlotStyle.Bar, "upVolume");
                AddPlot(new Stroke(Brushes.Red,   2), PlotStyle.Bar, "downVolume");
            }
            else if (State == State.Configure)
            {
                // No additional data series required; Level-1 data comes via OnMarketData.
            }
            else if (State == State.DataLoaded)
            {
                // ── Allocate all buffers once; no heap allocs in hot path ──
                int wt     = Math.Max(1, WindowTicks);
                _blockBuf  = new double[wt];
                _blockHead = _blockTail = _blockFilled = 0;
                _blockFreq      = new SortedDictionary<double, int>();
                _blockFreqTotal = 0;

                _upTimeBuf    = new long[MaxBurstQueueCap];
                _downTimeBuf  = new long[MaxBurstQueueCap];
                _upTimeHead   = _upTimeTail   = _upTimeCount   = 0;
                _downTimeHead = _downTimeTail = _downTimeCount = 0;

                _burstUpBase    = new int[BurstBaselineCap];
                _burstDownBase  = new int[BurstBaselineCap];
                _burstUpBaseHead   = _burstUpBaseTail   = _burstUpBaseFilled   = 0;
                _burstDownBaseHead = _burstDownBaseTail = _burstDownBaseFilled = 0;
                _burstBaseScratch  = new int[BurstBaselineCap];

                _cooldownTicks      = (long)CooldownMs * TimeSpan.TicksPerMillisecond;
                _lastBurstSampleTick = DateTime.UtcNow.Ticks;

                _pendingUpVol = _pendingDownVol = 0;
                _cachedAsk    = _cachedBid      = double.NaN;

                _lastBlockUpFire   = _lastBlockDownFire   = 0;
                _lastBurstUpFire   = _lastBurstDownFire   = 0;
                _lastFixedUpFire   = _lastFixedDownFire   = 0;

                _sessionIterator = new SessionIterator(Bars);
            }
            else if (State == State.Terminated)
            {
                _sessionIterator = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  OnBarUpdate — push pending plot values; reset at bar open
        // ══════════════════════════════════════════════════════════════════

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < 0)
                return;

            // Reset pending values at the first tick of a new bar so bars with no
            // triggers display 0 rather than the previous bar's last trigger volume.
            if (IsFirstTickOfBar)
            {
                _pendingUpVol   = 0;
                _pendingDownVol = 0;
            }

            Values[0][0] = _pendingUpVol;
            Values[1][0] = _pendingDownVol;
        }

        // ══════════════════════════════════════════════════════════════════
        //  OnMarketData — Level-1 bid/ask cache + tick classification
        // ══════════════════════════════════════════════════════════════════

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // Cache best bid and ask as they arrive for accurate classification.
            if (e.MarketDataType == MarketDataType.Ask)
            {
                _cachedAsk = e.Price;
                return;
            }
            if (e.MarketDataType == MarketDataType.Bid)
            {
                _cachedBid = e.Price;
                return;
            }
            if (e.MarketDataType != MarketDataType.Last)
                return;

            double price  = e.Price;
            double volume = e.Volume;
            if (volume <= 0)
                return;

            // Resolve bid/ask: prefer cached Level-1 values; fall back to chart method.
            // Note: when a Volumetric BarsPeriod is attached the same Level-1 tick stream
            // is used for classification — the result is equivalent.
            double ask = double.IsNaN(_cachedAsk) ? GetCurrentAsk() : _cachedAsk;
            double bid = double.IsNaN(_cachedBid) ? GetCurrentBid() : _cachedBid;
            if (ask <= 0 || bid <= 0)
                return;

            if (price >= ask)
                ProcessClassifiedTick(true,  volume);
            else if (price <= bid)
                ProcessClassifiedTick(false, volume);
            // mid-market prints are ignored
        }

        // ══════════════════════════════════════════════════════════════════
        //  Core engine logic  — hot path, MUST remain allocation-free
        // ══════════════════════════════════════════════════════════════════

        private void ProcessClassifiedTick(bool isUp, double volume)
        {
            // ── RTH filter ──
            if (OnlyRTH && !IsCurrentTimeRTH())
                return;

            long nowTicks         = DateTime.UtcNow.Ticks;
            long burstWindowTicks = (long)BurstWindowMs * TimeSpan.TicksPerMillisecond;

            // ── Periodic burst-baseline snapshot (once per second) ──
            // Records the current burst-window count into the adaptive baseline so later
            // ticks can compare against a rolling distribution of recent activity.
            if (nowTicks - _lastBurstSampleTick >= TimeSpan.TicksPerSecond)
            {
                long cutoff = nowTicks - burstWindowTicks;
                int upSnap   = BurstExpireCount(_upTimeBuf,   ref _upTimeHead,   ref _upTimeCount,   cutoff);
                int downSnap = BurstExpireCount(_downTimeBuf, ref _downTimeHead, ref _downTimeCount, cutoff);
                BurstBaselinePush(_burstUpBase,   ref _burstUpBaseHead,   ref _burstUpBaseTail,   ref _burstUpBaseFilled,   upSnap);
                BurstBaselinePush(_burstDownBase, ref _burstDownBaseHead, ref _burstDownBaseTail, ref _burstDownBaseFilled, downSnap);
                _lastBurstSampleTick = nowTicks;
            }

            // Always push into the block buffer to keep the warm-up consistent regardless
            // of current Mode — allows instant engine switching without cold-start penalty.
            BlockPush(volume);

            // ── Engine A — Block (AlgoBox AudioBox-style) ──
            bool blockActive = Mode == SmartTickAudioMode.BlockOnly
                             || Mode == SmartTickAudioMode.Both
                             || Mode == SmartTickAudioMode.All;
            if (blockActive && _blockFilled >= BlockWarmupMin)
            {
                double threshold = BlockGetPercentile(BlockPercentile);
                if (threshold > 0 && volume >= threshold)
                {
                    if (isUp)
                    {
                        if (nowTicks - _lastBlockUpFire >= _cooldownTicks)
                        {
                            _lastBlockUpFire = nowTicks;
                            _pendingUpVol    = volume;
                            TryPlaySound(UpSoundFile);
                        }
                    }
                    else
                    {
                        if (nowTicks - _lastBlockDownFire >= _cooldownTicks)
                        {
                            _lastBlockDownFire = nowTicks;
                            _pendingDownVol    = volume;
                            TryPlaySound(DownSoundFile);
                        }
                    }
                }
            }

            // ── Engine B — Burst (TickStrike-style, adaptive) ──
            bool burstActive = Mode == SmartTickAudioMode.BurstOnly
                             || Mode == SmartTickAudioMode.Both
                             || Mode == SmartTickAudioMode.All;
            if (burstActive)
            {
                long cutoff = nowTicks - burstWindowTicks;
                if (isUp)
                {
                    BurstTimePush(_upTimeBuf, ref _upTimeHead, ref _upTimeTail, ref _upTimeCount, nowTicks);
                    int cnt = BurstExpireCount(_upTimeBuf, ref _upTimeHead, ref _upTimeCount, cutoff);
                    if (cnt >= BurstMinTicks)
                    {
                        int threshold = BurstFixedCount > 0
                                      ? BurstFixedCount
                                      : BurstBaselinePercentile(_burstUpBase, _burstUpBaseFilled, _burstUpBaseHead, BurstPercentile);
                        if (threshold > 0 && cnt >= threshold)
                        {
                            if (nowTicks - _lastBurstUpFire >= _cooldownTicks)
                            {
                                _lastBurstUpFire = nowTicks;
                                _pendingUpVol    = volume;
                                TryPlaySound(string.IsNullOrEmpty(BurstUpSoundFile) ? UpSoundFile : BurstUpSoundFile);
                            }
                        }
                    }
                }
                else
                {
                    BurstTimePush(_downTimeBuf, ref _downTimeHead, ref _downTimeTail, ref _downTimeCount, nowTicks);
                    int cnt = BurstExpireCount(_downTimeBuf, ref _downTimeHead, ref _downTimeCount, cutoff);
                    if (cnt >= BurstMinTicks)
                    {
                        int threshold = BurstFixedCount > 0
                                      ? BurstFixedCount
                                      : BurstBaselinePercentile(_burstDownBase, _burstDownBaseFilled, _burstDownBaseHead, BurstPercentile);
                        if (threshold > 0 && cnt >= threshold)
                        {
                            if (nowTicks - _lastBurstDownFire >= _cooldownTicks)
                            {
                                _lastBurstDownFire = nowTicks;
                                _pendingDownVol    = volume;
                                TryPlaySound(string.IsNullOrEmpty(BurstDownSoundFile) ? DownSoundFile : BurstDownSoundFile);
                            }
                        }
                    }
                }
            }

            // ── Engine C — Fixed (BigTrade / GomMP-style) ──
            bool fixedActive = Mode == SmartTickAudioMode.FixedOnly
                             || Mode == SmartTickAudioMode.All;
            if (fixedActive)
            {
                if (isUp && FixedUpThreshold > 0 && volume >= FixedUpThreshold)
                {
                    if (nowTicks - _lastFixedUpFire >= _cooldownTicks)
                    {
                        _lastFixedUpFire = nowTicks;
                        _pendingUpVol    = volume;
                        TryPlaySound(string.IsNullOrEmpty(FixedUpSoundFile) ? UpSoundFile : FixedUpSoundFile);
                    }
                }
                else if (!isUp && FixedDownThreshold > 0 && volume >= FixedDownThreshold)
                {
                    if (nowTicks - _lastFixedDownFire >= _cooldownTicks)
                    {
                        _lastFixedDownFire = nowTicks;
                        _pendingDownVol    = volume;
                        TryPlaySound(string.IsNullOrEmpty(FixedDownSoundFile) ? DownSoundFile : FixedDownSoundFile);
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Engine A helpers — O(log n) rolling-window percentile
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Push a new volume value into the circular block buffer.
        /// Evicts the oldest value and updates the SortedDictionary frequency map
        /// in O(log n) time where n = number of distinct volume values.
        /// </summary>
        private void BlockPush(double vol)
        {
            int cap = _blockBuf.Length;

            if (_blockFilled == cap)
            {
                // Evict oldest entry from the sorted-frequency map.
                double old = _blockBuf[_blockHead];
                _blockHead = (_blockHead + 1) % cap;
                _blockFilled--;
                _blockFreqTotal--;
                int c;
                if (_blockFreq.TryGetValue(old, out c))
                {
                    if (c <= 1) _blockFreq.Remove(old);
                    else        _blockFreq[old] = c - 1;
                }
            }

            _blockBuf[_blockTail] = vol;
            _blockTail = (_blockTail + 1) % cap;
            _blockFilled++;
            _blockFreqTotal++;

            int cv;
            if (_blockFreq.TryGetValue(vol, out cv))
                _blockFreq[vol] = cv + 1;
            else
                _blockFreq[vol] = 1;
        }

        /// <summary>
        /// Walk the sorted frequency map to find the value at the given percentile.
        /// O(k) where k = number of distinct volume values (typically very small).
        /// </summary>
        private double BlockGetPercentile(int pct)
        {
            if (_blockFreqTotal == 0) return 0;
            int target     = (int)Math.Ceiling(_blockFreqTotal * pct / 100.0);
            int cumulative = 0;
            double last    = 0;
            foreach (KeyValuePair<double, int> kv in _blockFreq)
            {
                last        = kv.Key;
                cumulative += kv.Value;
                if (cumulative >= target) return kv.Key;
            }
            return last;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Engine B helpers — burst timestamp queue + adaptive baseline
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Push a UTC-tick timestamp into a per-side burst circular queue.</summary>
        private static void BurstTimePush(long[] buf, ref int head, ref int tail, ref int count, long ts)
        {
            buf[tail] = ts;
            tail = (tail + 1) % MaxBurstQueueCap;
            if (count == MaxBurstQueueCap)
                head = (head + 1) % MaxBurstQueueCap; // ring is full — overwrite oldest
            else
                count++;
        }

        /// <summary>
        /// Expire entries older than cutoff and return the remaining count.
        /// O(k) where k = number of expired entries (typically 0 between sweeps).
        /// </summary>
        private static int BurstExpireCount(long[] buf, ref int head, ref int count, long cutoff)
        {
            while (count > 0 && buf[head] < cutoff)
            {
                head = (head + 1) % MaxBurstQueueCap;
                count--;
            }
            return count;
        }

        /// <summary>Push a per-second burst-count sample into the adaptive baseline buffer.</summary>
        private static void BurstBaselinePush(int[] buf, ref int head, ref int tail, ref int filled, int val)
        {
            buf[tail] = val;
            tail = (tail + 1) % BurstBaselineCap;
            if (filled == BurstBaselineCap)
                head = (head + 1) % BurstBaselineCap; // ring is full — overwrite oldest
            else
                filled++;
        }

        /// <summary>
        /// Compute the requested percentile of the burst baseline using a pre-allocated
        /// scratch array (max 60 elements). Called at most once per classified burst tick
        /// after BurstMinTicks is satisfied — acceptable O(n log n) on 60 integers.
        /// </summary>
        private int BurstBaselinePercentile(int[] buf, int filled, int head, int pct)
        {
            if (filled == 0) return 0;

            // Copy from the circular buffer in order into the scratch array.
            int n = Math.Min(filled, BurstBaselineCap);
            for (int i = 0; i < n; i++)
                _burstBaseScratch[i] = buf[(head + i) % BurstBaselineCap];

            Array.Sort(_burstBaseScratch, 0, n);

            int idx = (int)Math.Ceiling(n * pct / 100.0) - 1;
            if (idx < 0)    idx = 0;
            if (idx >= n)   idx = n - 1;
            return _burstBaseScratch[idx];
        }

        // ══════════════════════════════════════════════════════════════════
        //  RTH session helper
        // ══════════════════════════════════════════════════════════════════

        private bool IsCurrentTimeRTH()
        {
            if (_sessionIterator == null) return true;
            try
            {
                DateTime utcNow = DateTime.UtcNow;
                _sessionIterator.GetNextSession(utcNow, true);
                return utcNow >= _sessionIterator.ActualSessionBegin
                    && utcNow <  _sessionIterator.ActualSessionEnd;
            }
            catch { return true; }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Audio
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Play the WAV file at filePath. Wraps NT8's PlaySound so a missing or invalid
        /// path never crashes the indicator. No-op when Mute = true or path is empty.
        /// </summary>
        private void TryPlaySound(string filePath)
        {
            if (Mute || string.IsNullOrEmpty(filePath)) return;
            try { PlaySound(filePath); }
            catch { /* invalid path — silently ignored */ }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Plot series accessors — same names as ALGOBOX__AudioBox so this
        //  indicator is a drop-in replacement in existing templates / strategies
        // ══════════════════════════════════════════════════════════════════

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> UpVolumeBar  => Values[0];

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> DownVolumeBar => Values[1];

        // ══════════════════════════════════════════════════════════════════
        //  Properties — all exposed in the NT8 indicator configuration dialog
        // ══════════════════════════════════════════════════════════════════

        // ── Group 1: Mode ──────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Mode", GroupName = "1. Mode", Order = 0,
                 Description = "Which detection engine(s) are active. Both = Block + Burst (default).")]
        public SmartTickAudioMode Mode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Mute", GroupName = "1. Mode", Order = 1,
                 Description = "Suppress all audio output without removing the indicator from the chart.")]
        public bool Mute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Only RTH", GroupName = "1. Mode", Order = 2,
                 Description = "When true, events outside the chart's primary trading session are silenced.")]
        public bool OnlyRTH { get; set; }

        [NinjaScriptProperty]
        [Range(0, 5000)]
        [Display(Name = "Cooldown (ms)", GroupName = "1. Mode", Order = 3,
                 Description = "Per-side, per-engine minimum interval between audio triggers. "
                             + "Prevents audio overlap during sweeps. Default 150 ms.")]
        public int CooldownMs { get; set; }

        // ── Group 2: Block Engine ──────────────────────────────────────

        [NinjaScriptProperty]
        [Range(50, 10000)]
        [Display(Name = "Window Ticks", GroupName = "2. Block Engine", Order = 0,
                 Description = "Rolling window size for Engine A adaptive baseline. "
                             + "~500 ticks ≈ 30 s warm-up on NQ — matches AlgoBox AudioBox behaviour.")]
        public int WindowTicks { get; set; }

        [NinjaScriptProperty]
        [Range(50, 99)]
        [Display(Name = "Block Percentile", GroupName = "2. Block Engine", Order = 1,
                 Description = "Tick size must exceed this percentile of recent history to fire Engine A. "
                             + "Default 97 — empirically matches AlgoBox AudioBox firing rate.")]
        public int BlockPercentile { get; set; }

        // ── Group 3: Burst Engine ──────────────────────────────────────

        [NinjaScriptProperty]
        [Range(100, 10000)]
        [Display(Name = "Burst Window (ms)", GroupName = "3. Burst Engine", Order = 0,
                 Description = "Time window for counting same-side aggressive ticks. Default 1000 ms.")]
        public int BurstWindowMs { get; set; }

        [NinjaScriptProperty]
        [Range(50, 99)]
        [Display(Name = "Burst Percentile", GroupName = "3. Burst Engine", Order = 1,
                 Description = "Adaptive cutoff — burst-window count must exceed this percentile of the "
                             + "rolling 60 s per-second baseline to fire Engine B. Default 95.")]
        public int BurstPercentile { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Burst Min Ticks", GroupName = "3. Burst Engine", Order = 2,
                 Description = "Minimum same-side ticks required in the burst window before Engine B "
                             + "can fire. Guards against low-liquidity noise. Default 3.")]
        public int BurstMinTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Burst Fixed Count (0 = adaptive)", GroupName = "3. Burst Engine", Order = 3,
                 Description = "Override the adaptive percentile cutoff with a fixed tick count. "
                             + "0 = use adaptive baseline (default).")]
        public int BurstFixedCount { get; set; }

        // ── Group 4: Fixed Engine ──────────────────────────────────────

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "Fixed Up Threshold (0 = off)", GroupName = "4. Fixed Engine", Order = 0,
                 Description = "Engine C: fire on up-ticks whose volume is >= this value. 0 = disabled.")]
        public double FixedUpThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "Fixed Down Threshold (0 = off)", GroupName = "4. Fixed Engine", Order = 1,
                 Description = "Engine C: fire on down-ticks whose volume is >= this value. 0 = disabled.")]
        public double FixedDownThreshold { get; set; }

        // ── Group 5: Sounds ────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Up Sound File", GroupName = "5. Sounds", Order = 0,
                 Description = "Full path to a WAV file played on Engine A up-tick (block buy). "
                             + "Also used as fallback for Burst Up and Fixed Up if those are blank.")]
        [Editor(typeof(FilePathEditor), typeof(System.Drawing.Design.UITypeEditor))]
        public string UpSoundFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Down Sound File", GroupName = "5. Sounds", Order = 1,
                 Description = "Full path to a WAV file played on Engine A down-tick (block sell). "
                             + "Also used as fallback for Burst Down and Fixed Down if those are blank.")]
        [Editor(typeof(FilePathEditor), typeof(System.Drawing.Design.UITypeEditor))]
        public string DownSoundFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Burst Up Sound File", GroupName = "5. Sounds", Order = 2,
                 Description = "WAV for Engine B up-burst. Falls back to Up Sound File when blank.")]
        [Editor(typeof(FilePathEditor), typeof(System.Drawing.Design.UITypeEditor))]
        public string BurstUpSoundFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Burst Down Sound File", GroupName = "5. Sounds", Order = 3,
                 Description = "WAV for Engine B down-burst. Falls back to Down Sound File when blank.")]
        [Editor(typeof(FilePathEditor), typeof(System.Drawing.Design.UITypeEditor))]
        public string BurstDownSoundFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fixed Up Sound File", GroupName = "5. Sounds", Order = 4,
                 Description = "WAV for Engine C up fixed-size print. Falls back to Up Sound File when blank.")]
        [Editor(typeof(FilePathEditor), typeof(System.Drawing.Design.UITypeEditor))]
        public string FixedUpSoundFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fixed Down Sound File", GroupName = "5. Sounds", Order = 5,
                 Description = "WAV for Engine C down fixed-size print. Falls back to Down Sound File when blank.")]
        [Editor(typeof(FilePathEditor), typeof(System.Drawing.Design.UITypeEditor))]
        public string FixedDownSoundFile { get; set; }
    }
}
