using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Utils;
using Microsoft.Extensions.Logging;
using Mono.Cecil;

namespace Il2CppInterop.Generator.Passes
{
    public static class Pass12CreateUnboxedStructTypedefs
    {
        public static void DoPass(RewriteGlobalContext context)
        {
            foreach (var assemblyContext in context.Assemblies)
            foreach (var typeContext in assemblyContext.Types)
                if (typeContext.ComputedTypeSpecifics == TypeRewriteContext.TypeSpecifics.NonBlittableStruct)
                {
                    AddUnboxedVariant(typeContext, Array.Empty<TypeReference>());
                }
        }

        public static TypeDefinition AddUnboxedVariant(TypeRewriteContext typeContext, TypeReference[] genericArguments)
        {
            if (typeContext.newUnboxedTypes.ContainsKey(genericArguments))
                return typeContext.newUnboxedTypes[genericArguments];

            var originalType = typeContext.OriginalType;
            if (originalType.HasGenericParameters && originalType.GenericParameters.Count != genericArguments.Length)
            {
                return null;
            }

            StringBuilder nameBuilder = new StringBuilder(originalType.Name);
            if (nameBuilder[nameBuilder.Length - 2] == '`')
            {
                nameBuilder.Remove(nameBuilder.Length - 2, 2);
            }

            foreach (TypeReference argument in genericArguments)
            {
                nameBuilder.Append('_');
                nameBuilder.Append(argument.Name);
            }

            nameBuilder.Append("_Unboxed");

            Logger.Instance.LogInformation($"Making unboxed variant {nameBuilder}");

            var imports = typeContext.AssemblyContext.Imports;

            TypeDefinition newType = new TypeDefinition(
                originalType.Namespace,
                nameBuilder.ToString(),
                AdjustAttributes(originalType.Attributes));
            newType.BaseType = imports.Module.ValueType();
            newType.IsExplicitLayout = true;

            typeContext.NewType.NestedTypes.Add(newType);
            typeContext.newUnboxedTypes.Add(genericArguments, newType);

            foreach (var originalField in originalType.Fields)
            {
                try
                {
                    if (originalField.IsStatic) continue;
                    var fieldType = originalField.FieldType;

                    if (fieldType.IsPrimitive)
                    {
                        AddField(originalField, newType, imports.Module.ImportReference(fieldType));
                    }else if (fieldType.IsPointer)
                    {
                        // TODO handle and generate pointers

                        AddField(originalField, newType, imports.Module.IntPtr());
                    }
                    else
                    {
                        if (fieldType.IsGenericParameter && fieldType is GenericParameter genParam)
                        {
                            var index = originalType.GenericParameters.IndexOf(genParam);
                            if (index < 0) continue;

                            fieldType = genericArguments[index];
                        }

                        var typedef = fieldType.Resolve();
                        if (typedef == null)
                        {
                            Logger.Instance.LogInformation($"Failed to resolve {fieldType.FullName}");
                            return null;
                        }

                        var fieldTypeContext = typeContext.AssemblyContext.GlobalContext.GetNewTypeForOriginal(typedef);
                        if (fieldTypeContext.ComputedTypeSpecifics == TypeRewriteContext.TypeSpecifics.ReferenceType)
                        {
                            AddField(originalField, newType, imports.Module.IntPtr());
                        }
                        else if (fieldTypeContext.ComputedTypeSpecifics == TypeRewriteContext.TypeSpecifics.NonBlittableStruct)
                        {
                            TypeDefinition newFieldType;
                            if (fieldTypeContext != typeContext)
                            {
                                var newTypeGenericArguments = Array.Empty<TypeReference>();
                                if (fieldType.HasGenericParameters)
                                {
                                    newTypeGenericArguments = ReinterpretGenericArguments(typeContext, genericArguments, fieldType);
                                }else if (fieldType is GenericInstanceType genericInstanceType && genericInstanceType.HasGenericArguments)
                                {
                                    newTypeGenericArguments = genericInstanceType.GenericArguments.ToArray();
                                }
                                newFieldType = AddUnboxedVariant(fieldTypeContext, newTypeGenericArguments);
                            }
                            else
                            {
                                newFieldType = newType;
                            }

                            if (newFieldType == null)
                            {
                                Logger.Instance.LogInformation($"Failed to get unbox for {fieldTypeContext.OriginalType.FullName}");
                                return null;
                            }

                            AddField(originalField, newType, imports.Module.ImportReference(newFieldType));
                        }
                        else
                        {
                            AddField(originalField, newType, imports.Module.ImportReference(fieldType));
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Instance.LogInformation($"Failed to add field {originalField.Name}:\n{e}");
                }
            }

            return newType;
        }

        private static void AddField(FieldDefinition originalField, TypeDefinition newType, TypeReference fieldType)
        {
            FieldDefinition newField = new FieldDefinition(originalField.Name, FieldAttributes.Public, fieldType);

            newField.Offset = Convert.ToInt32(
                (string)originalField.CustomAttributes
                    .Single(it => it.AttributeType.Name == "FieldOffsetAttribute")
                    .Fields.Single().Argument.Value, 16);

            // Special case: bools in Il2Cpp are bytes
            if (newField.FieldType.FullName == "System.Boolean")
                newField.MarshalInfo = new MarshalInfo(NativeType.U1);

            newType.Fields.Add(newField);
        }

        private static TypeReference[] ReinterpretGenericArguments(TypeRewriteContext typeContext, TypeReference[] genericArguments, TypeReference fieldType)
        {
            var typeGenParams = typeContext.OriginalType.GenericParameters;
            var newTypeGenericArguments = fieldType.GenericParameters.Select(parameter =>
            {
                var index = typeGenParams.IndexOf(parameter);
                if (index > 0) return genericArguments[index];
                Logger.Instance.LogInformation($"Failed to match generic parameter {parameter.FullName}");
                return null;
            }).ToArray();
            return newTypeGenericArguments;
        }

        private static TypeAttributes AdjustAttributes(TypeAttributes typeAttributes)
        {
            typeAttributes |= TypeAttributes.BeforeFieldInit;
            typeAttributes &= ~(TypeAttributes.Abstract | TypeAttributes.Interface | TypeAttributes.Class | TypeAttributes.Sealed);

            var visibility = typeAttributes & TypeAttributes.VisibilityMask;
            if (visibility == 0 || visibility == TypeAttributes.Public)
                return typeAttributes | TypeAttributes.Public;

            return (typeAttributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPublic;
        }
    }
}
