use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
    // https://github.com/facebook/openzl/blob/dev/include/openzl/openzl.h
   bindgen::Builder::default()
        .header("../../openzl/include/openzl/openzl.h")
        .clang_arg("-I../../openzl/include")
        .generate()?
        .write_to_file("src/openzl.rs")?;

    csbindgen::Builder::default()
        .input_bindgen_file("src/openzl.rs")
        .method_filter(|x| x.starts_with("ZL_"))
        .csharp_class_name("OpenZLNativeMethods")
        .csharp_namespace("NativeCompressions.Interop")
        .csharp_dll_name("libopenzl")
        .csharp_class_accessibility("public")
        .generate_csharp_file("../../src/NativeCompressions.OpenZL.Core/Interop/OpenZLNativeMethods.cs")?;

    Ok(())
}
