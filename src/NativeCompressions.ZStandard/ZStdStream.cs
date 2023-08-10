using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static NativeCompressions.ZStandard.ZStdNativeMethods;

namespace NativeCompressions.ZStandard
{
    public sealed class ZStdStream
    {
        static int GetRecommendedCompressSizeForInput() => (int)ZSTD_CStreamInSize();
        static int GetRecommendedCompressSizeForOutput() => (int)ZSTD_CStreamOutSize();

        static int GetRecommendedDecompressSizeForInput() => (int)ZSTD_DStreamInSize();
        static int GetRecommendedDecompressSizeForOutput() => (int)ZSTD_DStreamOutSize();



    }
}
