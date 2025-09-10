//namespace NativeCompressions.ZStandard
//{
//    // used in ZStdEncoder.

//    public enum EndDirective
//    {
//        /// <summary>
//        /// collect more data, encoder decides when to output compressed result, for optimal compression ratio
//        /// </summary>
//        Continue = 0,
//        /// <summary>
//        /// flush any data provided so far,
//        /// it creates (at least) one new block, that can be decoded immediately on reception;
//        /// frame will continue: any future data can still reference previously compressed data, improving compression.
//        /// </summary>
//        Flush = 1,
//        /// <summary>
//        /// flush any remaining data _and_ close current frame.
//        /// note that frame is only closed after compressed data is fully flushed (return value == 0).
//        /// After that point, any additional data starts a new frame.
//        /// note : each frame is independent (does not reference any content from previous frame).
//        /// </summary>
//        End = 2
//    }
//}
