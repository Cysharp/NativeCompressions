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
        .csharp_dll_name_if("__IOS__ || MACCATALYST", "__Internal")
        .csharp_class_accessibility("public")
        // .csharp_generate_const_filter(|x| x.starts_with("ZL_"))
        .generate_csharp_file("../../src/NativeCompressions.OpenZL.Core/Interop/OpenZLNativeMethods.cs")?;

    guard_function_pointer_fields("../../src/NativeCompressions.OpenZL.Core/Interop/OpenZLNativeMethods.cs")?;

    Ok(())
}

// .NET Framework cannot marshal a pointer to a struct that holds a function pointer,
// so the netstandard2.0 build declares those fields as void* (same size and layout).
fn guard_function_pointer_fields(path: &str) -> Result<(), Box<dyn Error>> {
    let source = std::fs::read_to_string(path)?;
    let mut output = String::with_capacity(source.len());
    for line in source.lines() {
        let trimmed = line.trim_start();
        let indent = &line[..line.len() - trimmed.len()];
        if let Some(rest) = trimmed.strip_prefix("public delegate* unmanaged[Cdecl]<") {
            if let (Some(close), true) = (rest.rfind('>'), rest.ends_with(';')) {
                let name = rest[close + 1..rest.len() - 1].trim();
                if !name.is_empty() && !name.contains(' ') {
                    output.push_str("#if NETSTANDARD2_0\n");
                    output.push_str(&format!("{indent}public void* {name}; // .NET Framework cannot marshal a pointer to a struct holding a function pointer\n"));
                    output.push_str("#else\n");
                    output.push_str(line);
                    output.push_str("\n#endif\n");
                    continue;
                }
            }
        }
        output.push_str(line);
        output.push('\n');
    }
    std::fs::write(path, output)?;
    Ok(())
}
