using System.Runtime.InteropServices;

// The native library is looked up next to this assembly and in the directories the OS loader treats as safe,
// which are the application directory, the system directory and the ones added with AddDllDirectory.
// The current directory and PATH are left out, so a library planted there is not loaded when the bundled one is missing.
// Only Windows has those extra places, the other platforms keep their usual lookup.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
