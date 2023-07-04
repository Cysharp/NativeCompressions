use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
    cc::Build::new()
        .files([
            "c/lz4/lz4.c",
            "c/lz4/lz4hc.c",
            "c/lz4/lz4frame.c",
            "c/lz4/xxhash.c",
        ])
        .compile("lz4");
    
    csbindgen::Builder::new()
        .input_bindgen_file("src/zstd.rs")
        .method_filter(|x| x.starts_with("ZSTD_"))
        .rust_file_header("use super::zstd::*;")
        .csharp_class_name("LibZstd")
        .csharp_dll_name("libzsd")
        .generate_to_file("src/zstd_ffi.rs", "../dotnet-sandbox/zstd_bindgen.cs")?;

    Ok(())
}
