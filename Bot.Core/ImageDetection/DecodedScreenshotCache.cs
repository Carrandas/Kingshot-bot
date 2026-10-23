using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace Bot.Core.ImageDetection
{
    /// <summary>
    /// Decodes each screenshot once and shares the result between template matches.
    /// Tasks usually match several templates against the same screenshot byte[] (the
    /// screenshot cache hands out the same array for 500 ms), so decoding the PNG and
    /// converting it on every call was the biggest per-match cost.
    /// Entries are keyed by array reference and are reference counted, so an entry that
    /// is evicted while another thread is still matching against it stays alive until
    /// that thread releases it.
    /// </summary>
    internal static class DecodedScreenshotCache
    {
        private const int Capacity = 6;
        private static readonly object _lock = new object();
        private static readonly LinkedList<Entry> _entries = new LinkedList<Entry>();

        public static Lease Acquire(byte[] screenshot)
        {
            lock (_lock)
            {
                var existing = Find(screenshot);
                if (existing != null)
                {
                    _entries.Remove(existing.Node);
                    _entries.AddFirst(existing.Node);
                    existing.Leases++;
                    return new Lease(existing);
                }
            }

            // Decode outside the lock so instances do not wait on each other
            var bgr = Cv2.ImDecode(screenshot, ImreadModes.Color);
            if (bgr.Empty())
            {
                bgr.Dispose();
                throw new ArgumentException("Screenshot data could not be decoded");
            }

            lock (_lock)
            {
                var existing = Find(screenshot);
                if (existing != null)
                {
                    // Another thread decoded the same screenshot meanwhile
                    bgr.Dispose();
                    existing.Leases++;
                    return new Lease(existing);
                }

                var entry = Insert(screenshot, bgr);
                entry.Leases = 1;
                return new Lease(entry);
            }
        }

        /// <summary>
        /// Stores pixels the caller already has for a screenshot (the ADB controller builds the
        /// PNG from the raw framebuffer), so the first match does not decode it again.
        /// Takes ownership of <paramref name="bgr"/>.
        /// </summary>
        public static void Add(byte[] screenshot, Mat bgr)
        {
            lock (_lock)
            {
                if (Find(screenshot) != null)
                {
                    bgr.Dispose();
                    return;
                }
                Insert(screenshot, bgr);
            }
        }

        private static Entry Insert(byte[] screenshot, Mat bgr)
        {
            var entry = new Entry(screenshot, bgr);
            entry.Node = _entries.AddFirst(entry);

            while (_entries.Count > Capacity)
            {
                var oldest = _entries.Last!.Value;
                _entries.RemoveLast();
                oldest.Evicted = true;
                if (oldest.Leases == 0) oldest.Dispose();
            }

            return entry;
        }

        private static Entry? Find(byte[] screenshot)
        {
            foreach (var entry in _entries)
            {
                if (ReferenceEquals(entry.Key, screenshot)) return entry;
            }
            return null;
        }

        private static void Release(Entry entry)
        {
            lock (_lock)
            {
                entry.Leases--;
                if (entry.Evicted && entry.Leases == 0) entry.Dispose();
            }
        }

        internal sealed class Entry
        {
            public readonly byte[] Key;
            public readonly Mat Bgr;
            public LinkedListNode<Entry> Node = null!;
            public int Leases;
            public bool Evicted;

            private readonly object _convertLock = new object();
            private Mat? _gray;
            private Mat? _hsv;
            private Mat[]? _bgrChannels;
            private Mat[]? _hsvChannels;

            public Entry(byte[] key, Mat bgr)
            {
                Key = key;
                Bgr = bgr;
            }

            public Mat Gray
            {
                get
                {
                    lock (_convertLock)
                    {
                        if (_gray == null)
                        {
                            var gray = new Mat();
                            Cv2.CvtColor(Bgr, gray, ColorConversionCodes.BGR2GRAY);
                            _gray = gray;
                        }
                        return _gray;
                    }
                }
            }

            public Mat[] BgrChannels
            {
                get
                {
                    lock (_convertLock)
                    {
                        return _bgrChannels ??= Bgr.Split();
                    }
                }
            }

            public Mat[] HsvChannels
            {
                get
                {
                    lock (_convertLock)
                    {
                        if (_hsvChannels == null)
                        {
                            _hsv = new Mat();
                            Cv2.CvtColor(Bgr, _hsv, ColorConversionCodes.BGR2HSV);
                            _hsvChannels = _hsv.Split();
                        }
                        return _hsvChannels;
                    }
                }
            }

            public void Dispose()
            {
                Bgr.Dispose();
                _gray?.Dispose();
                _hsv?.Dispose();
                if (_bgrChannels != null) foreach (var m in _bgrChannels) m.Dispose();
                if (_hsvChannels != null) foreach (var m in _hsvChannels) m.Dispose();
            }
        }

        public readonly struct Lease : IDisposable
        {
            private readonly Entry _entry;

            public Lease(Entry entry) => _entry = entry;

            public Mat Bgr => _entry.Bgr;
            public Mat Gray => _entry.Gray;
            public Mat[] BgrChannels => _entry.BgrChannels;
            public Mat[] HsvChannels => _entry.HsvChannels;

            public void Dispose() => Release(_entry);
        }
    }
}
