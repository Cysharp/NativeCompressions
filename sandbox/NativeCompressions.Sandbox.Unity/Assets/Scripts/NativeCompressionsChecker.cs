using System.IO;
using System.IO.Compression;
using System.Text;
using NativeCompressions.Lz4;
using UnityEngine;

public class NativeCompressionsChecker : MonoBehaviour
{
    void Start()
    {
        Debug.Log("[Check LZ4]");
        CheckLZ4();
    }

    void CheckLZ4()
    {
        using var ms = new MemoryStream();
        using var lz4 = new LZ4Stream(ms, CompressionMode.Compress);

        var bytes1 = Encoding.UTF8.GetBytes("あいうえおかきくけこ");
        lz4.Write(bytes1);
        lz4.Write(bytes1);
        lz4.Write(bytes1);
        lz4.Write(bytes1);
        lz4.Write(bytes1);

        lz4.Flush();
        
        var xss = ms.ToArray();

        Debug.Log(xss.Length);

        var dest = new byte[1024];
        if (!LZ4Decoder.TryDecompressFrame(xss, dest, out var written))
        {
            Debug.LogError("error");
        }
        else
        {
            Debug.Log(Encoding.UTF8.GetString(dest, 0, written));
        }
    }
}