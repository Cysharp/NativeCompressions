use std::error::Error;

// Declared by the sources but not exported by the shared library (static linking only or internal),
// calling them fails with EntryPointNotFoundException.
const NOT_EXPORTED: &[&str] = &[
    "LZ4_compress_fast_extState_fastReset",
    "LZ4_compress_destSize_extState",
    "LZ4_compress_forceExtDict",
    "LZ4_decompress_safe_forceExtDict",
    "LZ4_decompress_safe_partial_forceExtDict",
    "LZ4_loadDict_internal",
    "LZ4_setCompressionLevel",
    "LZ4_favorDecompressionSpeed",
    "LZ4_compress_HC_extStateHC_fastReset",
    "LZ4HC_searchExtDict",
    "LZ4F_getErrorCode",
    "LZ4F_getBlockSize",
    "LZ4F_uncompressedUpdate",
    "LZ4F_createCompressionContext_advanced",
    "LZ4F_createDecompressionContext_advanced",
    "LZ4F_createCDict_advanced",
    "LZ4F_compressBegin_internal",
    "LZ4F_compressBegin_usingDictOnce",
];

fn main() -> Result<(), Box<dyn Error>> {
   bindgen::Builder::default()
        .header("../../lz4/lib/lz4.c")
        .header("../../lz4/lib/lz4hc.c")
        .header("../../lz4/lib/lz4frame.c")
        .header("../../lz4/lib/xxhash.c")
        .default_enum_style(bindgen::EnumVariation::Rust {
            non_exhaustive: false,
        })
        .generate()?
        .write_to_file("src/lz4.rs")?;

    csbindgen::Builder::default()
        .input_bindgen_file("src/lz4.rs")
        .method_filter(|x| x.starts_with("LZ4") && !NOT_EXPORTED.contains(&x.as_str()))
        .csharp_class_name("LZ4NativeMethods")
        .csharp_namespace("NativeCompressions.Interop")
        .csharp_dll_name("lz4")
        .csharp_dll_name_if("__IOS__ || MACCATALYST", "__Internal")
        .csharp_class_accessibility("public")
        // .csharp_generate_const_filter(|x| x.starts_with("LZ4_"))
        .generate_csharp_file("../../src/NativeCompressions.LZ4.Core/Interop/LZ4NativeMethods.cs")?;

    guard_function_pointer_fields("../../src/NativeCompressions.LZ4.Core/Interop/LZ4NativeMethods.cs")?;

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
