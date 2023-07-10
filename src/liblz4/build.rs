use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
   bindgen::Builder::default()
        .header("../../lz4/lib/lz4.c")
        .header("../../lz4/lib/lz4hc.c")
        .header("../../lz4/lib/lz4frame.c")
        .header("../../lz4/lib/xxhash.c")
        .size_t_is_usize(false)
        .generate()
        .unwrap()
        .write_to_file("src/lz4.rs")
        .unwrap();

    csbindgen::Builder::default()
        .input_bindgen_file("src/lz4.rs")
        .method_filter(|x| x.starts_with("LZ4"))
        .rust_method_prefix("nativecompressions_")
        .rust_file_header("use super::lz4;")
        .rust_method_type_path("lz4")
        .csharp_class_name("Lz4NativeMethods")
        .csharp_namespace("NativeCompressions.Lz4")
        .csharp_dll_name("liblz4")
        .csharp_dll_name_if("UNITY_IOS && !UNITY_EDITOR", "__Internal")
        .csharp_entry_point_prefix("nativecompressions_")
        .csharp_method_prefix("")
        .csharp_class_accessibility("public")
        .generate_to_file("src/lz4_ffi.rs", "../NativeCompressions.Lz4/Lz4NativeMethods.cs")
        .unwrap();

    cc::Build::new()
        .files([
            "../../lz4/lib/lz4.c",
            "../../lz4/lib/lz4hc.c",
            "../../lz4/lib/lz4frame.c",
            "../../lz4/lib/xxhash.c",
        ])
        .opt_level(3)
        .compile("lz4");

    Ok(())
}
