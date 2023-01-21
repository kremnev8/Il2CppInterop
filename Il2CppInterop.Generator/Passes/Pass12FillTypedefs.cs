using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;
using Microsoft.Extensions.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Il2CppInterop.Generator.Passes;

public static class Pass12FillTypedefs
{
    public static void DoPass(RewriteGlobalContext context)
    {
        foreach (var assemblyContext in context.Assemblies)
        {
            var isUnmanagedAttribute = InjectIsUnmanagedAttribute(assemblyContext.NewAssembly);
            foreach (var typeContext in assemblyContext.Types)
            {
                foreach (var originalParameter in typeContext.OriginalType.GenericParameters)
                {
                    var newParameter = new GenericParameter(originalParameter.Name, typeContext.NewType);
                    typeContext.NewType.GenericParameters.Add(newParameter);

                    if (typeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.BlittableStruct)
                        newParameter.Attributes = originalParameter.Attributes.StripValueTypeConstraint();
                    else
                    {
                        newParameter.Attributes = originalParameter.Attributes;
                        newParameter.HasDefaultConstructorConstraint = true;
                        newParameter.Constraints.Add(
                            new GenericParameterConstraint(assemblyContext.Imports.Module.ImportReference(typeof(ValueType))
                                .MakeRequiredModifierType(
                                    assemblyContext.Imports.Module.ImportReference(typeof(System.Runtime.InteropServices.UnmanagedType)))));

                        newParameter.CustomAttributes.Add(new CustomAttribute(isUnmanagedAttribute.Methods[0]));
                        newParameter.HasNotNullableValueTypeConstraint = true;
                    }
                }

                if (typeContext.OriginalType.IsEnum)
                    typeContext.NewType.BaseType = assemblyContext.Imports.Module.Enum();
                else if (typeContext.ComputedTypeSpecifics == TypeRewriteContext.TypeSpecifics.BlittableStruct)
                    typeContext.NewType.BaseType = assemblyContext.Imports.Module.ValueType();
            }
        }

        // Second pass is explicitly done after first to account for rewriting of generic base types - value-typeness is important there
        foreach (var assemblyContext in context.Assemblies)
        foreach (var typeContext in assemblyContext.Types)
            if (!typeContext.OriginalType.IsEnum && typeContext.ComputedTypeSpecifics !=
                TypeRewriteContext.TypeSpecifics.BlittableStruct)
                typeContext.NewType.BaseType = assemblyContext.RewriteTypeRef(typeContext.OriginalType.BaseType);
    }

    private static TypeDefinition InjectIsUnmanagedAttribute(AssemblyDefinition assembly)
    {
        TypeDefinition attributeType = assembly.MainModule.GetType("System.Runtime.CompilerServices.IsUnmanagedAttribute");
        if (attributeType != null)
            return attributeType;

        attributeType = new TypeDefinition("System.Runtime.CompilerServices", "IsUnmanagedAttribute", TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit | TypeAttributes.NotPublic | TypeAttributes.Sealed, assembly.MainModule.ImportReference(typeof(Attribute)));
        assembly.MainModule.Types.Add(attributeType);

        var attributeCctr = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName, assembly.MainModule.TypeSystem.Void);
        attributeType.Methods.Add(attributeCctr);
        var ilProcessor = attributeCctr.Body.GetILProcessor();
        ilProcessor.Emit(OpCodes.Ldarg_0);
        ilProcessor.Emit(OpCodes.Call, assembly.MainModule.ImportReference(DefaultCtorFor(attributeType.BaseType)));
        ilProcessor.Emit(OpCodes.Ret);
        return attributeType;
    }

    public static MethodReference DefaultCtorFor(TypeReference type)
    {
        var resolved = type.Resolve();
        if (resolved == null)
            return null;

        var ctor = resolved.Methods.SingleOrDefault(m => m.IsConstructor && m.Parameters.Count == 0 && !m.IsStatic);
        if (ctor == null)
            return DefaultCtorFor(resolved.BaseType);

        return new MethodReference(".ctor", type.Module.TypeSystem.Void, type) { HasThis = true };
    }
}
