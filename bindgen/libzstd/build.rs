use std::{error::Error};

fn main() -> Result<(), Box<dyn Error>> {
     bindgen::Builder::default()
        .header("../../zstd/lib/zstd.h")
        .header("../../zstd/lib/zdict.h")
        .header("../../zstd/lib/zstd_errors.h")
        .generate()?
        .write_to_file("src/zstd.rs")?;

    csbindgen::Builder::new()
        .input_bindgen_file("src/zstd.rs")
        .method_filter(|x| x.starts_with("ZSTD_"))
        .csharp_class_name("ZstandardNativeMethods")
        .csharp_namespace("NativeCompressions.Interop")
        .csharp_dll_name("libzstd")
        .csharp_class_accessibility("public")
        .generate_csharp_file("../../src/NativeCompressions.Zstandard.Core/Interop/ZstandardNativeMethods.cs")?;

    Ok(())
}
