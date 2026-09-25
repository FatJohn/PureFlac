// Copyright (c) 2026 JohnShu. Licensed under the MIT License; see LICENSE in the repository root.
//
// Part of PureFlac, a C# port of the dr_flac decoding core (https://github.com/mackron/dr_libs,
// commit dfe8377631000664666519fdb83da193fd8037f4, public domain / MIT-0). This file is not derived from dr_flac:
// the STREAMINFO block is parsed independently from the FLAC specification (RFC 9639, section 8.2).
using System;

namespace PureFlac
{
    /// <summary>
    /// The contents of the FLAC STREAMINFO metadata block (RFC 9639, section 8.2).
    /// Instances are produced by <see cref="FlacStreamDecoder"/>; two instances are equal when every field,
    /// including every byte of <see cref="Md5Signature"/>, is equal.
    /// </summary>
    public sealed record FlacStreamInfo
    {
        /// <summary>Length in bytes of the STREAMINFO payload (without the metadata block header).</summary>
        internal const int BlockPayloadLength = 34;

        internal FlacStreamInfo()
        {
        }

        /// <summary>Minimum block size in samples (per channel) used in the stream.</summary>
        public int MinBlockSize { get; init; }

        /// <summary>Maximum block size in samples (per channel) used in the stream.</summary>
        public int MaxBlockSize { get; init; }

        /// <summary>Minimum frame size in bytes; 0 means unknown.</summary>
        public int MinFrameSize { get; init; }

        /// <summary>Maximum frame size in bytes; 0 means unknown.</summary>
        public int MaxFrameSize { get; init; }

        /// <summary>Sample rate in Hz.</summary>
        public int SampleRate { get; init; }

        /// <summary>Number of channels (1–8).</summary>
        public int Channels { get; init; }

        /// <summary>Bits per sample of the original audio (4–32).</summary>
        public int BitsPerSample { get; init; }

        /// <summary>Total number of samples per channel; 0 means unknown.</summary>
        public long TotalSamples { get; init; }

        /// <summary>
        /// The 16-byte MD5 of the unencoded audio (interleaved, little-endian, signed, ceil(bps/8) bytes per sample);
        /// all zeros means it was not computed.
        /// </summary>
        public ReadOnlyMemory<byte> Md5Signature { get; init; }

        /// <inheritdoc/>
        public bool Equals(FlacStreamInfo? other) =>
            other is not null
            && MinBlockSize == other.MinBlockSize
            && MaxBlockSize == other.MaxBlockSize
            && MinFrameSize == other.MinFrameSize
            && MaxFrameSize == other.MaxFrameSize
            && SampleRate == other.SampleRate
            && Channels == other.Channels
            && BitsPerSample == other.BitsPerSample
            && TotalSamples == other.TotalSamples
            && Md5Signature.Span.SequenceEqual(other.Md5Signature.Span);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(MinBlockSize);
            hash.Add(MaxBlockSize);
            hash.Add(MinFrameSize);
            hash.Add(MaxFrameSize);
            hash.Add(SampleRate);
            hash.Add(Channels);
            hash.Add(BitsPerSample);
            hash.Add(TotalSamples);
            hash.AddBytes(Md5Signature.Span);
            return hash.ToHashCode();
        }

        /// <summary>
        /// Parses the 34-byte STREAMINFO payload (without the metadata block header).
        /// </summary>
        internal static FlacStreamInfo ParseBlock(ReadOnlySpan<byte> block)
        {
            if (block.Length < BlockPayloadLength)
            {
                throw new ArgumentException($"The STREAMINFO payload must be {BlockPayloadLength} bytes long.", nameof(block));
            }

            var reader = new BigEndianBitReader(block);

            var minBlockSize = (int)reader.ReadBits(16);
            var maxBlockSize = (int)reader.ReadBits(16);
            var minFrameSize = (int)reader.ReadBits(24);
            var maxFrameSize = (int)reader.ReadBits(24);
            var sampleRate = (int)reader.ReadBits(20);
            var channels = (int)reader.ReadBits(3) + 1;
            var bitsPerSample = (int)reader.ReadBits(5) + 1;
            var totalSamples = (long)reader.ReadBits(36);
            var md5 = reader.ReadBytes(16);

            return new FlacStreamInfo
            {
                MinBlockSize = minBlockSize,
                MaxBlockSize = maxBlockSize,
                MinFrameSize = minFrameSize,
                MaxFrameSize = maxFrameSize,
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = bitsPerSample,
                TotalSamples = totalSamples,
                Md5Signature = md5,
            };
        }

        private ref struct BigEndianBitReader
        {
            private readonly ReadOnlySpan<byte> data;
            private int bitPosition;

            public BigEndianBitReader(ReadOnlySpan<byte> data)
            {
                this.data = data;
            }

            public ulong ReadBits(int count)
            {
                ulong value = 0;
                for (var i = 0; i < count; i++)
                {
                    var byteIndex = bitPosition / 8;
                    var bitIndexInByte = 7 - (bitPosition % 8);
                    var bit = (data[byteIndex] >> bitIndexInByte) & 1;
                    value = (value << 1) | (uint)bit;
                    bitPosition++;
                }

                return value;
            }

            public byte[] ReadBytes(int count)
            {
                var result = new byte[count];
                for (var i = 0; i < count; i++)
                {
                    result[i] = (byte)ReadBits(8);
                }

                return result;
            }
        }
    }
}
