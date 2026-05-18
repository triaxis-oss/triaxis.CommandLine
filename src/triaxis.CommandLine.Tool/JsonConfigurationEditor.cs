namespace triaxis.CommandLine;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>
/// Applies the minimal set of changes to an existing JSON document while leaving
/// every untouched byte — comments, whitespace, key order, value formatting —
/// exactly as it was. The document is read into a token tree where the raw source
/// run preceding each token (its <em>trivia</em>: whitespace, commas, comments) is
/// retained verbatim; only the tokens a change actually touches are rewritten, and
/// the tree is rendered back by concatenation. Insertion synthesises trivia from a
/// sibling so new members match the surrounding indentation; a JSON array that has
/// to gain a non-positional key is rewritten in place to an object.
/// </summary>
internal static class JsonConfigurationEditor
{
    private static readonly JsonSerializerOptions ScalarOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        // Skipped comments stay in the inter-token byte gap, so they are captured as
        // trivia and round-trip untouched without any comment-token handling.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private struct Tok
    {
        public string Lead;
        public string Text;
    }

    private abstract class Node;

    private sealed class Val : Node
    {
        public Tok Tok;
    }

    private sealed class Member
    {
        public Tok Name;     // Lead holds the run before the key (incl. any comma)
        public string Key = "";
        public Tok Colon;    // synthetic on insert; captured (": ") on parse
        public Node Value = null!;
    }

    private sealed class Obj : Node
    {
        public Tok Open;
        public List<Member> Members = [];
        public Tok Close;
    }

    private sealed class Elem
    {
        public Node Value = null!;
        public string Lead = "";
    }

    private sealed class Arr : Node
    {
        public Tok Open;
        public List<Elem> Elems = [];
        public Tok Close;
    }

