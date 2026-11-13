using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Reflection; using System.Reflection.Emit; using System.Text;
static class P {
  const BindingFlags All = BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;

  static IEnumerable<MethodBase> Methods(Assembly asm) {
    foreach (var t in asm.GetTypes())
      foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
        yield return m;
  }

  // The decoder's name changes between builds, so find it by use: every string literal in the
  // game is "ldc.i4 <id>; call <decoder>", which makes it the most-called static int->string method.
  static MethodInfo FindDecoder(Assembly asm) {
    var module = asm.ManifestModule;
    var counts = new Dictionary<int,int>();
    foreach (var m in Methods(asm)) {
      var il = m.GetMethodBody()?.GetILAsByteArray();
      if (il == null) continue;
      for (int i = 0; i + 9 < il.Length; i++)
        if (il[i] == 0x20 && il[i + 5] == 0x28) { // ldc.i4, call
          int token = BitConverter.ToInt32(il, i + 6);
          counts.TryGetValue(token, out int n); counts[token] = n + 1;
        }
    }
    foreach (var pair in counts.OrderByDescending(c => c.Value)) {
      MethodBase m;
      try { m = module.ResolveMethod(pair.Key); } catch (ArgumentException) { continue; }
      if (m is MethodInfo mi && mi.IsStatic && mi.ReturnType == typeof(string) && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == typeof(int))
        return mi;
    }
    throw new InvalidOperationException("string decoder not found");
  }

  // IL opcode table, for walking method bodies instruction by instruction.
  static readonly OpCode[] One = new OpCode[256], Two = new OpCode[256];
  static P() {
    foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
      var op = (OpCode)f.GetValue(null);
      if (op.Size == 1) One[(ushort)op.Value] = op; else Two[(ushort)op.Value & 0xFF] = op;
    }
  }

  static int OperandSize(OpCode op, byte[] il, int at) {
    switch (op.OperandType) {
      case OperandType.InlineNone: return 0;
      case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: return 1;
      case OperandType.InlineVar: return 2;
      case OperandType.InlineI8: case OperandType.InlineR: return 8;
      case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, at);
      default: return 4;
    }
  }

  // The constant an ldc.i4 form pushes, or null for any other instruction.
  static int? Constant(OpCode op, byte[] il, int at) {
    if (op == OpCodes.Ldc_I4) return BitConverter.ToInt32(il, at);
    if (op == OpCodes.Ldc_I4_S) return (sbyte)il[at];
    if (op == OpCodes.Ldc_I4_M1) return -1;
    if (op.Size == 1 && op.Value >= OpCodes.Ldc_I4_0.Value && op.Value <= OpCodes.Ldc_I4_8.Value) return op.Value - OpCodes.Ldc_I4_0.Value;
    return null;
  }

  static string Site(MethodBase m) => (m.DeclaringType?.FullName ?? "<module>") + "::" + m.Name;

  static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

  // Every call to the decoder, as id -> distinct call sites in scan order.
  static SortedDictionary<int, List<string>> ScanCalls(Assembly asm, MethodInfo decoder, out int nonConstant) {
    var calls = new SortedDictionary<int, List<string>>();
    nonConstant = 0;
    foreach (var m in Methods(asm)) {
      var il = m.GetMethodBody()?.GetILAsByteArray();
      if (il == null) continue;
      int? last = null;
      for (int i = 0; i < il.Length; ) {
        OpCode op = il[i] == 0xFE ? Two[il[i + 1]] : One[il[i]];
        int at = i + op.Size;
        if ((op == OpCodes.Call || op == OpCodes.Callvirt) && BitConverter.ToInt32(il, at) == decoder.MetadataToken) {
          if (last is int id) {
            if (!calls.TryGetValue(id, out var sites)) calls[id] = sites = new List<string>();
            var site = Site(m);
            if (!sites.Contains(site)) sites.Add(site);
          } else nonConstant++;
        }
        last = Constant(op, il, at);
        i = at + OperandSize(op, il, at);
      }
    }
    return calls;
  }

  static int WriteTable(Assembly asm, Func<int,string> decode, MethodInfo decoder, string outPath) {
    var calls = ScanCalls(asm, decoder, out int nonConstant);
    int failed = 0;
    var sb = new StringBuilder("id\ttext\tsites\n");
    foreach (var pair in calls) {
      string text;
      try { text = Escape(decode(pair.Key)); }
      catch (Exception e) { text = ""; failed++; Console.Error.WriteLine($"{pair.Key}: {e.GetType().Name}: {e.Message}"); }
      sb.Append(pair.Key).Append('\t').Append(text).Append('\t').Append(string.Join(" ", pair.Value)).Append('\n');
    }
    var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
    if (outPath == null) { using (var o = Console.OpenStandardOutput()) o.Write(bytes, 0, bytes.Length); }
    else File.WriteAllBytes(outPath, bytes);
    Console.Error.WriteLine($"{calls.Count} strings, {calls.Values.Sum(s => s.Count)} call sites, {failed} failed to decode, {nonConstant} calls without a constant id.");
    return failed == 0 ? 0 : 1;
  }

  static int Main(string[] a) {
    if (a.Length < 2) {
      Console.Error.WriteLine("usage: strtool <game exe> <id> [<id>...]\n       strtool <game exe> --all [<out.tsv>]");
      return 2;
    }
    var asm = Assembly.LoadFrom(a[0]);
    var m = FindDecoder(asm);
    // A direct delegate call keeps a normal caller frame on the stack (the decryptor rejects reflection callers).
    var f = (Func<int,string>)Delegate.CreateDelegate(typeof(Func<int,string>), m);
    if (a[1] == "--all") return WriteTable(asm, f, m, a.Length > 2 ? a[2] : null);
    foreach (var s in a.Skip(1)) {
      try { Console.WriteLine(s + "\t" + f(int.Parse(s)).Replace("\r", "").Replace("\n", "\\n")); }
      catch (Exception e) { Console.WriteLine(s + "\tERR " + e.Message); }
    }
    return 0;
  }
}
