using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class MetadataIl
{
    // Development inspection of assembly metadata/IL only; no target code is invoked.
    public static void Print(string assemblyPath, string[] targets)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var ops = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => unchecked((ushort)o.Value));
        string TypeName(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
            HandleKind.TypeSpecification => "TypeSpec",
            _ => handle.Kind.ToString()
        };
        string TokenName(int token)
        {
            var handle = MetadataTokens.Handle(token);
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
                HandleKind.MemberReference => TypeName(reader.GetMemberReference((MemberReferenceHandle)handle).Parent) + "." + reader.GetString(reader.GetMemberReference((MemberReferenceHandle)handle).Name),
                HandleKind.FieldDefinition => reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
                HandleKind.UserString => reader.GetUserString((UserStringHandle)handle),
                HandleKind.MethodSpecification => "MethodSpec " + TokenName(MetadataTokens.GetToken(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method)),
                _ => handle.Kind.ToString()
            };
        }
        foreach (var th in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(th);
            var typeName = reader.GetString(type.Name);
            foreach(var mh in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(mh);
                var name = typeName + "." + reader.GetString(method.Name);
                if (!targets.Contains(name) || method.RelativeVirtualAddress == 0) continue;
                Console.WriteLine(name);
                var code = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                for (var i = 0; i < code.Length;)
                {
                    var position = i;
                    ushort value = code[i++];
                    if(value == 0xfe) value = (ushort)(0xfe00 + code[i++]);
                    var op = ops[value];
                    object argument = "";
                    switch (op.OperandType)
                    {
                        case OperandType.InlineMethod: case OperandType.InlineField: case OperandType.InlineType:
                        case OperandType.InlineTok: case OperandType.InlineString:
                            argument = TokenName(BitConverter.ToInt32(code,i)); i += 4; break;
                        case OperandType.InlineI: case OperandType.InlineBrTarget: case OperandType.InlineSig:
                            argument = BitConverter.ToInt32(code,i); i += 4; break;
                        case OperandType.ShortInlineI: case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineVar:
                            argument = code[i++]; break;
                        case OperandType.InlineVar: argument = BitConverter.ToInt16(code,i); i += 2; break;
                        case OperandType.InlineI8: argument = BitConverter.ToInt64(code,i); i += 8; break;
                        case OperandType.InlineR: argument = BitConverter.ToDouble(code,i); i += 8; break;
                        case OperandType.ShortInlineR: argument = BitConverter.ToSingle(code,i); i += 4; break;
                        case OperandType.InlineSwitch: var count = BitConverter.ToInt32(code,i); i += 4 + count*4; argument = count + " switch cases"; break;
                    }
                    Console.WriteLine($" {position:X4} {op.Name} {argument}");
                }
            }
        }
    }
}
