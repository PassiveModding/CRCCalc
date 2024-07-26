using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace CRCCalc;

internal class Program
{
    private static string[] ParseFile(string path)
    {
        return File.ReadAllLines(path)
            .Select(x => x.Trim())
            .Distinct()
            .ToArray();
    }
    
    private static uint[] ParseCrcs(string path)
    {
        var targetCrcStrings = ParseFile(path);
        var targetCrcs = new uint[targetCrcStrings.Length];
        for (var i = 0; i < targetCrcStrings.Length; i++)
        {
            var val = targetCrcStrings[i].Trim();
            if (val.StartsWith("0x"))
            {
                val = val.Substring(2);
                targetCrcs[i] = uint.Parse(val, NumberStyles.HexNumber);
            }
            else
            {
                targetCrcs[i] = uint.Parse(val);
            }
        }

        return targetCrcs.Distinct().ToArray();
    }
    
    public static void Main(string[] args)
    {
        var crcs = ParseCrcs("crcs.txt");

        // Direct terms to test
        var terms = ParseFile("terms.txt");
        
        // Tests:
        // prefix-dictionary+-suffix
        // prefix-dictionary+
        var dictionary = ParseFile("dictionary.txt");
        var prefixes = ParseFile("prefixes.txt");
        var suffixes = ParseFile("suffixes.txt");
        
        var program = new Program(prefixes, terms, dictionary, suffixes, crcs);
        program.Run();
    }

    private Program(string[] prefixes, string[] terms, string[] dictionary, string[] suffixes, uint[] crcs)
    {
        _terms = terms;
        _crcs = crcs;
        _crcTable = GetCrcTable();
        
        var dBuf = dictionary.Select(Encoding.UTF8.GetBytes).ToArray();
        var pBuf = prefixes.Select(Encoding.UTF8.GetBytes).ToArray();
        var sBuf = suffixes.Select(Encoding.UTF8.GetBytes).ToArray();
        
        _dictBuf = dBuf;
        _prefixBuf = pBuf;
        _suffixBuf = sBuf;
        
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    return;
                }
                var elapsed = DateTime.Now - _startTime;
                Console.WriteLine($"Rate: {_tested / elapsed.TotalSeconds:F2} hashes/sec, {_tested} hashes tested");

                while (_newTested.TryDequeue(out var line))
                {
                    Console.WriteLine(line);
                    File.AppendAllText(FoundFileName, line + "\n");
                }
                
