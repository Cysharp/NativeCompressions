use std::{error::Error};

fn main() -> Result<(), Box<dyn Error>> {
    // TODO:bindgen, zdict.h
     bindgen::Builder::default()
         .header("../../zstd/lib/zstd.h")
         .generate()?
         .write_to_file("src/zstd.rs")?;

    csbindgen::Builder::new()
        .input_bindgen_file("src/zstd.rs")
        .method_filter(|x| x.starts_with("ZSTD_"))
        .rust_method_prefix("nativecompressions_")
        .rust_file_header("use super::zstd;")
        .rust_method_type_path("zstd")
        .csharp_class_name("ZStdNativeMethods")
        .csharp_namespace("NativeCompressions.ZStandard")
        .csharp_dll_name("libzstd")
        .csharp_dll_name_if("UNITY_IOS && !UNITY_EDITOR", "__Internal")
        .csharp_entry_point_prefix("nativecompressions_")
        .csharp_method_prefix("")
        .csharp_class_accessibility("public")
        .generate_to_file("src/zstd_ffi.rs", "../NativeCompressions.ZStandard/ZStdNativeMethods.cs")?;

    compile_zstd();
    Ok(())
}

fn compile_zstd() {
    // https://github.com/facebook/zstd/blob/dev/build/cmake/CMakeLists.txt

    let mut config = cmake::Config::new("../../zstd/build/cmake/");
    config.always_configure(true);
    // config.profile(profile);
    
    config.define("ZSTD_LEGACY_SUPPORT", "OFF");
    config.define("ZSTD_MULTITHREAD_SUPPORT_DEFAULT", "OFF");
    config.define("ZSTD_BUILD_PROGRAMS", "OFF");
    config.define("ZSTD_BUILD_CONTRIB", "OFF");
    // config.define("ZSTD_BUILD_STATIC", "ON");
    let dst = config.build();

    println!("dst display: {}", dst.display());

    println!("cargo:rustc-link-search={}/lib", dst.display());
    println!("cargo:rustc-link-lib=static=zstd");
}