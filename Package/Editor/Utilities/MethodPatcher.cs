using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace UnityMCP.Editor.Utilities
{
    /// <summary>
    /// Runtime method patching utility using Harmony 2.x.
    /// Provides reliable method-level hot reload during Play Mode.
    ///
    /// Harmony (MIT license) is bundled as 0Harmony.dll in Package/Plugins/.
    /// Falls back to native JIT redirect only if Harmony fails.
    /// </summary>
    public static class MethodPatcher
    {
        private static readonly Dictionary<string, PatchRecord> _activePatches = new Dictionary<string, PatchRecord>();

        private static Harmony _harmony;
        private static Harmony HarmonyInstance
        {
            get
            {
                if (_harmony == null)
                    _harmony = new Harmony("com.unitymcp.hotpatch");
                return _harmony;
            }
        }

        /// <summary>
        /// Patch a method by redirecting it to a replacement method.
        /// Uses Harmony prefix patching - the replacement completely replaces the original.
        /// </summary>
        /// <param name="original">The original method to patch</param>
        /// <param name="replacement">The replacement method. For Harmony prefix patching,
        /// this must be a static method. For instance methods, the first parameter should be
        /// the instance type (named __instance by Harmony convention).</param>
        /// <returns>A patch ID that can be used to unpatch later</returns>
        public static string PatchMethod(MethodInfo original, MethodInfo replacement)
        {
            if (original == null)
                throw new ArgumentNullException(nameof(original));
            if (replacement == null)
                throw new ArgumentNullException(nameof(replacement));

            string patchId = $"{original.DeclaringType?.FullName}.{original.Name}";

            // Unpatch if already patched
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

        /// <summary>
        /// Patch using Harmony prefix. The prefix method returning false skips the original.
        /// </summary>
        private static string PatchWithHarmony(MethodInfo original, MethodInfo replacement, string patchId)
        {
            HarmonyInstance.Patch(original, prefix: new HarmonyMethod(replacement));

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

            // Save original bytes for unpatching
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

        /// <summary>
        /// Unpatch a specific method by its patch ID.
        /// </summary>
        public static bool UnpatchMethod(string patchId)
        {
            if (!_activePatches.TryGetValue(patchId, out var record))
                return false;

            try
            {
                if (record.engine == PatchEngine.Harmony)
                {
                    HarmonyInstance.Unpatch(record.original, HarmonyPatchType.All, "com.unitymcp.hotpatch");
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

        /// <summary>
        /// Unpatch all active patches (called on exiting play mode).
        /// </summary>
        public static int UnpatchAll()
        {
            int count = _activePatches.Count;
            var patchIds = _activePatches.Keys.ToList();

            foreach (var id in patchIds)
                UnpatchMethod(id);

            try { HarmonyInstance.UnpatchAll("com.unitymcp.hotpatch"); } catch { }

            _activePatches.Clear();
            return count;
        }

        /// <summary>
        /// Get all active patches.
        /// </summary>
        public static IReadOnlyDictionary<string, PatchRecord> ActivePatches => _activePatches;

        /// <summary>
        /// Check if Harmony is available (always true now since it's bundled).
        /// </summary>
        public static bool IsHarmonyAvailable => true;

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
    }
}
