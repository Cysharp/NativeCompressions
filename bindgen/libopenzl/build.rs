use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
   bindgen::Builder::default()
        .header("../../openzl/include/openzl/openzl.h")
        .clang_arg("-I../../openzl/include")
        .generate_inline_functions(true)
        .default_enum_style(bindgen::EnumVariation::Rust {
            non_exhaustive: false,
        })
        // .wrap_static_fns(true) // check for ZL_INLINE function
        .generate()?
        .write_to_file("src/openzl.rs")?;

    csbindgen::Builder::default()
        .input_bindgen_file("src/openzl.rs")
        .method_filter(|x| x.starts_with("ZL_"))
        .always_included_types(["ZL_StandardGraphID", "ZL_StandardNodeID"])
        .csharp_class_name("OpenZLNativeMethods")
        .csharp_namespace("NativeCompressions.Interop")
        .csharp_dll_name("libopenzl")
        .csharp_class_accessibility("public")
        // .csharp_generate_const_filter(|x| x.starts_with("ZL_"))
        .generate_csharp_file("../../src/NativeCompressions.OpenZL.Core/Interop/OpenZLNativeMethods.cs")?;

    Ok(())
}
