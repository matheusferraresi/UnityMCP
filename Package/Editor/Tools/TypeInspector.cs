using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnixxtyMCP.Editor.Core;

namespace UnixxtyMCP.Editor.Tools
{
    /// <summary>
    /// Deep type reflection tool. Returns fields, properties, methods, attributes,
    /// and inheritance chain for any loaded C# type.
    /// </summary>
    public static class TypeInspector
    {
        [MCPTool("type_inspector", "Inspect a C# type's fields, properties, methods, attributes, and inheritance chain via reflection",
            Category = "Utility", ReadOnlyHint = true)]
        public static object Inspect(
            [MCPParam("type_name", "Type name - can be simple (e.g. 'Transform'), full (e.g. 'UnityEngine.Transform'), or assembly-qualified", required: true)] string typeName,
            [MCPParam("include_methods", "Include method signatures (default: false)")] bool includeMethods = false,
            [MCPParam("include_inherited", "Include inherited members (default: true)")] bool includeInherited = true,
            [MCPParam("serialized_only", "Only show Unity-serializable fields (default: false)")] bool serializedOnly = false)
        {
            if (string.IsNullOrEmpty(typeName))
                throw MCPException.InvalidParams("'type_name' is required.");

            var type = ResolveType(typeName);
            if (type == null)
                throw MCPException.InvalidParams($"Type '{typeName}' not found in any loaded assembly.");

            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            if (!includeInherited)
                bindingFlags |= BindingFlags.DeclaredOnly;

            var fields = GetFields(type, bindingFlags, serializedOnly);
            var properties = GetProperties(type, bindingFlags);

            var result = new Dictionary<string, object>
            {
                ["type"] = type.Name,
                ["namespace"] = type.Namespace ?? "",
                ["full_name"] = type.FullName,
                ["base_type"] = type.BaseType?.FullName,
                ["is_abstract"] = type.IsAbstract,
                ["is_sealed"] = type.IsSealed,
                ["is_interface"] = type.IsInterface,
                ["is_enum"] = type.IsEnum,
                ["interfaces"] = type.GetInterfaces().Select(i => i.FullName).ToArray(),
                ["fields"] = fields,
                ["properties"] = properties
            };

            if (type.IsEnum)
            {
                result["enum_values"] = Enum.GetNames(type);
            }

            if (includeMethods)
            {
                result["methods"] = GetMethods(type, bindingFlags);
            }

            // Custom attributes on the type itself
            var attrs = type.GetCustomAttributes(false);
            if (attrs.Length > 0)
            {
                result["attributes"] = attrs.Select(a => a.GetType().Name).ToArray();
            }

            return result;
        }

        private static Type ResolveType(string typeName)
        {
            // Try direct resolution first
            var type = Type.GetType(typeName);
            if (type != null) return type;

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Try exact match
                type = assembly.GetType(typeName);
                if (type != null) return type;
            }

            // Try common Unity namespaces
            string[] commonNamespaces = {
                "UnityEngine", "UnityEditor", "UnityEngine.UI",
                "TMPro", "UnityEngine.EventSystems",
                "UnityEngine.Rendering.Universal"
            };

            foreach (var ns in commonNamespaces)
            {
                var fullName = $"{ns}.{typeName}";
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
            }

            // Fuzzy: search by simple name across all assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
                    if (type != null) return type;
                }
                catch (ReflectionTypeLoadException) { }
            }

            return null;
        }

        private static List<object> GetFields(Type type, BindingFlags flags, bool serializedOnly)
        {
            var results = new List<object>();
            foreach (var field in type.GetFields(flags))
            {
                bool isSerialized = IsUnitySerialized(field);
                if (serializedOnly && !isSerialized)
                    continue;

                var attrs = field.GetCustomAttributes(false)
                    .Select(a => a.GetType().Name)
                    .ToArray();

                results.Add(new Dictionary<string, object>
                {
                    ["name"] = field.Name,
                    ["type"] = PrettyTypeName(field.FieldType),
                    ["access"] = field.IsPublic ? "public" : field.IsPrivate ? "private" : "protected",
                    ["is_static"] = field.IsStatic,
                    ["serialized"] = isSerialized,
                    ["attributes"] = attrs
                });
            }
            return results;
        }

        private static List<object> GetProperties(Type type, BindingFlags flags)
        {
            var results = new List<object>();
            foreach (var prop in type.GetProperties(flags))
            {
                var getter = prop.GetGetMethod(true);
                var setter = prop.GetSetMethod(true);

                results.Add(new Dictionary<string, object>
                {
                    ["name"] = prop.Name,
                    ["type"] = PrettyTypeName(prop.PropertyType),
                    ["can_read"] = prop.CanRead,
                    ["can_write"] = prop.CanWrite,
                    ["access"] = getter != null ? (getter.IsPublic ? "public" : getter.IsPrivate ? "private" : "protected") : "private"
                });
            }
            return results;
        }

        private static List<object> GetMethods(Type type, BindingFlags flags)
        {
            var results = new List<object>();
            foreach (var method in type.GetMethods(flags))
            {
                // Skip property accessors and object base methods unless declared
                if (method.IsSpecialName) continue;

                var parameters = method.GetParameters().Select(p => new Dictionary<string, object>
                {
                    ["name"] = p.Name,
                    ["type"] = PrettyTypeName(p.ParameterType),
                    ["optional"] = p.IsOptional
                }).ToArray();

                results.Add(new Dictionary<string, object>
                {
                    ["name"] = method.Name,
                    ["return_type"] = PrettyTypeName(method.ReturnType),
                    ["access"] = method.IsPublic ? "public" : method.IsPrivate ? "private" : "protected",
                    ["is_static"] = method.IsStatic,
                    ["is_virtual"] = method.IsVirtual,
                    ["parameters"] = parameters
                });
            }
            return results;
        }

        private static bool IsUnitySerialized(FieldInfo field)
        {
            // Public fields are serialized by default (unless NonSerialized)
            if (field.IsPublic && !field.IsDefined(typeof(NonSerializedAttribute), false))
                return true;

            // Private/protected fields with [SerializeField]
            if (field.IsDefined(typeof(SerializeField), false))
                return true;

            return false;
        }

        private static string PrettyTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type == typeof(long)) return "long";
            if (type == typeof(byte)) return "byte";

            if (type.IsGenericType)
            {
                var baseName = type.Name.Split('`')[0];
                var args = string.Join(", ", type.GetGenericArguments().Select(PrettyTypeName));
                return $"{baseName}<{args}>";
            }

            if (type.IsArray)
                return $"{PrettyTypeName(type.GetElementType())}[]";

            return type.Name;
        }
    }
}
