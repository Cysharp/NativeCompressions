using NativeCompressions.Fuzz;

namespace NativeCompressions.Tests;

// Replays the fuzz targets without libFuzzer: seed corpus, a fixed set of deterministic mutations,
// and every input kept under tests/NativeCompressions.Fuzz/regressions/.
public class ZstandardFuzzRegressionTest
{
    public static IEnumerable<object[]> Targets() => FuzzTargets.All.Keys.Select(x => new object[] { x });

    static void Run(string target, byte[] input, string label)
    {
        try
        {
            FuzzTargets.All[target](input);
        }
        catch (Exception ex)
        {
            throw new Exception($"fuzz target '{target}' failed on {label} ({input.Length} bytes): {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Seeds(string target)
    {
        var count = 0;
        foreach (var (name, data) in Corpus.For(target))
        {
            Run(target, data, "seed " + name);
            count++;
        }
        Assert.True(count > 0);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Mutations(string target)
    {
        // deterministic so a failure is reproducible from the seed and iteration number
        var random = new Random(0x5A5A + target.Length);
        var seeds = Corpus.For(target).Select(x => x.Data).ToArray();

        for (int i = 0; i < 300; i++)
        {
            var input = Mutate(seeds[random.Next(seeds.Length)], random, seeds);
            Run(target, input, $"mutation #{i}");
        }
    }

    [Fact]
    public void Regressions()
    {
        var assembly = typeof(FuzzTargets).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(x => x.StartsWith("regressions/", StringComparison.Ordinal)))
        {
            var parts = resource.Split('/');
            Assert.True(parts.Length >= 3, $"unexpected regression resource name {resource}, expected regressions/<target>/<name>");
            var target = parts[1];
            Assert.True(FuzzTargets.All.ContainsKey(target), $"unknown fuzz target in {resource}");

            using var stream = assembly.GetManifestResourceStream(resource)!;
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            Run(target, ms.ToArray(), resource);
        }
    }

    // A few libFuzzer style mutations: bit flips, byte edits, insert, delete, truncate, splice, repeat.
    static byte[] Mutate(byte[] seed, Random random, byte[][] all)
    {
        var data = seed.ToArray();
        var rounds = 1 + random.Next(4);
        for (int r = 0; r < rounds; r++)
        {
            switch (random.Next(8))
            {
                case 0 when data.Length > 0: // flip a bit
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when data.Length > 0: // random byte
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                    break;
                case 2: // insert
                {
                    var at = random.Next(data.Length + 1);
                    var insert = new byte[1 + random.Next(16)];
                    random.NextBytes(insert);
                    data = data.AsSpan(0, at).ToArray().Concat(insert).Concat(data.AsSpan(at).ToArray()).ToArray();
                    break;
                }
                case 3 when data.Length > 1: // delete
                {
                    var at = random.Next(data.Length);
                    var len = 1 + random.Next(Math.Min(16, data.Length - at));
                    data = data.AsSpan(0, at).ToArray().Concat(data.AsSpan(at + len).ToArray()).ToArray();
                    break;
                }
                case 4 when data.Length > 1: // truncate
                    data = data.AsSpan(0, random.Next(1, data.Length)).ToArray();
                    break;
                case 5: // splice with another seed
                {
                    var other = all[random.Next(all.Length)];
                    if (other.Length == 0) break;
                    var at = random.Next(data.Length + 1);
                    var from = random.Next(other.Length);
                    data = data.AsSpan(0, at).ToArray().Concat(other.AsSpan(from).ToArray()).ToArray();
                    break;
                }
                case 6 when data.Length > 0: // repeat a range
                {
                    var at = random.Next(data.Length);
                    var len = 1 + random.Next(Math.Min(64, data.Length - at));
                    data = data.Concat(data.AsSpan(at, len).ToArray()).ToArray();
                    break;
                }
                case 7 when data.Length >= 4: // overwrite a 32-bit value with an interesting number
                {
                    var at = random.Next(data.Length - 3);
                    uint[] interesting = [0, 1, 0x7F, 0x80, 0xFF, 0xFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFD2FB528, 0x184D2A50];
                    BitConverter.TryWriteBytes(data.AsSpan(at, 4), interesting[random.Next(interesting.Length)]);
                    break;
                }
            }
        }
        return data;
    }
}
