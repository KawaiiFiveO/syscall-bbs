using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Reflection; using System.Text; using System.Text.RegularExpressions;

// Makes ILSpy output of the game readable: every decoder call becomes its string, and obfuscated
// names with a known meaning get that name.
static class Rewrite {
  // ILSpy's form of a name that isn't a valid identifier: each other character becomes _XXXX (hex).
  public static string Mangle(string name) {
    var sb = new StringBuilder();
    foreach (char c in name)
      if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') sb.Append(c);
      else sb.Append('_').Append(((int)c).ToString("X4"));
    return sb.ToString();
  }

  public static string Literal(string s) {
    var sb = new StringBuilder("\"");
    foreach (char c in s)
      switch (c) {
        case '\\': sb.Append("\\\\"); break;
        case '"': sb.Append("\\\""); break;
        case '\n': sb.Append("\\n"); break;
        case '\r': sb.Append("\\r"); break;
        case '\t': sb.Append("\\t"); break;
        case '\0': sb.Append("\\0"); break;
        default:
          if (c < 0x20 || (c >= 0x7F && c <= 0x9F) || char.IsSurrogate(c) || c == (char)0x2028 || c == (char)0x2029
              || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            sb.Append("\\u").Append(((int)c).ToString("X4"));
          else sb.Append(c);
          break;
      }
    return sb.Append('"').ToString();
  }

  // names.tsv: a header row, then obfuscated name, readable name and an optional note per row.
  // Rows for other builds are harmless: their names don't occur.
  static IEnumerable<KeyValuePair<string,string>> LoadNames(string path) {
    foreach (var line in File.ReadAllLines(path).Skip(1)) {
      var f = line.Split('\t');
      if (f.Length >= 2 && f[0].Length > 0 && f[1].Length > 0) yield return new KeyValuePair<string,string>(f[0], f[1]);
    }
  }

  const string Marker = "_0023_003Dq"; // "#=q" as ILSpy escapes it

  static bool IsIdent(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

  // Replaces each escaped obfuscated name in one pass, taking the longest known name at each "#=q".
  static string ReplaceNames(string text, Dictionary<string,string> map, int[] lengths, Dictionary<string,int> used, ref int unknown) {
    int i = text.IndexOf(Marker, StringComparison.Ordinal);
    if (i < 0) return text;
    var sb = new StringBuilder(text.Length);
    int last = 0;
    while (i >= 0) {
      int end = i;
      while (end < text.Length && IsIdent(text[end])) end++;
      string key = null;
      foreach (int length in lengths)
        if (length <= end - i && map.ContainsKey(text.Substring(i, length))) { key = text.Substring(i, length); break; }
      if (key == null) { unknown++; i = text.IndexOf(Marker, i + Marker.Length, StringComparison.Ordinal); continue; }
      sb.Append(text, last, i - last).Append(map[key]);
      used.TryGetValue(key, out int u); used[key] = u + 1;
      last = i + key.Length;
      i = text.IndexOf(Marker, last, StringComparison.Ordinal);
    }
    return sb.Append(text, last, text.Length - last).ToString();
  }

  // ILSpy's file name for a type: other characters than [A-Za-z0-9_] become '-'.
  static string FileBase(string name) {
    var sb = new StringBuilder();
    foreach (char c in Aliases.StripArity(name)) sb.Append(IsIdent(c) ? c : '-');
    return sb.ToString();
  }

  public static int Run(Assembly asm, MethodInfo decoder, Func<int,string> decode, string dir, bool comments, string namesPath, bool aliases) {
    var type = Regex.Escape(Mangle(decoder.DeclaringType.Name));
    var call = new Regex(@"(?:(?:global::)?" + type + @"\.)?" + Regex.Escape(Mangle(decoder.Name)) + @"\((-?\d+)\)");

    // Known names, found by shape first, then the names file. They take priority over aliases.
    var names = new Dictionary<string,string>();
    names[decoder.DeclaringType.Name] = "StringDecoder";
    names[decoder.Name] = "Decode";
    if (asm.EntryPoint != null) {
      names[asm.EntryPoint.Name] = "Main";
      names[asm.EntryPoint.DeclaringType.Name] = "Program";
    }
    if (namesPath != null)
      foreach (var pair in LoadNames(namesPath)) names[pair.Key] = pair.Value;
    var aliasList = aliases ? Aliases.Build(asm) : new Dictionary<string, Aliases.Alias>();

    var map = new Dictionary<string,string>(); // escaped obfuscated name -> replacement
    foreach (var pair in aliasList) map[Mangle(pair.Key)] = pair.Value.Name;
    foreach (var pair in names.Where(p => Aliases.IsObfuscated(p.Key))) map[Mangle(pair.Key)] = pair.Value;
    var lengths = map.Keys.Select(k => k.Length).Distinct().OrderByDescending(n => n).ToArray();
    var used = new Dictionary<string,int>();

    var cache = new Dictionary<int,string>();
    int files = 0, replaced = 0, failed = 0, unknown = 0;
    var paths = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
    foreach (var path in paths) {
      var bytes = File.ReadAllBytes(path);
      bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
      var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
      var result = call.Replace(text, m => {
        int id = int.Parse(m.Groups[1].Value);
        if (!cache.TryGetValue(id, out var s)) {
          try { s = decode(id); } catch (Exception e) { s = null; Console.Error.WriteLine($"{id}: {e.GetType().Name}: {e.Message}"); }
          cache[id] = s;
        }
        if (s == null) { failed++; return m.Value; }
        replaced++;
        return comments ? m.Value + " /* " + Literal(s).Replace("*/", "*\\/") + " */" : Literal(s);
      });
      result = ReplaceNames(result, map, lengths, used, ref unknown);
      if (result == text) continue;
      var output = Encoding.UTF8.GetBytes(result);
      using (var o = File.Create(path)) {
        if (bom) o.Write(bytes, 0, 3);
        o.Write(output, 0, output.Length);
      }
      files++;
    }
    Console.Error.WriteLine($"{replaced} decoder calls replaced in {files} files, {failed} failed to decode.");
    var knownUsed = names.Keys.Count(k => used.ContainsKey(Mangle(k)));
    Console.Error.WriteLine($"{knownUsed} of {names.Count} known names occur.");

    if (aliases) {
      // Files named after obfuscated types take the type's new name.
      var newNames = new Dictionary<string,string>();
      foreach (var t in asm.GetTypes())
        if (!t.IsNested && Aliases.IsObfuscated(t.Name)) {
          var raw = Aliases.StripArity(t.Name);
          newNames[FileBase(raw)] = names.TryGetValue(raw, out var known) ? known : aliasList[raw].Name;
        }
      int renamed = 0, kept = 0;
      foreach (var path in paths)
        if (newNames.TryGetValue(Path.GetFileNameWithoutExtension(path), out var newName)) {
          var target = Path.Combine(Path.GetDirectoryName(path), newName + ".cs");
          if (File.Exists(target)) { kept++; continue; }
          File.Move(path, target);
          renamed++;
        }

      var table = new StringBuilder("obfuscated\tname\tuses\n");
      foreach (var pair in aliasList.OrderBy(p => p.Key, StringComparer.Ordinal))
        table.Append(pair.Key).Append('\t').Append(names.TryGetValue(pair.Key, out var known) ? known : pair.Value.Name).Append('\t').Append(pair.Value.Kind).Append('\n');
      File.WriteAllBytes(Path.Combine(dir, "aliases.tsv"), new UTF8Encoding(false).GetBytes(table.ToString()));
      Console.Error.WriteLine($"{aliasList.Count} obfuscated names aliased ({used.Values.Sum()} uses), {unknown} left unmatched. {renamed} files renamed, {kept} kept (name taken).");
    }
    return failed == 0 ? 0 : 1;
  }
}
