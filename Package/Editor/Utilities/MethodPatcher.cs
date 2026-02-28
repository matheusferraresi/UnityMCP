using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UnixxtyMCP.Editor.Utilities
{
    /// <summary>
    /// Runtime method patching utility using Harmony 2.x (loaded dynamically).
    /// Provides reliable method-level hot reload during Play Mode.
    ///
    /// Harmony (MIT license) is bundled as 0Harmony.dll in Package/Plugins/.
    /// Loaded via reflection to avoid Burst compiler hashing errors.
    /// Falls back to native JIT redirect only if Harmony fails.
    /// </summary>
    public static class MethodPatcher
    {
        private static readonly Dictionary<string, PatchRecord> _activePatches = new Dictionary<string, PatchRecord>();

        /// <summary>
        /// Patch a method by redirecting it to a replacement method.
        /// Uses Harmony prefix patching - the replacement completely replaces the original.
        /// </summary>
        public static string PatchMethod(MethodInfo original, MethodInfo replacement)
        {
            if (original == null)
                throw new ArgumentNullException(nameof(original));
            if (replacement == null)
                throw new ArgumentNullException(nameof(replacement));

            string patchId = $"{original.DeclaringType?.FullName}.{original.Name}";

            if (_activePatches.ContainsKey(patchId))
                UnpatchMethod(patchId);

            try
            {
                return PatchWithHarmony(original, replacement, patchId);
            }
            catch (Exception harmonyEx)
            {
                Debug.LogWarning($"[MethodPatcher] Harmony patch failed for {patchId}, falling back to native redirect: {harmonyEx.Message}");
                try
                {
                    return PatchWithNativeRedirect(original, replacement, patchId);
                }
                catch (Exception nativeEx)
                {
                    throw new InvalidOperationException(
                        $"Both Harmony and native redirect failed for {patchId}.\n" +
                        $"Harmony: {harmonyEx.Message}\nNative: {nativeEx.Message}", nativeEx);
                }
            }
        }

        private static string PatchWithHarmony(MethodInfo original, MethodInfo replacement, string patchId)
        {
            HarmonyBridge.Patch(original, replacement);

            _activePatches[patchId] = new PatchRecord
            {
                patchId = patchId,
                original = original,
                replacement = replacement,
                patchedAt = DateTime.UtcNow,
                engine = PatchEngine.Harmony
            };

            Debug.Log($"[MethodPatcher] Patched {patchId} via Harmony");
            return patchId;
        }

        /// <summary>
        /// Fallback: direct native code redirect (x64 only, more fragile).
        /// Writes a JMP instruction at the original method's native code entry point.
        /// </summary>
        private static string PatchWithNativeRedirect(MethodInfo original, MethodInfo replacement, string patchId)
        {
            RuntimeHelpers.PrepareMethod(original.MethodHandle);
            RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

            IntPtr originalPtr = original.MethodHandle.GetFunctionPointer();
            IntPtr replacementPtr = replacement.MethodHandle.GetFunctionPointer();

            byte[] originalBytes = new byte[12];
            Marshal.Copy(originalPtr, originalBytes, 0, 12);

            // Write x64 absolute jump: mov rax, addr; jmp rax
            byte[] jumpPatch = new byte[12];
            jumpPatch[0] = 0x48; // REX.W
            jumpPatch[1] = 0xB8; // MOV RAX, imm64
            BitConverter.GetBytes(replacementPtr.ToInt64()).CopyTo(jumpPatch, 2);
            jumpPatch[10] = 0xFF; // JMP RAX
            jumpPatch[11] = 0xE0;
            Marshal.Copy(jumpPatch, 0, originalPtr, 12);

            _activePatches[patchId] = new PatchRecord
            {
                patchId = patchId,
                original = original,
                replacement = replacement,
                originalNativePtr = originalPtr,
                originalBytes = originalBytes,
                patchedAt = DateTime.UtcNow,
                engine = PatchEngine.NativeRedirect
            };

            Debug.Log($"[MethodPatcher] Patched {patchId} via native redirect (fallback)");
            return patchId;
        }

        public static bool UnpatchMethod(string patchId)
        {
            if (!_activePatches.TryGetValue(patchId, out var record))
                return false;

            try
            {
                if (record.engine == PatchEngine.Harmony)
                {
                    HarmonyBridge.Unpatch(record.original);
                }
                else if (record.engine == PatchEngine.NativeRedirect && record.originalBytes != null)
                {
                    Marshal.Copy(record.originalBytes, 0, record.originalNativePtr, record.originalBytes.Length);
                }

                _activePatches.Remove(patchId);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MethodPatcher] Failed to unpatch {patchId}: {ex.Message}");
                return false;
            }
        }

        public static int UnpatchAll()
        {
            int count = _activePatches.Count;
            var patchIds = _activePatches.Keys.ToList();

            foreach (var id in patchIds)
                UnpatchMethod(id);

            try { HarmonyBridge.UnpatchAll(); } catch { }

            _activePatches.Clear();
            return count;
        }

        public static IReadOnlyDictionary<string, PatchRecord> ActivePatches => _activePatches;

        public static bool IsHarmonyAvailable => HarmonyBridge.IsAvailable;

        public static string HarmonyVersion => HarmonyBridge.Version;

        public class PatchRecord
        {
            public string patchId;
            public MethodInfo original;
            public MethodInfo replacement;
            public IntPtr originalNativePtr;
            public byte[] originalBytes;
            public DateTime patchedAt;
            public PatchEngine engine;
        }

        public enum PatchEngine
        {
            Harmony,
            NativeRedirect
        }

        /// <summary>
        /// Reflection bridge to Harmony 2.x — avoids compile-time reference
        /// that would trigger Burst compiler metadata hashing errors.
        /// </summary>
        private static class HarmonyBridge
        {
            private const string HARMONY_ID = "com.unixxtymcp.hotpatch";

            private static readonly Assembly _asm;
            private static readonly object _instance;
            private static readonly MethodInfo _patchMethod;
            private static readonly MethodInfo _unpatchMethod;
            private static readonly MethodInfo _unpatchAllMethod;
            private static readonly Type _harmonyMethodType;
            private static readonly object _patchTypeAll;

            public static bool IsAvailable { get; }
            public static string Version { get; }

            static HarmonyBridge()
            {
                try
                {
                    _asm = Assembly.Load("0Harmony");
                    if (_asm == null) return;

                    Version = _asm.GetName().Version?.ToString() ?? "unknown";

                    var harmonyType = _asm.GetType("HarmonyLib.Harmony");
                    _harmonyMethodType = _asm.GetType("HarmonyLib.HarmonyMethod");
                    var patchTypeEnum = _asm.GetType("HarmonyLib.HarmonyPatchType");

                    if (harmonyType == null || _harmonyMethodType == null || patchTypeEnum == null)
                        return;

                    _patchTypeAll = Enum.Parse(patchTypeEnum, "All");

                    // new Harmony(id)
                    _instance = Activator.CreateInstance(harmonyType, HARMONY_ID);

                    // Harmony.Patch(MethodBase original, HarmonyMethod prefix, ...)
                    _patchMethod = harmonyType.GetMethod("Patch", new[]
                    {
                        typeof(MethodBase),
                        _harmonyMethodType,
                        _harmonyMethodType,
                        _harmonyMethodType,
                        _harmonyMethodType
                    });

                    // Harmony.Unpatch(MethodBase original, HarmonyPatchType type, string harmonyID)
                    _unpatchMethod = harmonyType.GetMethod("Unpatch", new[]
                    {
                        typeof(MethodBase),
                        patchTypeEnum,
                        typeof(string)
                    });

                    // Harmony.UnpatchAll(string harmonyID)
                    _unpatchAllMethod = harmonyType.GetMethod("UnpatchAll", new[] { typeof(string) });

                    IsAvailable = _instance != null && _patchMethod != null;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[HarmonyBridge] Failed to load Harmony dynamically: {ex.Message}");
                    IsAvailable = false;
                    Version = "unavailable";
                }
            }

            public static void Patch(MethodInfo original, MethodInfo prefix)
            {
                if (!IsAvailable)
                    throw new InvalidOperationException("Harmony is not available");

                // new HarmonyMethod(prefix)
                var harmonyMethod = Activator.CreateInstance(_harmonyMethodType, prefix);

                // harmony.Patch(original, prefix: harmonyMethod, postfix: null, transpiler: null, finalizer: null)
                _patchMethod.Invoke(_instance, new object[] { original, harmonyMethod, null, null, null });
            }

            public static void Unpatch(MethodInfo original)
            {
                if (!IsAvailable || _unpatchMethod == null) return;
                _unpatchMethod.Invoke(_instance, new object[] { original, _patchTypeAll, HARMONY_ID });
            }

            public static void UnpatchAll()
            {
                if (!IsAvailable || _unpatchAllMethod == null) return;
                _unpatchAllMethod.Invoke(_instance, new object[] { HARMONY_ID });
            }
        }
    }
}
