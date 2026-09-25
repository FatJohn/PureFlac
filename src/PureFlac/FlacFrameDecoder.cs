// Copyright (c) 2026 JohnShu. Licensed under the MIT License; see LICENSE in the repository root.
//
// Ported from dr_flac (https://github.com/mackron/dr_libs, commit dfe8377631000664666519fdb83da193fd8037f4,
// dr_flac.h v0.13.4, public domain / MIT-0): the scalar path of the decoding core -- drflac__read_next_flac_frame_header,
// drflac__decode_subframe, drflac__decode_samples__{constant,verbatim,fixed,lpc}, drflac__decode_samples_with_residual,
// drflac__calculate_prediction_{32,64}, drflac__decode_flac_frame, and the stereo reconstruction of
// drflac_read_pcm_frames_s32__decode_*. The original is dual-licensed Public Domain (Unlicense) or MIT-0; it is
// used here under MIT-0.
//
// Deliberate differences from dr_flac (following RFC 9639):
// - A CRC-8/CRC-16 mismatch or a reserved value throws FlacDecodeException; no resync, no skipping of bad frames
//   (dr_flac searches for the next sync code).
// - Frame header bps code 7 means 32-bit (dr_flac treats it as reserved). The side channel of 32-bit stereo is a
//   33-bit subframe and takes a 64-bit path (dr_flac rejects it).
// - The Rice partition order may be up to 15 (dr_flac only accepts the Subset limit of 8), but the block size must
//   be divisible by the number of partitions.
// - The whole residual is decoded into the sample buffer first and the prediction is then restored in place
//   (dr_flac interleaves the two per sample); the result is identical.
using System;
using System.Runtime.CompilerServices;

namespace PureFlac
{
    /// <summary>
    /// Decodes one FLAC frame (header, all subframes, CRC-16) into per-channel int32 sample buffers (right-aligned
    /// original sample values, with wasted bits and stereo decorrelation already undone). The caller packs the output.
    /// </summary>
    internal sealed class FlacFrameDecoder
    {
        private static readonly byte[] Crc8Table = BuildCrc8Table();
        private static readonly int[] BitsPerSampleTable = { 0, 8, 12, -1, 16, 20, 24, 32 };

        private const int AssignmentLeftSide = 8;
        private const int AssignmentRightSide = 9;
        private const int AssignmentMidSide = 10;

        private readonly int channels;
        private readonly int bitsPerSample;
        private readonly int maxBlockSize;
        private readonly int[][] samples;
        private readonly long[] wideSide;
        private readonly int[] coefficients = new int[32];

        public FlacFrameDecoder(int channels, int bitsPerSample, int maxBlockSize)
        {
            this.channels = channels;
            this.bitsPerSample = bitsPerSample;
            this.maxBlockSize = maxBlockSize;
            samples = new int[channels][];
            for (var i = 0; i < channels; i++)
            {
                samples[i] = new int[maxBlockSize];
            }

            // Scratch for the 33-bit side subframe; stereo decorrelation also puts the side channel here
            // (so all of it is computed in 64 bits).
            wideSide = new long[channels == 2 ? maxBlockSize : 0];
        }

        /// <summary>Decoded samples per channel (length maxBlockSize; the valid length is the return value of <see cref="DecodeNextFrame"/>).</summary>
        public int[][] Samples => samples;

        /// <summary>
        /// Decodes the next frame and returns its block size (samples per channel). Returns 0 when the stream ends
        /// exactly on a frame boundary, and -1 when the current position is not a frame sync code (the caller decides
        /// whether that is trailing data at the end of the file or a format error).
        /// </summary>
        public int DecodeNextFrame(FlacBitReader reader)
        {
            if (reader.IsAtEnd())
            {
                return 0;
            }

            reader.ResetCrc16();
            if (!reader.TryEnsure(16))
            {
                return -1;
            }

            var blockSize = ReadFrameHeader(reader, out var assignment);
            if (blockSize < 0)
            {
                return -1;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                var sideBit = (assignment == AssignmentLeftSide && channel == 1)
                              || (assignment == AssignmentRightSide && channel == 0)
                              || (assignment == AssignmentMidSide && channel == 1) ? 1 : 0;
                DecodeSubframe(reader, blockSize, bitsPerSample + sideBit, channel, sideBit == 1);
            }

            reader.AlignToByte();
            var actualCrc = reader.GetCrc16();
            var expectedCrc = (ushort)reader.ReadBits(16);
            if (actualCrc != expectedCrc)
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.CrcMismatch, $"FLAC frame CRC-16 mismatch (computed 0x{actualCrc:X4}, recorded 0x{expectedCrc:X4}).");
            }

