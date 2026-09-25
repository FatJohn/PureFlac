#!/usr/bin/env python3
"""Helper for gen.sh: standard library only.

Subcommands:
  wav <in.s32le> <out.wav> <channels> <rate> <bps> [zero_low_bits]
      Reduces the full-scale 32-bit raw PCM produced by ffmpeg to <bps> bits (keeping the high bits,
      right-aligned), optionally clearing the lowest zero_low_bits bits to 0 (to create wasted bits), and
      writes it as WAVE_FORMAT_EXTENSIBLE (container ceil(bps/8) bytes, wValidBitsPerSample=bps, samples
      left-aligned as is customary for WAV) for the flac CLI to encode.
      flac's raw input only accepts 8/16/24/32 bps, so 12/20/4 bps have to go through WAV.
  pack <in.wav>
      Reads the WAV written by flac -d and repacks it as defined by the FLAC MD5 (ceil(bps/8) bytes per
      sample, little-endian, signed, right-aligned, interleaved), printing "sha256 md5 bps". 8-bit WAV is
      unsigned; it is converted back to signed here.
  synth <kind> <out.flac> <out.raw>
      Uses the minimal encoder in this file to write paths that neither the flac CLI nor ffmpeg can produce
      (kind: escape/varblock/side33/po10/po12/po15), and also writes the raw PCM before encoding (in the MD5 packing) so
      gen.sh can compare it with what libFLAC decodes.
"""
import hashlib
import random
import struct
import sys


# ---------------------------------------------------------------- WAV

def cmd_wav(src, dst, channels, rate, bps, zero_low=0):
    data = open(src, 'rb').read()
    n = len(data) // 4
    cont = (bps + 7) // 8
    vals = struct.unpack('<%di' % n, data[:n * 4])
    out = bytearray()
    mask = (1 << (cont * 8)) - 1
    for v in vals:
        s = v >> (32 - bps)
        if zero_low:
            s = (s >> zero_low) << zero_low
        out += ((s << (cont * 8 - bps)) & mask).to_bytes(cont, 'little')
    # Channel mask is always 0 (unspecified layout) so flac makes no layout assumptions for 3/6/8 channels
    guid = b'\x01\x00\x00\x00\x00\x00\x10\x00\x80\x00\x00\xaa\x00\x38\x9b\x71'
    fmt = struct.pack('<HHIIHHHHI', 0xFFFE, channels, rate, rate * channels * cont,
                      channels * cont, cont * 8, 22, bps, 0) + guid
    body = b'WAVE' + b'fmt ' + struct.pack('<I', len(fmt)) + fmt + b'data' + struct.pack('<I', len(out)) + bytes(out)
    open(dst, 'wb').write(b'RIFF' + struct.pack('<I', len(body)) + body)


def read_wav(path):
    d = open(path, 'rb').read()
    assert d[:4] == b'RIFF' and d[8:12] == b'WAVE', path
    pos = 12
    fmt = None
    while pos < len(d):
        cid, size = d[pos:pos + 4], struct.unpack('<I', d[pos + 4:pos + 8])[0]
        payload = d[pos + 8:pos + 8 + size]
        if cid == b'fmt ':
            fmt = payload
        elif cid == b'data':
            tag, ch, rate, _, align, cont_bits = struct.unpack('<HHIIHH', fmt[:16])
            valid = cont_bits
            if tag == 0xFFFE:
                valid = struct.unpack('<H', fmt[18:20])[0]
            return ch, rate, cont_bits, valid, payload
        pos += 8 + size + (size & 1)
    raise ValueError('no data chunk: ' + path)


def cmd_pack(path):
    ch, rate, cont_bits, bps, data = read_wav(path)
    cont = cont_bits // 8
    out_bytes = (bps + 7) // 8
    out = bytearray()
    shift = cont_bits - bps
    for i in range(0, len(data), cont):
        raw = int.from_bytes(data[i:i + cont], 'little', signed=(cont_bits != 8))
        if cont_bits == 8:
            raw -= 128
        if shift and (raw & ((1 << shift) - 1)):
            raise ValueError('WAV sample low bits are not 0 (not left-aligned): ' + path)
        s = raw >> shift
        out += (s & ((1 << (out_bytes * 8)) - 1)).to_bytes(out_bytes, 'little')
    print(hashlib.sha256(out).hexdigest(), hashlib.md5(out).hexdigest(), bps)


# ---------------------------------------------------------------- minimal FLAC encoder

