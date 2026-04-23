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
using NinjaTrader.NinjaScript;
#endregion

public enum SmartTickAudioMode
{
    BlockOnly,
    BurstOnly,
    FixedOnly,
    Both,
    All
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SmartTickAudio : Indicator
    {
        private const int MaxBurstQueueCap = 4096;
        private const int BurstBaselineCap = 60;
        private const int BlockWarmupMin = 30;

        private double[] _blockBuf;
        private int      _blockHead, _blockTail, _blockFilled;
        private SortedDictionary<double, int> _blockFreq;
        private int _blockFreqTotal;

        private long[] _upTimeBuf,  _downTimeBuf;
        private int    _upTimeHead, _upTimeTail,  _upTimeCount;
        private int    _downTimeHead, _downTimeTail, _downTimeCount;

        private int[] _burstUpBase,  _burstDownBase;
        private int   _burstUpBaseHead,   _burstUpBaseTail,   _burstUpBaseFilled;
        private int   _burstDownBaseHead, _burstDownBaseTail, _burstDownBaseFilled;
        private int[] _burstBaseScratch;
        private long _lastBurstSampleTick;

        private long _cooldownTicks;
        private long _lastBlockUpFire,   _lastBlockDownFire;
        private long _lastBurstUpFire,   _lastBurstDownFire;
        private long _lastFixedUpFire,   _lastFixedDownFire;

        private double _cachedAsk = double.NaN;
        private double _cachedBid = double.NaN;

        private double _pendingUpVol;
        private double _pendingDownVol;

        private SessionIterator _sessionIterator;

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
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                PaintPriceMarkers        = false;
                DrawOnPricePanel         = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.TwoHundredFiftySix;
                IsSuspendedWhileInactive = true;

                Mode        = SmartTickAudioMode.Both;
                Mute        = false;
                OnlyRTH     = false;
                CooldownMs  = 150;

                WindowTicks     = 500;
                BlockPercentile = 97;

                BurstWindowMs   = 1000;
                BurstPercentile = 95;
                BurstMinTicks   = 3;
                BurstFixedCount = 0;

                FixedUpThreshold   = 0;
                FixedDownThreshold = 0;

                UpSoundFile        = string.Empty;
                DownSoundFile      = string.Empty;
                BurstUpSoundFile   = string.Empty;
                BurstDownSoundFile = string.Empty;
                FixedUpSoundFile   = string.Empty;
                FixedDownSoundFile = string.Empty;

                AddPlot(new Stroke(Brushes.Green, 2), PlotStyle.Bar, "upVolume");
                AddPlot(new Stroke(Brushes.Red,   2), PlotStyle.Bar, "downVolume");
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.DataLoaded)
            {
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

                _cooldownTicks       = (long)CooldownMs * TimeSpan.TicksPerMillisecond;
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

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < 0)
                return;

            if (IsFirstTickOfBar)
            {
                _pendingUpVol   = 0;
                _pendingDownVol = 0;
            }

