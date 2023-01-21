using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Microsoft.Extensions.Logging;
using Mono.Cecil;

namespace Il2CppInterop.Generator.Passes;

public static class Pass11ComputeTypeSpecifics
{
    public static void DoPass(RewriteGlobalContext context)
    {
        foreach (var assemblyContext in context.Assemblies)
            foreach (var typeContext in assemblyContext.Types)
                ComputeSpecifics(typeContext);
    }

    private static bool IsValueTypeOnly(GenericParameter genericParameter) => genericParameter.Constraints.Any(constraint => constraint.ConstraintType.FullName == "System.ValueType");

    private static void ComputeSpecifics(TypeRewriteContext typeContext)
    {
        if (typeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.NotComputed) return;
        typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.Computing;

        foreach (var originalField in typeContext.OriginalType.Fields)
        {
            // Sometimes il2cpp metadata has invalid field offsets for some reason (https://github.com/SamboyCoding/Cpp2IL/issues/167)
            if (originalField.ExtractFieldOffset() >= 0x8000000)
            {
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }

            if (originalField.IsStatic) continue;

            var fieldType = originalField.FieldType;
            if (fieldType.IsPrimitive || fieldType.IsPointer) continue;

            if (fieldType is GenericParameter parameter &&
                !IsValueTypeOnly(parameter))
            {
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }
            if (fieldType.IsGenericParameter) continue;

            if (fieldType is GenericInstanceType genericInstance)
            {
                foreach (GenericParameter genericParameter in genericInstance.GenericParameters)
                {
                    if (!IsValueTypeOnly(genericParameter))
                    {
                        typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                        return;
                    }
                }
            }

            if (fieldType.FullName == "System.String" || fieldType.FullName == "System.Object" || fieldType.IsArray ||
                fieldType.IsByReference)
            {
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }

            var fieldTypeContext = typeContext.AssemblyContext.GlobalContext.GetNewTypeForOriginal(fieldType.Resolve());
            ComputeSpecifics(fieldTypeContext);
            if (fieldTypeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.BlittableStruct)
            {
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }
        }

        typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.BlittableStruct;
    }
}
