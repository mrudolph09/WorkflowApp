using System.Runtime.InteropServices;
using System.Windows;

// CA5392: every [DllImport] needs a search-path declaration. DefaultDllImportSearchPathsAttribute
// is only valid on a method or the assembly (not a class - see Workflow.Terminal.Native.NativeMethods),
// so it is declared once here for the whole assembly. All P/Invoke in this app is kernel32, so
// System32 is both correct and the hardened choice.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