            Values[0][0] = _pendingUpVol;
            Values[1][0] = _pendingDownVol;
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Ask)  { _cachedAsk = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Bid)  { _cachedBid = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;

            double price  = e.Price;
            double volume = e.Volume;
            if (volume <= 0) return;

            double ask = double.IsNaN(_cachedAsk) ? GetCurrentAsk() : _cachedAsk;
            double bid = double.IsNaN(_cachedBid) ? GetCurrentBid() : _cachedBid;
            if (ask <= 0 || bid <= 0) return;

            if      (price >= ask) ProcessClassifiedTick(true,  volume);
            else if (price <= bid) ProcessClassifiedTick(false, volume);
        }

        private void ProcessClassifiedTick(bool isUp, double volume)
        {
            if (OnlyRTH && !IsCurrentTimeRTH()) return;

            long nowTicks         = DateTime.UtcNow.Ticks;
            long burstWindowTicks = (long)BurstWindowMs * TimeSpan.TicksPerMillisecond;

            if (nowTicks - _lastBurstSampleTick >= TimeSpan.TicksPerSecond)
            {
                long cutoff  = nowTicks - burstWindowTicks;
                int upSnap   = BurstExpireCount(_upTimeBuf,   ref _upTimeHead,   ref _upTimeCount,   cutoff);
                int downSnap = BurstExpireCount(_downTimeBuf, ref _downTimeHead, ref _downTimeCount, cutoff);
                BurstBaselinePush(_burstUpBase,   ref _burstUpBaseHead,   ref _burstUpBaseTail,   ref _burstUpBaseFilled,   upSnap);
                BurstBaselinePush(_burstDownBase, ref _burstDownBaseHead, ref _burstDownBaseTail, ref _burstDownBaseFilled, downSnap);
                _lastBurstSampleTick = nowTicks;
            }

            BlockPush(volume);

            // Engine A
            bool blockActive = Mode == SmartTickAudioMode.BlockOnly || Mode == SmartTickAudioMode.Both || Mode == SmartTickAudioMode.All;
            if (blockActive && _blockFilled >= BlockWarmupMin)
            {
                double thr = BlockGetPercentile(BlockPercentile);
                if (thr > 0 && volume >= thr)
                {
                    if (isUp)
                    {
                        if (nowTicks - _lastBlockUpFire >= _cooldownTicks)
                        { _lastBlockUpFire = nowTicks; _pendingUpVol = volume; TryPlaySound(UpSoundFile); }
                    }
                    else
                    {
                        if (nowTicks - _lastBlockDownFire >= _cooldownTicks)
                        { _lastBlockDownFire = nowTicks; _pendingDownVol = volume; TryPlaySound(DownSoundFile); }
                    }
                }
            }

            // Engine B
            bool burstActive = Mode == SmartTickAudioMode.BurstOnly || Mode == SmartTickAudioMode.Both || Mode == SmartTickAudioMode.All;
            if (burstActive)
            {
                long cutoff = nowTicks - burstWindowTicks;
                if (isUp)
                {
                    BurstTimePush(_upTimeBuf, ref _upTimeHead, ref _upTimeTail, ref _upTimeCount, nowTicks);
                    int cnt = BurstExpireCount(_upTimeBuf, ref _upTimeHead, ref _upTimeCount, cutoff);
                    if (cnt >= BurstMinTicks)
                    {
                        int thr = BurstFixedCount > 0 ? BurstFixedCount : BurstBaselinePercentile(_burstUpBase, _burstUpBaseFilled, _burstUpBaseHead, BurstPercentile);
                        if (thr > 0 && cnt >= thr && nowTicks - _lastBurstUpFire >= _cooldownTicks)
                        { _lastBurstUpFire = nowTicks; _pendingUpVol = volume; TryPlaySound(string.IsNullOrEmpty(BurstUpSoundFile) ? UpSoundFile : BurstUpSoundFile); }
                    }
                }
                else
                {
                    BurstTimePush(_downTimeBuf, ref _downTimeHead, ref _downTimeTail, ref _downTimeCount, nowTicks);
                    int cnt = BurstExpireCount(_downTimeBuf, ref _downTimeHead, ref _downTimeCount, cutoff);
                    if (cnt >= BurstMinTicks)
                    {
                        int thr = BurstFixedCount > 0 ? BurstFixedCount : BurstBaselinePercentile(_burstDownBase, _burstDownBaseFilled, _burstDownBaseHead, BurstPercentile);
                        if (thr > 0 && cnt >= thr && nowTicks - _lastBurstDownFire >= _cooldownTicks)
                        { _lastBurstDownFire = nowTicks; _pendingDownVol = volume; TryPlaySound(string.IsNullOrEmpty(BurstDownSoundFile) ? DownSoundFile : BurstDownSoundFile); }
                    }
                }
            }

            // Engine C
            bool fixedActive = Mode == SmartTickAudioMode.FixedOnly || Mode == SmartTickAudioMode.All;
            if (fixedActive)
            {
                if (isUp && FixedUpThreshold > 0 && volume >= FixedUpThreshold && nowTicks - _lastFixedUpFire >= _cooldownTicks)
                { _lastFixedUpFire = nowTicks; _pendingUpVol = volume; TryPlaySound(string.IsNullOrEmpty(FixedUpSoundFile) ? UpSoundFile : FixedUpSoundFile); }
                else if (!isUp && FixedDownThreshold > 0 && volume >= FixedDownThreshold && nowTicks - _lastFixedDownFire >= _cooldownTicks)
                { _lastFixedDownFire = nowTicks; _pendingDownVol = volume; TryPlaySound(string.IsNullOrEmpty(FixedDownSoundFile) ? DownSoundFile : FixedDownSoundFile); }
            }
        }

        private void BlockPush(double vol)
        {
            int cap = _blockBuf.Length;
            if (_blockFilled == cap)
            {
                double old = _blockBuf[_blockHead];
                _blockHead = (_blockHead + 1) % cap;
                _blockFilled--;
                _blockFreqTotal--;
                int c;
                if (_blockFreq.TryGetValue(old, out c))
                { if (c <= 1) _blockFreq.Remove(old); else _blockFreq[old] = c - 1; }
            }
            _blockBuf[_blockTail] = vol;
            _blockTail = (_blockTail + 1) % cap;
            _blockFilled++;
            _blockFreqTotal++;
            int cv;
            if (_blockFreq.TryGetValue(vol, out cv)) _blockFreq[vol] = cv + 1;
            else                                      _blockFreq[vol] = 1;
        }

        private double BlockGetPercentile(int pct)
        {
            if (_blockFreqTotal == 0) return 0;
            int target = (int)Math.Ceiling(_blockFreqTotal * pct / 100.0);
            int cum = 0; double last = 0;
            foreach (KeyValuePair<double, int> kv in _blockFreq)
            { last = kv.Key; cum += kv.Value; if (cum >= target) return kv.Key; }
            return last;
        }

        private static void BurstTimePush(long[] buf, ref int head, ref int tail, ref int count, long ts)
        {
            buf[tail] = ts; tail = (tail + 1) % MaxBurstQueueCap;
            if (count == MaxBurstQueueCap) head = (head + 1) % MaxBurstQueueCap; else count++;
        }

        private static int BurstExpireCount(long[] buf, ref int head, ref int count, long cutoff)
        {
            while (count > 0 && buf[head] < cutoff) { head = (head + 1) % MaxBurstQueueCap; count--; }
            return count;
        }

        private static void BurstBaselinePush(int[] buf, ref int head, ref int tail, ref int filled, int val)
        {
            buf[tail] = val; tail = (tail + 1) % BurstBaselineCap;
            if (filled == BurstBaselineCap) head = (head + 1) % BurstBaselineCap; else filled++;
        }

        private int BurstBaselinePercentile(int[] buf, int filled, int head, int pct)
        {
            if (filled == 0) return 0;
            int n = Math.Min(filled, BurstBaselineCap);
            for (int i = 0; i < n; i++) _burstBaseScratch[i] = buf[(head + i) % BurstBaselineCap];
            Array.Sort(_burstBaseScratch, 0, n);
            int idx = (int)Math.Ceiling(n * pct / 100.0) - 1;
            if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
            return _burstBaseScratch[idx];
        }

        private bool IsCurrentTimeRTH()
        {
            if (_sessionIterator == null) return true;
            try
            {
                DateTime utcNow = DateTime.UtcNow;
                _sessionIterator.GetNextSession(utcNow, true);
                return utcNow >= _sessionIterator.ActualSessionBegin && utcNow < _sessionIterator.ActualSessionEnd;
            }
            catch { return true; }
        }

        private void TryPlaySound(string filePath)
        {
            if (Mute || string.IsNullOrEmpty(filePath)) return;
            try { PlaySound(filePath); } catch { }
        }

        [Browsable(false)][XmlIgnore]
        public Series<double> UpVolumeBar   => Values[0];
        [Browsable(false)][XmlIgnore]
        public Series<double> DownVolumeBar => Values[1];

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
                 Description = "Per-side, per-engine minimum interval between audio triggers. Default 150 ms.")]
        public int CooldownMs { get; set; }

        [NinjaScriptProperty]
        [Range(50, 10000)]
        [Display(Name = "Window Ticks", GroupName = "2. Block Engine", Order = 0,
                 Description = "Rolling window size for Engine A adaptive baseline.")]
        public int WindowTicks { get; set; }

        [NinjaScriptProperty]
        [Range(50, 99)]
        [Display(Name = "Block Percentile", GroupName = "2. Block Engine", Order = 1,
                 Description = "Tick size must exceed this percentile of recent history to fire Engine A. Default 97.")]
        public int BlockPercentile { get; set; }

        [NinjaScriptProperty]
        [Range(100, 10000)]
        [Display(Name = "Burst Window (ms)", GroupName = "3. Burst Engine", Order = 0,
                 Description = "Time window for counting same-side aggressive ticks. Default 1000 ms.")]
        public int BurstWindowMs { get; set; }

        [NinjaScriptProperty]
        [Range(50, 99)]
        [Display(Name = "Burst Percentile", GroupName = "3. Burst Engine", Order = 1,
                 Description = "Burst-window count must exceed this percentile of the rolling baseline to fire Engine B. Default 95.")]
        public int BurstPercentile { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Burst Min Ticks", GroupName = "3. Burst Engine", Order = 2,
                 Description = "Minimum same-side ticks required in the burst window before Engine B can fire. Default 3.")]
        public int BurstMinTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Burst Fixed Count (0 = adaptive)", GroupName = "3. Burst Engine", Order = 3,
                 Description = "Override the adaptive percentile cutoff with a fixed tick count. 0 = adaptive.")]
        public int BurstFixedCount { get; set; }

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

        [NinjaScriptProperty]
        [PropertyEditor(typeof(NinjaTrader.Gui.Tools.SoundFileEditor))]
        [Display(Name = "Up Sound File", GroupName = "5. Sounds", Order = 0,
                 Description = "WAV for Engine A up-tick (block buy). Fallback for Burst Up and Fixed Up.")]
        public string UpSoundFile { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor(typeof(NinjaTrader.Gui.Tools.SoundFileEditor))]
        [Display(Name = "Down Sound File", GroupName = "5. Sounds", Order = 1,
                 Description = "WAV for Engine A down-tick (block sell). Fallback for Burst Down and Fixed Down.")]
        public string DownSoundFile { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor(typeof(NinjaTrader.Gui.Tools.SoundFileEditor))]
        [Display(Name = "Burst Up Sound File", GroupName = "5. Sounds", Order = 2,
                 Description = "WAV for Engine B up-burst. Falls back to Up Sound File when blank.")]
        public string BurstUpSoundFile { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor(typeof(NinjaTrader.Gui.Tools.SoundFileEditor))]
        [Display(Name = "Burst Down Sound File", GroupName = "5. Sounds", Order = 3,
                 Description = "WAV for Engine B down-burst. Falls back to Down Sound File when blank.")]
        public string BurstDownSoundFile { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor(typeof(NinjaTrader.Gui.Tools.SoundFileEditor))]
        [Display(Name = "Fixed Up Sound File", GroupName = "5. Sounds", Order = 4,
                 Description = "WAV for Engine C up fixed-size print. Falls back to Up Sound File when blank.")]
        public string FixedUpSoundFile { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor(typeof(NinjaTrader.Gui.Tools.SoundFileEditor))]
        [Display(Name = "Fixed Down Sound File", GroupName = "5. Sounds", Order = 5,
                 Description = "WAV for Engine C down fixed-size print. Falls back to Down Sound File when blank.")]
        public string FixedDownSoundFile { get; set; }
    }
}