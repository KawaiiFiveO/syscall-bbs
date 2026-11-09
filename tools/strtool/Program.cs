using System; using System.Collections.Generic; using System.Linq; using System.Reflection;
static class P {
  const BindingFlags All = BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;

  // The decoder's name changes between builds, so find it by use: every string literal in the
  // game is "ldc.i4 <id>; call <decoder>", which makes it the most-called static int->string method.
  static MethodInfo FindDecoder(Assembly asm) {
    var module = asm.ManifestModule;
    var counts = new Dictionary<int,int>();
    foreach (var t in asm.GetTypes())
      foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All))) {
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

  static int Main(string[] a) {
    var asm = Assembly.LoadFrom(a[0]);
    var m = FindDecoder(asm);
    // A direct delegate call keeps a normal caller frame on the stack (the decryptor rejects reflection callers).
    var f = (Func<int,string>)Delegate.CreateDelegate(typeof(Func<int,string>), m);
    foreach (var s in a.Skip(1)) {
      try { Console.WriteLine(s + "\t" + f(int.Parse(s)).Replace("\r", "").Replace("\n", "\\n")); }
      catch (Exception e) { Console.WriteLine(s + "\tERR " + e.Message); }
    }
    return 0;
  }
}
