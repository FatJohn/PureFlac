# PureFlac

[![NuGet](https://img.shields.io/nuget/v/PureFlac.svg)](https://www.nuget.org/packages/PureFlac)
[![NuGet downloads](https://img.shields.io/nuget/dt/PureFlac.svg)](https://www.nuget.org/packages/PureFlac)

A pure managed, streaming FLAC decoder for .NET, ported from [dr_flac](https://github.com/mackron/dr_libs).

PureFlac decodes FLAC from any readable `Stream` — including network streams and pipes that cannot seek and
deliver data a few bytes at a time — into interleaved little-endian PCM. Its output is bit-exact: it is checked
against libFLAC for every test fixture, and the decoder itself verifies the STREAMINFO MD5 at the end of the
stream.

## Features

- **Pure managed C#.** No native libraries, no P/Invoke, nothing to ship per platform. Targets .NET 8 and .NET 10.
- **Streaming.** Only `Stream.Read` is ever called — never `Seek`, `Position` or `Length`. Reads that return a
  single byte, or that block, are fine. The first PCM is available as soon as the metadata and the first frame
  have arrived.
- **Bit-exact.** Output matches libFLAC byte for byte on all 39 test fixtures, and the STREAMINFO MD5 and total
  sample count are checked at the end of the stream.
- **Strict error reporting.** CRC failures, reserved values, truncation and end-of-stream mismatches throw a
  `FlacDecodeException` with a `FlacDecodeErrorKind`; the decoder never silently skips a damaged frame.
- **No dependencies** beyond the .NET base class library.

## Installation

Install from [NuGet](https://www.nuget.org/packages/PureFlac):

```sh
dotnet add package PureFlac
```

Or build it from source:

```sh
git clone https://github.com/FatJohn/PureFlac.git
cd PureFlac
dotnet build -c Release
dotnet test
```

Then reference `src/PureFlac/PureFlac.csproj` from your project, or create a local package with
`dotnet pack src/PureFlac/PureFlac.csproj -c Release`.

Packages published to NuGet are built only by GitHub Actions (deterministic build, with
`ContinuousIntegrationBuild` enabled there); a local `dotnet pack` is for your own use only.

## Usage

```csharp
using PureFlac;

using var input = File.OpenRead("song.flac");          // any readable Stream; seeking is never used
using var decoder = new FlacStreamDecoder(input);        // blocks until all metadata has been read
using var output = File.Create("song.pcm");

Console.WriteLine($"{decoder.SampleRate} Hz, {decoder.Channels} ch, {decoder.BitsPerSample}-bit");

var buffer = new byte[64 * 1024];
int read;
while ((read = decoder.Read(buffer)) > 0)
{
    // buffer[..read]: interleaved, little-endian, signed PCM, decoder.BytesPerSample bytes per sample
    output.Write(buffer, 0, read);
}
// Read returned 0: end of stream, and the MD5 / sample count checks passed.
```

By default the decoder disposes the input stream when it is disposed; pass `leaveOpen: true` to keep it open.

### API

| Member | Description |
|---|---|
| `FlacStreamDecoder(Stream stream, bool leaveOpen = false)` | Reads the `fLaC` marker and all metadata blocks (an ID3v2 tag before the marker is skipped). Throws `FlacDecodeException` if the input is not valid FLAC. |
| `int Read(Span<byte> destination)`, `int Read(byte[] buffer, int offset, int count)` | Returns decoded PCM; 0 only at the end of the stream, after the end-of-stream checks have passed. |
| `FlacStreamInfo StreamInfo` | The STREAMINFO block: block and frame size ranges, sample rate, channels, bits per sample, total samples (0 = unknown), MD5 (all zeros = unknown). |
| `int SampleRate`, `int Channels` | From STREAMINFO. |
| `int BitsPerSample` | The original bit depth of the stream (4–32). |
| `int BytesPerSample` | Width of each output sample: ceil(`BitsPerSample` / 8), i.e. 1, 2, 3 or 4. |
| `long SamplesDecoded` | Samples per channel (PCM frames) decoded so far. |
| `FlacDecodeException.Kind` | `Format`, `CrcMismatch`, `Truncated`, `Md5Mismatch` or `SampleCountMismatch`. |

### Streaming semantics

- **Input.** The decoder calls only `Stream.Read`. The constructor blocks until all metadata blocks have been
  read (STREAMINFO is required; all other blocks are read and discarded). After that, `Read` decodes the next
  frame only when the PCM on hand has been used up. Whenever it needs more bits it calls `Stream.Read` once (into
  a 32 KiB buffer) and uses whatever that returns, so decoding never waits for more data than the next frame needs.
- **Output.** Interleaved, little-endian, signed integers, `BytesPerSample` bytes per sample. Sample values are
  **right-aligned**: the original FLAC sample value is written unscaled, which is exactly the packing the
  STREAMINFO MD5 is defined over. For bit depths that are not a multiple of 8 (for example 12 or 20 bits), shift
  each sample left by `BytesPerSample * 8 - BitsPerSample` bits to play it at full scale. A single `Read` returns
  at most the rest of the current frame, and its byte count is not necessarily a multiple of the sample size.
- **Errors.** A CRC-8/CRC-16 mismatch, a reserved value or any other format error throws `FlacDecodeException`.
  There is no resync: after the first exception, every later `Read` throws the same exception again. Input that
  ends in the middle of the metadata or a frame throws `Truncated`. Exceptions thrown by the input stream itself
  (such as `IOException`) propagate unchanged.
- **End-of-stream checks.** When the stream ends, the decoder compares the number of decoded samples with the
  STREAMINFO total (`SampleCountMismatch`) and the MD5 of the decoded PCM with the STREAMINFO MD5
  (`Md5Mismatch`). These checks run in the final `Read` — the one that would otherwise return 0 — so PCM that has
  already been returned may belong to a stream that is only found to be damaged at the end. Consumers that play
  audio as it is decoded should be prepared for that final exception.
- **Trailing data.** Once the STREAMINFO total sample count is known and has been reached, data after the last
  frame that is not a frame (for example an ID3v1 tag) is ignored.

## Supported features and known limitations

Supported:

- 4 to 32 bits per sample, 1 to 8 channels, any sample rate that STREAMINFO can express.
- Fixed and variable block sizes, up to the STREAMINFO maximum of 65535 samples.
- CONSTANT, VERBATIM, FIXED and LPC subframes (LPC order up to 32), wasted bits.
- RICE and RICE2 residual coding, escaped partitions, partition orders up to 15.
- Independent, left/side, right/side and mid/side stereo, including the 33-bit side channel of 32-bit stereo.
- Arbitrary metadata blocks (skipped) and an ID3v2 tag before the `fLaC` marker (skipped).

Not supported:

- Seeking.
- Ogg FLAC (FLAC inside an Ogg container).
- Resync after errors: a damaged stream stops at the first error.
- Decoding metadata other than STREAMINFO (tags, pictures, cue sheets and seek tables are skipped).

Known limitations:

- Frame and sample numbers in frame headers are checked for valid encoding but not for continuity. If a whole
  frame is missing from the middle of a stream (and the frames around it have valid CRCs), this is detected only
  at the end, by the total sample count or the MD5. **If STREAMINFO records neither the total sample count
  (0) nor the MD5 (all zeros), both checks are skipped and a missing frame in the middle is not reported at all.**
- Scalar code only; there is no SIMD.

## How it is verified

- **Fixtures.** 39 short (≤ 0.3 s) synthetic FLAC files in `tests/PureFlac.Tests/Fixtures/flac/`, generated by
  `gen.sh` from synthetic signals only (sine waves, noise and silence; no real music). Most are synthesized with
  ffmpeg and encoded with the libFLAC 1.5.0
  `flac` tool, with ffmpeg's FLAC encoder (to force each stereo mode), and with a minimal encoder in
  `flacgen.py` for paths the other encoders do not produce (escaped partitions, variable block sizes, the 33-bit
  side channel, rice partition orders 10, 12 and 15). `coverage.tsv` lists the coding paths each file actually
  uses, as reported by `flac -a`.
  Re-running `gen.sh` reproduces the committed fixtures byte for byte on a machine with the same tool versions.
- **Reference values.** `expected.tsv` records, for every fixture, the SHA-256 of the PCM decoded by libFLAC
  (`flac -d`) and the STREAMINFO MD5. The tests require the decoder's output to have that exact SHA-256 and MD5.
- **Streaming tests.** Every fixture is also decoded from a forward-only stream whose `Seek`, `Position` and
  `Length` throw and whose reads return 1–7 bytes at a time; a gated stream checks that the first PCM is
  delivered before the rest of the file has arrived.
- **Corruption tests.** Every fixture is truncated at many offsets and has single bytes flipped in the marker,
  the STREAMINFO block and throughout the audio; every case must throw `FlacDecodeException` without hanging.
  Dedicated tests cover CRC, MD5 and sample-count mismatches, unknown MD5/total, ID3v2 prefixes and trailing data.
- **CI.** Tests run on Linux, Windows and macOS, on both .NET 8 and .NET 10.

## Performance

PureFlac is a straightforward scalar port: no SIMD, no unsafe code. It is fast enough for real-time playback of
high-resolution audio, but slower than native decoders such as libFLAC or dr_flac.

One informal measurement (2026-09-25, Apple Silicon Mac, .NET 10 Release build, input held in a `MemoryStream`,
64 KiB reads, median of 7 runs; a **non-exclusive measurement** — other work was running on the machine, so treat
the numbers as orders of magnitude only):

| Input (synthetic sine + noise, encoded with `flac` 1.5.0) | Speed |
|---|---|
| 24-bit / 192 kHz stereo, 10 s, `flac -8` | about 120× real time |
| 16-bit / 44.1 kHz stereo, 30 s, `flac -5` | about 820× real time |

To measure on your own machine, decode a file into a reusable buffer in a loop (discarding the output) and divide
the audio duration (`StreamInfo.TotalSamples / SampleRate`) by the elapsed time; run a Release build and take the
median of several runs.

## Acknowledgements

- [dr_flac](https://github.com/mackron/dr_libs) by David Reid — the decoder is a port of its decoding core
  (public domain / MIT-0).
- [RFC 9639](https://www.rfc-editor.org/rfc/rfc9639) — the FLAC format specification, used for the checks that
  go beyond dr_flac.
- [libFLAC](https://xiph.org/flac/) by the Xiph.Org Foundation — the reference implementation used to produce the
  test fixtures and reference values.

## License

PureFlac is licensed under the [MIT License](LICENSE). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for
dr_flac and libFLAC.

---

## 繁體中文摘要

PureFlac 是移植自 dr_flac 的純 C# 串流 FLAC 解碼器，支援 .NET 8 與 .NET 10，不需要任何 native 函式庫。

- **串流**：只呼叫 `Stream.Read`，不 seek、不讀 `Position`／`Length`；每次只回 1 byte 或阻塞的輸入都能正確解碼，
  metadata 與第一個 frame 到達就能交出第一段 PCM。
- **bit-exact**：39 個合成測試檔的輸出與 libFLAC 1.5.0 解碼結果逐 byte 相同（SHA-256 比對），並驗證 STREAMINFO 的 MD5。
- **輸出格式**：交錯、little-endian、有號整數，每樣本 ceil(bps/8) bytes，樣本值右對齊（與 FLAC MD5 的定義相同）。
- **錯誤處理**：CRC 不符、保留值、截斷、結尾總樣本數或 MD5 不符都丟 `FlacDecodeException`，不 resync。
  結尾檢查在最後一次 `Read` 才做，之前交出的 PCM 可能屬於之後才判定損壞的串流；STREAMINFO 未記錄總樣本數與
  MD5 時，串流中段整個遺失的 frame 不會報錯。
- **不支援**：seek、Ogg FLAC、SIMD。
- **安裝**：`dotnet add package PureFlac`（[NuGet](https://www.nuget.org/packages/PureFlac)）；NuGet 上的套件只由 GitHub Actions 打包。授權為 MIT。