    public static string Apply(string original, IEnumerable<KeyValuePair<string, string?>> changes)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            // Nothing on disk yet — there is nothing to preserve.
            return Fresh(changes);
        }

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(original);
            var reader = new Utf8JsonReader(bytes, ReaderOptions);
            if (!reader.Read())
            {
                return Fresh(changes);
            }

            int last = 0;
            Node root = ParseValue(ref reader, bytes, ref last);
            string trailing = Slice(bytes, last, bytes.Length);

            if (root is not Obj obj)
            {
                // A non-object root (array or bare scalar) can't carry keys.
                return Fresh(changes);
            }

            foreach (var change in changes)
            {
                ApplyOne(obj, change.Key.Split(':'), 0, change.Value);
            }

            var sb = new StringBuilder();
            Render(obj, sb);
            sb.Append(trailing);
            return sb.ToString();
        }
        catch (JsonException)
        {
            return Fresh(changes);
        }
    }

    private static void ApplyOne(Obj obj, string[] path, int depth, string? value)
    {
        string key = path[depth];
        bool last = depth == path.Length - 1;
        Member? member = obj.Members.Find(m => m.Key == key);

        if (last && value is null)
        {
            if (member is not null)
            {
                Remove(obj, member);
            }
            return;
        }

        if (member is null)
        {
            member = NewMember(obj, key);
            member.Value = last ? Scalar(value!) : new Obj { Open = T("{"), Close = T("}") };
            obj.Members.Add(member);
        }

        if (last)
        {
            member.Value = member.Value is Val v
                ? Replace(v, value!)
                : Scalar(value!, LeadOf(member.Value));
            return;
        }

        if (member.Value is Arr arr)
        {
            member.Value = ToObject(arr);
        }
        if (member.Value is not Obj child)
        {
            member.Value = child = new Obj { Open = T("{"), Close = T("}") };
        }
        ApplyOne(child, path, depth + 1, value);
    }

    private static Member NewMember(Obj obj, string key)
    {
        // Mirror an existing sibling's pre-key trivia (newline + indent, plus the
        // comma that separates members); fall back to a derived indent when empty.
        string lead;
        if (obj.Members.Count > 0)
        {
            lead = "," + StripLeadingComma(obj.Members[0].Name.Lead);
        }
        else
        {
            string indent = IndentOf(obj.Open.Lead);
            lead = $"\n{indent}  ";
            obj.Close.Lead = $"\n{indent}" + obj.Close.Lead.TrimStart(' ', '\t');
        }

        return new Member { Key = key, Name = new Tok { Lead = lead, Text = JsonScalar(key) }, Colon = new Tok { Text = ": " } };
    }

    private static void Remove(Obj obj, Member member)
    {
        int i = obj.Members.IndexOf(member);
        obj.Members.RemoveAt(i);

        // The removed member's lead holds the comments that preceded it; keep them
        // by folding that run into whatever now occupies its place, dropping exactly
        // one separating comma so the result stays well-formed.
        if (obj.Members.Count == 0)
        {
            obj.Close.Lead = StripLeadingComma(member.Name.Lead) + obj.Close.Lead;
        }
        else if (i < obj.Members.Count)
        {
            obj.Members[i].Name.Lead =
                member.Name.Lead + StripLeadingComma(obj.Members[i].Name.Lead);
        }
        else
        {
            obj.Close.Lead = StripLeadingComma(member.Name.Lead) + obj.Close.Lead;
        }
    }

    private static Obj ToObject(Arr arr)
    {
        var obj = new Obj
        {
            Open = new Tok { Lead = arr.Open.Lead, Text = "{" },
            Close = new Tok { Lead = arr.Close.Lead, Text = "}" },
        };
        for (int i = 0; i < arr.Elems.Count; i++)
        {
            obj.Members.Add(new Member
            {
                Key = i.ToString(),
                Name = new Tok { Lead = arr.Elems[i].Lead, Text = JsonScalar(i.ToString()) },
                Colon = new Tok { Text = ": " },
                Value = arr.Elems[i].Value,
            });
        }
        return obj;
    }

    // ---- value helpers -------------------------------------------------------

    private static Val Scalar(string value, string lead = "")
        => new() { Tok = new Tok { Lead = lead, Text = JsonScalar(value) } };

    private static Val Replace(Val v, string value)
    {
        v.Tok.Text = JsonScalar(value);
        return v;
    }

    private static string LeadOf(Node n) => n switch
    {
        Val v => v.Tok.Lead,
        Obj o => o.Open.Lead,
        Arr a => a.Open.Lead,
        _ => "",
    };

    private static Tok T(string text) => new() { Lead = "", Text = text };
    private static string JsonScalar(string v) => JsonSerializer.Serialize(v, ScalarOptions);

    private static string StripLeadingComma(string lead)
    {
        int c = lead.IndexOf(',');
        return c < 0 ? lead : lead.Remove(c, 1);
    }

    // The whitespace run after the last newline of a token's lead — the indent of
    // whatever follows. Empty when the lead has no newline or isn't pure whitespace.
    private static string IndentOf(string lead)
    {
        int nl = lead.LastIndexOf('\n');
        if (nl < 0)
        {
            return "";
        }
        string after = lead.Substring(nl + 1);
        return after.Trim().Length == 0 ? after : "";
    }

    // ---- parse ---------------------------------------------------------------

    private static Node ParseValue(ref Utf8JsonReader r, byte[] b, ref int last)
    {
        Tok tok = Take(ref r, b, ref last);
        switch (r.TokenType)
        {
            case JsonTokenType.StartObject:
                var obj = new Obj { Open = tok };
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndObject)
                    {
                        obj.Close = Take(ref r, b, ref last);
                        return obj;
                    }
                    string key = r.GetString() ?? "";
                    var name = Take(ref r, b, ref last);
                    var member = new Member { Name = name, Key = key };
                    r.Read();
                    member.Value = ParseValue(ref r, b, ref last);
                    // The ": " between key and value landed in the value's lead;
                    // move it onto the colon slot so inserts/renders stay uniform.
                    member.Colon = new Tok { Text = TakeLead(member.Value) };
                    obj.Members.Add(member);
                }
                throw new JsonException("Unterminated object.");

            case JsonTokenType.StartArray:
                var arr = new Arr { Open = tok };
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndArray)
                    {
                        arr.Close = Take(ref r, b, ref last);
                        return arr;
                    }
                    var elem = new Elem();
                    var v = ParseValue(ref r, b, ref last);
                    elem.Lead = TakeLead(v);
                    elem.Value = v;
                    arr.Elems.Add(elem);
                }
                throw new JsonException("Unterminated array.");

            default:
                return new Val { Tok = tok };
        }
    }

    private static string TakeLead(Node n)
    {
        switch (n)
        {
            case Val v: { var l = v.Tok.Lead; v.Tok.Lead = ""; return l; }
            case Obj o: { var l = o.Open.Lead; o.Open.Lead = ""; return l; }
            case Arr a: { var l = a.Open.Lead; a.Open.Lead = ""; return l; }
            default: return "";
        }
    }

    private static Tok Take(ref Utf8JsonReader r, byte[] b, ref int last)
    {
        // BytesConsumed swallows the trailing ':'/',' so it can't bound the raw
        // token. TokenStartIndex + the value length (ValueSpan mirrors the source
        // bytes, escapes and all) does — quotes are added back for strings/keys.
        int start = checked((int)r.TokenStartIndex);
        int len = r.TokenType switch
        {
            JsonTokenType.StartObject or JsonTokenType.StartArray
                or JsonTokenType.EndObject or JsonTokenType.EndArray => 1,
            JsonTokenType.String or JsonTokenType.PropertyName => r.ValueSpan.Length + 2,
            _ => r.ValueSpan.Length,
        };
        var tok = new Tok { Lead = Slice(b, last, start), Text = Slice(b, start, start + len) };
        last = start + len;
        return tok;
    }

    private static string Slice(byte[] b, int from, int to)
        => from >= to ? "" : Encoding.UTF8.GetString(b, from, to - from);

    // ---- render --------------------------------------------------------------

    private static void Render(Node n, StringBuilder sb)
    {
        switch (n)
        {
            case Val v:
                sb.Append(v.Tok.Lead).Append(v.Tok.Text);
                break;
            case Obj o:
                sb.Append(o.Open.Lead).Append(o.Open.Text);
                foreach (var m in o.Members)
                {
                    sb.Append(m.Name.Lead).Append(m.Name.Text).Append(m.Colon.Text);
                    Render(m.Value, sb);
                }
                sb.Append(o.Close.Lead).Append(o.Close.Text);
                break;
            case Arr a:
                sb.Append(a.Open.Lead).Append(a.Open.Text);
                foreach (var e in a.Elems)
                {
                    sb.Append(e.Lead);
                    Render(e.Value, sb);
                }
                sb.Append(a.Close.Lead).Append(a.Close.Text);
                break;
        }
    }

    private static string Fresh(IEnumerable<KeyValuePair<string, string?>> changes)
        => JsonSerializer.Serialize(
               PersistentConfigurationFile.BuildTree(changes),
               new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
           + Environment.NewLine;
}
