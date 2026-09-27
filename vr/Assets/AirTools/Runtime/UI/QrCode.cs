using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AirTools.UI
{
    /// A small QR Code Model 2 encoder (ISO/IEC 18004), enough for the links the headset shows (the job packet, the
    /// report, a booth post): byte mode (UTF-8), error correction level M, versions 1–10 (up to 213 bytes, so a
    /// ~200-character URL), the mask picked by the standard penalty score (the four rules as ZXing's MaskUtil counts
    /// them). Pure C#: the matrix, codewords, format and version bits are unit-tested offline against Kazuhiko Arase's
    /// reference implementation and the ISO worked example's Reed–Solomon block. `ToTexture` draws it with a quiet
    /// zone for a glass panel.
    public sealed class QrCode
    {
        public const int MinVersion = 1, MaxVersion = 10;
        /// Level M's format indicator bits (L = 01, M = 00, Q = 11, H = 10).
        public const int EccBitsM = 0;
        /// Quiet zone (light border) in modules; the spec asks for 4.
        public const int QuietZone = 4;

        // Level M, indexed by version: error correction codewords per block, blocks, total codewords.
        static readonly int[] s_EcPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        static readonly int[] s_Blocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
        static readonly int[] s_TotalCodewords = { 0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
        static readonly int[][] s_Align =
        {
            new int[0], new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
            new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
        };

        public int Version { get; }
        public int Size { get; }
        public int Mask { get; }
        readonly bool[,] m_Modules;    // [row, col], true = dark
        readonly bool[,] m_Function;   // finder, timing, alignment, format, version, dark module

        /// Dark module at (row, col).
        public bool this[int row, int col] => m_Modules[row, col];
        public bool IsFunction(int row, int col) => m_Function[row, col];

        QrCode(int version, int mask, bool[,] modules, bool[,] function)
        {
            Version = version; Size = 17 + 4 * version; Mask = mask; m_Modules = modules; m_Function = function;
        }

        // ---------------- capacity ----------------

        public static int TotalCodewords(int version) => s_TotalCodewords[version];
        public static int EcCodewordsPerBlock(int version) => s_EcPerBlock[version];
        public static int BlockCount(int version) => s_Blocks[version];
        public static int DataCodewords(int version) => s_TotalCodewords[version] - s_EcPerBlock[version] * s_Blocks[version];
        public static int CountBits(int version) => version < 10 ? 8 : 16;
        public static int RemainderBits(int version) => version >= 2 && version <= 6 ? 7 : 0;
        public static int[] AlignmentCentres(int version) => (int[])s_Align[version].Clone();

        /// Most bytes a version holds in byte mode at level M (14, 26, 42 … 213).
        public static int Capacity(int version) => (DataCodewords(version) * 8 - 4 - CountBits(version)) / 8;

        /// The smallest version (1–10) that holds `byteCount` bytes, or −1 when none does.
        public static int VersionFor(int byteCount, int minVersion = MinVersion)
        {
            for (int v = Math.Max(MinVersion, minVersion); v <= MaxVersion; v++)
                if (byteCount <= Capacity(v)) return v;
            return -1;
        }

        public static int MaxBytes => Capacity(MaxVersion);

        // ---------------- encode ----------------

        /// Encodes `text` as UTF-8. mask −1 = the lowest penalty (else forced 0–7). Null when it doesn't fit version 10.
        public static QrCode Encode(string text, int mask = -1, int minVersion = MinVersion) =>
            EncodeBytes(Encoding.UTF8.GetBytes(text ?? ""), mask, minVersion);

        public static QrCode EncodeBytes(byte[] data, int mask = -1, int minVersion = MinVersion)
        {
            if (data == null) data = new byte[0];
            if (mask < -1 || mask > 7) throw new ArgumentOutOfRangeException(nameof(mask));
            int version = VersionFor(data.Length, minVersion);
            if (version < 0) return null;
            var codewords = Codewords(data, version);
            int size = 17 + 4 * version;
            var function = new bool[size, size];
            var baseModules = new bool[size, size];
            DrawFunctionPatterns(version, baseModules, function);
            DrawCodewords(codewords, baseModules, function);

            int best = mask;
            if (best < 0)
            {
                int bestScore = int.MaxValue;
                for (int m = 0; m < 8; m++)
                {
                    int score = Penalty(Masked(version, m, baseModules, function));
                    if (score < bestScore) { bestScore = score; best = m; }
                }
            }
            return new QrCode(version, best, Masked(version, best, baseModules, function), function);
        }

        static bool[,] Masked(int version, int mask, bool[,] baseModules, bool[,] function)
        {
            int size = baseModules.GetLength(0);
            var m = (bool[,])baseModules.Clone();
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (!function[r, c] && MaskBit(mask, r, c)) m[r, c] = !m[r, c];
            DrawFormatBits(mask, m);
            return m;
        }

        /// The eight data masks (i = row, j = column).
        public static bool MaskBit(int mask, int i, int j)
        {
            switch (mask)
            {
                case 0: return (i + j) % 2 == 0;
                case 1: return i % 2 == 0;
                case 2: return j % 3 == 0;
                case 3: return (i + j) % 3 == 0;
                case 4: return (i / 2 + j / 3) % 2 == 0;
                case 5: return i * j % 2 + i * j % 3 == 0;
                case 6: return (i * j % 2 + i * j % 3) % 2 == 0;
                case 7: return ((i + j) % 2 + i * j % 3) % 2 == 0;
                default: throw new ArgumentOutOfRangeException(nameof(mask));
            }
        }

        // ---------------- codewords ----------------

        /// Data codewords for `data` in byte mode at `version` (mode, count, bytes, terminator, byte pad, 0xEC/0x11 pad).
        public static byte[] DataCodewordsFor(byte[] data, int version)
        {
            int capacityBits = DataCodewords(version) * 8;
            var bits = new BitWriter();
            bits.Put(0b0100, 4);
            bits.Put(data.Length, CountBits(version));
            foreach (var b in data) bits.Put(b, 8);
            if (bits.Length > capacityBits) throw new ArgumentException("data too long for this version");
            bits.Put(0, Math.Min(4, capacityBits - bits.Length));
            bits.Put(0, (8 - bits.Length % 8) % 8);
            var bytes = bits.ToBytes();
            var result = new byte[DataCodewords(version)];
            Array.Copy(bytes, result, bytes.Length);
            for (int i = bytes.Length, k = 0; i < result.Length; i++, k++) result[i] = (byte)(k % 2 == 0 ? 0xEC : 0x11);
            return result;
        }

        /// The final codeword sequence: data blocks interleaved, then their error correction interleaved.
        public static byte[] Codewords(byte[] data, int version)
        {
            var dataCw = DataCodewordsFor(data, version);
            int blocks = s_Blocks[version], ec = s_EcPerBlock[version];
            int shortLen = dataCw.Length / blocks, longBlocks = dataCw.Length % blocks;   // short blocks come first
            var dataBlocks = new byte[blocks][];
            var ecBlocks = new byte[blocks][];
            for (int b = 0, k = 0; b < blocks; b++)
            {
                int len = shortLen + (b >= blocks - longBlocks ? 1 : 0);
                dataBlocks[b] = new byte[len];
                Array.Copy(dataCw, k, dataBlocks[b], 0, len);
                k += len;
                ecBlocks[b] = ReedSolomon(dataBlocks[b], ec);
            }
            var result = new List<byte>(s_TotalCodewords[version]);
            for (int i = 0; i <= shortLen; i++)
                for (int b = 0; b < blocks; b++)
                    if (i < dataBlocks[b].Length) result.Add(dataBlocks[b][i]);
            for (int i = 0; i < ec; i++)
                for (int b = 0; b < blocks; b++) result.Add(ecBlocks[b][i]);
            return result.ToArray();
        }

        // GF(256) with the QR polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D).
        static readonly byte[] s_Exp = new byte[512];
        static readonly byte[] s_Log = new byte[256];

        static QrCode()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                s_Exp[i] = (byte)x;
                s_Log[x] = (byte)i;
                x <<= 1;
                if (x >= 256) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) s_Exp[i] = s_Exp[i - 255];
        }

        public static byte GfMul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : s_Exp[s_Log[a] + s_Log[b]];
        public static byte GfPow2(int e) => s_Exp[((e % 255) + 255) % 255];

        /// The generator polynomial Π (x − α^i), i = 0 … degree−1, highest coefficient (1) first.
        public static byte[] Generator(int degree)
        {
            var g = new byte[] { 1 };
            for (int i = 0; i < degree; i++)
            {
                var next = new byte[g.Length + 1];
                byte root = GfPow2(i);
                for (int k = 0; k < g.Length; k++)
                {
                    next[k] ^= g[k];
                    next[k + 1] ^= GfMul(g[k], root);
                }
                g = next;
            }
            return g;
        }

        /// Reed–Solomon error correction codewords for one block: the remainder of data·x^ec divided by the generator.
        public static byte[] ReedSolomon(byte[] data, int ecCount)
        {
            var gen = Generator(ecCount);
            var rem = new byte[ecCount];
            foreach (var d in data)
            {
                byte factor = (byte)(d ^ rem[0]);
                Array.Copy(rem, 1, rem, 0, ecCount - 1);
                rem[ecCount - 1] = 0;
                for (int k = 0; k < ecCount; k++) rem[k] ^= GfMul(gen[k + 1], factor);
            }
            return rem;
        }

        // ---------------- format and version information ----------------

        /// The 15 format bits for level M and `mask`: 5 data bits, BCH(15,5) with 0x537, XOR 0x5412.
        public static int FormatBits(int mask)
        {
            int data = EccBitsM << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            return (data << 10 | rem) ^ 0x5412;
        }

        /// The 18 version bits (versions 7+): 6 data bits, BCH(18,6) with 0x1F25.
        public static int VersionBits(int version)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            return version << 12 | rem;
        }

        static bool Bit(int x, int i) => ((x >> i) & 1) != 0;

        static void DrawFormatBits(int mask, bool[,] m)
        {
            int size = m.GetLength(0);
            int bits = FormatBits(mask);
            // Around the top-left finder.
            for (int i = 0; i <= 5; i++) m[i, 8] = Bit(bits, i);
            m[7, 8] = Bit(bits, 6);
            m[8, 8] = Bit(bits, 7);
            m[8, 7] = Bit(bits, 8);
            for (int i = 9; i < 15; i++) m[8, 14 - i] = Bit(bits, i);
            // The copy split between the other two finders.
            for (int i = 0; i < 8; i++) m[8, size - 1 - i] = Bit(bits, i);
            for (int i = 8; i < 15; i++) m[size - 15 + i, 8] = Bit(bits, i);
            m[size - 8, 8] = true;   // the dark module
        }

        /// Where the 15 format bits sit (row, col), first copy then second (bit i at index i and 15 + i). Tests read them back.
        public static (int row, int col)[] FormatPositions(int size)
        {
            var p = new (int, int)[30];
            for (int i = 0; i <= 5; i++) p[i] = (i, 8);
            p[6] = (7, 8); p[7] = (8, 8); p[8] = (8, 7);
            for (int i = 9; i < 15; i++) p[i] = (8, 14 - i);
            for (int i = 0; i < 8; i++) p[15 + i] = (8, size - 1 - i);
            for (int i = 8; i < 15; i++) p[15 + i] = (size - 15 + i, 8);
            return p;
        }

        // ---------------- function patterns ----------------

        static void DrawFunctionPatterns(int version, bool[,] m, bool[,] f)
        {
            int size = m.GetLength(0);
            void Set(int r, int c, bool dark) { m[r, c] = dark; f[r, c] = true; }

            for (int i = 0; i < size; i++)   // timing
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }
            foreach (var (r0, c0) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })   // finders + separators
                for (int dr = -1; dr <= 7; dr++)
                    for (int dc = -1; dc <= 7; dc++)
                    {
                        int r = r0 + dr, c = c0 + dc;
                        if (r < 0 || r >= size || c < 0 || c >= size) continue;
                        bool ring = (dr >= 0 && dr <= 6 && (dc == 0 || dc == 6)) || (dc >= 0 && dc <= 6 && (dr == 0 || dr == 6));
                        bool core = dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4;
                        Set(r, c, ring || core);
                    }
            var align = s_Align[version];
            for (int a = 0; a < align.Length; a++)
                for (int b = 0; b < align.Length; b++)
                {
                    bool finder = (a == 0 && b == 0) || (a == 0 && b == align.Length - 1) || (a == align.Length - 1 && b == 0);
                    if (finder) continue;
                    for (int dr = -2; dr <= 2; dr++)
                        for (int dc = -2; dc <= 2; dc++)
                            Set(align[a] + dr, align[b] + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
                }
            // Reserve the format areas (drawn per mask) and the dark module.
            DrawFormatBits(0, m);
            foreach (var (r, c) in FormatPositions(size)) f[r, c] = true;
            f[size - 8, 8] = true;
            if (version >= 7)
            {
                int bits = VersionBits(version);
                for (int i = 0; i < 18; i++)
                {
                    int a = size - 11 + i % 3, b = i / 3;
                    Set(b, a, Bit(bits, i));   // top right: row b, col a
                    Set(a, b, Bit(bits, i));   // bottom left
                }
            }
        }

        /// Places the codeword bits in the two-column zigzag (right to left, skipping the vertical timing column).
        static void DrawCodewords(byte[] codewords, bool[,] m, bool[,] f)
        {
            int size = m.GetLength(0);
            int i = 0, total = codewords.Length * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                bool upward = ((right + 1) & 2) == 0;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int c = right - j, r = upward ? size - 1 - vert : vert;
                        if (f[r, c]) continue;
                        if (i < total) m[r, c] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;   // remainder bits stay light
                        i++;
                    }
            }
        }

        /// The module order DrawCodewords fills (row, col); tests use it to read the codewords back.
        public static List<(int row, int col)> DataOrder(QrCode q)
        {
            var list = new List<(int, int)>();
            int size = q.Size;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                bool upward = ((right + 1) & 2) == 0;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int c = right - j, r = upward ? size - 1 - vert : vert;
                        if (!q.m_Function[r, c]) list.Add((r, c));
                    }
            }
            return list;
        }

        // ---------------- penalty (mask selection) ----------------

        public const int N1 = 3, N2 = 3, N3 = 40, N4 = 10;

        public static int Penalty(bool[,] m) => PenaltyRuns(m) + PenaltyBlocks(m) + PenaltyFinderLike(m) + PenaltyBalance(m);

        /// Rule 1: each run of ≥ 5 same-colour modules in a row or column scores 3 + (run − 5).
        public static int PenaltyRuns(bool[,] m) => Runs(m, true) + Runs(m, false);

        static int Runs(bool[,] m, bool horizontal)
        {
            int size = m.GetLength(0), penalty = 0;
            for (int i = 0; i < size; i++)
            {
                int run = 0;
                bool prev = false;
                for (int j = 0; j < size; j++)
                {
                    bool bit = horizontal ? m[i, j] : m[j, i];
                    if (j > 0 && bit == prev) run++;
                    else
                    {
                        if (run >= 5) penalty += N1 + (run - 5);
                        run = 1;
                        prev = bit;
                    }
                }
                if (run >= 5) penalty += N1 + (run - 5);
            }
            return penalty;
        }

        /// Rule 2: 3 for every 2×2 block of one colour (overlapping blocks count separately).
        public static int PenaltyBlocks(bool[,] m)
        {
            int size = m.GetLength(0), penalty = 0;
            for (int r = 0; r < size - 1; r++)
                for (int c = 0; c < size - 1; c++)
                {
                    bool v = m[r, c];
                    if (v == m[r, c + 1] && v == m[r + 1, c] && v == m[r + 1, c + 1]) penalty += N2;
                }
            return penalty;
        }

        /// Rule 3: 40 for every 1:1:3:1:1 finder-like run (dark light dark dark dark light dark) with 4 light modules on
        /// either side, in rows and columns; outside the symbol counts as light.
        public static int PenaltyFinderLike(bool[,] m)
        {
            int size = m.GetLength(0), count = 0;
            bool At(int line, int k, bool horizontal) => k >= 0 && k < size && (horizontal ? m[line, k] : m[k, line]);
            bool Light(int line, int from, int to, bool horizontal)
            {
                for (int k = Math.Max(0, from); k < Math.Min(size, to); k++) if (At(line, k, horizontal)) return false;
                return true;
            }
            foreach (bool h in new[] { true, false })
                for (int line = 0; line < size; line++)
                    for (int k = 0; k + 6 < size; k++)
                        if (At(line, k, h) && !At(line, k + 1, h) && At(line, k + 2, h) && At(line, k + 3, h) && At(line, k + 4, h)
                            && !At(line, k + 5, h) && At(line, k + 6, h)
                            && (Light(line, k - 4, k, h) || Light(line, k + 7, k + 11, h)))
                            count++;
            return count * N3;
        }

        /// Rule 4: 10 for every whole 5 % the dark share is away from 50 %.
        public static int PenaltyBalance(bool[,] m)
        {
            int size = m.GetLength(0), dark = 0, total = size * size;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (m[r, c]) dark++;
            return Math.Abs(dark * 2 - total) * 10 / total * N4;
        }

        /// The matrix as rows of bools (tests; the penalty of the chosen mask).
        public bool[,] Modules => (bool[,])m_Modules.Clone();

        // ---------------- rendering ----------------

        /// One pixel per module with a `quiet`-module light border (dark = black, light = white: scanners need the
        /// contrast, so these aren't theme colours). Row 0 of the array is the texture's bottom row (Unity's order).
        public Color32[] Pixels(int quiet, out int width)
        {
            width = Size + 2 * quiet;
            var px = new Color32[width * width];
            var light = new Color32(255, 255, 255, 255);
            var dark = new Color32(0, 0, 0, 255);
            for (int y = 0; y < width; y++)
                for (int x = 0; x < width; x++)
                {
                    int r = width - 1 - y - quiet, c = x - quiet;   // texture y grows up; QR rows grow down
                    bool d = r >= 0 && r < Size && c >= 0 && c < Size && m_Modules[r, c];
                    px[y * width + x] = d ? dark : light;
                }
            return px;
        }

        /// A point-filtered texture of the code with its quiet zone, `scale` pixels per module.
        public Texture2D ToTexture(int quiet = QuietZone, int scale = 4)
        {
            scale = Math.Max(1, scale);
            var modules = Pixels(quiet, out int w);
            var px = new Color32[w * scale * w * scale];
            for (int y = 0; y < w * scale; y++)
                for (int x = 0; x < w * scale; x++)
                    px[y * w * scale + x] = modules[(y / scale) * w + x / scale];
            var tex = new Texture2D(w * scale, w * scale, TextureFormat.RGBA32, false)
            {
                name = $"QR v{Version} mask {Mask}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// The matrix as text rows ("1" dark), for logs and tests.
        public string[] Rows()
        {
            var rows = new string[Size];
            var sb = new StringBuilder(Size);
            for (int r = 0; r < Size; r++)
            {
                sb.Clear();
                for (int c = 0; c < Size; c++) sb.Append(m_Modules[r, c] ? '1' : '0');
                rows[r] = sb.ToString();
            }
            return rows;
        }

        sealed class BitWriter
        {
            readonly List<bool> m_Bits = new List<bool>();
            public int Length => m_Bits.Count;

            public void Put(int value, int count)
            {
                for (int i = count - 1; i >= 0; i--) m_Bits.Add(((value >> i) & 1) != 0);
            }

            public byte[] ToBytes()
            {
                var bytes = new byte[(m_Bits.Count + 7) / 8];
                for (int i = 0; i < m_Bits.Count; i++) if (m_Bits[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
                return bytes;
            }
        }
    }
}
