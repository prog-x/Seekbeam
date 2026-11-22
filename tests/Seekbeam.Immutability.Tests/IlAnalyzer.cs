using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Seekbeam.Immutability.Tests;

/// <summary>A call site found in IL: caller (Type::Method) → callee (Type::Member(signature)).</summary>
public sealed record CallSite(string Assembly, string CallerType, string CallerMethod, string CalleeType, string CalleeMember, string CalleeSignature, string OpCode)
{
    public override string ToString() => $"{Assembly}: {CallerType}::{CallerMethod} -> {CalleeType}::{CalleeMember}({CalleeSignature}) [{OpCode}]";
}

public sealed record PInvoke(string Assembly, string DeclaringType, string Method, string Module, string EntryPoint)
{
    public override string ToString() => $"{Assembly}: {DeclaringType}::{Method} -> {Module}!{EntryPoint}";
}

public sealed record StringLiteral(string Assembly, string CallerType, string CallerMethod, string Value);

/// <summary>
/// Minimal IL decoder over System.Reflection.Metadata: enumerates every method body of an assembly and reports every
/// call/callvirt/newobj/ldftn/ldvirtftn target, every ldstr literal and every P/Invoke declaration. This sees through
/// wrappers, lambdas, local functions, async state machines and generated interop code (it works on compiled IL).
/// </summary>
public static class IlAnalyzer
{
    public sealed class Result
    {
        public List<CallSite> Calls { get; } = [];
        public List<PInvoke> PInvokes { get; } = [];
        public List<StringLiteral> Strings { get; } = [];
        public int Methods { get; set; }
    }

