using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace NativeCompressions.Unity.Editor
{
    /// <summary>
    /// iOS links native plugins statically, so P/Invoke has to use "__Internal" as the library name.
    /// The NativeCompressions assemblies installed from NuGet use regular library names, so this
    /// rewrites those names in the player build output before IL2CPP runs. The assemblies in the
    /// project are left untouched, so the Editor keeps working.
    /// </summary>
    public sealed class IosNativeLibraryNameProcessor : IPostBuildPlayerScriptDLLs
    {
        // Library names used by the DllImport declarations in the Core assemblies.
        static readonly HashSet<string> NativeLibraryNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "lz4",
            "libzstd",
            "libopenzl",
        };

        const string InternalLibraryName = "__Internal";

        // Player assemblies, including precompiled ones, have been copied here when this callback runs.
        const string StagingManagedDirectory = "Temp/StagingArea/Data/Managed";

        public int callbackOrder => 0;

        public void OnPostBuildPlayerScriptDLLs(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;

            var rewritten = 0;
            foreach (var path in EnumerateAssemblies(report))
            {
                if (RewriteNativeLibraryNames(path)) rewritten++;
            }

            if (rewritten == 0)
            {
                Debug.LogWarning("NativeCompressions: no assembly referencing the native libraries was found in the player build output. Native calls will fail on iOS.");
            }
        }

        static IEnumerable<string> EnumerateAssemblies(BuildReport report)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(StagingManagedDirectory))
            {
                foreach (var path in Directory.GetFiles(StagingManagedDirectory, "*.dll"))
                {
                    if (seen.Add(Path.GetFullPath(path))) yield return path;
                }
            }

#if UNITY_2022_1_OR_NEWER
            var files = report.GetFiles();
#else
            var files = report.files;
#endif
            foreach (var file in files)
            {
                if (file.role != "ManagedLibrary" || !File.Exists(file.path)) continue;
                if (seen.Add(Path.GetFullPath(file.path))) yield return file.path;
            }
        }

        static bool RewriteNativeLibraryNames(string path)
        {
            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, ReadSymbols = false });
            }
            catch (Exception)
            {
                // Not a managed assembly, or not readable. Nothing to do.
                return false;
            }

            using (assembly)
            {
                var changed = false;
                foreach (var module in assembly.Modules)
                {
                    foreach (var moduleReference in module.ModuleReferences)
                    {
                        if (NativeLibraryNames.Contains(moduleReference.Name))
                        {
                            moduleReference.Name = InternalLibraryName;
                            changed = true;
                        }
                    }
                }

                if (!changed) return false;

                assembly.Write(path);
                Debug.Log($"NativeCompressions: rewrote native library names to {InternalLibraryName} in {Path.GetFileName(path)} for iOS.");
                return true;
            }
        }
    }
}
