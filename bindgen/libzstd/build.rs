use std::{error::Error};

fn main() -> Result<(), Box<dyn Error>> {
    let size_t_is_usize = match std::env::var("TARGET").unwrap().as_str() {
        "aarch64-linux-android" => false,
        _ => true,
    };

     bindgen::Builder::default()
        .header("../../zstd/lib/zstd.h")
        .header("../../zstd/lib/zdict.h")
        .header("../../zstd/lib/zstd_errors.h")
        .size_t_is_usize(size_t_is_usize)
        .generate()?
         .write_to_file("src/zstd.rs")?;

    csbindgen::Builder::new()
        .input_bindgen_file("src/zstd.rs")
        .method_filter(|x| x.starts_with("ZSTD_"))
        .rust_file_header("use super::zstd;")
        .rust_method_type_path("zstd")
        .csharp_class_name("NativeMethods")
        .csharp_namespace("NativeCompressions.ZStandard.Raw")
        .csharp_dll_name("libzstd")
        // .csharp_dll_name_if("UNITY_IOS && !UNITY_EDITOR", "__Internal")
        .csharp_entry_point_prefix("")
        .csharp_method_prefix("")
        .csharp_class_accessibility("public")
        .generate_to_file("src/zstd_ffi.rs", "../../src/NativeCompressions.ZStandard.Core/Raw/NativeMethods.cs")?;

    Ok(())
}
