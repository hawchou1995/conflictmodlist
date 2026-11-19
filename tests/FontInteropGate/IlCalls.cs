using System.Reflection;
using System.Reflection.Emit;

namespace FontInteropGate;

// ECMA-335 IL 里 call / callvirt / newobj 的 4 字节方法 token 提取。
// OpCode.Value 是 short：双字节指令（0xFE 前缀）在 short 里是负数（如 0xFE01 -> -511），
// 所以统一归一化成 ushort 键（0xFE00 | 第二字节），否则双字节指令一律查不到、解码会停在那里。
// 解码失败（未识别 opcode、操作数越界、token 不足 4 字节）必须显式抛错，由调用方判红，绝不静默截断。
public static class IlCalls
{
    public sealed class DecodeException : Exception
    {
        public DecodeException(string message) : base(message) { }
    }

    private static readonly Dictionary<ushort, OpCode> Ops = Build();

    public static IReadOnlyCollection<ushort> KnownOpcodes => Ops.Keys;

    public static IReadOnlyList<int> CallTokens(byte[] il)
    {
        var tokens = new List<int>();
        int i = 0;
        while (i < il.Length)
        {
            ushort key;
            int opcodeOffset = i;
            if (il[i] == 0xFE)
            {
                if (i + 1 >= il.Length) throw new DecodeException($"偏移 {opcodeOffset} 处只有 0xFE 前缀，缺少第二个 opcode 字节");
                key = (ushort)(0xFE00 | il[i + 1]);
                i += 2;
            }
            else
            {
                key = il[i];
                i += 1;
            }
            if (!Ops.TryGetValue(key, out var op)) throw new DecodeException($"偏移 {opcodeOffset} 处未识别的 opcode 0x{key:X4}");

            if (op.Value == OpCodes.Call.Value || op.Value == OpCodes.Callvirt.Value || op.Value == OpCodes.Newobj.Value)
            {
                if (i + 4 > il.Length) throw new DecodeException($"偏移 {opcodeOffset} 处 {op.Name} 的方法 token 不足 4 字节");
                tokens.Add(BitConverter.ToInt32(il, i));
            }

            int operand = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => i + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, i) : -1,
                _ => 4,
            };
            if (operand < 0 || i + operand > il.Length) throw new DecodeException($"偏移 {opcodeOffset} 处 {op.Name} 的操作数越界（需要 {operand}，剩余 {il.Length - i}）");
            i += operand;
        }
        return tokens;
    }

    private static Dictionary<ushort, OpCode> Build()
    {
        var map = new Dictionary<ushort, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is OpCode op) map[unchecked((ushort)op.Value)] = op;
        return map;
    }
}