CRC8 = []
CRC16 = []
for _i in range(256):
    c = _i
    for _ in range(8):
        c = ((c << 1) ^ 0x07) & 0xFF if c & 0x80 else (c << 1) & 0xFF
    CRC8.append(c)
    c = _i << 8
    for _ in range(8):
        c = ((c << 1) ^ 0x8005) & 0xFFFF if c & 0x8000 else (c << 1) & 0xFFFF
    CRC16.append(c)


def crc8(b):
    c = 0
    for x in b:
        c = CRC8[c ^ x]
    return c


def crc16(b):
    c = 0
    for x in b:
        c = ((c << 8) & 0xFFFF) ^ CRC16[(c >> 8) ^ x]
    return c


class Bits:
    def __init__(self):
        self.acc = 0
        self.n = 0

    def put(self, value, width):
        if width:
            self.acc = (self.acc << width) | (value & ((1 << width) - 1))
            self.n += width

    def unary(self, zeros):
        self.put(0, zeros)
        self.put(1, 1)

    def tobytes(self):
        pad = (-self.n) % 8
        return (self.acc << pad).to_bytes((self.n + pad) // 8, 'big')


def utf8_number(v):
    if v < 0x80:
        return bytes([v])
    for n in range(2, 8):
        if v < (1 << (5 * n + 1)):
            break
    out = []
    for _ in range(n - 1):
        out.append(0x80 | (v & 0x3F))
        v >>= 6
    first = ((0xFF00 >> n) & 0xFF) | v
    return bytes([first] + out[::-1])


FIXED = [[], [1], [2, -1], [3, -3, 1], [4, -6, 4, -1]]


def residuals_fixed(x, order):
    return [x[i] - sum(c * x[i - 1 - j] for j, c in enumerate(FIXED[order])) for i in range(order, len(x))]


def zigzag(r):
    return 2 * r if r >= 0 else -2 * r - 1


def put_residual(bits, res, blocksize, order, method, partition_order, plan):
    """plan[p] = ('rice', k) or ('escape', None); the raw width of an escape is the minimum needed for that partition's values."""
    bits.put(method, 2)
    bits.put(partition_order, 4)
    per = blocksize >> partition_order
    pos = 0
    for p in range(1 << partition_order):
        cnt = per - (order if p == 0 else 0)
        part = res[pos:pos + cnt]
        pos += cnt
        kind, k = plan[p]
        esc = 15 if method == 0 else 31
        pbits = 4 if method == 0 else 5
        if kind == 'rice':
            bits.put(k, pbits)
            for r in part:
                u = zigzag(r)
                bits.unary(u >> k)
                bits.put(u & ((1 << k) - 1), k)
        else:
            width = 0
            for r in part:
                w = 1
                while not (-(1 << (w - 1)) <= r < (1 << (w - 1))):
                    w += 1
                width = max(width, w if r != 0 else 0)
            bits.put(esc, pbits)
            bits.put(width, 5)
            for r in part:
                bits.put(r, width)


def put_subframe(bits, x, sub_bps, spec):
    kind = spec['type']
    wasted = spec.get('wasted', 0)
    if wasted:
        assert all((v & ((1 << wasted) - 1)) == 0 for v in x)
        x = [v >> wasted for v in x]
        sub_bps -= wasted
    tcode = {'constant': 0, 'verbatim': 1}.get(kind)
    if kind == 'fixed':
        tcode = 8 | spec['order']
    bits.put(0, 1)
    bits.put(tcode, 6)
    bits.put(1 if wasted else 0, 1)
    if wasted:
        bits.unary(wasted - 1)
    if kind == 'constant':
        assert len(set(x)) == 1
        bits.put(x[0], sub_bps)
    elif kind == 'verbatim':
        for v in x:
            bits.put(v, sub_bps)
    else:
        order = spec['order']
        for v in x[:order]:
            bits.put(v, sub_bps)
        put_residual(bits, residuals_fixed(x, order), len(x), order,
                     spec.get('method', 0), spec['po'], spec['plan'])


BPS_CODE = {8: 1, 12: 2, 16: 4, 20: 5, 24: 6, 32: 7}
RATE_CODE = {88200: 1, 176400: 2, 192000: 3, 8000: 4, 16000: 5, 22050: 6, 24000: 7, 32000: 8, 44100: 9, 48000: 10, 96000: 11}


def blocksize_code(bs):
    if bs == 192:
        return 1, b''
    for k in range(4):
        if bs == 576 << k:
            return 2 + k, b''
    for k in range(8):
        if bs == 256 << k:
            return 8 + k, b''
    if bs <= 256:
        return 6, bytes([bs - 1])
    return 7, struct.pack('>H', bs - 1)


def frame(bps, rate, variable, number, assign, chans, specs):
    bs = len(chans[0])
    bcode, bextra = blocksize_code(bs)
    hdr = bytes([0xFF, 0xF8 | (1 if variable else 0),
                 (bcode << 4) | RATE_CODE.get(rate, 0),
                 (assign << 4) | (BPS_CODE.get(bps, 0) << 1)]) + utf8_number(number) + bextra
    hdr += bytes([crc8(hdr)])
    # Stereo decorrelation: the side channel has 1 extra bit
    if assign == 8:
        subs = [chans[0], [l - r for l, r in zip(chans[0], chans[1])]]
        sbps = [bps, bps + 1]
    elif assign == 9:
        subs = [[l - r for l, r in zip(chans[0], chans[1])], chans[1]]
        sbps = [bps + 1, bps]
    elif assign == 10:
        subs = [[(l + r) >> 1 for l, r in zip(chans[0], chans[1])], [l - r for l, r in zip(chans[0], chans[1])]]
        sbps = [bps, bps + 1]
    else:
        subs = chans
        sbps = [bps] * len(chans)
    bits = Bits()
    bits.acc = int.from_bytes(hdr, 'big')
    bits.n = len(hdr) * 8
    for x, b, spec in zip(subs, sbps, specs):
        put_subframe(bits, x, b, spec)
    body = bits.tobytes()
    return body + struct.pack('>H', crc16(body))


def stream(bps, rate, channels, frames_bytes, blocksizes, samples_raw, total):
    md5 = hashlib.md5(samples_raw).digest()
    minb = min(blocksizes[:-1] or blocksizes)
    maxb = max(blocksizes)
    si = Bits()
    for v, w in ((minb, 16), (maxb, 16), (0, 24), (0, 24), (rate, 20), (channels - 1, 3), (bps - 1, 5), (total, 36)):
        si.put(v, w)
    si = si.tobytes() + md5
    return b'fLaC' + bytes([0x80, 0, 0, 34]) + si + b''.join(frames_bytes)


def pack_samples(chans, bps):
    nb = (bps + 7) // 8
    out = bytearray()
    for i in range(len(chans[0])):
        for c in chans:
            out += (c[i] & ((1 << (nb * 8)) - 1)).to_bytes(nb, 'little')
    return bytes(out)


def noise(rng, n, amp):
    return [rng.randint(-amp, amp) for _ in range(n)]


def build(kind):
    rng = random.Random(123)
    frames = []
    blocks = []
    allch = None
    if kind == 'escape':
        # 16-bit stereo, fixed block 1024: FIXED order 2 residual partitions mixing rice/escape (including
        # width-0 escapes: the second difference of a linear ramp is all 0), the RICE2 escape (31),
        # wasted bits + verbatim, constant.
        bps, rate, ch = 16, 44100, 2
        for f in range(4):
            bs = 1024
            ramp_start = rng.randint(-3000, 3000)
            if f == 3:
                # The whole frame is a linear ramp: the second difference is all 0, every partition is a width-0 escape
                left = [ramp_start + 7 * i for i in range(bs)]
                plan_l = [('escape', None)] * 4
            else:
                left = noise(rng, 256, 20) + [ramp_start + 7 * i for i in range(256)] + noise(rng, 256, 30000) + noise(rng, 256, 3)
                plan_l = [('rice', 5), ('escape', None), ('escape', None), ('rice', 1)]
            right = [((v >> 4) << 4) for v in noise(rng, bs, 30000)] if f % 2 == 0 else [1234] * bs
            spec_l = {'type': 'fixed', 'order': 2, 'po': 2, 'method': f % 2, 'plan': plan_l}
            spec_r = {'type': 'verbatim', 'wasted': 4} if f % 2 == 0 else {'type': 'constant'}
            frames.append(frame(bps, rate, False, f, 1, [left, right], [spec_l, spec_r]))
            blocks.append(bs)
            allch = [left, right] if allch is None else [allch[0] + left, allch[1] + right]
    elif kind == 'varblock':
        # Variable block size (blocking strategy 1, the header carries the sample number): sizes deliberately
        # cover blocksize codes 1 (192), 2-5 (576*2^k), 6 (explicit 8-bit), 7 (explicit 16-bit) and 8-15 (256*2^k);
        # 24-bit/192k mono (about 0.15 s in total).
        bps, rate, ch = 24, 192000, 1
        sizes = [4096, 192, 1000, 16, 576, 2304, 256, 4608, 37, 16384, 100]
        pos = 0
        for i, bs in enumerate(sizes):
            x = [int(3000000 * ((j + pos) % 97 - 48) / 48) + rng.randint(-5000, 5000) for j in range(bs)]
            po = 0
            while po < 3 and bs % (2 << po) == 0 and (bs >> (po + 1)) > 2:
                po += 1
            plan = [('rice', 14 + (p % 3)) if p % 2 == 0 else ('escape', None) for p in range(1 << po)]
            spec = {'type': 'fixed', 'order': min(2, bs - 1), 'po': po, 'method': 1, 'plan': plan} if i % 3 != 2 else {'type': 'verbatim'}
            frames.append(frame(bps, rate, True, pos, 0, [x], [spec]))
            blocks.append(bs)
            allch = [x] if allch is None else [allch[0] + x]
            pos += bs
    elif kind == 'side33':
        # left/side, right/side and mid/side for 32-bit stereo: the side channel is a 33-bit subframe (both
        # verbatim and FIXED order 1 + rice/escape); the flac CLI only picks whichever decorrelation it deems best.
        bps, rate, ch = 32, 96000, 2
        for f, assign in enumerate([8, 9, 10, 1, 8, 10]):
            bs = 512
            saw = [int(2000000000 * ((j % 50) - 25) / 25) for j in range(bs)]
            left = [rng.randint(-(1 << 31), (1 << 31) - 1) for _ in range(bs)] if f in (0, 2) else saw
            right = [rng.randint(-(1 << 31), (1 << 31) - 1) for _ in range(bs)]
            if f >= 4:
                # side = j*1000: the first difference is always 1000, so FIXED order 1 works (the residual must fit in int32)
                right = [l - (j * 1000) for j, l in enumerate(left)]
                spec_side = {'type': 'fixed', 'order': 1, 'po': 1, 'method': 1, 'plan': [('rice', 12), ('escape', None)]}
            else:
                spec_side = {'type': 'verbatim'}
            if assign == 8:
                specs = [{'type': 'verbatim'}, spec_side]
            elif assign == 9:
                specs = [spec_side, {'type': 'verbatim'}]
            elif assign == 10:
                specs = [{'type': 'verbatim'}, spec_side]
            else:
                specs = [{'type': 'verbatim'}, {'type': 'verbatim'}]
            frames.append(frame(bps, rate, False, f, assign, [left, right], specs))
            blocks.append(bs)
            allch = [left, right] if allch is None else [allch[0] + left, allch[1] + right]
    elif kind in ('po10', 'po12', 'po15'):
        # Rice partition orders above what the flac CLI picks for these block sizes (10, 12 and the maximum 15),
        # 16-bit mono, FIXED subframes, each partition with its own parameter (rice, RICE2 and escapes mixed).
        # po12 (4096-sample blocks) and po15 (32768-sample blocks) have one sample per partition, so the first
        # partition of their order-1 predictor holds none (partition size == predictor order, the smallest legal case).
        po = int(kind[2:])
        bps, ch = 16, 1
        rate, sizes, order = {10: (44100, [4096, 4096], 2), 12: (48000, [4096, 4096], 1), 15: (192000, [32768], 1)}[po]
        pos = 0
        for i, bs in enumerate(sizes):
            # A slow ramp plus +-1 noise keeps the residuals tiny, so the files stay small even with 1-4 samples per partition
            x = [(j + pos) // 16 - 1000 + rng.randint(-1, 1) for j in range(bs)]
            plan = [('escape', None) if p % 7 == 3 else ('rice', p % 3) for p in range(1 << po)]
            spec = {'type': 'fixed', 'order': order, 'po': po, 'method': i % 2 if len(sizes) > 1 else 1, 'plan': plan}
            frames.append(frame(bps, rate, False, i, 0, [x], [spec]))
            blocks.append(bs)
            allch = [x] if allch is None else [allch[0] + x]
            pos += bs
    else:
        raise ValueError(kind)
    raw = pack_samples(allch, bps)
    return stream(bps, rate, ch, frames, blocks, raw, len(allch[0])), raw


def main(argv):
    if argv[0] == 'wav':
        cmd_wav(argv[1], argv[2], int(argv[3]), int(argv[4]), int(argv[5]), int(argv[6]) if len(argv) > 6 else 0)
    elif argv[0] == 'pack':
        cmd_pack(argv[1])
    elif argv[0] == 'synth':
        data, raw = build(argv[1])
        open(argv[2], 'wb').write(data)
        open(argv[3], 'wb').write(raw)
    else:
        raise SystemExit('unknown command')


if __name__ == '__main__':
    main(sys.argv[1:])