            Decorrelate(assignment, blockSize);
            return blockSize;
        }

        // ---------------------------------------------------------------- frame header

        /// <summary>Reads the frame header and verifies its CRC-8; returns the block size, or -1 if it does not start with a sync code.</summary>
        private int ReadFrameHeader(FlacBitReader reader, out int assignment)
        {
            Span<byte> header = stackalloc byte[16];
            var length = 0;

            var sync = (byte)reader.ReadBits(8);
            var second = (byte)reader.ReadBits(8);
            assignment = 0;
            if (sync != 0xFF || (second & 0xFC) != 0xF8)
            {
                return -1;
            }

            header[length++] = sync;
            header[length++] = second;
            if ((second & 0x02) != 0)
            {
                throw Format("The reserved bit after the frame sync code is not 0.");
            }

            var third = (byte)reader.ReadBits(8);
            header[length++] = third;
            var fourth = (byte)reader.ReadBits(8);
            header[length++] = fourth;

            var blockSizeCode = third >> 4;
            var sampleRateCode = third & 0x0F;
            assignment = fourth >> 4;
            var bpsCode = (fourth >> 1) & 0x07;

            if (blockSizeCode == 0)
            {
                throw Format("The frame header block size code is the reserved value 0.");
            }

            if (sampleRateCode == 15)
            {
                throw Format("The frame header sample rate code is the invalid value 15.");
            }

            if (assignment > AssignmentMidSide)
            {
                throw Format($"The frame header channel assignment code is the reserved value {assignment}.");
            }

            if (bpsCode == 3)
            {
                throw Format("The frame header bits-per-sample code is the reserved value 3.");
            }

            if ((fourth & 0x01) != 0)
            {
                throw Format("The reserved bit after the frame header bits-per-sample code is not 0.");
            }

            // Frame number (fixed block size) or sample number (variable block size), UTF-8-like coding
            var variable = (second & 0x01) != 0;
            var first = (byte)reader.ReadBits(8);
            header[length++] = first;
            int extraBytes;
            if ((first & 0x80) == 0)
            {
                extraBytes = 0;
            }
            else if ((first & 0xE0) == 0xC0)
            {
                extraBytes = 1;
            }
            else if ((first & 0xF0) == 0xE0)
            {
                extraBytes = 2;
            }
            else if ((first & 0xF8) == 0xF0)
            {
                extraBytes = 3;
            }
            else if ((first & 0xFC) == 0xF8)
            {
                extraBytes = 4;
            }
            else if ((first & 0xFE) == 0xFC)
            {
                extraBytes = 5;
            }
            else if (first == 0xFE && variable)
            {
                extraBytes = 6;
            }
            else
            {
                throw Format("The frame header frame/sample number coding is invalid.");
            }

            for (var i = 0; i < extraBytes; i++)
            {
                var b = (byte)reader.ReadBits(8);
                header[length++] = b;
                if ((b & 0xC0) != 0x80)
                {
                    throw Format("The frame header frame/sample number coding is invalid.");
                }
            }

            int blockSize;
            if (blockSizeCode == 1)
            {
                blockSize = 192;
            }
            else if (blockSizeCode <= 5)
            {
                blockSize = 576 << (blockSizeCode - 2);
            }
            else if (blockSizeCode == 6)
            {
                var b = (byte)reader.ReadBits(8);
                header[length++] = b;
                blockSize = b + 1;
            }
            else if (blockSizeCode == 7)
            {
                var hi = (byte)reader.ReadBits(8);
                var lo = (byte)reader.ReadBits(8);
                header[length++] = hi;
                header[length++] = lo;
                blockSize = ((hi << 8) | lo) + 1;
            }
            else
            {
                blockSize = 256 << (blockSizeCode - 8);
            }

            // The sample rate does not affect decoding (STREAMINFO is authoritative); explicit bytes only need to
            // be included in the CRC-8.
            var rateBytes = sampleRateCode switch
            {
                12 => 1,
                13 or 14 => 2,
                _ => 0,
            };
            for (var i = 0; i < rateBytes; i++)
            {
                header[length++] = (byte)reader.ReadBits(8);
            }

            var expectedCrc8 = (byte)reader.ReadBits(8);
            var actualCrc8 = Crc8(header[..length]);
            if (actualCrc8 != expectedCrc8)
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.CrcMismatch, $"FLAC frame header CRC-8 mismatch (computed 0x{actualCrc8:X2}, recorded 0x{expectedCrc8:X2}).");
            }

            var frameChannels = assignment >= AssignmentLeftSide ? 2 : assignment + 1;
            if (frameChannels != channels)
            {
                throw Format($"The frame has {frameChannels} channels but STREAMINFO says {channels}.");
            }

            var frameBps = bpsCode == 0 ? bitsPerSample : BitsPerSampleTable[bpsCode];
            if (frameBps != bitsPerSample)
            {
                throw Format($"The frame has {frameBps} bits per sample but STREAMINFO says {bitsPerSample}.");
            }

            if (blockSize > maxBlockSize)
            {
                throw Format($"The frame block size {blockSize} exceeds the STREAMINFO maximum {maxBlockSize}.");
            }

            return blockSize;
        }

        // ---------------------------------------------------------------- subframe

        private void DecodeSubframe(FlacBitReader reader, int blockSize, int subframeBps, int channel, bool isSide)
        {
            var header = reader.ReadBits(8);
            if ((header & 0x80) != 0)
            {
                throw Format("The first bit of the subframe header is not 0.");
            }

            var type = (int)((header >> 1) & 0x3F);
            var wasted = 0;
            if ((header & 0x01) != 0)
            {
                wasted = (int)reader.ReadUnary() + 1;
                if (wasted >= subframeBps)
                {
                    throw Format($"Wasted bits {wasted} is not less than the subframe bits per sample {subframeBps}.");
                }
            }

            var codedBps = subframeBps - wasted;

            // Only the side channel of 32-bit audio exceeds 32 bits; that path uses 64 bits throughout.
            if (isSide && bitsPerSample == 32)
            {
                var wide = wideSide.AsSpan(0, blockSize);
                DecodeSubframeBody64(reader, type, blockSize, codedBps, samples[channel], wide);
                if (wasted > 0)
                {
                    for (var i = 0; i < wide.Length; i++)
                    {
                        wide[i] <<= wasted;
                    }
                }

                return;
            }

            var output = samples[channel].AsSpan(0, blockSize);
            DecodeSubframeBody32(reader, type, blockSize, codedBps, output);
            if (wasted > 0)
            {
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = (int)((uint)output[i] << wasted);
                }
            }
        }

        private void DecodeSubframeBody32(FlacBitReader reader, int type, int blockSize, int bps, Span<int> output)
        {
            if (type == 0)
            {
                output.Fill(reader.ReadSignedBits(bps));
            }
            else if (type == 1)
            {
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = reader.ReadSignedBits(bps);
                }
            }
            else if (type >= 8 && type <= 12)
            {
                var order = type - 8;
                CheckOrder(order, blockSize);
                for (var i = 0; i < order; i++)
                {
                    output[i] = reader.ReadSignedBits(bps);
                }

                ReadResidual(reader, blockSize, order, output);
                RestoreFixed32(order, output);
            }
            else if (type >= 32)
            {
                var order = type - 31;
                CheckOrder(order, blockSize);
                for (var i = 0; i < order; i++)
                {
                    output[i] = reader.ReadSignedBits(bps);
                }

                var (precision, shift) = ReadLpcParameters(reader, order);
                ReadResidual(reader, blockSize, order, output);
                var coefs = coefficients.AsSpan(0, order);
                if (Use64BitPrediction(bps, order, precision))
                {
                    RestoreLpc64(coefs, shift, output);
                }
                else
                {
                    RestoreLpc32(coefs, shift, output);
                }
            }
            else
            {
                throw Format($"Subframe type {type} is reserved.");
            }
        }

        private void DecodeSubframeBody64(FlacBitReader reader, int type, int blockSize, int bps, int[] residualScratch, Span<long> output)
        {
            if (type == 0)
            {
                output.Fill(reader.ReadSignedBits64(bps));
            }
            else if (type == 1)
            {
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = reader.ReadSignedBits64(bps);
                }
            }
            else if ((type >= 8 && type <= 12) || type >= 32)
            {
                var isFixed = type <= 12;
                var order = isFixed ? type - 8 : type - 31;
                CheckOrder(order, blockSize);
                for (var i = 0; i < order; i++)
                {
                    output[i] = reader.ReadSignedBits64(bps);
                }

                var shift = 0;
                if (!isFixed)
                {
                    (_, shift) = ReadLpcParameters(reader, order);
                }
                else
                {
                    FixedCoefficients(order).CopyTo(coefficients);
                }

                // The residual always fits in int32 (RFC 9639). Decode it into the side channel's own int buffer
                // (unused at this point), then restore in 64 bits.
                var residual = residualScratch.AsSpan(0, blockSize);
                ReadResidual(reader, blockSize, order, residual);
                RestoreLpcWide(coefficients.AsSpan(0, order), shift, residual, output);
            }
            else
            {
                throw Format($"Subframe type {type} is reserved.");
            }
        }

        private static void CheckOrder(int order, int blockSize)
        {
            if (order > blockSize)
            {
                throw Format($"Predictor order {order} is larger than the block size {blockSize}.");
            }
        }

        /// <summary>Reads the LPC coefficient precision, shift and coefficients (dr_flac drflac__decode_samples__lpc).</summary>
        private (int Precision, int Shift) ReadLpcParameters(FlacBitReader reader, int order)
        {
            var precision = (int)reader.ReadBits(4);
            if (precision == 15)
            {
                throw Format("The LPC coefficient precision is the invalid value 15.");
            }

            precision++;
            var shift = reader.ReadSignedBits(5);
            if (shift < 0)
            {
                throw Format($"The LPC shift is negative ({shift}).");
            }

            for (var i = 0; i < order; i++)
            {
                coefficients[i] = reader.ReadSignedBits(precision);
            }

            return (precision, shift);
        }

        /// <summary>dr_flac drflac__use_64_bit_prediction: use 64 bits when the accumulator may exceed 32 bits.</summary>
        private static bool Use64BitPrediction(int bps, int order, int precision)
        {
            var orderBits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)order);
            return bps + precision + orderBits > 32;
        }

        private static ReadOnlySpan<int> FixedCoefficients(int order) => order switch
        {
            0 => ReadOnlySpan<int>.Empty,
            1 => new[] { 1 },
            2 => new[] { 2, -1 },
            3 => new[] { 3, -3, 1 },
            _ => new[] { 4, -6, 4, -1 },
        };

        // ---------------------------------------------------------------- residual

        /// <summary>
        /// Decodes the residual (dr_flac drflac__decode_samples_with_residual) into output[order..]: RICE (4-bit
        /// parameter, 15 = escape) or RICE2 (5-bit parameter, 31 = escape); an escaped partition holds raw signed
        /// values whose width is given in 5 bits.
        /// </summary>
        private static void ReadResidual(FlacBitReader reader, int blockSize, int order, Span<int> output)
        {
            var method = reader.ReadBits(2);
            if (method > 1)
            {
                throw Format($"Residual coding method {method} is reserved.");
            }

            var parameterBits = method == 0 ? 4 : 5;
            var escapeCode = method == 0 ? 15 : 31;
            var partitionOrder = (int)reader.ReadBits(4);
            var partitionSize = blockSize >> partitionOrder;
            if ((partitionSize << partitionOrder) != blockSize || partitionSize < order)
            {
                throw Format($"Rice partition order {partitionOrder} is incompatible with block size {blockSize} / predictor order {order}.");
            }

            var position = order;
            var partitions = 1 << partitionOrder;
            for (var partition = 0; partition < partitions; partition++)
            {
                var count = partition == 0 ? partitionSize - order : partitionSize;
                var part = output.Slice(position, count);
                position += count;

                var parameter = (int)reader.ReadBits(parameterBits);
                if (parameter == escapeCode)
                {
                    var width = (int)reader.ReadBits(5);
                    if (width == 0)
                    {
                        part.Clear();
                    }
                    else
                    {
                        for (var i = 0; i < part.Length; i++)
                        {
                            part[i] = reader.ReadSignedBits(width);
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < part.Length; i++)
                    {
                        part[i] = reader.ReadRice(parameter);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- prediction restore (in place: residual -> samples)

        /// <summary>
        /// FIXED predictor. It only adds, subtracts and multiplies by small integers, so int32 overflow wraps around
        /// and the result is still correct (the true value always fits in 32 bits).
        /// </summary>
        private static void RestoreFixed32(int order, Span<int> s)
        {
            switch (order)
            {
                case 1:
                    for (var i = 1; i < s.Length; i++)
                    {
                        s[i] += s[i - 1];
                    }

                    break;
                case 2:
                    for (var i = 2; i < s.Length; i++)
                    {
                        s[i] += (2 * s[i - 1]) - s[i - 2];
                    }

                    break;
                case 3:
                    for (var i = 3; i < s.Length; i++)
                    {
                        s[i] += (3 * s[i - 1]) - (3 * s[i - 2]) + s[i - 3];
                    }

                    break;
                case 4:
                    for (var i = 4; i < s.Length; i++)
                    {
                        s[i] += (4 * s[i - 1]) - (6 * s[i - 2]) + (4 * s[i - 3]) - s[i - 4];
                    }

                    break;
            }
        }

        /// <summary>dr_flac drflac__calculate_prediction_32.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RestoreLpc32(ReadOnlySpan<int> coefs, int shift, Span<int> s)
        {
            var order = coefs.Length;
            for (var i = order; i < s.Length; i++)
            {
                var history = s.Slice(i - order, order);
                var sum = 0;
                for (var j = 0; j < order; j++)
                {
                    sum += coefs[j] * history[order - 1 - j];
                }

                s[i] += sum >> shift;
            }
        }

        /// <summary>dr_flac drflac__calculate_prediction_64.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RestoreLpc64(ReadOnlySpan<int> coefs, int shift, Span<int> s)
        {
            var order = coefs.Length;
            for (var i = order; i < s.Length; i++)
            {
                var history = s.Slice(i - order, order);
                long sum = 0;
                for (var j = 0; j < order; j++)
                {
                    sum += (long)coefs[j] * history[order - 1 - j];
                }

                s[i] += (int)(sum >> shift);
            }
        }

        /// <summary>33-bit side channel: samples and prediction are 64-bit throughout; the residual comes from an int buffer.</summary>
        private static void RestoreLpcWide(ReadOnlySpan<int> coefs, int shift, ReadOnlySpan<int> residual, Span<long> s)
        {
            var order = coefs.Length;
            for (var i = order; i < s.Length; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++)
                {
                    sum += coefs[j] * s[i - 1 - j];
                }

                s[i] = residual[i] + (sum >> shift);
            }
        }

        // ---------------------------------------------------------------- stereo decorrelation restore

        /// <summary>
        /// dr_flac drflac_read_pcm_frames_s32__decode_{left_side,right_side,mid_side}: side = left - right,
        /// mid = (left + right) &gt;&gt; 1 (the dropped lowest bit is recovered from the parity of side). Computed in
        /// 64 bits throughout, so 32-bit audio cannot overflow.
        /// </summary>
        private void Decorrelate(int assignment, int blockSize)
        {
            if (assignment < AssignmentLeftSide)
            {
                return;
            }

            var sideChannel = assignment == AssignmentRightSide ? 0 : 1;
            var side = wideSide.AsSpan(0, blockSize);
            if (bitsPerSample < 32)
            {
                var narrow = samples[sideChannel];
                for (var i = 0; i < side.Length; i++)
                {
                    side[i] = narrow[i];
                }
            }

            var ch0 = samples[0].AsSpan(0, blockSize);
            var ch1 = samples[1].AsSpan(0, blockSize);
            switch (assignment)
            {
                case AssignmentLeftSide:
                    for (var i = 0; i < ch0.Length; i++)
                    {
                        ch1[i] = (int)(ch0[i] - side[i]);
                    }

                    break;
                case AssignmentRightSide:
                    for (var i = 0; i < ch1.Length; i++)
                    {
                        ch0[i] = (int)(ch1[i] + side[i]);
                    }

                    break;
                default:
                    for (var i = 0; i < ch0.Length; i++)
                    {
                        var s = side[i];
                        var mid = ((long)ch0[i] << 1) | (s & 1);
                        ch0[i] = (int)((mid + s) >> 1);
                        ch1[i] = (int)((mid - s) >> 1);
                    }

                    break;
            }
        }

        // ---------------------------------------------------------------- helpers

        private static byte Crc8(ReadOnlySpan<byte> data)
        {
            byte crc = 0;
            foreach (var b in data)
            {
                crc = Crc8Table[crc ^ b];
            }

            return crc;
        }

        private static byte[] BuildCrc8Table()
        {
            // CRC-8, polynomial x^8 + x^2 + x^1 + 1 (0x07), initial value 0 (FLAC frame header)
            var table = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
                }

                table[i] = (byte)crc;
            }

            return table;
        }

        private static FlacDecodeException Format(string message) => new(FlacDecodeErrorKind.Format, message);
    }
}
