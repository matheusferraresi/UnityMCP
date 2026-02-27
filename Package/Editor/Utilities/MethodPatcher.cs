using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UnityMCP.Editor.Utilities
{
    /// <summary>
    /// Runtime method patching utility. Uses Harmony when available (USE_HARMONY define),
    /// falls back to direct JIT redirect for simple cases.
    ///
    /// To enable Harmony:
    /// 1. Add 0Harmony.dll (~200KB) to Package/Plugins/
    /// 2. Add USE_HARMONY to Scripting Define Symbols (Player Settings)
    ///
    /// Without Harmony, uses unsafe native code redirect (x64 only, more fragile).
    /// </summary>
    public static class MethodPatcher
    {
        private static readonly Dictionary<string, PatchRecord> _activePatches = new Dictionary<string, PatchRecord>();

#if USE_HARMONY
        private static HarmonyLib.Harmony _harmony;
        private static HarmonyLib.Harmony Harmony
        {
            get
            {
                if (_harmony == null)
                    _harmony = new HarmonyLib.Harmony("com.unitymcp.hotpatch");
                return _harmony;
            }
        }
#endif

        /// <summary>
        /// Patch a method by redirecting it to a replacement delegate.
        /// </summary>
        /// <param name="original">The original method to patch</param>
        /// <param name="replacement">The replacement method (must have same signature)</param>
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

#if USE_HARMONY
            return PatchWithHarmony(original, replacement, patchId);
#else
            return PatchWithRedirect(original, replacement, patchId);
#endif
        }

#if USE_HARMONY
        private static string PatchWithHarmony(MethodInfo original, MethodInfo replacement, string patchId)
        {
            try
            {
                // Use Harmony prefix that completely replaces the method
                Harmony.Patch(original, prefix: new HarmonyLib.HarmonyMethod(replacement));

                _activePatches[patchId] = new PatchRecord
                {
                    patchId = patchId,
                    original = original,
                    replacement = replacement,
                    patchedAt = DateTime.UtcNow,
                    engine = PatchEngine.Harmony
                };

                return patchId;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Harmony patch failed for {patchId}: {ex.Message}", ex);
            }
        }
#endif

        private static string PatchWithRedirect(MethodInfo original, MethodInfo replacement, string patchId)
        {
            // Fallback: direct native code redirect (x64 only)
            // Force JIT compilation of both methods
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
#if USE_HARMONY
                if (record.engine == PatchEngine.Harmony)
                {
                    Harmony.Unpatch(record.original, HarmonyLib.HarmonyPatchType.All, "com.unitymcp.hotpatch");
                }
                else
#endif
                if (record.engine == PatchEngine.NativeRedirect && record.originalBytes != null)
                {
                    // Restore original bytes
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

#if USE_HARMONY
            try { Harmony.UnpatchAll("com.unitymcp.hotpatch"); } catch { }
#endif

            _activePatches.Clear();
            return count;
        }

        /// <summary>
        /// Get all active patches.
        /// </summary>
        public static IReadOnlyDictionary<string, PatchRecord> ActivePatches => _activePatches;

        /// <summary>
        /// Check if Harmony is available.
        /// </summary>
        public static bool IsHarmonyAvailable
        {
            get
            {
#if USE_HARMONY
                return true;
#else
                return false;
#endif
            }
        }

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
