using System; using System.Collections.Generic; using System.Linq; using System.Reflection;

// Short aliases for obfuscated names: a prefix from what the name is used for, then the start of
// the obfuscated name, made unique. One alias per name, since every member with the same original
// name shares its obfuscated name (overrides must).
static class Aliases {
  const BindingFlags All = BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
  const int MinTag = 4, MaxPrefixTag = 8, DivergeTag = 3, MaxHint = 24;

  static bool IsAsciiIdent(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

  public sealed class Alias { public string Name, Kind; }

  public static bool IsObfuscated(string name) => name.StartsWith("#=q");

  public static string StripArity(string name) { int i = name.IndexOf('`'); return i < 0 ? name : name.Substring(0, i); }

  static readonly Dictionary<Type,string> Keywords = new Dictionary<Type,string> {
    [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(char)] = "char",
    [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint",
    [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double",
    [typeof(decimal)] = "decimal", [typeof(string)] = "string", [typeof(object)] = "obj",
  };

  static string TypeKind(Type t) =>
    t.IsInterface ? "Interface" : t.IsEnum ? "Enum" : t.IsValueType ? "Struct" :
    t.IsSubclassOf(typeof(MulticastDelegate)) ? "Delegate" : "Class";

  // A field or parameter name from its type: int -> int, Vector2 -> vector2, float[] -> floatArray.
  static string Hint(Type t) {
    try {
      if (t.IsByRef) return Hint(t.GetElementType());
      if (t.IsArray) return Hint(t.GetElementType()) + "Array";
      if (t.IsPointer) return Hint(t.GetElementType()) + "Ptr";
      if (t.IsGenericParameter) return "t";
      if (Keywords.TryGetValue(t, out var k)) return k;
      var name = StripArity(t.Name);
      // Obfuscated, invisible or very long type names give the kind instead.
      if (IsObfuscated(name) || name.Length == 0 || name.Length > MaxHint || !name.All(IsAsciiIdent))
        switch (TypeKind(t)) { case "Interface": return "iface"; case "Enum": return "enum"; case "Struct": return "val"; case "Delegate": return "fn"; default: return "obj"; }
      return char.ToLowerInvariant(name[0]) + name.Substring(1);
    } catch (Exception) { return "obj"; }
  }

  // Every obfuscated name in the assembly, with how it's used ("Class", "Method", "field:int", ...).
  static Dictionary<string, HashSet<string>> Uses(Assembly asm) {
    var uses = new Dictionary<string, HashSet<string>>();
    void Add(string name, string use) {
      name = StripArity(name);
      if (!IsObfuscated(name)) return;
      if (!uses.TryGetValue(name, out var set)) uses[name] = set = new HashSet<string>();
      set.Add(use);
    }
    foreach (var t in asm.GetTypes()) {
      Add(t.Name, TypeKind(t));
      if (t.IsGenericTypeDefinition) foreach (var g in t.GetGenericArguments()) Add(g.Name, "Generic");
      foreach (var f in t.GetFields(All)) Add(f.Name, t.IsEnum && f.IsLiteral ? "Value" : "field:" + Hint(f.FieldType));
      foreach (var p in t.GetProperties(All)) Add(p.Name, "Prop");
      foreach (var e in t.GetEvents(All)) Add(e.Name, "Event");
      foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All))) {
        if (m is MethodInfo) Add(m.Name, "Method");
        if (m.IsGenericMethodDefinition) foreach (var g in m.GetGenericArguments()) Add(g.Name, "Generic");
        ParameterInfo[] ps;
        try { ps = m.GetParameters(); } catch (Exception) { continue; }
        foreach (var p in ps) if (p.Name != null) Add(p.Name, "param:" + Hint(p.ParameterType));
      }
    }
    return uses;
  }

  static string Prefix(HashSet<string> uses) {
    var kinds = new HashSet<string>(uses.Select(u => u.Split(':')[0]));
    if (kinds.Count == 1) {
      var kind = kinds.First();
      if (kind == "field" || kind == "param") {
        var hints = new HashSet<string>(uses.Select(u => u.Split(':')[1]));
        return hints.Count == 1 ? hints.First() : kind == "field" ? "field" : "arg";
      }
      return kind == "Generic" ? "T" : kind;
    }
    var typeKinds = new[] { "Class", "Struct", "Enum", "Interface", "Delegate" };
    if (kinds.All(typeKinds.Contains)) return "Type";
    if (kinds.IsSubsetOf(new[] { "field", "param" })) {
      var hints = new HashSet<string>(uses.Select(u => u.Split(':')[1]));
      return hints.Count == 1 ? hints.First() : "var";
    }
    return "Name";
  }

  // The obfuscated name without "#=q" and padding, as identifier characters.
  static string Body(string name) => name.Substring(3).TrimEnd('=').Replace('$', 'S');

  public static Dictionary<string, Alias> Build(Assembly asm) {
    var uses = Uses(asm);
    var bodies = uses.Keys.Select(n => new { Name = n, Body = Body(n) }).OrderBy(x => x.Body, StringComparer.Ordinal).ToList();
    var tags = new string[bodies.Count];
    for (int i = 0; i < bodies.Count; i++) {
      // Longest prefix shared with a sorted neighbour. Names whose first 16 original characters
      // match share their first 22 obfuscated ones (Eazfuscator encrypts in 16-byte blocks).
      int shared = 0;
      foreach (int j in new[] { i - 1, i + 1 })
        if (j >= 0 && j < bodies.Count) shared = Math.Max(shared, CommonPrefix(bodies[i].Body, bodies[j].Body));
      var body = bodies[i].Body;
      if (shared >= body.Length) tags[i] = body + "_" + i;                                  // identical bodies
      else if (shared < MaxPrefixTag) tags[i] = body.Substring(0, Math.Max(MinTag, shared + 1)); // shortest unique prefix
      else tags[i] = body.Substring(0, MinTag) + "_" + body.Substring(shared, Math.Min(DivergeTag, body.Length - shared));
    }
    // The divergence form can repeat by chance: those names get their whole body.
    var repeated = new HashSet<string>(tags.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key));
    var result = new Dictionary<string, Alias>();
    for (int i = 0; i < bodies.Count; i++) {
      var tag = repeated.Contains(tags[i]) ? bodies[i].Body : tags[i];
      var prefix = Prefix(uses[bodies[i].Name]);
      result[bodies[i].Name] = new Alias { Name = prefix + "_" + tag, Kind = string.Join(" ", uses[bodies[i].Name].OrderBy(u => u, StringComparer.Ordinal)) };
    }
    if (result.Values.Select(a => a.Name).Distinct().Count() != result.Count) throw new InvalidOperationException("duplicate aliases");
    return result;
  }

  static int CommonPrefix(string a, string b) {
    int n = 0;
    while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
    return n;
  }
}
