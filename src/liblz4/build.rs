use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
   bindgen::Builder::default()
        .header("../../lz4/lib/lz4.c")
        .header("../../lz4/lib/lz4hc.c")
        .header("../../lz4/lib/lz4frame.c")
        .header("../../lz4/lib/xxhash.c")
        .size_t_is_usize(true)
        .generate()
        .unwrap()
        .write_to_file("src/lz4.rs")
        .unwrap();

    csbindgen::Builder::default()
        .input_bindgen_file("src/lz4.rs")
        .method_filter(|x| x.starts_with("LZ4"))
        .rust_method_prefix("csbindgen_")
        .rust_file_header("use super::lz4;")
        .rust_method_type_path("lz4")
        .csharp_class_name("LibLz4")
        .csharp_namespace("CsBindgen")
        .csharp_dll_name("csbindgen_tests")
        .csharp_dll_name_if("UNITY_IOS && !UNITY_EDITOR", "__Internal")
        .csharp_entry_point_prefix("csbindgen_")
        .csharp_method_prefix("")
        .csharp_class_accessibility("public")
        //.csharp_c_long_convert("int")
        //.csharp_c_ulong_convert("uint")
        // .csharp_use_function_pointer(true)
        .generate_to_file("src/lz4_ffi.rs", "../atodekesu.cs")
        .unwrap();

    cc::Build::new()
        .files([
            "../../lz4/lib/lz4.c",
            "../../lz4/lib/lz4hc.c",
            "../../lz4/lib/lz4frame.c",
            "../../lz4/lib/xxhash.c",
        ])
        .compile("lz4");

    Ok(())
}
