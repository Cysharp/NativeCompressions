using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NativeCompressions.LZ4
{
    public enum LZ4HCCompressionLevel : int
    {
        Min = 3,
        Default = 9,
        OptMin = 10,
        Max = 12,

        Level3 = 3,
        Level4 = 4,
        Level5 = 5,
        Level6 = 6,
        Level7 = 7,
        Level8 = 8,
        Level9 = 9,
        Level10 = 10,
        Level11 = 11,
        Level12 = 12,
    }
}