                await Task.Delay(1000, cts.Token);
            }
        }, cts.Token);
    }

    private readonly string[] _terms;
    private readonly uint[] _crcs;
    private readonly uint[] _crcTable;
    private readonly byte[][] _dictBuf;
    private readonly byte[][] _prefixBuf;
    private readonly byte[][] _suffixBuf;

    private static uint[] GetCrcTable()
    {
        const uint poly = 0x04C11DB7;
        var table = new uint[256];
        const int width = 32;
        const int msbMask = 0x01 << (width - 1);
        for (var i = 0; i < 256; i++)
        {
            var currentByte = (uint) (i << (width - 8));
            for (var bit = 0; bit < 8; bit++)
            {
                if ((currentByte & msbMask) != 0)
                {
                    currentByte = (currentByte << 1) ^ poly;
                }
                else
                {
                    currentByte <<= 1;
                }
            }
        
            table[i] = currentByte;
        }
    
        return table;
    }

    private static uint Reflect32(uint val)
    {
        val = ((val & 0x55555555) << 1) | ((val >> 1) & 0x55555555);
        val = ((val & 0x33333333) << 2) | ((val >> 2) & 0x33333333);
        val = ((val & 0x0F0F0F0F) << 4) | ((val >> 4) & 0x0F0F0F0F);
        val = ((val & 0x00FF00FF) << 8) | ((val >> 8) & 0x00FF00FF);
        val = (val << 16) | (val >> 16);
        return val;
    }
        
    // https://graphics.stanford.edu/~seander/bithacks.html#ReverseByteWith64BitsDiv
    private static byte Reflect8(byte val)
    {
        val = (byte)(((val * 0x0802LU & 0x22110LU) | (val * 0x8020LU & 0x88440LU)) * 0x10101LU >> 16);
        return val;
    }
    
    private uint GetHashFromBytes(ReadOnlySpan<byte> data)
    {
        var crcLocal = 0U;
        const int width = 32;
        const int shift = width - 8;
        foreach (var t in data)
        {
            crcLocal ^= (uint)Reflect8(t) << shift;
            crcLocal = (crcLocal << 8) ^ _crcTable[crcLocal >> shift];
        }
            
        // reverse crc
        crcLocal = Reflect32(crcLocal);
            
        return crcLocal;
    }

    private ulong _tested;
    private readonly DateTime _startTime = DateTime.Now;
    private string FoundFileName => $"found-{_startTime:yyyyMMdd-HHmmss}.txt";
    private readonly ConcurrentQueue<string> _newTested = new();
    private void Test(ReadOnlySpan<byte> bytes)
    {
        Interlocked.Increment(ref _tested);
        var hash = GetHashFromBytes(bytes);
        foreach (var targetCrc in _crcs)
        {
            if (hash == targetCrc)
            {
                _newTested.Enqueue($"0x{hash:X8}\t{hash}\t{Encoding.UTF8.GetString(bytes)}");
            }
        }
    }
    

    private void Test(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix)
    {
        var combined = new byte[prefix.Length + suffix.Length];
        prefix.CopyTo(combined);
        suffix.CopyTo(combined.AsSpan(prefix.Length));
        Test(combined);
    }
    
    private void TestSuffixes(ReadOnlySpan<byte> prefix)
    {
        foreach (var suffix in _suffixBuf)
        {
            Test(prefix, suffix);
        }
    }

    private void Run()
    {
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        foreach (var term in _terms)
        {
            var termBytes = Encoding.UTF8.GetBytes(term);
            Test(termBytes);
        }
        
        // 1 word, 2 words, 3 words, etc
        // will keep running until process is killed for any sufficiently large dictionary
        for (var length = 0; length < _dictBuf.Length; length++)
        {
            var localLength = length;
            long max = 1;
            for (var i = 0; i < localLength; i++)
            {
                max *= _dictBuf.Length;
            }
            
            Console.WriteLine($"Testing {localLength} elements, {max} size");

            // 100 element partitions
            var partitions = max / 100;
            // each parallel should get a range of indices to calculate
            var maxPrefixLength = _prefixBuf.Max(x => x.Length);
            Parallel.For(0, partitions, parallelOptions, i =>
            {
                var start = i * 100;
                var end = Math.Min(start + 100, max);
                for (var j = start; j < end; j++)
                {
                    var indices = Calculate(_dictBuf.Length, localLength, j);
                    var size = 0;
                    foreach (var index in indices)
                    {
                        size += _dictBuf[index].Length;
                    }
                    
                    var dictBuf = new byte[size];
                    var offset = 0;
                    foreach (var index in indices)
                    {
                        _dictBuf[index].CopyTo(dictBuf.AsSpan(offset));
                        offset += _dictBuf[index].Length;
                    }
                    
                    var maxPrefixBuf = new byte[maxPrefixLength + dictBuf.Length];

                    foreach (var prefix in _prefixBuf)
                    {
                        //prefix.CopyTo(maxPrefixBuf.AsSpan());
                        //dictBuf.CopyTo(maxPrefixBuf.AsSpan(prefix.Length));
                        Array.Copy(prefix, 0, maxPrefixBuf, 0, prefix.Length);
                        Array.Copy(dictBuf, 0, maxPrefixBuf, prefix.Length, size);
                        
                        Test(maxPrefixBuf.AsSpan(0, prefix.Length + size));
                        TestSuffixes(maxPrefixBuf.AsSpan(0, prefix.Length + size));
                    }
                }
            });
        }
    }

    private static ReadOnlySpan<int> Calculate(int size, int length, long index)
    {
        var combination = new int[length];
        var remainder = index;

        for (var i = 0; i < length; i++)
        {
            combination[i] = (int)(remainder % size);
            remainder /= size;
        }

        return combination;
    }
}