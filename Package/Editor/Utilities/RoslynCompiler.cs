using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Utilities
{
    /// <summary>
    /// Dynamically loads Roslyn from Unity's editor installation and compiles C# code at runtime.
    /// No bundled DLLs needed - uses the Roslyn that ships with every Unity installation.
    /// </summary>
    public static class RoslynCompiler
    {
        private static Assembly _csharpAssembly;
        private static Assembly _codeAnalysisAssembly;
        private static bool _initialized;
        private static bool _available;
        private static string _loadError;

        public static bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _available;
            }
        }

        public static string LoadError
        {
            get
            {
                EnsureInitialized();
                return _loadError;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                // Unity ships Roslyn at Editor/Data/DotNetSdkRoslyn/
                string editorPath = Path.GetDirectoryName(EditorApplication.applicationPath);
                string roslynDir = Path.Combine(editorPath, "Data", "DotNetSdkRoslyn");

                if (!Directory.Exists(roslynDir))
                {
                    _loadError = $"Roslyn directory not found at '{roslynDir}'.";
                    return;
                }

                string codeAnalysisPath = Path.Combine(roslynDir, "Microsoft.CodeAnalysis.dll");
                string csharpPath = Path.Combine(roslynDir, "Microsoft.CodeAnalysis.CSharp.dll");

                if (!File.Exists(codeAnalysisPath) || !File.Exists(csharpPath))
                {
                    _loadError = "Microsoft.CodeAnalysis DLLs not found in Unity's Roslyn directory.";
                    return;
                }

                _codeAnalysisAssembly = Assembly.LoadFrom(codeAnalysisPath);
                _csharpAssembly = Assembly.LoadFrom(csharpPath);
                _available = true;
            }
            catch (Exception ex)
            {
                _loadError = $"Failed to load Roslyn: {ex.Message}";
            }
        }

        /// <summary>
        /// Compile a Harmony-compatible prefix method from a method body string.
        /// Returns the compiled MethodInfo, or null on failure.
        /// </summary>
        public static MethodInfo CompileHarmonyPrefix(MethodInfo original, string newBody, Type ownerType)
        {
            if (!IsAvailable)
            {
                Debug.LogWarning($"[RoslynCompiler] Roslyn not available: {_loadError}");
                return null;
            }

            try
            {
                string code = BuildPrefixSource(original, newBody, ownerType);
                byte[] assemblyBytes = Compile(code);
                if (assemblyBytes == null) return null;

                var patchAssembly = Assembly.Load(assemblyBytes);
                var patchType = patchAssembly.GetType("__HotPatchTemp");
                return patchType?.GetMethod("__Replacement", BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RoslynCompiler] Compilation error: {ex.Message}");
                return null;
            }
        }

        private static string BuildPrefixSource(MethodInfo original, string newBody, Type ownerType)
        {
            var parameters = original.GetParameters();
            var paramList = string.Join(", ",
                parameters.Select(p => $"{FormatTypeName(p.ParameterType)} {p.Name}"));

            // Harmony prefix: instance methods get __instance as first param
            string thisParam = "";
            if (!original.IsStatic)
            {
                thisParam = $"{FormatTypeName(ownerType)} __instance";
                if (parameters.Length > 0) thisParam += ", ";
            }

            // If method returns a value, add __result ref parameter
            string resultParam = "";
            if (original.ReturnType != typeof(void))
                resultParam = $", ref {FormatTypeName(original.ReturnType)} __result";

            return $@"
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class __HotPatchTemp {{
    public static bool __Replacement({thisParam}{paramList}{resultParam}) {{
        {newBody}
        return false; // Skip original
    }}
}}";
        }

        private static string FormatTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type == typeof(object)) return "object";
            if (type == typeof(long)) return "long";
            if (type == typeof(byte)) return "byte";

            // Use FullName for everything else, falling back to Name
            return type.FullName ?? type.Name;
        }

        /// <summary>
        /// Compile C# source code into an in-memory assembly. Returns raw bytes or null on failure.
        /// All Roslyn calls use reflection to avoid compile-time dependency.
        /// </summary>
        private static byte[] Compile(string sourceCode)
        {
            // CSharpSyntaxTree.ParseText(sourceCode)
            var syntaxTreeType = _csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
            var parseText = syntaxTreeType.GetMethod("ParseText",
                new[] { typeof(string), _csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions"),
                        typeof(string), _codeAnalysisAssembly.GetType("System.Text.Encoding") ?? typeof(object) });

            // Fallback: find the simplest ParseText overload
            if (parseText == null)
            {
                parseText = syntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "ParseText")
                    .OrderBy(m => m.GetParameters().Length)
                    .First();
            }

            object syntaxTree;
            var parseParams = parseText.GetParameters();
            if (parseParams.Length == 1)
            {
                syntaxTree = parseText.Invoke(null, new object[] { sourceCode });
            }
            else
            {
                // Fill optional params with defaults
                var args = new object[parseParams.Length];
                args[0] = sourceCode;
                for (int i = 1; i < args.Length; i++)
                    args[i] = parseParams[i].HasDefaultValue ? parseParams[i].DefaultValue : null;
                syntaxTree = parseText.Invoke(null, args);
            }

            // Build MetadataReferences from all loaded assemblies
            var metadataRefType = _codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.MetadataReference");
            var createFromFile = metadataRefType.GetMethod("CreateFromFile",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);

            // Fallback: find it with more params
            if (createFromFile == null)
            {
                createFromFile = metadataRefType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "CreateFromFile")
                    .OrderBy(m => m.GetParameters().Length)
                    .First();
            }

            var portableRefType = _codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.PortableExecutableReference");
            var refListType = typeof(List<>).MakeGenericType(portableRefType ?? metadataRefType);
            var references = Activator.CreateInstance(refListType);
            var addMethod = refListType.GetMethod("Add");

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
                    {
                        object metaRef;
                        if (createFromFile.GetParameters().Length == 1)
                            metaRef = createFromFile.Invoke(null, new object[] { assembly.Location });
                        else
                        {
                            var cfParams = createFromFile.GetParameters();
                            var cfArgs = new object[cfParams.Length];
                            cfArgs[0] = assembly.Location;
                            for (int i = 1; i < cfArgs.Length; i++)
                                cfArgs[i] = cfParams[i].HasDefaultValue ? cfParams[i].DefaultValue : null;
                            metaRef = createFromFile.Invoke(null, cfArgs);
                        }
                        addMethod.Invoke(references, new[] { metaRef });
                    }
                }
                catch { /* Skip assemblies that can't be referenced */ }
            }

            // CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            var outputKindType = _codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.OutputKind");
            var dllOutputKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");

            var compilationOptionsType = _csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
            var optionsCtor = compilationOptionsType.GetConstructors()
                .OrderBy(c => c.GetParameters().Length)
                .First();
            var optionsCtorParams = optionsCtor.GetParameters();
            var optionsArgs = new object[optionsCtorParams.Length];
            optionsArgs[0] = dllOutputKind;
            for (int i = 1; i < optionsArgs.Length; i++)
                optionsArgs[i] = optionsCtorParams[i].HasDefaultValue ? optionsCtorParams[i].DefaultValue : null;
            var options = optionsCtor.Invoke(optionsArgs);

            // CSharpCompilation.Create(name, syntaxTrees, references, options)
            var compilationType = _csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
            var createMethod = compilationType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Create")
                .OrderBy(m => m.GetParameters().Length)
                .First();

            var syntaxTreeBaseType = _codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.SyntaxTree");
            var syntaxTreeArray = Array.CreateInstance(syntaxTreeBaseType, 1);
            syntaxTreeArray.SetValue(syntaxTree, 0);

            var createParams = createMethod.GetParameters();
            var createArgs = new object[createParams.Length];
            createArgs[0] = $"HotPatch_{Guid.NewGuid():N}"; // assemblyName
            createArgs[1] = syntaxTreeArray;                  // syntaxTrees
            createArgs[2] = references;                        // references
            createArgs[3] = options;                           // options
            for (int i = 4; i < createArgs.Length; i++)
                createArgs[i] = createParams[i].HasDefaultValue ? createParams[i].DefaultValue : null;
            var compilation = createMethod.Invoke(null, createArgs);

            // compilation.Emit(stream)
            using (var ms = new MemoryStream())
            {
                var emitMethod = compilation.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Emit" && m.GetParameters().Length >= 1 &&
                                m.GetParameters()[0].ParameterType == typeof(Stream))
                    .OrderBy(m => m.GetParameters().Length)
                    .First();

                object emitResult;
                var emitParams = emitMethod.GetParameters();
                if (emitParams.Length == 1)
                {
                    emitResult = emitMethod.Invoke(compilation, new object[] { ms });
                }
                else
                {
                    var emitArgs = new object[emitParams.Length];
                    emitArgs[0] = ms;
                    for (int i = 1; i < emitArgs.Length; i++)
                        emitArgs[i] = emitParams[i].HasDefaultValue ? emitParams[i].DefaultValue : null;
                    emitResult = emitMethod.Invoke(compilation, emitArgs);
                }

                // Check result.Success
                var successProp = emitResult.GetType().GetProperty("Success");
                bool success = (bool)successProp.GetValue(emitResult);

                if (!success)
                {
                    // Get diagnostics
                    var diagProp = emitResult.GetType().GetProperty("Diagnostics");
                    var diagnostics = (System.Collections.IEnumerable)diagProp.GetValue(emitResult);
                    var errorSeverity = _codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity");
                    var errorValue = Enum.Parse(errorSeverity, "Error");

                    var errors = new List<string>();
                    foreach (var diag in diagnostics)
                    {
                        var sevProp = diag.GetType().GetProperty("Severity");
                        var severity = sevProp.GetValue(diag);
                        if (severity.Equals(errorValue))
                        {
                            var msgMethod = diag.GetType().GetMethod("GetMessage",
                                new Type[] { }) ?? diag.GetType().GetMethod("GetMessage");
                            string msg = msgMethod != null
                                ? (string)msgMethod.Invoke(diag, msgMethod.GetParameters().Length == 0
                                    ? Array.Empty<object>()
                                    : new object[] { null })
                                : diag.ToString();
                            errors.Add(msg);
                        }
                    }

                    Debug.LogError($"[RoslynCompiler] Compilation failed:\n{string.Join("\n", errors)}");
                    return null;
                }

                ms.Seek(0, SeekOrigin.Begin);
                return ms.ToArray();
            }
        }
    }
}