    public static Result Analyze(string assemblyPath)
    {
        var res = new Result();
        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        string asm = md.GetString(md.GetAssemblyDefinition().Name);
        var sigProvider = new SigNameProvider(md);
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            string typeName = FullName(md, td);
            foreach (var mh in td.GetMethods())
            {
                var mdef = md.GetMethodDefinition(mh);
                string methodName = md.GetString(mdef.Name);
                if ((mdef.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    var imp = mdef.GetImport();
                    string module = md.GetString(md.GetModuleReference(imp.Module).Name);
                    string entry = imp.Name.IsNil ? methodName : md.GetString(imp.Name);
                    res.PInvokes.Add(new PInvoke(asm, typeName, methodName, module, entry));
                }
                if (mdef.RelativeVirtualAddress == 0) continue;
                res.Methods++;
                var body = pe.GetMethodBody(mdef.RelativeVirtualAddress);
                var il = body.GetILReader();
                while (il.RemainingBytes > 0)
                {
                    ILOpCode op = ReadOpCode(ref il);
                    switch (op)
                    {
                        case ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Jmp:
                        {
                            var handle = MetadataTokens.EntityHandle(il.ReadInt32());
                            var (t, m, s) = Resolve(md, handle, sigProvider);
                            res.Calls.Add(new CallSite(asm, typeName, methodName, t, m, s, op.ToString()));
                            break;
                        }
                        case ILOpCode.Ldstr:
                            res.Strings.Add(new StringLiteral(asm, typeName, methodName, md.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32()))));
                            break;
                        default:
                            SkipOperand(ref il, op);
                            break;
                    }
                }
            }
        }
        return res;
    }

    private static string FullName(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        if (td.GetDeclaringType() is { IsNil: false } decl) return FullName(md, md.GetTypeDefinition(decl)) + "+" + name;
        string ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string TypeName(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition: return FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)h));
            case HandleKind.TypeReference:
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)h);
                string name = md.GetString(tr.Name);
                if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return TypeName(md, (EntityHandle)tr.ResolutionScope) + "+" + name;
                string ns = md.GetString(tr.Namespace);
                return ns.Length == 0 ? name : ns + "." + name;
            }
            case HandleKind.TypeSpecification:
            {
                var ts = md.GetTypeSpecification((TypeSpecificationHandle)h);
                var br = md.GetBlobReader(ts.Signature);
                // Generic instantiation: 0x15 GENERICINST, (CLASS|VALUETYPE), TypeDefOrRef
                if (br.ReadSignatureHeader().RawValue is 0x15)
                {
                    br.ReadByte();
                    return TypeName(md, br.ReadTypeHandle());
                }
                return "<typespec>";
            }
            default: return "<" + h.Kind + ">";
        }
    }

    private static (string Type, string Member, string Sig) Resolve(MetadataReader md, EntityHandle h, SigNameProvider sp)
    {
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                return (FullName(md, md.GetTypeDefinition(m.GetDeclaringType())), md.GetString(m.Name), ParamSig(m.DecodeSignature(sp, null)));
            }
            case HandleKind.MemberReference:
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)h);
                string sig = "";
                try { if (mr.GetKind() == MemberReferenceKind.Method) sig = ParamSig(mr.DecodeMethodSignature(sp, null)); } catch (BadImageFormatException) { }
                string parent = mr.Parent.Kind switch
                {
                    HandleKind.MethodDefinition => FullName(md, md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)mr.Parent).GetDeclaringType())),
                    HandleKind.ModuleReference => "<module>",
                    _ => TypeName(md, (EntityHandle)mr.Parent),
                };
                return (parent, md.GetString(mr.Name), sig);
            }
            case HandleKind.MethodSpecification:
            {
                var ms = md.GetMethodSpecification((MethodSpecificationHandle)h);
                return Resolve(md, ms.Method, sp);
            }
            default: return ("<" + h.Kind + ">", "?", "");
        }
    }

    private static string ParamSig(MethodSignature<string> s) => string.Join(",", s.ParameterTypes);

    private sealed class SigNameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string e, ArrayShape s) => e + "[,]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
        public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
        public string GetGenericTypeParameter(object? c, int i) => "!" + i;
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetPinnedType(string e) => e;
        public string GetPointerType(string e) => e + "*";
        public string GetPrimitiveType(PrimitiveTypeCode t) => "System." + t;
        public string GetSZArrayType(string e) => e + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => FullName(r, r.GetTypeDefinition(h));
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => TypeName(r, h);
        public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => TypeName(r, h);
    }

    private static ILOpCode ReadOpCode(ref BlobReader il)
    {
        byte b = il.ReadByte();
        return b == 0xFE ? (ILOpCode)(0xFE00 | il.ReadByte()) : (ILOpCode)b;
    }

    private static void SkipOperand(ref BlobReader il, ILOpCode op)
    {
        switch (op)
        {
            case ILOpCode.Switch: { int n = il.ReadInt32(); il.Offset += n * 4; return; }
            default:
                il.Offset += OperandSize(op);
                return;
        }
    }

    private static int OperandSize(ILOpCode op) => op switch
    {
        ILOpCode.Ldarg_s or ILOpCode.Ldarga_s or ILOpCode.Starg_s or ILOpCode.Ldloc_s or ILOpCode.Ldloca_s or ILOpCode.Stloc_s or ILOpCode.Ldc_i4_s
            or ILOpCode.Br_s or ILOpCode.Brfalse_s or ILOpCode.Brtrue_s or ILOpCode.Beq_s or ILOpCode.Bge_s or ILOpCode.Bgt_s or ILOpCode.Ble_s
            or ILOpCode.Blt_s or ILOpCode.Bne_un_s or ILOpCode.Bge_un_s or ILOpCode.Bgt_un_s or ILOpCode.Ble_un_s or ILOpCode.Blt_un_s
            or ILOpCode.Leave_s or ILOpCode.Unaligned => 1,
        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => 2,
        ILOpCode.Ldc_i8 or ILOpCode.Ldc_r8 => 8,
        ILOpCode.Ldc_i4 or ILOpCode.Ldc_r4 or ILOpCode.Br or ILOpCode.Brfalse or ILOpCode.Brtrue or ILOpCode.Beq or ILOpCode.Bge or ILOpCode.Bgt
            or ILOpCode.Ble or ILOpCode.Blt or ILOpCode.Bne_un or ILOpCode.Bge_un or ILOpCode.Bgt_un or ILOpCode.Ble_un or ILOpCode.Blt_un
            or ILOpCode.Leave or ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld
            or ILOpCode.Ldtoken or ILOpCode.Box or ILOpCode.Unbox or ILOpCode.Unbox_any or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Newarr
            or ILOpCode.Ldelema or ILOpCode.Ldelem or ILOpCode.Stelem or ILOpCode.Ldobj or ILOpCode.Stobj or ILOpCode.Cpobj or ILOpCode.Initobj
            or ILOpCode.Sizeof or ILOpCode.Mkrefany or ILOpCode.Refanyval or ILOpCode.Constrained or ILOpCode.Calli => 4,
        _ => 0,
    };
}
