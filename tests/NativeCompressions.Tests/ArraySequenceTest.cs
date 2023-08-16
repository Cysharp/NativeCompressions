using NativeCompressions.ZStandard;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NativeCompressions.Tests
{
    public class ArraySequenceTest
    {
        [Fact]
        public void Foo()
        {
            var seq = new ArraySequence(65535);
            var span = seq.CurrentSpan;
            span = seq.AllocateNextBlock(span.Length);



        }
    }
}
