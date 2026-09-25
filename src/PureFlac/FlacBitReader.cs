// Copyright (c) 2026 JohnShu. Licensed under the MIT License; see LICENSE in the repository root.
//
// Ported from dr_flac (https://github.com/mackron/dr_libs, commit dfe8377631000664666519fdb83da193fd8037f4,
// dr_flac.h v0.13.4, public domain / MIT-0): the bitstream reader (drflac_bs: bit cache, CRC-16 accumulation).
// The original is dual-licensed Public Domain (Unlicense) or MIT-0; it is used here under MIT-0.
// Changes from the original: the input is a byte buffer plus a 64-bit cache, and only Stream.Read is called
// (no seeking, no length queries).
using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PureFlac
{
    /// <summary>
    /// Big-endian bit reader for FLAC. Pulls data from a <see cref="Stream"/> in chunks via
    /// <see cref="Stream.Read(byte[], int, int)"/> (a read may return a single byte, and may block), and
    /// <b>never seeks nor reads Position/Length</b>. It asks the stream for more data only when the bits on hand
    /// are not enough, and each refill calls Read exactly once and takes whatever it gets, so when only part of
    /// the stream has arrived, the frames that have arrived can already be decoded.
    /// It also accumulates the CRC-16 of the consumed bytes (checked against the frame footer).
    /// </summary>
    internal sealed class FlacBitReader
    {
        private const int BufferSize = 32 * 1024;

        private static readonly ushort[] Crc16Table = BuildCrc16Table();

        private readonly Stream input;
        private readonly byte[] buffer = new byte[BufferSize];

        // buffer[bufferPos..bufferEnd) has not been loaded into the cache yet. The cache is left-aligned (its most
        // significant bit is the next bit) and holds cacheBits valid bits; every bit below the valid ones is 0
        // (ReadUnary's LeadingZeroCount relies on this).
        private int bufferPos;
        private int bufferEnd;
        private ulong cache;
        private int cacheBits;
        private bool endOfStream;

        // buffer[crcStart..ConsumedPos) holds consumed bytes that have not been folded into crc16 yet.
        private int crcStart;
        private ushort crc16;

        public FlacBitReader(Stream input)
        {
            this.input = input;
        }

        /// <summary>Buffer position of the fully consumed bytes (a byte still partly unread in the cache does not count).</summary>
        private int ConsumedPos => bufferPos - ((cacheBits + 7) >> 3);

        public bool IsByteAligned => (cacheBits & 7) == 0;

        /// <summary>
        /// Ensures the cache holds at least <paramref name="bits"/> valid bits (≤ 57), asking the stream for more
        /// data when needed. Returns false if the stream has ended and there are still not enough bits (the cache
        /// is left unchanged).
        /// </summary>
        public bool TryEnsure(int bits)
        {
            while (true)
            {
                RefillCache();
                if (cacheBits >= bits)
                {
                    return true;
                }

                if (!ReadMoreFromStream())
                {
                    return false;
                }
            }
        }

        /// <summary>On a byte boundary: returns true when no data is left at all (and the stream has ended).</summary>
        public bool IsAtEnd() => !TryEnsure(8);

        /// <summary>Reads an unsigned value of 1–32 bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadBits(int count)
        {
            if (cacheBits < count && !TryEnsure(count))
            {
                throw Truncated();
            }

            var value = (uint)(cache >> (64 - count));
            cache <<= count;
            cacheBits -= count;
            return value;
        }

        /// <summary>Reads a two's-complement signed value of 1–32 bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadSignedBits(int count)
        {
            var shift = 32 - count;
            return (int)(ReadBits(count) << shift) >> shift;
        }

        /// <summary>Reads a two's-complement signed value of 1–33 bits (33 bits only occur in the side channel of 32-bit audio).</summary>
        public long ReadSignedBits64(int count)
        {
            if (count <= 32)
            {
                return ReadSignedBits(count);
            }

            var high = (ulong)ReadBits(count - 32);
            var value = (high << 32) | ReadBits(32);
            var shift = 64 - count;
            return (long)(value << shift) >> shift;
        }

        /// <summary>Reads a unary code: zeros up to the first 1. Returns the number of zeros (the 1 is consumed too).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUnary()
        {
            uint zeros = 0;
            while (true)
            {
                if (cacheBits == 0 && !TryEnsure(1))
                {
                    throw Truncated();
                }

                var leading = BitOperations.LeadingZeroCount(cache);
                if (leading < cacheBits)
                {
                    zeros += (uint)leading;
                    cache = (cache << leading) << 1;
                    cacheBits -= leading + 1;
                    return zeros;
                }

                zeros += (uint)cacheBits;
                cache = 0;
                cacheBits = 0;
            }
        }

        /// <summary>
        /// Reads one Rice-coded residual and undoes the zigzag folding (dr_flac drflac__read_rice_parts plus sign
        /// restoration). <paramref name="riceParameter"/> is 0–30.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadRice(int riceParameter)
        {
            var quotient = ReadUnary();
            uint low = 0;
            if (riceParameter > 0)
            {
                if (cacheBits < riceParameter && !TryEnsure(riceParameter))
                {
                    throw Truncated();
                }

                low = (uint)(cache >> (64 - riceParameter));
                cache <<= riceParameter;
                cacheBits -= riceParameter;
            }

            var folded = (quotient << riceParameter) | low;
            return (int)(folded >> 1) ^ -(int)(folded & 1);
        }

        /// <summary>Discards the bits up to the next byte boundary (the zero padding at the end of a frame).</summary>
        public void AlignToByte()
        {
            var skip = cacheBits & 7;
            cache <<= skip;
            cacheBits -= skip;
        }

        /// <summary>On a byte boundary: reads <paramref name="destination"/>.Length bytes.</summary>
        public void ReadBytes(Span<byte> destination)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = (byte)ReadBits(8);
            }
        }

        /// <summary>On a byte boundary: reads and discards <paramref name="count"/> bytes (used for metadata blocks; never seeks).</summary>
        public void SkipBytes(long count)
        {
            while (count > 0 && cacheBits >= 8)
            {
                cache <<= 8;
                cacheBits -= 8;
                count--;
            }

            while (count > 0)
            {
                var available = bufferEnd - bufferPos;
                if (available == 0)
                {
                    if (!ReadMoreFromStream())
                    {
                        throw Truncated();
                    }

                    continue;
                }

                var take = (int)Math.Min(available, count);
                bufferPos += take;
                count -= take;
            }
        }

        /// <summary>On a byte boundary: restarts CRC-16 accumulation at the current position (called at the start of every frame).</summary>
        public void ResetCrc16()
        {
            crcStart = ConsumedPos;
            crc16 = 0;
        }

        /// <summary>On a byte boundary: returns the CRC-16 of the bytes consumed since <see cref="ResetCrc16"/>.</summary>
        public ushort GetCrc16()
        {
            FoldCrc16();
            return crc16;
        }

        public static FlacDecodeException Truncated() =>
            new(FlacDecodeErrorKind.Truncated, "The FLAC stream ended in the middle of metadata or a frame (truncated data).");

        private void FoldCrc16()
        {
            var end = ConsumedPos;
            var crc = crc16;
            var table = Crc16Table;
            for (var i = crcStart; i < end; i++)
            {
                crc = (ushort)((crc << 8) ^ table[(crc >> 8) ^ buffer[i]]);
            }

            crc16 = crc;
            crcStart = end;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RefillCache()
        {
            if (bufferEnd - bufferPos >= 8)
            {
                var bytes = (64 - cacheBits) >> 3;
                if (bytes == 0)
                {
                    return;
                }

                var next = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(bufferPos, 8));
                if (bytes < 8)
                {
                    next &= ~(ulong.MaxValue >> (bytes * 8));
                }

                cache |= next >> cacheBits;
                cacheBits += bytes * 8;
                bufferPos += bytes;
                return;
            }

            while (cacheBits <= 56 && bufferPos < bufferEnd)
            {
                cache |= (ulong)buffer[bufferPos++] << (56 - cacheBits);
                cacheBits += 8;
            }
        }

        /// <summary>
        /// Moves the bytes that are still needed to the start of the buffer, then calls
        /// <see cref="Stream.Read(byte[], int, int)"/> once. Consumed bytes are folded into the CRC-16 before the
        /// move. Returns false when the stream has ended.
        /// </summary>
        private bool ReadMoreFromStream()
        {
            if (endOfStream)
            {
                return false;
            }

            FoldCrc16();
            var keep = crcStart;
            if (keep > 0)
            {
                Buffer.BlockCopy(buffer, keep, buffer, 0, bufferEnd - keep);
                bufferPos -= keep;
                bufferEnd -= keep;
                crcStart = 0;
            }

            var read = input.Read(buffer, bufferEnd, buffer.Length - bufferEnd);
            if (read <= 0)
            {
                endOfStream = true;
                return false;
            }

            bufferEnd += read;
            return true;
        }

        private static ushort[] BuildCrc16Table()
        {
            // CRC-16, polynomial x^16 + x^15 + x^2 + 1 (0x8005), initial value 0 (FLAC frame footer)
            var table = new ushort[256];
            for (var i = 0; i < 256; i++)
            {
                var crc = i << 8;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
                }

                table[i] = (ushort)crc;
            }

            return table;
        }
    }
}
