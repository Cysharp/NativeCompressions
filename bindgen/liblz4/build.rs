use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
   bindgen::Builder::default()
        .header("../../lz4/lib/lz4.c")
        .header("../../lz4/lib/lz4hc.c")
        .header("../../lz4/lib/lz4frame.c")
        .header("../../lz4/lib/xxhash.c")
        .generate()?
        .write_to_file("src/lz4.rs")?;

    csbindgen::Builder::default()
        .input_bindgen_file("src/lz4.rs")
        .method_filter(|x| x.starts_with("LZ4"))
        .csharp_class_name("LZ4NativeMethods")
        .csharp_namespace("NativeCompressions.Interop")
        .csharp_dll_name("lz4")
        .csharp_class_accessibility("public")
        .generate_csharp_file("../../src/NativeCompressions.LZ4.Core/Interop/LZ4NativeMethods.cs")?;

    Ok(())
}
