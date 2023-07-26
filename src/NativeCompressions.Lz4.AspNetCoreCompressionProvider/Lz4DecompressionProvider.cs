using Microsoft.AspNetCore.RequestDecompression;
using System.IO.Compression;

namespace NativeCompressions.Lz4.AspNetCoreCompressionProvider;

public class Lz4DecompressionProvider : IDecompressionProvider
{
    public Stream GetDecompressionStream(Stream stream)
    {
        return new LZ4Stream(stream, CompressionMode.Decompress, leaveOpen: true);
    }
}