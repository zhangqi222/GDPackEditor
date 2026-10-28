using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace GDPackEditor.Tests
{
    public class ArcRoundTripTests
    {
        [Fact]
        public void WriteThenRead_PreservesNamesAndContent()
        {
            var files = new Dictionary<string, byte[]>
            {
                ["data/readme.txt"] = Encoding.UTF8.GetBytes("hello grim dawn\r\n"),
                ["data/compressible.txt"] = Encoding.UTF8.GetBytes(new string('A', 300 * 1024)),
                ["data/random.bin"] = CreateRandomBytes(700 * 1024, 12345)
            };

            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var writer = new ARCWriter(ms))
                {
                    foreach (var pair in files)
                        using (var input = new MemoryStream(pair.Value))
                            writer.WriteFromStream(pair.Key, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), input);
                }
                packed = ms.ToArray();
            }

            Assert.NotEmpty(packed);

            using (var ms = new MemoryStream(packed))
            {
                var arc = new ARCFile();
                arc.ReadStream(ms);

                Assert.Equal(files.Count, arc.toc.Count);
                foreach (var pair in files)
                {
                    var entry = arc.toc.Single(e => e.GetEntryString(arc.strs) == pair.Key);
                    using (var output = new MemoryStream())
                    {
                        arc.UnpackToStream(entry, output);
                        Assert.Equal(pair.Value, output.ToArray());
                    }
                }
            }
        }

        private static byte[] CreateRandomBytes(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }
    }
}
