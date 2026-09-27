using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AirTools.UI;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// The in-app QR encoder (Grok G3: show_packet / show_report / the booth post's link) against Kazuhiko Arase's
    /// reference implementation ("QRCode for JavaScript", MIT, as vendored by npm's qrcode-terminal: versions 1–10 at
    /// level M, every mask forced once), the ISO/IEC 18004 worked example's Reed–Solomon block, the format / version BCH
    /// codes, a read-back decoder (syndromes zero, payload back) and the four penalty rules. Pure: runs offline.
    public class QrCodeTests
    {
        public sealed class RefCase
        {
            public readonly string Text; public readonly int Version, Mask; public readonly string Codewords, Rows;
            public RefCase(string text, int version, int mask, string codewords, string rows)
            { Text = text; Version = version; Mask = mask; Codewords = codewords; Rows = rows; }
            public override string ToString() => $"v{Version} mask {Mask} ({Text.Length} chars)";
        }

        /// Arase's output (tools: g3-build/qrref/gen2.js): payload, its version, the mask forced for the matrix, the full
        /// codeword sequence (data + EC, interleaved) and the masked matrix as hex rows (4 modules per digit, MSB first).
        static readonly RefCase[] s_Ref =
        {
            new RefCase("http://h.io/", 1, 0,
                "40c687474703a2f2f682e696f2f0ec112c84412c21acb1462432",
                "fe0bf8 82ca08 ba62e8 ba52e8 bafae8 825208"
                    + " feabf8 004000 aa6890 5c5788 4a93b8 4cbc90"
                    + " ee3140 00d398 fe08b8 824890 baf150 ba7fd0"
                    + " ba8ea8 8229d0 feb058"),
            new RefCase("http://h.io/bipw3-elsz6aho", 2, 1,
                "41a687474703a2f2f682e696f2f62697077332d656c737a3661686f0ffe860a30d139c176be71299e340d024",
                "febabf8 8242a08 bae4ae8 ba46ae8 ba5eae8 8292a08"
                    + " feaabf8 005b800 a341128 093db58 6f65d68 1c385c0"
                    + " 9f29308 0de3a18 f3f31e8 11fa680 e648f90 009e8a8"
                    + " fefaac8 8221888 ba0bfd0 ba3a8f0 ba91c98 8240f80"
                    + " feb8848"),
            new RefCase("http://h.io/cjqx4_fmt07bipw3-elsz6ahov29", 3, 2,
                "428687474703a2f2f682e696f2f636a7178345f666d74303762697077332d656c737a3661686f7632390ec11fa2b2d57"
                    + "190e1ea51821c5aa88cfe391068a9946af734cb7625b",
                "fe6493f8 824f4208 babb02e8 bade4ae8 bad2b2e8 8297e208"
                    + " feaaabf8 00c92800 be4d4be0 e932ff88 437d2d00 bc488b10"
                    + " 3365d060 85ae1ea8 f6112720 400a3f10 0e80c020 f8149be8"
                    + " 8ac7cde0 a8041810 8fc3ffb8 008098f8 fe796ae0 829b98d0"
                    + " baa06fb8 babee038 babf9390 825f9e10 fe8d4520"),
            new RefCase("http://h.io/dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu", 4, 3,
                "43d7c6438703477647260397a207f273f63282d6e65696c7f237f6a34666b716278693f75263f6237696e74653b71327"
                    + "86933652a7f6177683e74550f6ec6611c3969121be315cb62bb76f5bc77504a885d9b86aed12fa971806cc721d8f0197"
                    + "a8e04a74",
                "fef4d53f8 82f487208 ba093aae8 baf2422e8 ba22742e8 823a16208"
                    + " feaaaabf8 009ae2000 b70a5d258 10119f278 c74209bc8 697e09b50"
                    + " aec1d30c0 1d4db0810 1fae9ef60 1858a5860 93afbf2e0 4853cd6c8"
                    + " 5ff92c6a0 616070f88 1223dd960 a13491228 065a89478 455720ed0"
                    + " a6d9c1f98 0099d78d0 fed83da80 82811f8b0 ba5a17fb0 bae341028"
                    + " baa5e6360 8245d3e88 fe8377060"),
            new RefCase("http://h.io/elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_f", 5, 4,
                "45d706438703477647260397a207f273f63282d6e65696c7f237f6a35666c7163786a3f7666316238696f74663b72327"
                    + "96934652b7f6277693e75253f6137686e73653a7131786833645a7f6176083ec4511f6ec6611ab8879d4ea54da9b69d7"
                    + "8a2c8c7393d7166b0c782dd8d9909a6adb7805014e703be39a1e453bf83618f87275b3601c9b",
                "fe8e45bbf8 8251342208 ba2fc59ae8 bad02942e8 bae16f1ae8 82bd368a08"
                    + " feaaaaabf8 0092c5a800 8b85f607c8 3cda01d8d0 8e4703f300 618df71fb0"
                    + " 6a0a478f38 80f7ab79d0 cffbe9f870 21524d10b0 ef841f3768 e1d2c55990"
                    + " 2afa8f1fc0 ed9e542030 87d3540720 641bc7f890 27192153f0 d0b67e15f0"
                    + " 6b53ce2e78 9ddc2315a0 025f459ba0 300de40b30 e34ecd1fe8 00a081a8b0"
                    + " fe83ad0ac0 82135ca8e0 ba98348fb0 ba00ef3b18 ba60c96660 826445a5b0"
                    + " feabfe1478"),
            new RefCase("http://h.io/fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3", 6, 5,
                "4637d77646a343e787660353471676134786268603f79736a26307a7f2237317f69632838246d645e6b756f69627c766"
                    + "f29337d7f652a34366f66603d776167643e786260353f79776136307268623739736963007a746ec7317b711328327ec"
                    + "d645931156f652ecc766f61190142ebf7384d4c0e204e7e8d5c4a324c1d35b734c5e756c72244a681ad291dd547dbf0b"
                    + "c73448db99bf424761072b32df7d256461c6e211d94d6141de0980e6",
                "fe184ce63f8 82a62e80a08 baac878aae8 ba9bb6e9ae8 ba7d8b6dae8 822200ac208"
                    + " feaaaaaabf8 00a20aba800 82fb43e4670 edbdf117d90 db3348f6580 b5698bb8648"
                    + " e75c8910200 59ec2d113d8 f61d1490730 74d21080968 4eac7a7d520 843af997298"
                    + " c3eb1b993f8 1dc0f331288 a68a0c64478 6d467bbf5e0 8a6b043acc0 80982088000"
                    + " fb9e09b8b08 253f177bfc8 5e03c87ee00 98113b330f0 76edca6d860 b183e37df98"
                    + " f24c15751a8 9157008a650 a3c44b6cfa8 00e137318f0 fe662a5dae0 827315128d8"
                    + " ba3be411fd8 ba72d997908 ba53aa1d7c0 824688292a8 fecb28467e0"),
            new RefCase("http://h.io/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6a", 7, 6,
                "479745e76607f653877366134732d78647d64336035603a7a2c77617f2372683f6a39745826607f6e6167366968632d7"
                    + "f2f7d643f66356037623c776e79637265346a39713b766078627167336938632a752f7d617f66356837623c745e79637"
                    + "f65346a36613b766d7862710433693ec03a752117617f6ec2683761173c0e3907e9ef1844d065fce60153660707048ec"
                    + "b99001271376412ce9d5a39b3b6c672662996e04689f2a9f13feead429b461c011919e4001cada087d5d2168cfd8fbe4"
                    + "0f96b6e9",
                "feb78f230bf8 82dacd9c5208 bac2d631d2e8 ba4265efdae8 bafa5fe33ae8 823508bd0208"
                    + " feaaaaaaabf8 001808fa7000 9fa4cf8c54b8 b5bbb966fa70 52167bd97998 7518c767bf60"
                    + " 8fc5a91db690 109e1c3f5ae0 1bc31df94310 d9f9aee67f78 36bdfe83b558 9d41140fbce8"
                    + " e7f5a3909da8 cdb9cdbe46a0 3fd67f9b5fc8 688718fee8f0 eae06a9ceaf8 08fdb8a498a0"
                    + " 5f876fecbf98 91e76ffac330 bf7df43c40c0 4d62cbf115a0 1bc26852d4d8 f45d0852fe98"
                    + " 0b178e48c7a8 84e523eb1c70 9b19ab6e0d48 399dbcbb3d98 0b2b05516ef8 788446f5c6a0"
                    + " 9aceefe9bfc8 00a058afd8e0 fe800abc1aa0 82f578e658f8 ba9fefa5af80 baa37acb2118"
                    + " ba72bf345da8 822ec51a6a78 fedd57ea0680"),
            new RefCase("http://h.io/hov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4", 8, 7,
                "4943d7d76603434387760303472676764797262603079797a2730707f2327373f6d632328256d6d6e6c756569637c7c7"
                    + "f2a33737f666a3a386166666f786161663f786862363f7f79623636346962323b746969627b746469327b7b752932727"
                    + "f652939376f65252e776f6f653e776761353e7e78613535336861313a736868617a736368317a7a745831717f6458383"
                    + "66f64540d766f6ec661156d26b1d078f169036cd3767dde2a580b3cd19777a53cda062989eb71a38b040120cbfdc924d"
                    + "cde3736d2670d51f3130260cac2f69e987404a7ea9cfd407e7fb2e5c2e5079ff0e108cb1a060c099d1e7f94cc3f5f387"
                    + "f340",
                "fe189ae768bf8 827b31e2dfa08 ba3db333b1ae8 ba374040612e8 ba0e1bea082e8 82fdca2b2e208"
                    + " feaaaaaaaabf8 00556227a9000 96cfcfeb0e500 c456a685c8158 529432d5f1598 4960c8e838658"
                    + " 57574de8ca5d8 388f9c893cc68 5669e9e8cfc28 6970489dba4d0 ce3cf7b0ffde0 bc95e8ed92018"
                    + " 47db125dcc9a8 eccedad2de0b0 2f9b0ed96e048 c9942649045c8 0fb5b3f0acfa8 a8d04e2c4c880"
                    + " aae3f2bf99a98 d8817635f08a8 cff5d7f4d6fd8 61550f2e8cb90 621f3866b8e28 b95e9f015be88"
                    + " 7b62acfcd5bd8 fde435b6b82b8 8b81333b0e188 9477664c59a08 7f57debd79698 60378add08b10"
                    + " 8f54c43dbc550 115194413d148 4612fc148aa68 716bad3bf8700 e3b977f4dcff0 00986e2d938d8"
                    + " fe4c62b988a88 82ed7621fb8b0 ba4e3ffb0bf88 baad30dcd5cc0 ba6cdbb9605a0 8212d4fc5c980"
                    + " fee3d24e88898"),
            new RefCase("http://h.io/ipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-el", 9, 0,
                "4a76934623f6e752b7968753f6274647137693b74786e75227033653f693a2a7137652f21786e7f6f6833653768245a7"
                    + "13e7e6f61786539666833613f2d745a786f643f6173697036683a70776d74517732643f6833297036645d60776d7f656"
                    + "73264366c7329703d737d6077643a35673260366c73297761637d6072686a3567397f766c73207631637d6732386a356"
                    + "3296f766c7d64663163756b72386a3c02796f766ec934663161152b72386ecf62796f71163ec29fc00e86dff2045cd5c"
                    + "9899cb8d0fe081316fb236668bd16836de7463833f2f4035d01e3cb66e054424e401715150f788779e6889226e29dd59"
                    + "5222007770688f7c5c549bf3532d3196d661fe4ed5a06bb59498b385473d02b1d353de5e964bff599b24cb69230b9282"
                    + "2281f2b9",
                "fe37f6d26e63f8 82895003abf208 ba7aa6ee2312e8 ba29ade8252ae8 bab341fea6e2e8 8215b78fe3a208"
                    + " feaaaaaaaaabf8 001e6a89e9a800 aa6e3efded9090 cd5c5b1d919588 b7851b3cd99878 3407df1edab6d0"
                    + " 978eaa1fbed0c0 89349a09590038 a253381ccc8458 64a53c4ba8dd80 eab22ed5ebff80 b4a08e580804c8"
                    + " fe77fd7d5e4d18 d1036700dde088 962add81dfa4c0 04312dd8809018 c22f8cc8dc89a8 4970aa8bb89980"
                    + " 8fc3befbfdef98 089066880c4888 8aa72bac94dab8 38b1268dcdf898 9fa2eefdb9ffd0 2c1572b4808858"
                    + " db9ce0e1d10378 69827084988748 93ff767bda8550 cc7aeee00d1b08 6b5814141d8ac8 b9854ab18bef18"
                    + " b7791fe0bda308 3decaabcd0c078 c67f4224c94678 806b0164ffc350 f2bf2b2f9bc858 ed1b18b9451568"
                    + " de40e691541fc8 61b1ee5584ce00 13c50afdfdefc0 00ef538995c898 fe662cad985ab8 8246378a9e2888"
                    + " bac0ecfedfdfd8 ba746d19595af8 bac15308dd99a0 82140ee9cca990 fef8588d8bf518"),
            new RefCase("http://h.io/jqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu18cjqx4_fmt07bipw3-elsz6ahov29dkry5/gnu1", 10, 1,
                "40b7f613170c27768683d693e73645875253a7f647f613176647768683d703e7364543a253a7f603f213176676f68683"
                    + "d7268236454397e6a7f603079617667673f283d72632f6454397d6a7f603075617667673c783d7263237454397d6a3f6"
                    + "03075666667673c716d7263237864397d6a3f703075666637673c71623263237869697d6a3f74607566663b773c71623"
                    + "273237869693d6a3f74652566663b7f6c71623277637869693e7a3f74652536663b7f61016232776ec869693e711f746"
                    + "5253ec63b7f6131123277686ec9693e73611465253a7ec11b5f3ae97077931042f1e182209cd6daf54ac006d4fbab25c"
                    + "1d0af3c5df05e6e93d1278bff7bca5014b34f46a0dde57eafe14d0a5babc4599d6b26b7bd10d94bbb6de72d2cd368c02"
                    + "73678f8ec129277e24e3eed55639bb5732b0aec0c4f96e1301445962a849309937ba8d3fba3ec508401d6fdec506c33c"
                    + "cffc60efc82c06a31136",
                "feed21aa33b73f8 826b932e32b5208 bad97cd532272e8 ba0f24d131752e8 ba06c1fe6e292e8 82fcf7237626208"
                    + " feaaaaaaaaaabf8 0030f363bace800 a303f13e8b8c128 f5346164c594478 371487d5998dad8 f0458d43cefb4d0"
                    + " 33adb6838aece90 39cd4c0c84c5188 43b084a58d99988 45444e0afbbf3d0 c32b032aecf94c0 0423fd7959050d8"
                    + " 3afc7809801c478 747f965f9ee85c0 2f572e0cb8af3b0 ecdadf4c4d58028 375b157851819e8 00ebb3da8998bc0"
                    + " 93c4385ccecf490 2cff2249904d548 ffba9cfe0051fa8 78cf24e3998b898 2aa55b6acf8bac8 38f7df23d5948d8"
                    + " dff0f6bf10c4fa8 3d75e8878c99f88 4ae69bd18a8b018 7ca5d7b695d4e20 aac7eb2ad4903c0 657057e68cdd2c0"
                    + " dff1651a8cfdf18 fd04bd40c4c4178 d3dd37ca94acf88 85256e82bcd5ed0 f29bcf12ece9958 80391d155884758"
                    + " f79c5626d88ce78 e80ff17aacadb50 7e758a23b8a9618 501779355d59d68 a6841f06dc80f88 f8067a6eadeff58"
                    + " 032a283ea8b9fc0 009aee620859898 fe99df2a99dcaa8 827d6423ffab898 ba5090bebfcff90 ba42023dcc08880"
                    + " baabd6fc5998ab8 823e244da8fecc0 fe96bc6dca8a888"),
        };

        public static IEnumerable<RefCase> RefCases() => s_Ref;

        static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

        static string HexRow(QrCode q, int r)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < q.Size; c += 4)
            {
                int v = 0;
                for (int k = 0; k < 4; k++) v = (v << 1) | (c + k < q.Size && q[r, c + k] ? 1 : 0);
                sb.Append(v.ToString("x"));
            }
            return sb.ToString();
        }

        // ---------------- Reed–Solomon, BCH ----------------

        [Test]
        public void ReedSolomonMatchesTheIsoWorkedExample()
        {
            // ISO/IEC 18004 Annex I: "01234567" at 1-M → 16 data codewords, 10 EC codewords.
            var data = new byte[] { 0x10, 0x20, 0x0C, 0x56, 0x61, 0x80, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11 };
            Assert.AreEqual("a524d4c1ed36c7872c55", Hex(QrCode.ReedSolomon(data, 10)));
        }

        [TestCase(7, new[] { 0, 87, 229, 146, 149, 238, 102, 21 })]
        [TestCase(10, new[] { 0, 251, 67, 46, 61, 118, 70, 64, 94, 32, 45 })]
        [TestCase(16, new[] { 0, 120, 104, 107, 109, 102, 161, 76, 3, 91, 191, 147, 169, 182, 194, 225, 120 })]
        public void GeneratorPolynomialsMatchTheStandard(int degree, int[] alphaExponents)
        {
            var g = QrCode.Generator(degree);
            Assert.AreEqual(degree + 1, g.Length);
            for (int k = 0; k <= degree; k++) Assert.AreEqual(QrCode.GfPow2(alphaExponents[k]), g[k], $"x^{degree - k}");
        }

        static int PolyRemainder(int value, int valueBits, int generator, int genBits)
        {
            for (int i = valueBits - 1; i >= genBits - 1; i--)
                if (((value >> i) & 1) != 0) value ^= generator << (i - (genBits - 1));
            return value;
        }

        [Test]
        public void FormatBitsForLevelMAreTheBchCodesXorTheMask()
        {
            var expected = new[] { 0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0 };
            for (int mask = 0; mask < 8; mask++)
            {
                int bits = QrCode.FormatBits(mask);
                Assert.AreEqual(expected[mask], bits, $"mask {mask}");
                int unmasked = bits ^ 0x5412;
                Assert.AreEqual(mask, unmasked >> 10, "level M (00) + mask in the top 5 bits");
                Assert.AreEqual(0, PolyRemainder(unmasked, 15, 0x537, 11), "a BCH(15,5) codeword");
            }
        }

        [Test]
        public void VersionBitsAreTheBchCodes()
        {
            var expected = new[] { 0x07C94, 0x085BC, 0x09A99, 0x0A4D3 };
            for (int v = 7; v <= 10; v++)
            {
                int bits = QrCode.VersionBits(v);
                Assert.AreEqual(expected[v - 7], bits, $"version {v}");
                Assert.AreEqual(v, bits >> 12);
                Assert.AreEqual(0, PolyRemainder(bits, 18, 0x1F25, 13), "a BCH(18,6) codeword");
            }
        }

        // ---------------- capacity ----------------

        [Test]
        public void ByteCapacityAtLevelMPerVersion()
        {
            var expected = new[] { 14, 26, 42, 62, 84, 106, 122, 152, 180, 213 };
            for (int v = 1; v <= 10; v++) Assert.AreEqual(expected[v - 1], QrCode.Capacity(v), $"version {v}");
            Assert.AreEqual(1, QrCode.VersionFor(14));
            Assert.AreEqual(2, QrCode.VersionFor(15));
            Assert.AreEqual(10, QrCode.VersionFor(213));
            Assert.AreEqual(-1, QrCode.VersionFor(214));
            Assert.IsNull(QrCode.Encode(new string('a', 214)), "too long for version 10: no code");
            Assert.IsNotNull(QrCode.Encode(new string('a', 213)));
        }

        // ---------------- the reference implementation ----------------

        [TestCaseSource(nameof(RefCases))]
        public void CodewordsAndMatrixMatchTheReference(RefCase c)
        {
            var bytes = Encoding.UTF8.GetBytes(c.Text);
            Assert.AreEqual(c.Version, QrCode.VersionFor(bytes.Length), "version");
            Assert.AreEqual(c.Codewords, Hex(QrCode.Codewords(bytes, c.Version)), "codewords (data + EC, interleaved)");
            var q = QrCode.Encode(c.Text, c.Mask);
            Assert.AreEqual(c.Version, q.Version);
            Assert.AreEqual(17 + 4 * c.Version, q.Size);
            var rows = c.Rows.Split(' ');
            Assert.AreEqual(q.Size, rows.Length);
            for (int r = 0; r < q.Size; r++) Assert.AreEqual(rows[r], HexRow(q, r), $"row {r}");
        }

        [TestCaseSource(nameof(RefCases))]
        public void FormatAndVersionBitsReadBackFromTheMatrix(RefCase c)
        {
            var q = QrCode.Encode(c.Text, c.Mask);
            var pos = QrCode.FormatPositions(q.Size);
            int first = 0, second = 0;
            for (int i = 0; i < 15; i++)
            {
                if (q[pos[i].row, pos[i].col]) first |= 1 << i;
                if (q[pos[15 + i].row, pos[15 + i].col]) second |= 1 << i;
            }
            Assert.AreEqual(QrCode.FormatBits(c.Mask), first, "format bits around the top-left finder");
            Assert.AreEqual(QrCode.FormatBits(c.Mask), second, "format bits by the other two finders");
            Assert.IsTrue(q[q.Size - 8, 8], "dark module");
            if (c.Version < 7) return;
            int top = 0, left = 0;
            for (int i = 0; i < 18; i++)
            {
                int a = q.Size - 11 + i % 3, b = i / 3;
                if (q[b, a]) top |= 1 << i;
                if (q[a, b]) left |= 1 << i;
            }
            Assert.AreEqual(QrCode.VersionBits(c.Version), top, "version block, top right");
            Assert.AreEqual(QrCode.VersionBits(c.Version), left, "version block, bottom left");
        }

        // ---------------- read it back ----------------

        /// A minimal decoder: unmask, read the codewords along the zigzag, de-interleave, check every block's RS
        /// syndromes are zero, then parse the byte-mode segment.
        static string Decode(QrCode q, out int blocksChecked)
        {
            int v = q.Version;
            var order = QrCode.DataOrder(q);
            int total = QrCode.TotalCodewords(v);
            var cw = new byte[total];
            for (int i = 0; i < total * 8; i++)
            {
                var (r, c) = order[i];
                bool bit = q[r, c] ^ QrCode.MaskBit(q.Mask, r, c);
                if (bit) cw[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
            Assert.AreEqual(total * 8 + QrCode.RemainderBits(v), order.Count, "data modules = codewords + remainder bits");
            int blocks = QrCode.BlockCount(v), ec = QrCode.EcCodewordsPerBlock(v), dataTotal = QrCode.DataCodewords(v);
            int shortLen = dataTotal / blocks, longBlocks = dataTotal % blocks;
            var lens = Enumerable.Range(0, blocks).Select(b => shortLen + (b >= blocks - longBlocks ? 1 : 0)).ToArray();
            var blockBytes = lens.Select(l => new List<byte>()).ToArray();
            int k = 0;
            for (int i = 0; i <= shortLen; i++)
                for (int b = 0; b < blocks; b++)
                    if (i < lens[b]) blockBytes[b].Add(cw[k++]);
            for (int i = 0; i < ec; i++)
                for (int b = 0; b < blocks; b++) blockBytes[b].Add(cw[k++]);
            blocksChecked = 0;
            foreach (var block in blockBytes)
            {
                for (int s = 0; s < ec; s++)
                {
                    byte x = QrCode.GfPow2(s), acc = 0;
                    foreach (var y in block) acc = (byte)(QrCode.GfMul(acc, x) ^ y);   // Horner at α^s
                    Assert.AreEqual(0, acc, $"syndrome {s}");
                }
                blocksChecked++;
            }
            var data = blockBytes.SelectMany((b, i) => b.Take(lens[i])).ToArray();
            int bitPos = 0;
            int Read(int n) { int val = 0; for (int i = 0; i < n; i++, bitPos++) val = (val << 1) | ((data[bitPos >> 3] >> (7 - (bitPos & 7))) & 1); return val; }
            Assert.AreEqual(0b0100, Read(4), "byte mode");
            int count = Read(QrCode.CountBits(v));
            var payload = new byte[count];
            for (int i = 0; i < count; i++) payload[i] = (byte)Read(8);
            return Encoding.UTF8.GetString(payload);
        }

        [TestCase("https://files-cdn.x.ai/2f1c9a7e4b5d6c8a/file_7c1e2d3f-4a5b-6c7d-8e9f-0a1b2c3d4e5f.pdf")]
        [TestCase("http://127.0.0.1:8000/packet/f7b741f8bc5847cf.pdf")]
        [TestCase("http://192.168.1.20:8000/report/quest-0123456789")]
        [TestCase("https://x.com/i/web/status/1790412901175330649")]
        [TestCase("Añadir café · 5 m²")]
        [TestCase("")]
        public void DecodesBackWithZeroSyndromes(string text)
        {
            var q = QrCode.Encode(text);
            Assert.AreEqual(text, Decode(q, out int blocks));
            Assert.AreEqual(QrCode.BlockCount(q.Version), blocks);
        }

        [Test]
        public void EveryVersionAndMaskDecodes()
        {
            for (int v = 1; v <= 10; v++)
                for (int mask = 0; mask < 8; mask++)
                {
                    string text = ("http://a.io/" + new string('z', 300)).Substring(0, QrCode.Capacity(v));   // exactly full
                    var q = QrCode.Encode(text, mask);
                    Assert.AreEqual(v, q.Version, $"{text.Length} bytes");
                    Assert.AreEqual(text, Decode(q, out _), $"v{v} mask {mask}");
                }
        }

        // ---------------- mask selection ----------------

        [TestCaseSource(nameof(RefCases))]
        public void AutoMaskHasTheLowestPenalty(RefCase c)
        {
            var auto = QrCode.Encode(c.Text);
            int best = QrCode.Penalty(auto.Modules);
            for (int m = 0; m < 8; m++)
            {
                int p = QrCode.Penalty(QrCode.Encode(c.Text, m).Modules);
                Assert.GreaterOrEqual(p, best, $"mask {m} scores {p}, auto (mask {auto.Mask}) {best}");
                if (p == best) { Assert.AreEqual(m, auto.Mask, "ties go to the lowest mask"); break; }
            }
        }

        static bool[,] Grid(params string[] rows)
        {
            var g = new bool[rows.Length, rows.Length];
            for (int r = 0; r < rows.Length; r++)
                for (int c = 0; c < rows.Length; c++) g[r, c] = rows[r][c] == '1';
            return g;
        }

        [Test]
        public void PenaltyRulesScoreAsTheStandardSays()
        {
            // Rule 1: a run of 5 scores 3, each extra module 1 more (rows and columns).
            var g = new bool[7, 7];
            for (int r = 0; r < 7; r++) for (int c = 0; c < 7; c++) g[r, c] = (r + c) % 2 == 0;   // checkerboard: no runs
            Assert.AreEqual(0, QrCode.PenaltyRuns(g));
            Assert.AreEqual(0, QrCode.PenaltyBlocks(g));
            for (int c = 0; c < 5; c++) g[0, c] = true;   // row 0: 11111 then 0 1
            Assert.AreEqual(3, QrCode.PenaltyRuns(g));
            g[0, 5] = true;                               // 111111 then 1 → a run of 7
            Assert.AreEqual(5, QrCode.PenaltyRuns(g));

            // Rule 2: every 2×2 block of one colour, overlapping ones too.
            var blocks = Grid("110", "110", "110");
            Assert.AreEqual(2 * QrCode.N2, QrCode.PenaltyBlocks(blocks));

            // Rule 3: 1011101 with four light modules on one side (edges count as light), rows and columns.
            var finder = new bool[11, 11];
            var pattern = "00001011101";
            for (int c = 0; c < 11; c++) finder[5, c] = pattern[c] == '1';
            Assert.AreEqual(QrCode.N3, QrCode.PenaltyFinderLike(finder));
            var edge = new bool[7, 7];
            for (int c = 0; c < 7; c++) edge[3, c] = "1011101"[c] == '1';
            Assert.AreEqual(QrCode.N3, QrCode.PenaltyFinderLike(edge), "outside the symbol is light");

            // Rule 4: 10 per whole 5 % away from half dark.
            var half = new bool[10, 10];
            for (int i = 0; i < 50; i++) half[i / 10, i % 10] = true;
            Assert.AreEqual(0, QrCode.PenaltyBalance(half));
            for (int i = 50; i < 60; i++) half[i / 10, i % 10] = true;   // 60 % dark
            Assert.AreEqual(2 * QrCode.N4, QrCode.PenaltyBalance(half));
        }

        // ---------------- rendering ----------------

        [Test]
        public void PixelsHaveAQuietZoneAndTheFinderInTheTopLeft()
        {
            var q = QrCode.Encode("http://127.0.0.1:8000/report/demo");
            var px = q.Pixels(QrCode.QuietZone, out int w);
            Assert.AreEqual(q.Size + 8, w);
            for (int i = 0; i < w; i++)
            {
                foreach (int k in new[] { 0, 3, w - 4, w - 1 })
                {
                    Assert.AreEqual(255, px[i * w + k].r, "quiet zone column");
                    Assert.AreEqual(255, px[k * w + i].r, "quiet zone row");
                }
            }
            // Texture rows grow up: QR row 0 is pixel row w − 1 − quiet.
            for (int r = 0; r < q.Size; r++)
                for (int c = 0; c < q.Size; c++)
                    Assert.AreEqual(q[r, c], px[(w - 1 - QrCode.QuietZone - r) * w + QrCode.QuietZone + c].r == 0, $"({r},{c})");
            Assert.IsTrue(q[0, 0] && q[6, 6] && !q[1, 1] && q[3, 3], "finder ring and core");
        }
    }
}
