using System.Text;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Utils;
using Microsoft.Extensions.Logging;
using Mono.Cecil;
using Mono.Cecil.Rocks;

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
                    AddUnboxedVariant(typeContext);
                }
        }

        public static TypeDefinition AddUnboxedVariant(TypeRewriteContext typeContext)
        {
            if (typeContext.NewUnboxedType != null)
                return typeContext.NewUnboxedType;

            //TODO handle generics correctly

            bool isGeneric = typeContext.OriginalType.IsGenericInstance;

            StringBuilder nameBuilder = new StringBuilder(typeContext.OriginalType.Name);
            if (nameBuilder[nameBuilder.Length - 2] == '`')
            {
                nameBuilder.Remove(nameBuilder.Length - 2, 2);
            }

            if (isGeneric)
            {
                nameBuilder.Append("_Gen");
                GenericInstanceType genericType = typeContext.OriginalType.GetElementType() as GenericInstanceType;

                foreach (TypeReference argument in genericType.GenericArguments)
                {
                    nameBuilder.Append("_");
                    nameBuilder.Append(argument.Name);
                }
            }

            nameBuilder.Append("_Unboxed");

            Logger.Instance.LogInformation($"Making unboxed variant {nameBuilder}");

            TypeDefinition newType = new TypeDefinition(
                typeContext.OriginalType.Namespace,
                nameBuilder.ToString(),
                AdjustAttributes(typeContext.OriginalType.Attributes));
            newType.IsSequentialLayout = false;

            typeContext.NewType.NestedTypes.Add(newType);
            typeContext.NewUnboxedType = newType;

            var imports = typeContext.AssemblyContext.Imports;


            foreach (var originalField in typeContext.OriginalType.Fields)
            {
                try
                {
                    if (originalField.IsStatic) continue;
                    var fieldType = originalField.FieldType;

                    if (fieldType.IsPrimitive)
                    {
                        newType.Fields.Add(new FieldDefinition(originalField.Name, FieldAttributes.Public, imports.Module.ImportReference(fieldType)));
                    }
                    else if (fieldType.IsGenericParameter)
                    {
                        Logger.Instance.LogInformation($"Struct has a generic parameter {fieldType.Name}");
                    }
                    else
                    {
                        TypeReference mainFieldType = fieldType;
                        if (fieldType.IsPointer)
                        {
                            mainFieldType = fieldType.GetElementType();
                        }

                        var typedef = mainFieldType.Resolve();
                        if (typedef == null)
                        {
                            Logger.Instance.LogInformation($"Failed to resolve {mainFieldType.FullName}");
                            return null;
                        }

                        var fieldTypeContext = typeContext.AssemblyContext.GlobalContext.GetNewTypeForOriginal(typedef);
                        if (fieldTypeContext.ComputedTypeSpecifics == TypeRewriteContext.TypeSpecifics.ReferenceType)
                        {
                            var field = new FieldDefinition(originalField.Name, FieldAttributes.Public, imports.Module.IntPtr());
                            newType.Fields.Add(field);
                        }
                        else if (fieldTypeContext.ComputedTypeSpecifics == TypeRewriteContext.TypeSpecifics.NonBlittableStruct)
                        {
                            TypeDefinition newFieldType;
                            if (fieldTypeContext != typeContext)
                                newFieldType = AddUnboxedVariant(fieldTypeContext);
                            else
                                newFieldType = newType;

                            if (newFieldType == null)
                            {
                                Logger.Instance.LogInformation($"Failed to get unbox for {fieldTypeContext.OriginalType.FullName}");
                                return null;
                            }

                            var field = new FieldDefinition(originalField.Name, FieldAttributes.Public, imports.Module.ImportReference(newFieldType));
                            newType.Fields.Add(field);
                        }
                        else
                        {
                            newType.Fields.Add(new FieldDefinition(originalField.Name, FieldAttributes.Public, imports.Module.ImportReference(fieldType)));
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
